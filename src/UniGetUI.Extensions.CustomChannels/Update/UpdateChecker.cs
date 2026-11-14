using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Models;
using UniGetUI.Extensions.CustomChannels.Sources;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Update;

/// <summary>
/// 检查器：按渠道优先级依次查询，汇聚全部候选版本（用于多版本自选），
/// 单个渠道失败不影响其他渠道，失败原因作为告警返回。
/// </summary>
public sealed class UpdateChecker(
    UpdateSourceFactory factory,
    VersionResolver resolver,
    VersionCache cache,
    DownloadConfig downloadConfig,
    ILogger<UpdateChecker> logger,
    ReadmeStore readmeStore)
{
    public async Task<TargetCheckResult> CheckAsync(
        TargetConfig target,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        var localVersion = resolver.GetLocalVersion(target);
        var warnings = new List<string>();
        var candidates = new List<VersionCandidate>();
        var statuses = new List<SourceStatus>();
        var ttl = TimeSpan.FromMinutes(Math.Max(1, downloadConfig.ReleaseCacheMinutes));
        var priority = 0;

        foreach (var sourceConfig in target.Sources.Where(s => s.Enabled))
        {
            ct.ThrowIfCancellationRequested();
            priority++;

            var source = factory.Create(sourceConfig, target);
            if (source is null)
            {
                continue;
            }

            var key = $"{target.Id}|{source.SourceId}";
            IReadOnlyList<VersionInfo> versions;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                if (!forceRefresh && cache.TryGet(key, ttl, out var cached))
                {
                    versions = cached;
                }
                else
                {
                    versions = await source.ListVersionsAsync(
                        new VersionQuery(sourceConfig.AssetPattern, sourceConfig.IncludePrerelease, 60),
                        ct).ConfigureAwait(false);

                    // 仅在真正获取版本时顺带取 README（GitHub/GitLab），缓存命中时不再重复请求
                    versions = await AttachReadmeAsync(target, source, versions, ct).ConfigureAwait(false);
                    cache.Set(key, versions);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("渠道 {Source} 查询失败：{Message}", source.DisplayName, ex.Message);
                warnings.Add($"{source.DisplayName}：{ex.Message}");
                statuses.Add(new SourceStatus(
                    source.SourceId,
                    source.DisplayName,
                    sourceConfig.Type,
                    priority,
                    Success: false,
                    VersionCount: 0,
                    stopwatch.Elapsed,
                    ex.Message));
                continue;
            }

            statuses.Add(new SourceStatus(
                source.SourceId,
                source.DisplayName,
                sourceConfig.Type,
                priority,
                Success: true,
                versions.Count,
                stopwatch.Elapsed,
                Error: null));

            foreach (var version in versions)
            {
                candidates.Add(VersionPolicy.Evaluate(version, localVersion, target.Policy));
            }
        }

        var deduped = new List<VersionCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in VersionPolicy.Order(candidates))
        {
            if (seen.Add(candidate.Version.DisplayVersion))
            {
                deduped.Add(candidate);
            }
        }

        var error = deduped.Count == 0 && warnings.Count > 0
            ? string.Join("；", warnings)
            : null;

        return new TargetCheckResult(
            target.Id,
            target.DisplayName,
            localVersion,
            deduped,
            error,
            warnings.Count > 0 ? warnings : null,
            statuses.Count > 0 ? statuses : null);
    }

    /// <summary>
    /// 为最新版本附加与其 tag 匹配的 README（仅 GitHub/GitLab 渠道），
    /// 并记录内容哈希以判断说明文档是否更新。
    /// </summary>
    private async Task<IReadOnlyList<VersionInfo>> AttachReadmeAsync(
        TargetConfig target,
        IUpdateSource source,
        IReadOnlyList<VersionInfo> versions,
        CancellationToken ct)
    {
        if (source is not IReadmeProvider provider || versions.Count == 0 || versions[0].Readme is not null)
        {
            return versions;
        }

        var newest = versions[0];
        try
        {
            var readme = await provider.GetReadmeAsync(newest, ct).ConfigureAwait(false);
            if (readme is null)
            {
                return versions;
            }

            var changed = readmeStore.TryUpdate(
                $"{target.Id}|{source.SourceId}|{readme.Ref}",
                readme.Sha256,
                DateTimeOffset.Now);

            var enriched = newest with { Readme = readme with { HasChanged = changed } };
            var result = versions.ToList();
            result[0] = enriched;
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("获取 {Source} 的 README 失败：{Message}", source.DisplayName, ex.Message);
            return versions;
        }
    }
}
