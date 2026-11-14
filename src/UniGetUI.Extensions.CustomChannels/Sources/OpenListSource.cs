using System.Text.RegularExpressions;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Models;
using UniGetUI.Extensions.CustomChannels.OpenList;
using UniGetUI.Extensions.CustomChannels.Pan;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Sources;

/// <summary>
/// OpenList 渠道：通过 OpenList 的 HTTP API 浏览目录并取 raw_url 直链，
/// 由 OpenList 服务端完成网盘鉴权与 302/代理转发，本机无需安装网盘客户端。
/// </summary>
public sealed partial class OpenListSource(
    SourceConfig config,
    OpenListRuntime runtime,
    ILogger<OpenListSource> logger) : UpdateSourceBase
{
    public override string SourceId => $"openlist:{config.OpenListPath}";

    public override string DisplayName => config.DisplayName ?? "OpenList";

    public override SourceType Type => SourceType.OpenList;

    [GeneratedRegex(@"\d+(?:\.\d+){1,3}", RegexOptions.Compiled)]
    private static partial Regex FallbackVersionRegex();

    public override async Task<IReadOnlyList<VersionInfo>> ListVersionsAsync(
        VersionQuery query,
        CancellationToken ct = default)
    {
        var client = await runtime.EnsureReadyAsync(null, ct).ConfigureAwait(false);

        var (directory, pattern) = SplitPattern(config.OpenListPath ?? config.PathPattern);
        if (!string.IsNullOrWhiteSpace(config.FilePattern))
        {
            pattern = config.FilePattern!;
        }

        var entries = await client.ListAsync(directory, ct).ConfigureAwait(false);

        var matched = entries
            .Where(e => !e.IsDir && WildcardMatcher.IsMatch(e.Name, pattern))
            .OrderByDescending(e => ExtractVersion(e.Name), VersionComparer.Instance)
            .Take(query.MaxVersions)
            .ToList();

        if (matched.Count == 0)
        {
            throw new PanApiException(
                $"OpenList 目录 {directory} 中没有匹配 '{pattern}' 的文件。请检查 openListPath 与 filePattern。");
        }

        var versions = new List<VersionInfo>(matched.Count);
        foreach (var entry in matched)
        {
            var version = ExtractVersion(entry.Name);
            var metadata = new Dictionary<string, string> { ["path"] = entry.Path };

            versions.Add(new VersionInfo(
                SourceId,
                DisplayName,
                version ?? entry.Name,
                VersionComparer.Normalize(version),
                entry.Modified,
                false,
                null,
                [new AssetSummary(entry.Name, entry.Size, null, null, metadata)]));
        }

        logger.LogInformation("OpenList 渠道匹配到 {Count} 个候选版本", versions.Count);
        return versions;
    }

    public override async Task<AssetRef?> ResolveAssetAsync(
        VersionInfo version,
        string? assetName,
        CancellationToken ct = default)
    {
        var asset = version.Assets.FirstOrDefault(a =>
                        string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase))
                    ?? version.Assets[0];

        var path = asset.Metadata is not null && asset.Metadata.TryGetValue("path", out var p)
            ? p
            : $"{config.OpenListPath?.TrimEnd('/')}/{asset.Name}";

        var client = await runtime.EnsureReadyAsync(null, ct).ConfigureAwait(false);
        var file = await client.GetFileAsync(path, ct).ConfigureAwait(false);

        if (file is null || string.IsNullOrWhiteSpace(file.RawUrl))
        {
            throw new PanApiException($"OpenList 未能为 {path} 返回直链，请确认存储可用。");
        }

        if (!Uri.TryCreate(file.RawUrl, UriKind.Absolute, out var uri))
        {
            throw new PanApiException($"OpenList 返回的直链无效：{file.RawUrl}");
        }

        return new AssetRef(asset.Name, file.Size > 0 ? file.Size : asset.Size, file.Sha256 ?? asset.Sha256, uri);
    }

    public override async Task<string?> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            // 本地模式允许按需启动托管实例，因此先确保就绪再判断在线状态
            if (!runtime.IsLocalMode)
            {
                var ping = await runtime.Client.PingAsync(ct).ConfigureAwait(false);
                if (ping != "在线")
                {
                    return ping;
                }
            }

            await runtime.EnsureReadyAsync(null, ct).ConfigureAwait(false);
            return "在线";
        }
        catch (PanApiException ex)
        {
            return ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
        catch (HttpRequestException ex)
        {
            return $"不可达：{ex.Message}";
        }
    }

    private static (string Directory, string Pattern) SplitPattern(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ("/", "*");
        }

        var normalized = value.Replace('\\', '/');
        var index = normalized.LastIndexOf('/');
        if (index < 0)
        {
            return ("/", normalized);
        }

        var directory = normalized[..index];
        var pattern = normalized[(index + 1)..];
        return (string.IsNullOrEmpty(directory) ? "/" : directory, string.IsNullOrEmpty(pattern) ? "*" : pattern);
    }

    private string? ExtractVersion(string fileName)
    {
        if (!string.IsNullOrWhiteSpace(config.VersionPattern))
        {
            var match = Regex.Match(fileName, config.VersionPattern);
            if (match.Success)
            {
                return match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
            }
        }

        var fallback = FallbackVersionRegex().Match(fileName);
        return fallback.Success ? fallback.Value : null;
    }
}
