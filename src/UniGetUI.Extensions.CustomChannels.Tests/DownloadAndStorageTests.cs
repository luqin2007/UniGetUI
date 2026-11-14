using UniGetUI.Extensions.CustomChannels.Download;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using FluentAssertions;

namespace UniGetUI.Extensions.CustomChannels.Tests;

public sealed class DownloadAndStorageTests : IDisposable
{
    private readonly string _root;

    public DownloadAndStorageTests()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "test-artifacts", "io-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void 工作区守卫应拒绝越界路径()
    {
        var inside = Path.Combine(_root, "cache", "a.txt");
        WorkspaceGuard.AssertInside(_root, inside).Should().Be(Path.GetFullPath(inside));

        var act = () => WorkspaceGuard.AssertInside(_root, Path.Combine(_root, "..", "outside.txt"));
        act.Should().Throw<WorkspaceViolationException>();
    }

    [Fact]
    public void 应拒绝系统临时目录()
    {
        var temp = Path.Combine(Path.GetTempPath(), "appupdater-test.tmp");
        var act = () => WorkspaceGuard.AssertNotSystemTemp(temp);

        act.Should().Throw<WorkspaceViolationException>();
    }

    [Fact]
    public async Task 流式哈希校验应能识别大小与摘要()
    {
        var file = Path.Combine(_root, "payload.bin");
        var bytes = new byte[4096];
        Random.Shared.NextBytes(bytes);
        await File.WriteAllBytesAsync(file, bytes);

        var sha = await HashVerifier.ComputeSha256Async(file);

        (await HashVerifier.VerifyAsync(file, sha, bytes.Length)).Should().BeTrue();
        (await HashVerifier.VerifyAsync(file, sha, bytes.Length + 1)).Should().BeFalse();
        (await HashVerifier.VerifyAsync(file, new string('0', 64), bytes.Length)).Should().BeFalse();
    }

    [Fact]
    public void 分片计划应正确计算完成度()
    {
        var plan = new DownloadPlanDocument
        {
            Destination = "x",
            TotalLength = 100,
            Segments =
            [
                new SegmentState { Start = 0, End = 49, Completed = 50 },
                new SegmentState { Start = 50, End = 99, Completed = 10 },
            ],
        };

        plan.CompletedBytes.Should().Be(60);
        plan.IsComplete.Should().BeFalse();

        plan.Segments[1].Completed = 50;
        plan.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void 下载句柄应能识别即将过期的链接()
    {
        var expiring = new DownloadHandle(
            new Uri("https://example.com/a.zip"),
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(20));
        var stable = new DownloadHandle(new Uri("https://example.com/a.zip"));

        expiring.IsExpired(DateTimeOffset.UtcNow).Should().BeTrue();
        stable.IsExpired(DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void 脱敏应隐藏令牌与Cookie()
    {
        var text = "Cookie=BDUSS=abcdef;STOKEN=secret; Authorization: Bearer xyz pwd=123456";

        var redacted = SensitiveDataRedactor.Redact(text);

        redacted.Should().NotContain("abcdef");
        redacted.Should().NotContain("secret");
        redacted.Should().NotContain("123456");
        redacted.Should().Contain("***");
    }

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
