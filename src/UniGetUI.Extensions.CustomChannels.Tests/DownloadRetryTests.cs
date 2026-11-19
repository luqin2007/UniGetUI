using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Download;
using Xunit;

namespace UniGetUI.Extensions.CustomChannels.Tests;

/// <summary>下载被 CDN 以 412/403 拒绝时，应重新取链再重试一次。</summary>
public sealed class DownloadRetryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory, "test-artifacts", "retry-" + Guid.NewGuid().ToString("N")[..6]);

    [Fact]
    public async Task 下载被拒绝时重新取链后应成功()
    {
        Directory.CreateDirectory(_root);
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var destination = Path.Combine(_root, "pkg.bin");

        var handler = new TestHttpHandler()
            .Enqueue(_ => Rejected())   // HEAD 探测
            .Enqueue(_ => Rejected())   // Range 探测
            .Enqueue(_ => Rejected())   // 首次下载（旧令牌）
            .Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });

        var handleRequests = 0;
        var request = new DownloadRequest(
            _ =>
            {
                handleRequests++;
                return Task.FromResult(new DownloadHandle(new Uri("https://dl.example/pkg.bin")));
            },
            destination,
            Concurrency: 1,
            ExpectedSize: payload.Length);

        using var downloader = new SegmentedDownloader(
            new DownloadConfig(), NullLogger<SegmentedDownloader>.Instance, handler);

        await downloader.DownloadAsync(request);

        handleRequests.Should().Be(2);
        (await File.ReadAllBytesAsync(destination)).Should().Equal(payload);
    }

    private static HttpResponseMessage Rejected() =>
        new(HttpStatusCode.PreconditionFailed) { Content = new ByteArrayContent([]) };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
