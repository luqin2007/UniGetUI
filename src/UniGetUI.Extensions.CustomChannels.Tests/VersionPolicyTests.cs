using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Models;
using UniGetUI.Extensions.CustomChannels.Update;
using FluentAssertions;

namespace UniGetUI.Extensions.CustomChannels.Tests;

public sealed class VersionPolicyTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.4", VersionRelation.Newer)]
    [InlineData("1.2.4", "1.2.4", VersionRelation.Same)]
    [InlineData("v1.2.10", "1.2.9", VersionRelation.Older)]
    [InlineData("2024.10.1", "2024.9.30", VersionRelation.Older)]
    [InlineData("1.0.0-beta.1", "1.0.0", VersionRelation.Newer)]
    [InlineData(null, "1.0.0", VersionRelation.Unknown)]
    public void Compare_应该是语义化比较(string? local, string? remote, VersionRelation expected)
    {
        VersionPolicy.Compare(local, remote).Should().Be(expected);
    }

    [Fact]
    public void 默认策略应禁止降级()
    {
        var version = CreateVersion("1.0.0");
        var candidate = VersionPolicy.Evaluate(version, "2.0.0", new UpdatePolicyConfig());

        candidate.Relation.Should().Be(VersionRelation.Older);
        candidate.IsSelectable.Should().BeFalse();
        candidate.BlockReason.Should().Contain("允许降级");
    }

    [Fact]
    public void 开启允许降级后旧版本可选()
    {
        var version = CreateVersion("1.0.0");
        var candidate = VersionPolicy.Evaluate(version, "2.0.0", new UpdatePolicyConfig { AllowDowngrade = true });

        candidate.IsSelectable.Should().BeTrue();
        candidate.BlockReason.Should().BeNull();
    }

    [Fact]
    public void 固定版本会屏蔽其它版本()
    {
        var version = CreateVersion("2.0.0");
        var policy = new UpdatePolicyConfig { PinnedVersion = "1.5.0" };

        var candidate = VersionPolicy.Evaluate(version, "1.0.0", policy);

        candidate.IsSelectable.Should().BeFalse();
        candidate.BlockReason.Should().Contain("固定");
    }

    [Fact]
    public void 跳过版本应被标记()
    {
        var version = CreateVersion("v2.1.0");
        var policy = new UpdatePolicyConfig { SkippedVersions = ["2.1.0"] };

        var candidate = VersionPolicy.Evaluate(version, "1.0.0", policy);

        candidate.IsSkipped.Should().BeTrue();
        candidate.IsSelectable.Should().BeTrue();
    }

    [Fact]
    public void 排序应按版本降序()
    {
        var candidates = new[]
        {
            VersionPolicy.Evaluate(CreateVersion("1.0.0"), null, new UpdatePolicyConfig()),
            VersionPolicy.Evaluate(CreateVersion("2.5.0"), null, new UpdatePolicyConfig()),
            VersionPolicy.Evaluate(CreateVersion("2.10.0"), null, new UpdatePolicyConfig()),
        };

        var ordered = VersionPolicy.Order(candidates).Select(c => c.Version.DisplayVersion).ToList();

        ordered.Should().ContainInOrder("2.10.0", "2.5.0", "1.0.0");
    }

    private static VersionInfo CreateVersion(string tag) => new(
        "test",
        "测试渠道",
        tag,
        VersionComparer.Normalize(tag),
        DateTimeOffset.UtcNow,
        false,
        null,
        [new AssetSummary("app.zip", 1024, null, "https://example.com/app.zip")]);
}
