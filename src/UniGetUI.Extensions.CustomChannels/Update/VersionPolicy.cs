using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Models;

namespace UniGetUI.Extensions.CustomChannels.Update;

/// <summary>版本决策：升级/降级/跳过/固定版本的判定。</summary>
public static class VersionPolicy
{
    public static VersionRelation Compare(string? localVersion, string? candidateVersion)
    {
        if (string.IsNullOrWhiteSpace(localVersion) || string.IsNullOrWhiteSpace(candidateVersion))
        {
            return VersionRelation.Unknown;
        }

        var result = VersionComparer.Instance.Compare(candidateVersion, localVersion);
        return result switch
        {
            > 0 => VersionRelation.Newer,
            < 0 => VersionRelation.Older,
            _ => VersionRelation.Same,
        };
    }

    public static VersionCandidate Evaluate(VersionInfo version, string? localVersion, UpdatePolicyConfig policy)
    {
        var relation = Compare(localVersion, version.DisplayVersion);

        var skipped = policy.SkippedVersions.Any(v => Matches(v, version));
        string? blockReason = null;
        var selectable = true;

        if (!string.IsNullOrWhiteSpace(policy.PinnedVersion) && !Matches(policy.PinnedVersion!, version))
        {
            selectable = false;
            blockReason = $"已固定版本 {policy.PinnedVersion}";
        }
        else if (relation == VersionRelation.Older && !policy.AllowDowngrade)
        {
            selectable = false;
            blockReason = "低于当前版本，需开启「允许降级」";
        }

        return new VersionCandidate(version, relation, skipped, selectable, blockReason);
    }

    /// <summary>按版本降序排列，无法解析的排在最后。</summary>
    public static IEnumerable<VersionCandidate> Order(IEnumerable<VersionCandidate> candidates) =>
        candidates
            .OrderByDescending(c => c.Version.NormalizedVersion, VersionComparer.Instance)
            .ThenByDescending(c => c.Version.PublishedAt);

    private static bool Matches(string configured, VersionInfo version) =>
        string.Equals(configured, version.RawTag, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            VersionComparer.Normalize(configured),
            version.NormalizedVersion,
            StringComparison.OrdinalIgnoreCase);
}
