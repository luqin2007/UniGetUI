using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Models;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Download;

/// <summary>
/// 分片并发下载器：HEAD/Range 探测、断点续传（.parts.json）、限速、短时效链接重取、SHA256 校验。
/// 所有数据流式写入目标文件，绝不整包进内存。
/// </summary>
public sealed class SegmentedDownloader : IDisposable
{
    private const int BufferSize = 128 * 1024;
    private const long MinSegmentSize = 2 * 1024 * 1024;

    /// <summary>下载被拒（链接/会话令牌失效）时重新取链的最大次数。</summary>
    private const int MaxHandleRefresh = 2;

    private readonly HttpClient _http;
    private readonly DownloadConfig _config;
    private readonly ILogger<SegmentedDownloader> _logger;
    private readonly bool _ownsHttpClient;

    public SegmentedDownloader(DownloadConfig config, ILogger<SegmentedDownloader> logger, HttpMessageHandler? handler = null)
    {
        _config = config;
        _logger = logger;

        if (handler is not null)
        {
            // 单元测试注入：直接用外部 handler，不需要真实网络
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _ownsHttpClient = true;
            return;
        }

        var socketsHandler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseCookies = false,
        };

        if (!string.IsNullOrWhiteSpace(config.Proxy) &&
            Uri.TryCreate(config.Proxy, UriKind.Absolute, out var proxy))
        {
            socketsHandler.Proxy = new WebProxy(proxy);
            socketsHandler.UseProxy = true;
        }

