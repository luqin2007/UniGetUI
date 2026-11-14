using System.Net;
using System.Text;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Sources;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace UniGetUI.Extensions.CustomChannels.Tests;

public sealed class GithubSourceTests
{
    private const string TwoReleaseJson = """
    [
      {
        "tag_name": "v1.2.0",
        "name": "1.2.0",
        "prerelease": false,
        "published_at": "2026-01-02T03:04:05Z",
        "body": "## 修复\n- 修复启动崩溃",
        "assets": [
          {
            "name": "app-1.2.0.zip",
            "size": 2048,
            "digest": "sha256:abc123",
            "browser_download_url": "https://example.com/app-1.2.0.zip"
          },
          {
            "name": "app-1.2.0.txt",
            "size": 10,
            "browser_download_url": "https://example.com/app-1.2.0.txt"
          }
        ]
      },
      {
        "tag_name": "v1.3.0-beta.1",
        "prerelease": true,
        "published_at": "2026-02-01T00:00:00Z",
        "body": "预览版",
        "assets": [
          {
            "name": "app-1.3.0-beta.1.zip",
            "size": 4096,
            "browser_download_url": "https://example.com/beta.zip"
          }
        ]
      }
    ]
    """;

    [Fact]
    public async Task 应解析版本号_说明_时间与资产哈希()
    {
        var handler = new TestHttpHandler().EnqueueJson(TwoReleaseJson);
        var source = CreateSource(handler);

        var versions = await source.ListVersionsAsync(new VersionQuery("*.zip", IncludePrerelease: false, MaxVersions: 10));

        versions.Should().ContainSingle();
        var version = versions[0];
        version.RawTag.Should().Be("v1.2.0");
        version.NormalizedVersion.Should().Be("1.2.0");
        version.IsPrerelease.Should().BeFalse();
        version.ReleaseNotes.Should().Contain("修复启动崩溃");
        version.PublishedAt.Should().NotBeNull();
        version.Assets.Should().ContainSingle();
        version.Assets[0].Name.Should().Be("app-1.2.0.zip");
        version.Assets[0].Size.Should().Be(2048);
        version.Assets[0].Sha256.Should().Be("abc123");
    }

    [Fact]
    public async Task 开启预发布时应同时返回预发布版本并按版本降序()
    {
        var handler = new TestHttpHandler().EnqueueJson(TwoReleaseJson);
        var source = CreateSource(handler);

        var versions = await source.ListVersionsAsync(new VersionQuery("*.zip", IncludePrerelease: true, MaxVersions: 10));

        versions.Should().HaveCount(2);
        versions[0].DisplayVersion.Should().Be("1.3.0-beta.1");
        versions[1].DisplayVersion.Should().Be("1.2.0");
        versions[0].IsPrerelease.Should().BeTrue();
    }

    [Fact]
    public async Task 未匹配资产通配的版本应被忽略()
    {
        var handler = new TestHttpHandler().EnqueueJson(TwoReleaseJson);
        var source = CreateSource(handler);

        var versions = await source.ListVersionsAsync(
            new VersionQuery("*.msi", IncludePrerelease: true, MaxVersions: 10));

        versions.Should().BeEmpty();
    }

    [Fact]
    public async Task 草稿版本应被忽略()
    {
        const string json = """
        [
          {
            "tag_name": "v9.0.0",
            "draft": true,
            "prerelease": false,
            "assets": [
              { "name": "app.zip", "browser_download_url": "https://example.com/app.zip" }
            ]
          }
        ]
        """;

        var handler = new TestHttpHandler().EnqueueJson(json);
        var source = CreateSource(handler);

        var versions = await source.ListVersionsAsync(new VersionQuery());

        versions.Should().BeEmpty();
    }

    [Fact]
    public async Task 第一页满额时应继续请求下一页()
    {
        var handler = new TestHttpHandler()
            .EnqueueJson(BuildReleasesJson(100))
            .EnqueueJson("[]");
        var source = CreateSource(handler);

        var versions = await source.ListVersionsAsync(new VersionQuery(MaxVersions: 200));

        handler.RequestUris.Should().HaveCount(2);
        handler.RequestUris[0].Should().Contain("per_page=100").And.Contain("page=1");
        handler.RequestUris[1].Should().Contain("page=2");
        versions.Should().HaveCount(100);
    }

    [Fact]
    public async Task 接口返回404时应抛出含排查提示与请求地址的异常()
    {
        var handler = new TestHttpHandler().EnqueueJson(
            """{"message":"Not Found"}""",
            HttpStatusCode.NotFound);
        var source = CreateSource(handler);

        var act = async () => await source.ListVersionsAsync(new VersionQuery());

        await act.Should().ThrowAsync<Exception>()
            .WithMessage("*404*未携带令牌*请求地址*owner/demo*");
    }

    [Fact]
    public async Task 仓库地址粘贴为浏览器链接时应归一化后请求()
    {
        var handler = new TestHttpHandler().EnqueueJson("[]");
        var source = new GithubReleaseSource(
            new HttpClient(handler),
            new SourceConfig
            {
                Type = SourceType.GithubRelease,
                Repo = "https://github.com/Justsenger/ExHyperV/",
            },
            NullLogger.Instance);

        await source.ListVersionsAsync(new VersionQuery());

        handler.RequestUris.Should().ContainSingle()
            .Which.Should().Be("https://api.github.com/repos/Justsenger/ExHyperV/releases?per_page=100&page=1");
        source.SourceId.Should().Be("github:Justsenger/ExHyperV");
    }

    [Fact]
    public async Task 配置token时应附加授权头()
    {
        var handler = new TestHttpHandler().EnqueueJson("[]");
        var source = CreateSource(handler, token: "gh-token");

        await source.ListVersionsAsync(new VersionQuery());

        handler.AuthorizationHeaders.Should().ContainSingle().Which.Should().Be("Bearer gh-token");
    }

    [Fact]
    public async Task 解析资产时应按名称精确匹配并携带下载请求头()
    {
        var handler = new TestHttpHandler().EnqueueJson(TwoReleaseJson);
        var source = CreateSource(handler);
        var versions = await source.ListVersionsAsync(new VersionQuery("*.txt", IncludePrerelease: false));

        var asset = await source.ResolveAssetAsync(versions[0], "app-1.2.0.txt");

        asset.Should().NotBeNull();
        asset!.Name.Should().Be("app-1.2.0.txt");
        asset.Size.Should().Be(10);
        asset.DirectUri!.Host.Should().Be("example.com");
        asset.Headers.Should().ContainKey("Accept");
    }

    private static GithubReleaseSource CreateSource(TestHttpHandler handler, string? token = null) =>
        new(
            new HttpClient(handler),
            new SourceConfig
            {
                Type = SourceType.GithubRelease,
                Repo = "owner/demo",
                Token = token,
            },
            NullLogger.Instance);

    private static string BuildReleasesJson(int count)
    {
        var builder = new StringBuilder("[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append($$"""
            {
              "tag_name": "v1.{{i}}.0",
              "prerelease": false,
              "assets": [
                {
                  "name": "app-1.{{i}}.0.zip",
                  "size": {{i + 1}},
                  "browser_download_url": "https://example.com/app-1.{{i}}.0.zip"
                }
              ]
            }
            """);
        }

        return builder.Append(']').ToString();
    }
}
