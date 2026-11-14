using System.Text.RegularExpressions;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Models;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Sources;

/// <summary>直链兜底渠道：单一资产，版本号来自配置或文件名。</summary>
public sealed partial class DirectUrlSource(HttpClient http, SourceConfig config, ILogger logger) : UpdateSourceBase
{
    public override string SourceId => $"direct:{config.Url}";

    public override string DisplayName => config.DisplayName ?? "直链";

    public override SourceType Type => SourceType.DirectUrl;

    [GeneratedRegex(@"[vV]?(\d+(?:\.\d+){1,3})", RegexOptions.Compiled)]
    private static partial Regex VersionInNameRegex();

    public override Task<IReadOnlyList<VersionInfo>> ListVersionsAsync(VersionQuery query, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"直链渠道地址无效：{config.Url}");
        }

        var fileName = GetFileName(uri);
        var version = config.Version
                      ?? TryExtractVersion(fileName)
                      ?? "latest";

        var asset = new AssetSummary(fileName, config.Size, config.Sha256, uri.ToString());
        IReadOnlyList<VersionInfo> versions =
        [
            new VersionInfo(
                SourceId,
                DisplayName,
                version,
                VersionComparer.Normalize(version),
                null,
                false,
                null,
                [asset]),
        ];

        return Task.FromResult(versions);
    }

    public override async Task<AssetRef?> ResolveAssetAsync(
        VersionInfo version,
        string? assetName,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        long? size = config.Size;
        if (size is null)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, uri);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is { } length)
                {
                    size = length;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogDebug("直链 HEAD 探测失败，将交由下载器处理：{Message}", ex.Message);
            }
        }

        return new AssetRef(GetFileName(uri), size, config.Sha256, uri);
    }

    public override async Task<string?> ProbeAsync(CancellationToken ct = default)
    {
        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var uri))
        {
            return "地址无效";
        }

        try
        {
            using var response = await http.SendAsync(
                new HttpRequestMessage(HttpMethod.Head, uri),
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? $"可访问（{response.Content.Headers.ContentLength?.ToString() ?? "未知大小"} 字节）"
                : $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"不可达：{ex.Message}";
        }
    }

    private static string GetFileName(Uri uri)
    {
        var name = Path.GetFileName(uri.AbsolutePath);
        return string.IsNullOrWhiteSpace(name) ? "package" : Uri.UnescapeDataString(name);
    }

    private static string? TryExtractVersion(string fileName)
    {
        var match = VersionInNameRegex().Match(fileName);
        return match.Success ? match.Groups[1].Value : null;
    }
}
