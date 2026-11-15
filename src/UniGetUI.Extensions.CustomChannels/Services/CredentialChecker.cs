using System.Text.Json;
using UniGetUI.Extensions.CustomChannels.Pan;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Services;

/// <summary>单个凭据的校验结果；<see cref="Skipped"/> 为 true 表示未配置、无需校验。</summary>
public sealed record CredentialCheckResult(string Name, bool Ok, bool Skipped, string Message)
{
    public static CredentialCheckResult Skip(string name, string message) => new(name, Ok: true, Skipped: true, message);

    public static CredentialCheckResult Pass(string name, string message) => new(name, Ok: true, Skipped: false, message);

    public static CredentialCheckResult Fail(string name, string message) => new(name, Ok: false, Skipped: false, message);
}

/// <summary>
/// 凭据校验器：GitHub/GitLab 令牌与网盘登录状态的可用性检测。
/// 网盘部分委托给各 <c>*PanApi</c>（复用既有请求管道与错误提示），令牌部分直接探测官方"当前用户"接口。
/// </summary>
public sealed class CredentialChecker(HttpClient http, ILogger logger)
{
    public async Task<CredentialCheckResult> CheckGithubTokenAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return CredentialCheckResult.Skip("GitHub 令牌", "未配置，跳过。");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token.Trim()}");
            request.Headers.TryAddWithoutValidation("User-Agent", "UniGetUI");

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.OK => CredentialCheckResult.Pass(
                    "GitHub 令牌", await DescribeGithubUserAsync(response, ct).ConfigureAwait(false)),
                System.Net.HttpStatusCode.Unauthorized =>
                    CredentialCheckResult.Fail("GitHub 令牌", "令牌无效或已过期（401），请重新生成。"),
                System.Net.HttpStatusCode.Forbidden =>
                    CredentialCheckResult.Fail("GitHub 令牌", "令牌被拒绝（403）：可能触发了次级限流或未授权该资源。"),
                _ => CredentialCheckResult.Fail(
                    "GitHub 令牌", $"校验失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}"),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "GitHub 令牌校验请求失败");
            return CredentialCheckResult.Fail("GitHub 令牌", $"网络错误：{ex.Message}");
        }
    }

    public async Task<CredentialCheckResult> CheckGitlabTokenAsync(
        string? token,
        string? baseUrl = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return CredentialCheckResult.Skip("GitLab 令牌", "未配置，跳过。");
        }

        var host = (string.IsNullOrWhiteSpace(baseUrl)
            ? "https://gitlab.com"
            : baseUrl.TrimEnd('/')).TrimEnd('/');

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{host}/api/v4/user");
            request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token.Trim());

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.OK => CredentialCheckResult.Pass(
                    "GitLab 令牌", await DescribeGitlabUserAsync(response, ct).ConfigureAwait(false)),
                System.Net.HttpStatusCode.Unauthorized =>
                    CredentialCheckResult.Fail("GitLab 令牌", "令牌无效或已过期（401），请重新生成。"),
                System.Net.HttpStatusCode.Forbidden =>
                    CredentialCheckResult.Fail("GitLab 令牌", "令牌被拒绝（403）：可能缺少 api 权限或被限流。"),
                _ => CredentialCheckResult.Fail(
                    "GitLab 令牌", $"校验失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}"),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "GitLab 令牌校验请求失败");
            return CredentialCheckResult.Fail("GitLab 令牌", $"网络错误：{ex.Message}");
        }
    }

    private static async Task<string> DescribeGithubUserAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var login = document.RootElement.TryGetProperty("login", out var loginNode)
                ? loginNode.GetString()
                : null;
            return login is { Length: > 0 } name
                ? $"令牌有效（账号 {name}）"
                : "令牌有效";
        }
        catch (JsonException)
        {
            return "令牌有效";
        }
    }

    private static async Task<string> DescribeGitlabUserAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var username = document.RootElement.TryGetProperty("username", out var usernameNode)
                ? usernameNode.GetString()
                : null;
            return username is { Length: > 0 } name
                ? $"令牌有效（账号 {name}）"
                : "令牌有效";
        }
        catch (JsonException)
        {
            return "令牌有效";
        }
    }
}
