using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UniGetUI.Extensions.CustomChannels.Services;
using Xunit;

namespace UniGetUI.Extensions.CustomChannels.Tests;

/// <summary>CredentialChecker：GitHub/GitLab 令牌校验与未配置跳过逻辑。</summary>
public sealed class CredentialCheckerTests
{
    private static CredentialChecker CreateChecker(TestHttpHandler handler) =>
        new(new HttpClient(handler), NullLogger.Instance);

    [Fact]
    public async Task GitHub令牌有效时返回通过并带账号名()
    {
        var handler = new TestHttpHandler()
            .EnqueueJson("""{"login":"octocat","id":1}""");
        var checker = CreateChecker(handler);

        var result = await checker.CheckGithubTokenAsync("ghp_token");

        result.Ok.Should().BeTrue();
        result.Skipped.Should().BeFalse();
        result.Message.Should().Contain("octocat");
        handler.RequestUris.Should().ContainSingle(u => u.Contains("api.github.com/user"));
    }

    [Fact]
    public async Task GitHub令牌无效时返回失败()
    {
        var handler = new TestHttpHandler()
            .EnqueueJson("""{"message":"Bad credentials"}""", System.Net.HttpStatusCode.Unauthorized);
        var checker = CreateChecker(handler);

        var result = await checker.CheckGithubTokenAsync("ghp_expired");

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("401");
    }

    [Fact]
    public async Task GitHub令牌未配置时跳过()
    {
        // 不入队任何响应：若跳过逻辑失效（发起请求）会直接抛出异常
        var checker = CreateChecker(new TestHttpHandler());

        var result = await checker.CheckGithubTokenAsync(null);

        result.Skipped.Should().BeTrue();
        result.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task GitLab令牌有效时返回通过并带账号名()
    {
        var handler = new TestHttpHandler()
            .EnqueueJson("""{"username":"gitlab-user","id":2}""");
        var checker = CreateChecker(handler);

        var result = await checker.CheckGitlabTokenAsync("glpat_token");

        result.Ok.Should().BeTrue();
        result.Message.Should().Contain("gitlab-user");
        handler.RequestUris.Should().ContainSingle(u => u.Contains("/api/v4/user"));
    }

    [Fact]
    public async Task GitLab令牌未配置时跳过()
    {
        var checker = CreateChecker(new TestHttpHandler());

        var result = await checker.CheckGitlabTokenAsync(string.Empty);

        result.Skipped.Should().BeTrue();
    }
}
