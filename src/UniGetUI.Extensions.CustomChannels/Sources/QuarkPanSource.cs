using System.Text.RegularExpressions;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Models;
using UniGetUI.Extensions.CustomChannels.Pan;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Sources;

/// <summary>
/// 夸克网盘分享渠道。夸克要求分享文件先转存到当前账号才能取直链，
/// 转存后可依据配置在下载完成或安装成功后自动删除。
/// </summary>
public sealed partial class QuarkPanSource(
    SourceConfig config,
    QuarkPanApi api,
    PanSessionStore sessions,
    TransferManager transfers,
    TransferConfig transferConfig,
    ILogger<QuarkPanSource> logger) : UpdateSourceBase
{
    private string? _pwdId;
    private string? _pwd;

    public override string SourceId => $"quark:{config.ShareUrl}";

    public override string DisplayName => config.DisplayName ?? "夸克网盘";

    public override SourceType Type => SourceType.QuarkPan;

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
            throw new PanApiException("夸克网盘渠道缺少 shareUrl。");
        }

        var (pwdId, urlPwd) = QuarkPanApi.ParseShareUrl(config.ShareUrl);
        _pwdId = pwdId;
        _pwd = string.IsNullOrWhiteSpace(config.Pwd) ? urlPwd : config.Pwd;

        var stoken = await api.GetStokenAsync(pwdId, _pwd, ct).ConfigureAwait(false);
        var entries = await api.ListShareAsync(pwdId, stoken, ct).ConfigureAwait(false);

        var pattern = ExtractFileNamePattern(config.PathPattern);
        var matched = entries
            .Where(e => !e.IsDir && WildcardMatcher.IsMatch(e.Name, pattern))
            .OrderByDescending(e => ExtractVersion(e.Name), VersionComparer.Instance)
            .Take(query.MaxVersions)
            .ToList();

        if (matched.Count == 0)
        {
            throw new PanApiException(
                $"夸克网盘分享中没有匹配 '{pattern}' 的文件。请检查 pathPattern（夸克只支持分享根目录）。");
        }

        var versions = new List<VersionInfo>(matched.Count);
        foreach (var entry in matched)
        {
            var version = ExtractVersion(entry.Name);
            var metadata = new Dictionary<string, string>
            {
                ["fid"] = entry.Fid,
                ["token"] = entry.Token,
            };

            versions.Add(new VersionInfo(
                SourceId,
                DisplayName,
                version ?? entry.Name,
                VersionComparer.Normalize(version),
                null,
                false,
                null,
                [new AssetSummary(entry.Name, entry.Size, null, null, metadata)]));
        }

        logger.LogInformation("夸克网盘渠道匹配到 {Count} 个候选版本", versions.Count);
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

        if (!Uri.TryCreate(config.ShareUrl, UriKind.Absolute, out _))
        {
            throw new PanApiException("夸克网盘分享链接无效。");
        }

        if (_pwdId is null)
        {
            var (pwdId, urlPwd) = QuarkPanApi.ParseShareUrl(config.ShareUrl!);
            _pwdId = pwdId;
            _pwd = string.IsNullOrWhiteSpace(config.Pwd) ? urlPwd : config.Pwd;
        }

        var fid = asset.Metadata is not null && asset.Metadata.TryGetValue("fid", out var f) ? f : string.Empty;
        var token = asset.Metadata is not null && asset.Metadata.TryGetValue("token", out var t) ? t : string.Empty;
        if (string.IsNullOrEmpty(fid))
        {
            throw new PanApiException("夸克网盘资产缺少 fid 元数据。");
        }

        var reuse = transfers.FindReusable(PanPlatform.Quark, asset.Name, asset.Size ?? 0);
        string ownFid;

        if (reuse is null)
        {
            var result = await api.TransferAsync(
                new TransferRequest(
                    PanPlatform.Quark,
                    _pwdId!,
                    config.ShareUrl!,
                    _pwd,
                    fid,
                    token,
                    asset.Name,
                    asset.Size ?? 0,
                    TransferPath),
                ct).ConfigureAwait(false);

            if (!result.Success || string.IsNullOrEmpty(result.FileId))
            {
                throw new PanApiException(result.Message ?? "夸克网盘转存失败，未取得账号内文件 ID。");
            }

            ownFid = result.FileId;
            transfers.AddOrUpdate(new TransferRecord
            {
                Platform = PanPlatform.Quark,
                ShareId = _pwdId!,
                FileId = ownFid,
                Path = TransferPath,
                FileName = asset.Name,
                Size = asset.Size ?? 0,
                State = TransferState.Active,
            });
        }
        else
        {
            ownFid = reuse.FileId;
            transfers.AddOrUpdate(new TransferRecord
            {
                Platform = PanPlatform.Quark,
                ShareId = reuse.ShareId,
                FileId = reuse.FileId,
                Path = reuse.Path,
                FileName = reuse.FileName,
                Size = reuse.Size,
                State = TransferState.Reused,
            });
        }

        var url = await api.GetDownloadUrlAsync(ownFid, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new PanApiException("未能取得夸克网盘直链，请稍后重试。");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = PanApiEndpoints.Quark.Origin + "/",
            ["User-Agent"] = PanApiEndpoints.BrowserUserAgent,
        };

        var cookie = sessions.GetCookie(PanPlatform.Quark);
        if (!string.IsNullOrEmpty(cookie))
        {
            headers["Cookie"] = cookie;
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
            var (pwdId, urlPwd) = QuarkPanApi.ParseShareUrl(config.ShareUrl);
            var stoken = await api.GetStokenAsync(pwdId, string.IsNullOrWhiteSpace(config.Pwd) ? urlPwd : config.Pwd, ct)
                .ConfigureAwait(false);
            return string.IsNullOrEmpty(stoken) ? "无法获取分享 token" : "分享有效";
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

    private static string ExtractFileNamePattern(string? pathPattern)
    {
        if (string.IsNullOrWhiteSpace(pathPattern))
        {
            return "*";
        }

        var normalized = pathPattern.Replace('\\', '/');
        var index = normalized.LastIndexOf('/');
        return index < 0 ? normalized : normalized[(index + 1)..] is { Length: > 0 } name ? name : "*";
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
