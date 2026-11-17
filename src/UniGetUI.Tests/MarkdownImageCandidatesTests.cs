using UniGetUI.Avalonia.Integrations.CustomChannels;
using Xunit;

namespace UniGetUI.Tests;

/// <summary>README 图片加载候选源：原图失败时回退 jsDelivr CDN 镜像。</summary>
public sealed class MarkdownImageCandidatesTests
{
    [Fact]
    public void RawGitHubusercontentRefsHeads形式转换为jsDelivr()
    {
        var candidates = MarkdownPresenter.BuildImageCandidates(
            "https://raw.githubusercontent.com/LinYuanovo/pic_bed/refs/heads/main/HGame-Manager/game_detail_page.png");

        Assert.Equal(3, candidates.Length);
        Assert.Equal(
            "https://raw.githubusercontent.com/LinYuanovo/pic_bed/refs/heads/main/HGame-Manager/game_detail_page.png",
            candidates[0]);
        Assert.Equal(
            "https://cdn.jsdelivr.net/gh/LinYuanovo/pic_bed@main/HGame-Manager/game_detail_page.png",
            candidates[1]);
        Assert.Equal(
            "https://testingcf.jsdelivr.net/gh/LinYuanovo/pic_bed@main/HGame-Manager/game_detail_page.png",
            candidates[2]);
    }

    [Fact]
    public void RawGitHubusercontentBranch形式转换为jsDelivr()
    {
        var candidates = MarkdownPresenter.BuildImageCandidates(
            "https://raw.githubusercontent.com/user/repo/main/docs/img.jpg");

        Assert.Equal(
            "https://cdn.jsdelivr.net/gh/user/repo@main/docs/img.jpg",
            candidates[1]);
    }

    [Fact]
    public void GitHubRaw路径形式转换为jsDelivr()
    {
        var candidates = MarkdownPresenter.BuildImageCandidates(
            "https://github.com/user/repo/raw/main/docs/img.png");

        Assert.Equal(
            "https://cdn.jsdelivr.net/gh/user/repo@main/docs/img.png",
            candidates[1]);
    }

    [Fact]
    public void 非GitHub图床不生成镜像()
    {
        var candidates = MarkdownPresenter.BuildImageCandidates("https://img.example.com/a.png");

        Assert.Single(candidates);
        Assert.Equal("https://img.example.com/a.png", candidates[0]);
    }
}
