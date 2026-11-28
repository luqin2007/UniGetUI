using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Sources;
using FluentAssertions;

namespace UniGetUI.Extensions.CustomChannels.Tests;

public sealed class SourceUrlHelperTests
{
    [Theory]
    [InlineData("https://github.com/Justsenger/ExHyperV/", "Justsenger/ExHyperV")]
    [InlineData("https://github.com/Justsenger/ExHyperV", "Justsenger/ExHyperV")]
    [InlineData("https://github.com/Justsenger/ExHyperV/releases", "Justsenger/ExHyperV")]
    [InlineData("http://github.com/owner/repo.git", "owner/repo")]
    [InlineData("github.com/owner/repo", "owner/repo")]
    [InlineData("owner/repo", "owner/repo")]
    [InlineData("owner/repo/", "owner/repo")]
    [InlineData("  owner/repo  ", "owner/repo")]
    public void GitHub仓库地址应归一化为owner_repo(string input, string expected)
    {
        SourceUrlHelper.NormalizeGithubRepo(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://gitlab.com/group/sub/project", "group/sub/project")]
    [InlineData("https://gitlab.com/group/sub/project/-/releases", "group/sub/project")]
    [InlineData("https://gitlab.com/group/project.git", "group/project")]
    [InlineData("group/project", "group/project")]
    public void GitLab项目地址应归一化为路径(string input, string expected)
    {
        SourceUrlHelper.NormalizeGitlabProject(input).Should().Be(expected);
    }

    [Fact]
    public void 自建GitLab实例应从地址推断根路径()
    {
        SourceUrlHelper.InferGitlabBaseUrl("https://git.example.com/group/project")
            .Should().Be("https://git.example.com");
        SourceUrlHelper.InferGitlabBaseUrl("https://gitlab.com/group/project").Should().BeNull();
        SourceUrlHelper.InferGitlabBaseUrl("group/project").Should().BeNull();
    }

    [Theory]
    [InlineData("ExHyperV_V1.5.2_Setup_x64.exe", "ExHyperV_V*_Setup_x64.exe")]
    [InlineData("app-1.2.3-win-x64.zip", "app-*-win-x64.zip")]
    [InlineData("tool-2024.10.1.msi", "tool-*.msi")]
    [InlineData("readme.txt", "readme.txt")]
    public void 资产名应转换成通配符(string input, string expected)
    {
        AssetPatternSuggester.ToPattern(input).Should().Be(expected);
    }

    [Fact]
    public void 应按安装方式与架构挑选建议资产()
    {
        var assets = new[]
        {
            "ExHyperV_V1.5.2_arm64.exe",
            "ExHyperV_V1.5.2_Setup_arm64.exe",
            "ExHyperV_V1.5.2_Setup_x64.exe",
            "ExHyperV_V1.5.2_x64.exe",
        };

        // 优先 x64，且同架构下取第一个命中项（Setup 版在 arm64 之后）
        AssetPatternSuggester.Suggest(assets, InstallKind.Msi)
            .Should().Be("ExHyperV_V*_Setup_x64.exe");

        // 没有 zip 时回落到全部资产，仍优先 x64
        AssetPatternSuggester.Suggest(assets, InstallKind.ZipOverwrite)
            .Should().Be("ExHyperV_V*_Setup_x64.exe");

        AssetPatternSuggester
            .Suggest(["app-1.2.3-win-arm64.zip", "app-1.2.3-win-x64.zip"], InstallKind.ZipOverwrite)
            .Should().Be("app-*-win-x64.zip");
    }
}
