using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Pan;
using UniGetUI.Extensions.CustomChannels.Services;
using UniGetUI.Extensions.CustomChannels.Sources;
using Xunit;

namespace UniGetUI.Extensions.CustomChannels.Tests;

/// <summary>全局令牌回退链：渠道令牌优先，未配置时使用全局令牌（GitHub/GitLab）。</summary>
public sealed class GitTokenFallbackTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(
        Path.GetTempPath(), "cc-token-test-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    private GitTokenStore CreateStore()
    {
        _paths.EnsureCreated();
        return new GitTokenStore(new SecretStore(_paths));
    }

    [Fact]
    public async Task 渠道未配置令牌时使用全局令牌()
    {
        var handler = new TestHttpHandler().EnqueueJson("""[]""");
        var source = new GithubReleaseSource(
            new HttpClient(handler),
            new SourceConfig { Type = SourceType.GithubRelease, Repo = "a/b", Enabled = true },
            NullLogger.Instance,
            fallbackToken: "gh-global");

        await source.ListVersionsAsync(new VersionQuery(MaxVersions: 1));

        handler.AuthorizationHeaders.Should().Contain("Bearer gh-global");
    }

    [Fact]
    public async Task 渠道令牌优先于全局令牌()
    {
        var handler = new TestHttpHandler().EnqueueJson("""[]""");
        var source = new GithubReleaseSource(
            new HttpClient(handler),
            new SourceConfig { Type = SourceType.GithubRelease, Repo = "a/b", Enabled = true, Token = "gh-per-source" },
            NullLogger.Instance,
            fallbackToken: "gh-global");

        await source.ListVersionsAsync(new VersionQuery(MaxVersions: 1));

        handler.AuthorizationHeaders.Should().Contain("Bearer gh-per-source");
        handler.AuthorizationHeaders.Should().NotContain("Bearer gh-global");
    }

    [Fact]
    public async Task GitLab渠道未配置令牌时使用全局令牌()
    {
        var handler = new TestHttpHandler().EnqueueJson(
            """{"description":"p","tag_list":[],"releases":[]}""");
        var source = new GitlabReleaseSource(
            new HttpClient(handler),
            new SourceConfig { Type = SourceType.GitlabRelease, ProjectId = "a/b", Enabled = true },
            NullLogger.Instance,
            fallbackToken: "gl-global");

        await source.ListVersionsAsync(new VersionQuery(MaxVersions: 1));

        handler.PrivateTokenHeaders.Should().Contain("gl-global");
    }

    [Fact]
    public void GitTokenStore往返与清除()
    {
        var store = CreateStore();

        store.GetGithubToken().Should().BeNull();

        store.SetGithubToken("  ghp_value  ");
        store.GetGithubToken().Should().Be("ghp_value");

        store.SetGithubToken("   ");
        store.GetGithubToken().Should().BeNull();

        store.SetGitlabToken("gl-value");
        store.GetGitlabToken().Should().Be("gl-value");
        store.SetGitlabToken(null);
        store.GetGitlabToken().Should().BeNull();
    }

    [Fact]
    public async Task 夸克会话校验通过与失效()
    {
        var handler = new TestHttpHandler().EnqueueJson("""{"status":200,"code":0,"data":{}}""");
        var api = CreateQuarkApi(handler);
        await api.ValidateSessionAsync();

        handler.RequestUris.Should().Contain(u => u.Contains("/behavior/recent/list?"));

        var failing = CreateQuarkApi(new TestHttpHandler().EnqueueJson(
            """{"status":401,"code":32003,"message":"token expired"}"""));
        await Assert.ThrowsAsync<PanApiException>(() => failing.ValidateSessionAsync());
    }

    private QuarkPanApi CreateQuarkApi(TestHttpHandler handler)
    {
        _paths.EnsureCreated();
        var sessions = new PanSessionStore(new SecretStore(_paths));
        sessions.SaveCookie(PanPlatform.Quark, "k=v; foo=bar");
        return new QuarkPanApi(new HttpClient(handler), sessions, NullLogger<QuarkPanApi>.Instance);
    }
}
