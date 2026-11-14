using System.Text.RegularExpressions;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Models;
using UniGetUI.Extensions.CustomChannels.Pan;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Sources;

/// <summary>
/// 百度网盘分享渠道：校验分享 → 列举文件 → 转存到当前账号 → 取直链。
/// 取到的是普通 HTTP 直链，无需安装百度网盘客户端。
/// </summary>
public sealed partial class BaiduPanSource(
    SourceConfig config,
    BaiduPanApi api,
    PanSessionStore sessions,
    TransferManager transfers,
    TransferConfig transferConfig,
    ILogger<BaiduPanSource> logger) : UpdateSourceBase
{
    private BaiduShareSession? _session;

    public override string SourceId => $"baidu:{config.ShareUrl}";

    public override string DisplayName => config.DisplayName ?? "百度网盘";

    public override SourceType Type => SourceType.BaiduPan;

    private string TransferPath => string.IsNullOrWhiteSpace(config.TransferPath)
        ? transferConfig.TransferPath
        : config.TransferPath!;

    [GeneratedRegex(@"\d+(?:\.\d+){1,3}", RegexOptions.Compiled)]
    private static partial Regex FallbackVersionRegex();

    public override async Task<IReadOnlyList<VersionInfo>> ListVersionsAsync(
        VersionQuery query,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.ShareUrl))
        {
            throw new PanApiException("百度网盘渠道缺少 shareUrl。");
        }

        _session = await api.VerifyShareAsync(config.ShareUrl, config.Pwd, ct).ConfigureAwait(false);

        var (pattern, directory) = SplitPattern(config.PathPattern);
        var files = await CollectFilesAsync(_session, directory, depth: 0, ct).ConfigureAwait(false);

        var matched = files
            .Where(f => WildcardMatcher.IsMatch(f.Name, pattern))
            .OrderByDescending(f => ExtractVersion(f.Name), VersionComparer.Instance)
            .Take(query.MaxVersions)
            .ToList();

        if (matched.Count == 0)
        {
            throw new PanApiException(
                $"百度网盘分享中没有匹配 '{pattern}' 的文件（目录：{directory}）。请检查 pathPattern。");
        }

        var versions = new List<VersionInfo>(matched.Count);
        foreach (var file in matched)
        {
            var version = ExtractVersion(file.Name);
            var metadata = new Dictionary<string, string>
            {
                ["fsId"] = file.FsId,
                ["path"] = file.Path,
            };

            versions.Add(new VersionInfo(
                SourceId,
                DisplayName,
                version ?? file.Name,
                VersionComparer.Normalize(version),
                null,
                false,
                null,
                [new AssetSummary(file.Name, file.Size, null, null, metadata)]));
        }

        logger.LogInformation("百度网盘渠道匹配到 {Count} 个候选版本", versions.Count);
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

        if (asset.Metadata is null || !asset.Metadata.TryGetValue("fsId", out var fsId))
        {
            throw new PanApiException("百度网盘资产缺少 fsId 元数据。");
        }

        _session ??= await api.VerifyShareAsync(config.ShareUrl!, config.Pwd, ct).ConfigureAwait(false);

        var reuse = transfers.FindReusable(PanPlatform.Baidu, asset.Name, asset.Size ?? 0);
        if (reuse is null)
        {
            var result = await api.TransferAsync(
                new TransferRequest(
                    PanPlatform.Baidu,
                    _session.ShareId,
                    config.ShareUrl!,
                    config.Pwd,
                    fsId,
                    null,
                    asset.Name,
                    asset.Size ?? 0,
                    TransferPath),
                ct).ConfigureAwait(false);

            if (!result.Success)
            {
                throw new PanApiException(result.Message ?? "百度网盘转存失败。");
            }

            transfers.AddOrUpdate(new TransferRecord
            {
                Platform = PanPlatform.Baidu,
                ShareId = _session.ShareId,
                FileId = result.FileId ?? string.Empty,
                Path = $"{TransferPath.TrimEnd('/')}/{asset.Name}",
                FileName = asset.Name,
                Size = asset.Size ?? 0,
                State = TransferState.Active,
            });
        }
        else
        {
            transfers.AddOrUpdate(new TransferRecord
            {
                Platform = PanPlatform.Baidu,
                ShareId = reuse.ShareId,
                FileId = reuse.FileId,
                Path = reuse.Path,
                FileName = reuse.FileName,
                Size = reuse.Size,
                State = TransferState.Reused,
            });
        }

        var url = await api.GetDownloadUrlAsync(TransferPath, asset.Name, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new PanApiException(
                "未能取得百度网盘直链。可能是限速/风控导致，请稍后重试或改用 OpenList 渠道。");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = PanApiEndpoints.Baidu.Referer,
            ["User-Agent"] = PanApiEndpoints.BrowserUserAgent,
        };

        if (url.Contains("pan.baidu.com", StringComparison.OrdinalIgnoreCase))
        {
            var sessionCookie = sessions.GetCookie(PanPlatform.Baidu);
            if (!string.IsNullOrEmpty(sessionCookie))
            {
                headers["Cookie"] = sessionCookie;
            }
        }

        return new AssetRef(asset.Name, asset.Size, asset.Sha256, new Uri(url), headers);
    }

    public override async Task<string?> ProbeAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.ShareUrl))
        {
            return "未配置分享链接";
        }

        try
        {
            _session = await api.VerifyShareAsync(config.ShareUrl, config.Pwd, ct).ConfigureAwait(false);
            return "分享有效";
        }
        catch (PanApiException ex)
        {
            return ex.Message;
        }
        catch (HttpRequestException ex)
        {
            return $"不可达：{ex.Message}";
        }
    }

    public override Task CleanupAsync(bool installSucceeded, CancellationToken ct = default) =>
        transfers.CleanupAsync(api, transfers.ResolvePolicy(config), installSucceeded, ct);

    private async Task<List<BaiduShareEntry>> CollectFilesAsync(
        BaiduShareSession session,
        string directory,
        int depth,
        CancellationToken ct)
    {
        var result = new List<BaiduShareEntry>();
        var entries = await api.ListShareAsync(session, directory, ct).ConfigureAwait(false);

        foreach (var entry in entries)
        {
            if (!entry.IsDir)
            {
                result.Add(entry);
                continue;
            }

            if (depth >= 2)
            {
                continue;
            }

            var childDir = entry.Path.StartsWith('/') ? entry.Path : "/" + entry.Path;
            try
            {
                result.AddRange(await CollectFilesAsync(session, childDir, depth + 1, ct).ConfigureAwait(false));
            }
            catch (PanApiException ex)
            {
                logger.LogDebug("跳过子目录 {Dir}：{Message}", childDir, ex.Message);
            }
        }

        return result;
    }

    private static (string Pattern, string Directory) SplitPattern(string? pathPattern)
    {
        if (string.IsNullOrWhiteSpace(pathPattern))
        {
            return ("*", "/");
        }

        var normalized = pathPattern.Replace('\\', '/');
        var index = normalized.LastIndexOf('/');
        if (index < 0)
        {
            return (normalized, "/");
        }

        var dir = normalized[..index];
        var pattern = normalized[(index + 1)..];
        return (string.IsNullOrEmpty(pattern) ? "*" : pattern, string.IsNullOrEmpty(dir) ? "/" : dir);
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
