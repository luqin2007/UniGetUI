namespace UniGetUI.Extensions.CustomChannels.Models;

public enum UpdateStage
{
    Idle,
    Checking,
    Transferring,
    Resolving,
    Downloading,
    Verifying,
    Extracting,
    BackingUp,
    Applying,
    Cleaning,
    Restarting,
    Completed,
    Failed,
    Canceled,
}

public static class UpdateStageText
{
    public static string ToText(this UpdateStage stage) => stage switch
    {
        UpdateStage.Idle => "空闲",
        UpdateStage.Checking => "检查环境",
        UpdateStage.Transferring => "转存分享文件",
        UpdateStage.Resolving => "解析下载地址",
        UpdateStage.Downloading => "下载更新包",
        UpdateStage.Verifying => "校验更新包",
        UpdateStage.Extracting => "解压更新包",
        UpdateStage.BackingUp => "备份现有文件",
        UpdateStage.Applying => "应用更新",
        UpdateStage.Cleaning => "清理临时文件",
        UpdateStage.Restarting => "重新启动",
        UpdateStage.Completed => "更新完成",
        UpdateStage.Failed => "更新失败",
        UpdateStage.Canceled => "已取消",
        _ => stage.ToString(),
    };
}

/// <summary>阶段进度与下载指标。</summary>
public sealed record StageProgress(
    UpdateStage Stage,
    string Message,
    double? Percent = null,
    long? BytesReceived = null,
    long? TotalBytes = null,
    double? BytesPerSecond = null)
{
    public TimeSpan? Remaining
    {
        get
        {
            if (BytesReceived is not { } received || TotalBytes is not { } total ||
                BytesPerSecond is not { } speed || speed <= 1 || total <= received)
            {
                return null;
            }

            return TimeSpan.FromSeconds((total - received) / speed);
        }
    }
}

/// <summary>更新结果汇总。</summary>
public sealed record UpdateResult(
    bool Success,
    string Message,
    VersionInfo? InstalledVersion,
    IReadOnlyList<UpdateStage> CompletedStages,
    Exception? Error = null)
{
    public static UpdateResult Ok(VersionInfo? version, IReadOnlyList<UpdateStage> stages, string message = "更新完成") =>
        new(true, message, version, stages);

    public static UpdateResult Fail(string message, IReadOnlyList<UpdateStage> stages, Exception? error = null) =>
        new(false, message, null, stages, error);
}

/// <summary>单个渠道在一次检查中的表现，用于在界面上逐条展示可达性。</summary>
public sealed record SourceStatus(
    string SourceId,
    string DisplayName,
    Configuration.SourceType Type,
    int Priority,
    bool Success,
    int VersionCount,
    TimeSpan Elapsed,
    string? Error)
{
    public string Summary => Success
        ? $"可用 · {VersionCount} 个版本 · {Elapsed.TotalMilliseconds:0} ms"
        : $"失败 · {Error}";
}

/// <summary>一个目标的检查结果。</summary>
public sealed record TargetCheckResult(
    string TargetId,
    string DisplayName,
    string? LocalVersion,
    IReadOnlyList<VersionCandidate> Candidates,
    string? Error,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<SourceStatus>? Sources = null)
{
    public VersionCandidate? LatestStable => Candidates
        .Where(c => !c.Version.IsPrerelease && c.IsSelectable)
        .OrderByDescending(c => c.Version.NormalizedVersion, VersionComparer.Instance)
        .FirstOrDefault();

    public VersionCandidate? Best => LatestStable ?? Candidates.FirstOrDefault(c => c.IsSelectable);

    public bool HasUpdate => LatestStable is { Relation: VersionRelation.Newer };

    public bool HasError => !string.IsNullOrEmpty(Error);
}
