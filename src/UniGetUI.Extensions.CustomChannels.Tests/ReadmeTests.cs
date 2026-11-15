using System.Net;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Models;
using UniGetUI.Extensions.CustomChannels.OpenList;
using UniGetUI.Extensions.CustomChannels.Pan;
using UniGetUI.Extensions.CustomChannels.Services;
using UniGetUI.Extensions.CustomChannels.Sources;
using UniGetUI.Extensions.CustomChannels.Update;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace UniGetUI.Extensions.CustomChannels.Tests;

public sealed class ReadmeTests
{
    private const string ReleaseJson = """
    [
      {
        "tag_name": "v1.2.0",
        "name": "1.2.0",
        "prerelease": false,
        "published_at": "2026-01-02T03:04:05Z",
        "body": "修复若干问题",
        "assets": [
          { "name": "app.zip", "size": 10, "browser_download_url": "https://example.com/app.zip" }
        ]
      }
    ]
    """;

    [Theory]
    [InlineData("![shot](docs/shot.png)", "https://raw.example/repo/v1/docs/shot.png")]
    [InlineData("![shot](./docs/shot.png)", "https://raw.example/repo/v1/docs/shot.png")]
    [InlineData("![shot](/docs/shot.png)", "https://raw.example/repo/v1/docs/shot.png")]
    public void 相对图片地址应改写为绝对地址(string markdown, string expected)
    {
        var fixedText = MarkdownImageLinkFixer.Fix(markdown, "https://raw.example/repo/v1/");

        fixedText.Should().Contain(expected);
    }

    [Fact]
    public void 图片标题与绝对地址应保持不变()
    {
        var markdown = """
        ![a](docs/a.png "标题")
        ![b](https://cdn.example.com/b.png)
        """;

        var fixedText = MarkdownImageLinkFixer.Fix(markdown, "https://raw.example/repo/v1/");

        fixedText.Should().Contain("![a](<https://raw.example/repo/v1/docs/a.png> \"标题\")");
        fixedText.Should().Contain("https://cdn.example.com/b.png");
    }

    [Fact]
    public void 尖括号写法的地址应保持尖括号()
    {
        var fixedText = MarkdownImageLinkFixer.Fix(
            "![a](<docs/my shot.png>)", "https://raw.example/repo/v1/");

        fixedText.Should().Be("![a](<https://raw.example/repo/v1/docs/my%20shot.png>)");
    }

    [Theory]
    [InlineData(
        "![a](https://github.com/owner/repo/blob/main/img/1.png)",
        "![a](<https://raw.githubusercontent.com/owner/repo/main/img/1.png>)")]
    [InlineData(
        "![a](https://github.com/owner/repo/blob/main/img/1.png?raw=true)",
        "![a](<https://raw.githubusercontent.com/owner/repo/main/img/1.png>)")]
    public void GitHub网页blob地址应转为raw地址(string markdown, string expected)
    {
        MarkdownImageLinkFixer.Fix(markdown, "https://raw.githubusercontent.com/owner/repo/v1/")
            .Should().Be(expected);
    }

    [Fact]
    public void GitLab网页blob地址应转为raw地址()
    {
        var fixedText = MarkdownImageLinkFixer.Fix(
            "![a](https://gitlab.example.com/group/demo/-/blob/v2/img/1.png)",
            "https://gitlab.example.com/group/demo/-/raw/v2/");

        fixedText.Should().Be("![a](<https://gitlab.example.com/group/demo/-/raw/v2/img/1.png>)");
    }

    [Fact]
    public void HTML图片标签的相对地址也应改写()
    {
        var fixedText = MarkdownImageLinkFixer.Fix(
            "<img src=\"docs/a.png\" width=\"240\" />", "https://raw.example/repo/v1/");

        fixedText.Should().Contain("src=\"https://raw.example/repo/v1/docs/a.png\"");
        fixedText.Should().Contain("width=\"240\"");
    }

    [Fact]
    public async Task GitHub应按版本tag下载README并修正图片()
    {
        var handler = new TestHttpHandler()
            .EnqueueText("# 标题\n\n![截图](docs/shot.png)\n")
            .EnqueueText(string.Empty, HttpStatusCode.NotFound);

        var source = new GithubReleaseSource(
            new HttpClient(handler),
            new SourceConfig { Type = SourceType.GithubRelease, Repo = "owner/demo" },
            NullLogger.Instance);

        var readme = await source.GetReadmeAsync(CreateGithubVersion());

        readme.Should().NotBeNull();
        readme!.Ref.Should().Be("v1.2.0");
        readme.Sections.Should().ContainSingle();
        readme.Sections[0].File.Should().Be("README.md");
        readme.Sections[0].Language.Should().Be("en");
        readme.Sections[0].Markdown.Should().Contain(
            "https://raw.githubusercontent.com/owner/demo/v1.2.0/docs/shot.png");
        readme.Sha256.Should().HaveLength(64);

        handler.RequestUris[0].Should().Be(
            "https://api.github.com/repos/owner/demo/contents/README.md?ref=v1.2.0");
        handler.RequestUris[1].Should().Contain("contents/README_CN.md");
    }

    [Fact]
    public async Task GitHub应分别保留中文与英文README()
    {
        var handler = new TestHttpHandler()
            .EnqueueText("English")
            .EnqueueText("中文说明");

        var source = new GithubReleaseSource(
            new HttpClient(handler),
            new SourceConfig { Type = SourceType.GithubRelease, Repo = "owner/demo" },
            NullLogger.Instance);

        var readme = await source.GetReadmeAsync(CreateGithubVersion());

        readme.Should().NotBeNull();
        readme!.Sections.Should().HaveCount(2);
        readme.Sections[0].File.Should().Be("README.md");
        readme.Sections[0].Language.Should().Be("en");
        readme.Sections[0].Markdown.Trim().Should().Be("English");
        readme.Sections[1].File.Should().Be("README_CN.md");
        readme.Sections[1].Language.Should().Be("zh");
        readme.Sections[1].Markdown.Trim().Should().Be("中文说明");
        readme.FileListText.Should().Be("README.md + README_CN.md");
    }

