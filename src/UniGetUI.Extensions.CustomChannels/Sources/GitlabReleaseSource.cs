using System.Net;
using System.Text.Json;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Models;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Sources;

/// <summary>GitLab Release 渠道，支持自建实例与私有项目 token。</summary>
public sealed class GitlabReleaseSource(HttpClient http, SourceConfig config, ILogger logger) : UpdateSourceBase, IReadmeProvider
{
    private const int PerPage = 100;

    /// <summary>与版本一起下载的说明文档，按顺序合并。</summary>
    private static readonly string[] ReadmeFiles = ["README.md", "README_CN.md"];

    public override string SourceId => $"gitlab:{Project}";

    public override string DisplayName => config.DisplayName ?? $"GitLab · {Project}";

    public override SourceType Type => SourceType.GitlabRelease;

    /// <summary>支持直接粘贴项目地址；自建实例会自动推断 baseUrl。</summary>
    private string Project => SourceUrlHelper.NormalizeGitlabProject(config.ProjectId);

    private string ApiBase
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(config.BaseUrl))
            {
                return $"{config.BaseUrl.TrimEnd('/')}/api/v4";
            }

            var inferred = SourceUrlHelper.InferGitlabBaseUrl(config.ProjectId);
            return inferred is null ? "https://gitlab.com/api/v4" : $"{inferred}/api/v4";
        }
    }

    public override async Task<IReadOnlyList<VersionInfo>> ListVersionsAsync(VersionQuery query, CancellationToken ct = default)
    {
        var results = new List<VersionInfo>();
        var project = Uri.EscapeDataString(Project);

        for (var page = 1; page <= 5 && results.Count < query.MaxVersions; page++)
        {
            var url = $"{ApiBase}/projects/{project}/releases?per_page={PerPage}&page={page}";
            using var request = CreateRequest(url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(response, ct).ConfigureAwait(false);
                var hint = response.StatusCode switch
                {
                    HttpStatusCode.NotFound => "项目不存在或无权访问（私有项目需要 token）。",
                    HttpStatusCode.Unauthorized => "认证失败，请检查 token。",
                    _ => "请检查实例地址与项目 ID。",
                };
                throw new InvalidOperationException(
                    $"GitLab 接口返回 HTTP {(int)response.StatusCode}。{hint} 响应：{body}");
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

        logger.LogInformation("GitLab 渠道 {Project} 返回 {Count} 个候选版本", Project, results.Count);
        return results
            .OrderByDescending(v => v.NormalizedVersion, VersionComparer.Instance)
            .Take(query.MaxVersions)
            .ToList();
    }

    public override Task<AssetRef?> ResolveAssetAsync(VersionInfo version, string? assetName, CancellationToken ct = default)
    {
        var asset = version.Assets.FirstOrDefault(a =>
                       !string.IsNullOrWhiteSpace(assetName) &&
                       string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase))
                   ?? version.Assets.FirstOrDefault(a =>
                       !string.IsNullOrWhiteSpace(assetName) && WildcardMatcher.IsMatch(a.Name, assetName))
                   ?? version.Assets.FirstOrDefault();

        if (asset is null || !Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri))
        {
            return Task.FromResult<AssetRef?>(null);
        }

        Dictionary<string, string>? headers = null;
        if (!string.IsNullOrWhiteSpace(config.Token))
        {
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PRIVATE-TOKEN"] = config.Token,
            };
        }

        return Task.FromResult<AssetRef?>(new AssetRef(asset.Name, asset.Size, asset.Sha256, uri, headers));
    }

    public override async Task<string?> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            var project = Uri.EscapeDataString(Project);
            var url = $"{ApiBase}/projects/{project}";
            using var request = CreateRequest(url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? "可访问"
                : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"不可达：{ex.Message}";
        }
    }

    /// <summary>
    /// 按版本 tag 拉取 README.md 与 README_CN.md，并把相对图片地址改写为绝对地址。
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
            logger.LogDebug("GitLab 渠道 {Project} 在 {Ref} 上未找到 README", Project, reference);
            return null;
        }

        return ReadmeDocument.Create(reference, sections, RawBaseUrl(reference));
    }

    private async Task<string?> TryGetFileAsync(string path, string reference, CancellationToken ct)
    {
        var project = Uri.EscapeDataString(Project);
        var file = Uri.EscapeDataString(path);
        var url = $"{ApiBase}/projects/{project}/repository/files/{file}/raw?ref={Uri.EscapeDataString(reference)}";

        using var request = CreateRequest(url);
        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogDebug(
                "GitLab README {File}@{Ref} 不可用：HTTP {Status}", path, reference, (int)response.StatusCode);
            return null;
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>README 相对图片的解析前缀：{host}/{project}/-/raw/{ref}/。</summary>
    private string RawBaseUrl(string reference)
    {
        var host = config.BaseUrl;
        if (string.IsNullOrWhiteSpace(host))
        {
            host = SourceUrlHelper.InferGitlabBaseUrl(config.ProjectId) ?? "https://gitlab.com";
        }
        else
        {
            host = host.TrimEnd('/');
            if (host.EndsWith("/api/v4", StringComparison.OrdinalIgnoreCase))
            {
                host = host[..^"/api/v4".Length];
            }
        }

        return $"{host.TrimEnd('/')}/{Project}/-/raw/{Uri.EscapeDataString(reference)}/";
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Length > 400 ? body[..400] + "…" : body;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return string.Empty;
        }
    }

    private VersionInfo? ParseRelease(JsonElement item, VersionQuery query)
    {
        var tag = GetString(item, "tag_name") ?? GetString(item, "name");
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var isPrerelease = item.TryGetProperty("upcoming_release", out var upcoming) &&
                           upcoming.ValueKind == JsonValueKind.True;

        var assets = new List<AssetSummary>();

        if (item.TryGetProperty("assets", out var assetsElement) &&
            assetsElement.TryGetProperty("links", out var links) &&
            links.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in links.EnumerateArray())
            {
                var name = GetString(link, "name");
                var url = GetString(link, "direct_asset_url") ?? GetString(link, "url");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                if (!WildcardMatcher.IsMatch(name, query.AssetPattern))
                {
                    continue;
                }

                assets.Add(new AssetSummary(name, null, null, url));
            }
        }

        // 源码包（zip/tar.gz）作为兜底资产
        if (assets.Count == 0 &&
            item.TryGetProperty("sources", out var sources) &&
            sources.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in sources.EnumerateArray())
            {
                var format = GetString(source, "format");
                var url = GetString(source, "url");
                if (string.IsNullOrWhiteSpace(format) || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var name = $"{tag}.{format}";
                if (!WildcardMatcher.IsMatch(name, query.AssetPattern))
                {
                    continue;
                }

                assets.Add(new AssetSummary(name, null, null, url));
            }
        }

        if (assets.Count == 0)
        {
            return null;
        }

        var publishedRaw = GetString(item, "released_at") ?? GetString(item, "created_at");
        var published = DateTimeOffset.TryParse(publishedRaw, out var dt) ? dt : (DateTimeOffset?)null;

        return new VersionInfo(
            SourceId,
            DisplayName,
            tag,
            VersionComparer.Normalize(tag),
            published,
            isPrerelease,
            GetString(item, "description"),
            assets);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(config.Token))
        {
            request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", config.Token);
        }

        return request;
    }
}
