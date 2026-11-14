namespace UniGetUI.Extensions.CustomChannels.Models;

/// <summary>渠道提供的一个候选版本。</summary>
public sealed record VersionInfo(
    string SourceId,
    string SourceDisplayName,
    string RawTag,
    string? NormalizedVersion,
    DateTimeOffset? PublishedAt,
    bool IsPrerelease,
    string? ReleaseNotes,
    IReadOnlyList<AssetSummary> Assets)
{
    public string DisplayVersion => string.IsNullOrWhiteSpace(NormalizedVersion) ? RawTag : NormalizedVersion!;

    public string Key => $"{SourceId}|{RawTag}";

    public bool HasNotes => !string.IsNullOrWhiteSpace(ReleaseNotes);

    public string? PrimaryAssetName => Assets.Count > 0 ? Assets[0].Name : null;

    public long? TotalSize => Assets.Count > 0 ? Assets.Max(a => a.Size) : null;

    /// <summary>与该版本 tag 匹配的项目说明文档（仅 GitHub/GitLab 渠道，拉取失败时为 null）。</summary>
    public Sources.ReadmeDocument? Readme { get; init; }

    public bool HasReadme => Readme is not null;
}

/// <summary>版本内的一个可下载资产。</summary>
public sealed record AssetSummary(
    string Name,
    long? Size,
    string? Sha256,
    string? DownloadUrl,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// 用于取链的资产引用：Headers 为下载所需请求头（Cookie/Referer/Token），
/// Metadata 保存渠道私有信息（网盘 id、token 等）。
/// </summary>
public sealed record AssetRef(
    string Name,
    long? Size,
    string? Sha256,
    Uri? DirectUri,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>一次更新检查得到的评估结果。</summary>
public enum VersionRelation
{
    Unknown,
    Newer,
    Same,
    Older,
}

public sealed record VersionCandidate(
    VersionInfo Version,
    VersionRelation Relation,
    bool IsSkipped,
    bool IsSelectable,
    string? BlockReason)
{
    public bool IsCurrent => Relation == VersionRelation.Same;
}
