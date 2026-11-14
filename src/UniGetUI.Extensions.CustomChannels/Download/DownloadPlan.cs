namespace UniGetUI.Extensions.CustomChannels.Download;

/// <summary>分片下载计划，持久化到 &lt;目标文件&gt;.parts.json，用于断点续传。</summary>
public sealed class DownloadPlanDocument
{
    public string Destination { get; set; } = string.Empty;

    public string? SourceUri { get; set; }

    public long? TotalLength { get; set; }

    public string? Sha256 { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<SegmentState> Segments { get; set; } = new();

    public long CompletedBytes => Segments.Sum(s => Math.Min(s.Completed, s.Length));

    public bool IsComplete => Segments.Count > 0 && Segments.All(s => s.Completed >= s.Length);
}

public sealed class SegmentState
{
    public long Start { get; set; }

    public long End { get; set; }

    public long Completed { get; set; }

    public long Length => End - Start + 1;
}

/// <summary>下载请求。HandleFactory 允许短时效链接过期后重新取链。</summary>
public sealed record DownloadRequest(
    Func<CancellationToken, Task<DownloadHandle>> HandleFactory,
    string DestinationPath,
    int Concurrency = 4,
    long MaxBytesPerSecond = 0,
    long? ExpectedSize = null,
    string? Sha256 = null,
    int RefreshThresholdSeconds = 60);

/// <summary>可下载句柄：直链 + 必需请求头 + 时效。</summary>
public sealed record DownloadHandle(
    Uri Uri,
    IReadOnlyDictionary<string, string>? Headers = null,
    long? ExpectedSize = null,
    string? Sha256 = null,
    DateTimeOffset? ExpiresAt = null)
{
    public bool IsExpired(DateTimeOffset now, int thresholdSeconds = 60) =>
        ExpiresAt is { } expires && expires - now < TimeSpan.FromSeconds(thresholdSeconds);
}

public sealed record DownloadResult(string Path, long Bytes, string? Sha256, TimeSpan Elapsed);
