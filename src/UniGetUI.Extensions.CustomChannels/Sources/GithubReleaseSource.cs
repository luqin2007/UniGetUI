using System.Net;
using System.Text.Json;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Models;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Sources;

/// <summary>GitHub Release 渠道：分页列举全部版本，含更新说明与预发布标记。</summary>
public sealed class GithubReleaseSource(
    HttpClient http,
    SourceConfig config,
    ILogger logger,
    string? fallbackToken = null) : UpdateSourceBase, IReadmeProvider
{
    private const int PerPage = 100;

    /// <summary>与版本一起下载的说明文档，按顺序合并。</summary>
    private static readonly string[] ReadmeFiles = ["README.md", "README_CN.md"];

    public override string SourceId => $"github:{Repo}";

    public override string DisplayName => config.DisplayName ?? $"GitHub · {Repo}";

    public override SourceType Type => SourceType.GithubRelease;

    /// <summary>支持直接粘贴仓库地址（https://github.com/owner/repo/ 或 repo.git）。</summary>
    private string Repo => SourceUrlHelper.NormalizeGithubRepo(config.Repo);

    private string ApiBase => string.IsNullOrWhiteSpace(config.BaseUrl)
        ? "https://api.github.com"
        : config.BaseUrl.TrimEnd('/');

    public override async Task<IReadOnlyList<VersionInfo>> ListVersionsAsync(VersionQuery query, CancellationToken ct = default)
    {
        var results = new List<VersionInfo>();

        for (var page = 1; page <= 5 && results.Count < query.MaxVersions; page++)
        {
            var url = $"{ApiBase}/repos/{Repo}/releases?per_page={PerPage}&page={page}";
            using var request = CreateRequest(url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw await GithubApiException.CreateAsync(response, Repo, url, config.Token, ct)
                    .ConfigureAwait(false);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var count = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                count++;
                var version = ParseRelease(item, query);
                if (version is not null)
                {
                    results.Add(version);
                }
            }

            if (count < PerPage)
            {
                break;
            }
        }

        logger.LogInformation("GitHub 渠道 {Repo} 返回 {Count} 个候选版本", Repo, results.Count);
        return results
            .OrderByDescending(v => v.NormalizedVersion, VersionComparer.Instance)
            .Take(query.MaxVersions)
            .ToList();
    }

    public override Task<AssetRef?> ResolveAssetAsync(VersionInfo version, string? assetName, CancellationToken ct = default)
    {
        var asset = PickAsset(version, assetName);
        if (asset is null || !Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri))
        {
            return Task.FromResult<AssetRef?>(null);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/octet-stream",
        };

        if (!string.IsNullOrWhiteSpace(EffectiveToken))
        {
            headers["Authorization"] = $"Bearer {EffectiveToken}";
        }

        return Task.FromResult<AssetRef?>(new AssetRef(asset.Name, asset.Size, asset.Sha256, uri, headers));
    }

    public override async Task<string?> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"{ApiBase}/repos/{Repo}/releases?per_page=1";
            using var request = CreateRequest(url);
            using var response = await http.SendAsync(request, HttpCompletionPolicy, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? $"可访问（限额剩余 {GetRemaining(response)}）"
                : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"不可达：{ex.Message}";
        }
    }

    /// <summary>
    /// 按版本 tag 拉取 README.md 与 README_CN.md，并把相对图片地址改写为绝对地址，
    /// 保证说明文档与所展示的版本一致。
    /// </summary>
    public async Task<ReadmeDocument?> GetReadmeAsync(VersionInfo version, CancellationToken ct = default)
    {
        var reference = version.RawTag;
        var sections = new List<(string File, string Text)>();

        foreach (var file in ReadmeFiles)
        {
            var text = await TryGetFileAsync(file, reference, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                sections.Add((file, text));
            }
        }

        if (sections.Count == 0)
        {
            logger.LogDebug("GitHub 渠道 {Repo} 在 {Ref} 上未找到 README", Repo, reference);
            return null;
        }

        return ReadmeDocument.Create(reference, sections, RawBaseUrl(reference));
    }

    private async Task<string?> TryGetFileAsync(string path, string reference, CancellationToken ct)
    {
        var url = $"{ApiBase}/repos/{Repo}/contents/{path}?ref={Uri.EscapeDataString(reference)}";
        using var request = CreateRequest(url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/vnd.github.raw");

        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogDebug(
                "GitHub README {File}@{Ref} 不可用：HTTP {Status}", path, reference, (int)response.StatusCode);
            return null;
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>README 相对图片的解析前缀（公开站与 Enterprise 使用不同的 raw 地址）。</summary>
    private string RawBaseUrl(string reference)
    {
        var escaped = Uri.EscapeDataString(reference);

        if (ApiBase.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://raw.githubusercontent.com/{Repo}/{escaped}/";
        }

        var host = ApiBase;
        foreach (var suffix in new[] { "/api/v3", "/api" })
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                host = host[..^suffix.Length];
                break;
            }
        }

        return $"{host.TrimEnd('/')}/{Repo}/raw/{escaped}/";
    }

    private const HttpCompletionOption HttpCompletionPolicy = HttpCompletionOption.ResponseHeadersRead;

    private static string GetRemaining(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var values))
        {
            return values.FirstOrDefault() ?? "未知";
        }

        return "未知";
    }

    private static AssetSummary? PickAsset(VersionInfo version, string? assetName)
    {
        if (version.Assets.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(assetName))
        {
            var exact = version.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        return version.Assets[0];
    }

    private VersionInfo? ParseRelease(JsonElement item, VersionQuery query)
    {
        if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        var isPrerelease = item.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
        if (isPrerelease && !query.IncludePrerelease)
        {
            return null;
        }

        var tag = GetString(item, "tag_name") ?? GetString(item, "name");
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var assets = new List<AssetSummary>();
        if (item.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetArray.EnumerateArray())
            {
                var name = GetString(asset, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (!WildcardMatcher.IsMatch(name, query.AssetPattern))
                {
                    continue;
                }

                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var sizeValue)
                    ? sizeValue
                    : (long?)null;

                var digest = GetString(asset, "digest");
                var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? digest["sha256:".Length..]
                    : null;

                assets.Add(new AssetSummary(
                    name,
                    size,
                    sha,
                    GetString(asset, "browser_download_url")));
            }
        }

        if (assets.Count == 0)
        {
            return null;
        }

        var publishedRaw = GetString(item, "published_at") ?? GetString(item, "created_at");
        var published = DateTimeOffset.TryParse(publishedRaw, out var dt) ? dt : (DateTimeOffset?)null;

        return new VersionInfo(
            SourceId,
            DisplayName,
            tag,
            VersionComparer.Normalize(tag),
            published,
            isPrerelease,
            GetString(item, "body"),
            assets);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>渠道令牌优先，未配置时回退到全局令牌（设置页配置，SecretStore 存储）。</summary>
    private string? EffectiveToken =>
        !string.IsNullOrWhiteSpace(config.Token) ? config.Token : fallbackToken;

    private HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        if (!string.IsNullOrWhiteSpace(EffectiveToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {EffectiveToken}");
        }

        return request;
    }

    private sealed class GithubApiException : Exception
    {
        private GithubApiException(string message, Exception? inner = null) : base(message, inner)
        {
        }

        public static async Task<GithubApiException> CreateAsync(
            HttpResponseMessage response,
            string repo,
            string requestUrl,
            string? token,
            CancellationToken ct)
        {
            var body = string.Empty;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }

            if (body.Length > 400)
            {
                body = body[..400] + "…";
            }

            var tokenState = string.IsNullOrWhiteSpace(token) ? "未携带令牌" : "已携带令牌";
            var hint = response.StatusCode switch
            {
                HttpStatusCode.NotFound =>
                    $"未找到仓库 {repo}。请确认：① 仓库标识应为 owner/repo，也可直接粘贴仓库地址（本工具会自动解析）；" +
                    "② 私有仓库必须提供 token（填写令牌或 tokenEnv 指向环境变量）；" +
                    "③ GitHub Enterprise 需把 baseUrl 设为 https://<主机>/api/v3；④ 确认账号有该仓库访问权限。",
                HttpStatusCode.Unauthorized => "令牌无效或已过期，请重新生成 token。",
                HttpStatusCode.Forbidden =>
                    "访问被拒绝，可能是令牌权限不足（需 repo 读取权限）或触发 API 限流，建议配置 token 后重试。",
                _ => "请检查网络与仓库配置。",
            };

            return new GithubApiException(
                $"GitHub 接口返回 HTTP {(int)response.StatusCode}（{repo}，{tokenState}）。{hint} " +
                $"请求地址：{requestUrl}；响应：{body}");
        }
    }
}