    [Theory]
    [InlineData("README.md", "en")]
    [InlineData("README_CN.md", "zh")]
    [InlineData("readme_zh-CN.md", "zh")]
    [InlineData("README_EN.md", "en")]
    public void 应按文件名识别README语言(string file, string language)
    {
        ReadmeDocument.DetectLanguage(file).Should().Be(language);
    }

    [Fact]
    public async Task GitLab应按版本tag下载README并修正图片()
    {
        var handler = new TestHttpHandler()
            .EnqueueText("![p](images/p.png)")
            .EnqueueText(string.Empty, HttpStatusCode.NotFound);

        var source = new GitlabReleaseSource(
            new HttpClient(handler),
            new SourceConfig
            {
                Type = SourceType.GitlabRelease,
                ProjectId = "group/demo",
                BaseUrl = "https://gitlab.example.com",
            },
            NullLogger.Instance);

        var version = new VersionInfo(
            source.SourceId,
            source.DisplayName,
            "v2.0.0",
            "2.0.0",
            null,
            false,
            null,
            [new AssetSummary("app.zip", 1, null, "https://gitlab.example.com/app.zip")]);

        var readme = await source.GetReadmeAsync(version);

        readme.Should().NotBeNull();
        readme!.Sections[0].Markdown.Should().Contain(
            "https://gitlab.example.com/group/demo/-/raw/v2.0.0/images/p.png");
        handler.RequestUris[0].Should().Contain("repository/files/README.md/raw");
        handler.RequestUris[0].Should().Contain("ref=v2.0.0");
    }

    [Fact]
    public async Task README不存在时返回空()
    {
        var handler = new TestHttpHandler()
            .EnqueueText(string.Empty, HttpStatusCode.NotFound)
            .EnqueueText(string.Empty, HttpStatusCode.NotFound);

        var source = new GithubReleaseSource(
            new HttpClient(handler),
            new SourceConfig { Type = SourceType.GithubRelease, Repo = "owner/demo" },
            NullLogger.Instance);

        var readme = await source.GetReadmeAsync(CreateGithubVersion());

        readme.Should().BeNull();
    }

    [Fact]
    public void 内容哈希变化应判定为README有更新()
    {
        var paths = new AppPaths(Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new ReadmeStore(paths);

        store.TryUpdate("demo|github:owner/demo|v1", "hash-1", DateTimeOffset.Now).Should().BeFalse();
        store.TryUpdate("demo|github:owner/demo|v1", "hash-1", DateTimeOffset.Now).Should().BeFalse();
        store.TryUpdate("demo|github:owner/demo|v1", "hash-2", DateTimeOffset.Now).Should().BeTrue();

        var reloaded = new ReadmeStore(paths);
        reloaded.GetHash("demo|github:owner/demo|v1").Should().Be("hash-2");
    }

    [Fact]
    public async Task 版本检查时应把README附加到最新版本并缓存哈希()
    {
        var handler = new TestHttpHandler()
            .EnqueueJson(ReleaseJson)
            .EnqueueText("# Demo\n\n![a](img/a.png)")
            .EnqueueText(string.Empty, HttpStatusCode.NotFound);

        var paths = new AppPaths(Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new ReadmeStore(paths);
        var checker = CreateChecker(handler, paths, store);

        var target = new TargetConfig
        {
            Id = "demo",
            DisplayName = "Demo",
            Sources =
            [
                new SourceConfig { Type = SourceType.GithubRelease, Repo = "owner/demo", AssetPattern = "*.zip" },
            ],
        };

        var result = await checker.CheckAsync(target, forceRefresh: true);

        var candidate = result.Candidates.Should().ContainSingle().Subject;
        candidate.Version.Readme.Should().NotBeNull();
        candidate.Version.Readme!.Sections[0].Markdown.Should().Contain(
            "https://raw.githubusercontent.com/owner/demo/v1.2.0/img/a.png");
        store.GetHash("demo|github:owner/demo|v1.2.0").Should().Be(candidate.Version.Readme.Sha256);
    }

    private static VersionInfo CreateGithubVersion() =>
        new(
            "github:owner/demo",
            "GitHub · owner/demo",
            "v1.2.0",
            "1.2.0",
            null,
            false,
            null,
            [new AssetSummary("app.zip", 1, null, "https://example.com/app.zip")]);

    private static UpdateChecker CreateChecker(TestHttpHandler handler, AppPaths paths, ReadmeStore store)
    {
        var http = new HttpClient(handler);
        var config = new UpdaterConfig();
        var sessions = new PanSessionStore(new SecretStore(paths));
        var transfers = new TransferManager(paths, config.Transfer, NullLogger<TransferManager>.Instance);
        var openList = new OpenListRuntime(
            paths, () => config.OpenList, config.Download, NullLogger<OpenListRuntime>.Instance);
        var factory = new UpdateSourceFactory(
            http, config, paths, sessions, transfers, openList,
            new GitTokenStore(new SecretStore(paths)), NullLoggerFactory.Instance);

        var records = new VersionRecordStore(paths, NullLogger<VersionRecordStore>.Instance);
        var resolver = new VersionResolver(records, NullLogger<VersionResolver>.Instance);
        return new UpdateChecker(
            factory, resolver, new VersionCache(), config.Download,
            NullLogger<UpdateChecker>.Instance, store);
    }
}