        _http = new HttpClient(socketsHandler) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttpClient = true;
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<StageProgress>? progress = null,
        CancellationToken ct = default)
    {
        WorkspaceGuard.AssertNotSystemTemp(request.DestinationPath);

        var directory = Path.GetDirectoryName(request.DestinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var planPath = request.DestinationPath + ".parts.json";
        var stopwatch = Stopwatch.StartNew();

        // 上次下好的包还在且内容一致时直接复用：先用请求自带的大小/哈希判断，
        // 命中就不用再取链，连渠道接口都不必访问
        if (request.ExpectedSize is not null &&
            await TryReuseAsync(request, request.ExpectedSize, request.Sha256, progress, ct).ConfigureAwait(false))
        {
            return ReusedResult(request, request.Sha256, stopwatch);
        }

        var handle = await request.HandleFactory(ct).ConfigureAwait(false);

        var sha256 = request.Sha256 ?? handle.Sha256;
        var knownSize = request.ExpectedSize ?? handle.ExpectedSize;

        if (await TryReuseAsync(request, knownSize, sha256, progress, ct).ConfigureAwait(false))
        {
            return ReusedResult(request, sha256, stopwatch);
        }

        var probe = await ProbeAsync(handle, request, ct).ConfigureAwait(false);
        var total = probe.TotalLength ?? knownSize;

        if (total is { } expected)
        {
            WorkspaceGuard.EnsureFreeSpace(request.DestinationPath, expected);
        }

        var useSegments = probe.SupportsRanges && total is > MinSegmentSize && request.Concurrency > 1;
        var counter = new ProgressCounter(total);

        if (useSegments && total is { } length)
        {
            await DownloadSegmentedAsync(
                request, handle, length, planPath, counter, progress, ct).ConfigureAwait(false);
        }
        else
        {
            await DownloadSingleStreamAsync(
                request, handle, total, counter, progress, ct).ConfigureAwait(false);
        }

        JsonFileStore.TryDelete(planPath);

        if (!string.IsNullOrWhiteSpace(sha256) || request.ExpectedSize is not null)
        {
            progress?.Report(new StageProgress(UpdateStage.Verifying, "正在校验更新包…", 100));
            var ok = await HashVerifier.VerifyAsync(
                request.DestinationPath,
                sha256,
                request.ExpectedSize ?? total,
                ct).ConfigureAwait(false);

            if (!ok)
            {
                TryDelete(request.DestinationPath);
                throw new InvalidDataException(
                    "更新包校验失败（大小或 SHA256 不匹配），已删除下载内容，请重试或切换渠道。");
            }
        }

        var finalSize = new FileInfo(request.DestinationPath).Length;
        _logger.LogInformation(
            "下载完成：{Path}（{Size} 字节，用时 {Elapsed:F1}s）",
            request.DestinationPath,
            finalSize,
            stopwatch.Elapsed.TotalSeconds);

        return new DownloadResult(request.DestinationPath, finalSize, sha256, stopwatch.Elapsed);
    }

    private DownloadResult ReusedResult(DownloadRequest request, string? sha256, Stopwatch stopwatch)
    {
        var size = new FileInfo(request.DestinationPath).Length;
        _logger.LogInformation("复用已下载的更新包：{Path}（{Size} 字节）", request.DestinationPath, size);
        return new DownloadResult(request.DestinationPath, size, sha256, stopwatch.Elapsed);
    }

    /// <summary>
    /// 目标文件已存在且与待下载内容一致时直接复用：配置了 SHA256 就比哈希，
    /// 只有大小时比大小（每次发布的更新包文件名唯一，同名的两份内容不会大小相同）。
    /// 存在断点文件说明上次没下完，不能当完整文件复用。
    /// </summary>
    private static async Task<bool> TryReuseAsync(
        DownloadRequest request,
        long? expectedSize,
        string? sha256,
        IProgress<StageProgress>? progress,
        CancellationToken ct)
    {
        if (expectedSize is null || !File.Exists(request.DestinationPath))
        {
            return false;
        }

        if (File.Exists(request.DestinationPath + ".parts.json"))
        {
            return false;
        }

        var info = new FileInfo(request.DestinationPath);
        if (info.Length != expectedSize)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(sha256) &&
            !await HashVerifier.VerifyAsync(request.DestinationPath, sha256, expectedSize, ct).ConfigureAwait(false))
        {
            return false;
        }

        progress?.Report(new StageProgress(
            UpdateStage.Downloading,
            "已存在相同的更新包，跳过下载",
            100,
            info.Length,
            info.Length,
            0));
        return true;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    private async Task<ProbeResult> ProbeAsync(
        DownloadHandle handle,
        DownloadRequest request,
        CancellationToken ct)
    {
        // 优先 HEAD
        try
        {
            using var headRequest = CreateRequest(HttpMethod.Head, handle, null);
            using var response = await _http
                .SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var length = response.Content.Headers.ContentLength;
                var acceptsRanges = response.Headers.AcceptRanges
                    .Any(v => v.Contains("bytes", StringComparison.OrdinalIgnoreCase));
                return new ProbeResult(length, acceptsRanges);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug("HEAD 探测失败，改用 Range 探测：{Message}", ex.Message);
        }

        // 回退：请求 1 字节判断是否支持 Range
        try
        {
            using var rangeRequest = CreateRequest(HttpMethod.Get, handle, new RangeHeaderValue(0, 0));
            using var response = await _http
                .SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var total = response.Content.Headers.ContentRange?.Length;
                return new ProbeResult(total ?? request.ExpectedSize, true);
            }

            var length = response.Content.Headers.ContentLength;
            return new ProbeResult(length ?? request.ExpectedSize, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug("Range 探测失败，按单流下载：{Message}", ex.Message);
            return new ProbeResult(request.ExpectedSize, false);
        }
    }

    private async Task DownloadSegmentedAsync(
        DownloadRequest request,
        DownloadHandle handle,
        long total,
        string planPath,
        ProgressCounter counter,
        IProgress<StageProgress>? progress,
        CancellationToken ct)
    {
        var plan = LoadOrCreatePlan(planPath, request.DestinationPath, total, request.Sha256);
        var currentHandle = handle;

        using var fileHandle = File.OpenHandle(
            request.DestinationPath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.ReadWrite,
            FileOptions.Asynchronous);
        RandomAccess.SetLength(fileHandle, total);

        var semaphore = new SemaphoreSlim(Math.Clamp(request.Concurrency, 1, 8));
        var limiter = new SpeedLimiter(request.MaxBytesPerSecond);
        var lastReport = 0L;
        var reportGate = new object();

        void Report(bool force = false)
        {
            lock (reportGate)
            {
                var now = Stopwatch.GetTimestamp();
                if (!force && now - lastReport < Stopwatch.Frequency / 10)
                {
                    return;
                }

                lastReport = now;
                progress?.Report(new StageProgress(
                    UpdateStage.Downloading,
                    "正在下载更新包…",
                    counter.Percent(total),
                    counter.Received,
                    total,
                    counter.Speed));
            }
        }

        var tasks = plan.Segments.Select(async segment =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                while (segment.Completed < segment.Length)
                {
                    ct.ThrowIfCancellationRequested();

                    var (segmentRequest, response) = await SendWithFreshHandleAsync(
                        request,
                        HttpMethod.Get,
                        currentHandle,
                        new RangeHeaderValue(segment.Start + segment.Completed, segment.End),
                        ct,
                        refreshed => currentHandle = refreshed).ConfigureAwait(false);

                    using (segmentRequest)
                    using (response)
                    {
                        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                        {
                            // 已完整下载
                            counter.Add(segment.Length - segment.Completed);
                            segment.Completed = segment.Length;
                            break;
                        }

                        response.EnsureSuccessStatusCode();
                        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

                        var buffer = new byte[BufferSize];
                        int read;
                        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            var remaining = segment.Length - segment.Completed;
                            if (read > remaining)
                            {
                                read = (int)remaining;
                            }

                            if (read <= 0)
                            {
                                break;
                            }

                            RandomAccess.Write(fileHandle, buffer.AsSpan(0, read), segment.Start + segment.Completed);
                            segment.Completed += read;
                            counter.Add(read);
                            Report();
                            await limiter.ThrottleAsync(read, ct).ConfigureAwait(false);
                        }
                    }

                    SavePlan(planPath, plan);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SavePlan(planPath, plan);
            throw;
        }

        SavePlan(planPath, plan);
        Report(force: true);
    }

    private async Task DownloadSingleStreamAsync(
        DownloadRequest request,
        DownloadHandle handle,
        long? total,
        ProgressCounter counter,
        IProgress<StageProgress>? progress,
        CancellationToken ct)
    {
        var (message, response) = await SendWithFreshHandleAsync(
            request, HttpMethod.Get, handle, null, ct, refreshed => handle = refreshed).ConfigureAwait(false);

        using (message)
        using (response)
        {
            response.EnsureSuccessStatusCode();

            var length = total ?? response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var destination = new FileStream(
                request.DestinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var limiter = new SpeedLimiter(request.MaxBytesPerSecond);
            var buffer = new byte[BufferSize];
            var lastReport = 0L;
            int read;

            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                counter.Add(read);

                var now = Stopwatch.GetTimestamp();
                if (now - lastReport >= Stopwatch.Frequency / 10)
                {
                    lastReport = now;
                    progress?.Report(new StageProgress(
                        UpdateStage.Downloading,
                        "正在下载更新包…",
                        counter.Percent(length),
                        counter.Received,
                        length,
                        counter.Speed));
                }

                await limiter.ThrottleAsync(read, ct).ConfigureAwait(false);
            }

            await destination.FlushAsync(ct).ConfigureAwait(false);
            progress?.Report(new StageProgress(
                UpdateStage.Downloading,
                "下载完成",
                100,
                counter.Received,
                length ?? counter.Received,
                counter.Speed));
        }
    }

    /// <summary>
    /// 发送一次下载请求；链接过期或 CDN 判定前置条件不满足（夸克直链的 412
    /// 就是会话令牌轮换造成的）时重新取链重试，最多 <see cref="MaxHandleRefresh"/> 次。
    /// </summary>
    private async Task<(HttpRequestMessage Request, HttpResponseMessage Response)> SendWithFreshHandleAsync(
        DownloadRequest request,
        HttpMethod method,
        DownloadHandle handle,
        RangeHeaderValue? range,
        CancellationToken ct,
        Action<DownloadHandle> onRefreshed)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (handle.IsExpired(DateTimeOffset.UtcNow, request.RefreshThresholdSeconds))
            {
                handle = await request.HandleFactory(ct).ConfigureAwait(false);
                onRefreshed(handle);
                _logger.LogInformation("下载链接即将过期，已重新取链。");
            }

            var message = CreateRequest(method, handle, range);
            var response = await _http
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!IsStaleHandleStatus(response.StatusCode) || attempt >= MaxHandleRefresh)
            {
                return (message, response);
            }

            var status = (int)response.StatusCode;
            response.Dispose();
            message.Dispose();
            _logger.LogWarning("下载请求被拒绝（HTTP {Status}），重新取链后重试", status);

            handle = await request.HandleFactory(ct).ConfigureAwait(false);
            onRefreshed(handle);
        }
    }

    private static bool IsStaleHandleStatus(HttpStatusCode status) =>
        status is HttpStatusCode.PreconditionFailed or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;

    private HttpRequestMessage CreateRequest(HttpMethod method, DownloadHandle handle, RangeHeaderValue? range)
    {
        var message = new HttpRequestMessage(method, handle.Uri);
        if (range is not null)
        {
            message.Headers.Range = range;
        }

        if (handle.Headers is not null)
        {
            foreach (var (key, value) in handle.Headers)
            {
                if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                message.Headers.TryAddWithoutValidation(key, value);
            }
        }

        return message;
    }

    private static DownloadPlanDocument LoadOrCreatePlan(
        string planPath,
        string destination,
        long total,
        string? sha256)
    {
        try
        {
            var existing = JsonFileStore.Read(planPath, AppJsonContext.Default.DownloadPlanDocument);
            if (existing is not null &&
                existing.TotalLength == total &&
                string.Equals(existing.Destination, destination, StringComparison.OrdinalIgnoreCase) &&
                existing.Segments.Count > 0)
            {
                // 已完成的分片直接计入进度
                return existing;
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }

        var plan = new DownloadPlanDocument
        {
            Destination = destination,
            TotalLength = total,
            Sha256 = sha256,
            Segments = CreateSegments(total),
        };
        JsonFileStore.Write(planPath, plan, AppJsonContext.Default.DownloadPlanDocument);
        return plan;
    }

    private static List<SegmentState> CreateSegments(long total)
    {
        var segments = new List<SegmentState>();
        var count = (int)Math.Min(8, Math.Max(1, total / MinSegmentSize));
        var chunk = total / count;
        long start = 0;

        for (var i = 0; i < count; i++)
        {
            var end = i == count - 1 ? total - 1 : start + chunk - 1;
            segments.Add(new SegmentState { Start = start, End = end, Completed = 0 });
            start = end + 1;
        }

        return segments;
    }

    private static void SavePlan(string planPath, DownloadPlanDocument plan)
    {
        try
        {
            JsonFileStore.Write(planPath, plan, AppJsonContext.Default.DownloadPlanDocument);
        }
        catch (IOException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed record ProbeResult(long? TotalLength, bool SupportsRanges);

    private sealed class ProgressCounter(long? total)
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private long _received;

        public long Received => Interlocked.Read(ref _received);

        public double Speed
        {
            get
            {
                var elapsed = _watch.Elapsed.TotalSeconds;
                return elapsed <= 0.1 ? 0 : Received / elapsed;
            }
        }

        public void Add(long bytes) => Interlocked.Add(ref _received, bytes);

        public double? Percent(long? knownTotal)
        {
            if (knownTotal is not > 0)
            {
                return total is > 0 ? Math.Min(100, Received * 100d / total.Value) : null;
            }

            return Math.Min(100, Received * 100d / knownTotal.Value);
        }
    }
}

internal sealed class SpeedLimiter(long bytesPerSecond)
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private long _total;

    public async Task ThrottleAsync(int bytes, CancellationToken ct)
    {
        if (bytesPerSecond <= 0)
        {
            return;
        }

        _total += bytes;
        var expectedMs = _total * 1000d / bytesPerSecond;
        var actualMs = _watch.Elapsed.TotalMilliseconds;
        if (expectedMs <= actualMs)
        {
            return;
        }

        var delay = Math.Min(expectedMs - actualMs, 500);
        if (delay > 1)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delay), ct).ConfigureAwait(false);
        }
    }
}
