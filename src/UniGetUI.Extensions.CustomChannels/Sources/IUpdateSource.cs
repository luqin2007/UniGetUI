using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Download;
using UniGetUI.Extensions.CustomChannels.Models;

namespace UniGetUI.Extensions.CustomChannels.Sources;

/// <summary>渠道查询参数。</summary>
public sealed record VersionQuery(
    string? AssetPattern = null,
    bool IncludePrerelease = false,
    int MaxVersions = 60);

/// <summary>
/// 更新渠道契约：列举候选版本、把选中的版本解析为可下载资产、并在链接过期时重新取链。
/// </summary>
public interface IUpdateSource
{
    string SourceId { get; }

    string DisplayName { get; }

    SourceType Type { get; }

    Task<IReadOnlyList<VersionInfo>> ListVersionsAsync(VersionQuery query, CancellationToken ct = default);

    Task<IReadOnlyList<AssetRef>> ListAssetsAsync(VersionInfo version, CancellationToken ct = default);

    /// <summary>取链（网盘为短时效直链，每次调用都应返回新鲜地址）。</summary>
    Task<AssetRef?> ResolveAssetAsync(VersionInfo version, string? assetName, CancellationToken ct = default);

    /// <summary>渠道可用性探测，用于界面展示登录态/连通性。</summary>
    Task<string?> ProbeAsync(CancellationToken ct = default);

    /// <summary>更新流程结束后的清理钩子（例如按策略删除转存文件）。</summary>
    Task CleanupAsync(bool installSucceeded, CancellationToken ct = default);
}

/// <summary>
/// 可选能力：按版本 tag 拉取仓库 README，作为与版本匹配的说明文档。
/// 仅在「获取版本」时调用，不会在界面操作中额外发起请求。
/// </summary>
public interface IReadmeProvider
{
    Task<ReadmeDocument?> GetReadmeAsync(VersionInfo version, CancellationToken ct = default);
}

public abstract class UpdateSourceBase : IUpdateSource
{
    public abstract string SourceId { get; }

    public abstract string DisplayName { get; }

    public abstract SourceType Type { get; }

    public abstract Task<IReadOnlyList<VersionInfo>> ListVersionsAsync(VersionQuery query, CancellationToken ct = default);

    public virtual Task<IReadOnlyList<AssetRef>> ListAssetsAsync(VersionInfo version, CancellationToken ct = default)
    {
        if (version.Assets.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<AssetRef>>([]);
        }

        var assets = version.Assets
            .Select(a => new AssetRef(a.Name, a.Size, a.Sha256, ToUri(a.DownloadUrl), Metadata: a.Metadata))
            .ToList();
        return Task.FromResult<IReadOnlyList<AssetRef>>(assets);
    }

    public virtual async Task<AssetRef?> ResolveAssetAsync(VersionInfo version, string? assetName, CancellationToken ct = default)
    {
        var assets = await ListAssetsAsync(version, ct).ConfigureAwait(false);
        if (assets.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(assetName))
        {
            return assets[0];
        }

        return assets.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase))
               ?? assets.FirstOrDefault(a => WildcardMatcher.IsMatch(a.Name, assetName))
               ?? assets[0];
    }

    public virtual Task<string?> ProbeAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

    public virtual Task CleanupAsync(bool installSucceeded, CancellationToken ct = default) => Task.CompletedTask;

    protected static Uri? ToUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    /// <summary>把下载句柄包装成 AssetRef（供网盘渠道复用）。</summary>
    protected static AssetRef ToAssetRef(DownloadHandle handle, string name, long? size, string? sha256) =>
        new(name, handle.ExpectedSize ?? size, handle.Sha256 ?? sha256, handle.Uri,
            handle.Headers is null ? null : new Dictionary<string, string>(handle.Headers));
}
