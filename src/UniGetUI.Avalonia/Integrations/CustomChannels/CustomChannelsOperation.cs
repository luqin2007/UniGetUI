using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Models;
using UniGetUI.Extensions.CustomChannels.Packages;
using UniGetUI.Extensions.CustomChannels.Services;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Integrations.CustomChannels;

/// <summary>
/// 自定义渠道的更新任务。外观、排队与日志展示完全走 UniGetUI 的操作列表，
/// 阶段日志通过 <see cref="AbstractOperation.Line"/> 推送，点击卡片即可查看。
/// </summary>
internal sealed class CustomChannelsOperation : AbstractOperation
{
    private readonly CustomChannelsService _service;
    private readonly TargetConfig _target;
    private readonly VersionCandidate _candidate;
    private readonly IPasswordProvider? _passwords;
    private readonly Action _completed;
    private DateTimeOffset _lastLineAt = DateTimeOffset.MinValue;

    public CustomChannelsOperation(
        CustomChannelsService service,
        TargetConfig target,
        VersionCandidate candidate,
        IPasswordProvider? passwords,
        Action completed)
        : base(queue_enabled: true)
    {
        _service = service;
        _target = target;
        _candidate = candidate;
        _passwords = passwords;
        _completed = completed;

        Metadata.Status = "In queue";
        Metadata.Title = $"更新 {DisplayName}";
        Metadata.OperationInformation = $"{DisplayName}：{candidate.Version.DisplayVersion}";
        Metadata.SuccessTitle = "更新完成";
        Metadata.SuccessMessage = $"{DisplayName} 已更新到 {candidate.Version.DisplayVersion}";
        Metadata.FailureTitle = "更新失败";
        Metadata.FailureMessage = $"{DisplayName} 更新到 {candidate.Version.DisplayVersion} 失败";
    }

    private string DisplayName =>
        string.IsNullOrWhiteSpace(_target.DisplayName) ? _target.Id : _target.DisplayName;

    protected override async Task<OperationVeredict> PerformOperation()
    {
        Line($"开始更新 {DisplayName} → {_candidate.Version.DisplayVersion}", LineType.Information);
        Line($"安装方式：{_target.Install.Kind}，目标目录：{_target.Install.RootPath}", LineType.VerboseDetails);

        var progress = new Progress<StageProgress>(ReportProgress);
        var result = await _service
            .InstallAsync(_target, _candidate, _passwords, progress, CancellationToken)
            .ConfigureAwait(false);

        foreach (var stage in result.CompletedStages)
        {
            Line($"阶段完成：{stage.ToText()}", LineType.VerboseDetails);
        }

        if (result.Success)
        {
            Line(result.Message, LineType.Information);
            return OperationVeredict.Success;
        }

        Line(result.Message, LineType.Error);
        if (result.Error is not null)
        {
            Line(result.Error.Message, LineType.VerboseDetails);
        }

        return OperationVeredict.Failure;
    }

    /// <summary>进度行按 250ms 节流，避免下载时逐块刷屏拖慢界面。</summary>
    private void ReportProgress(StageProgress progress)
    {
        var now = DateTimeOffset.UtcNow;
        bool isStageChange = progress.Stage is UpdateStage.Failed or UpdateStage.Canceled or UpdateStage.Completed;
        if (!isStageChange && now - _lastLineAt < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        _lastLineAt = now;
        var line = $"{progress.Stage.ToText()}：{progress.Message}";
        if (progress.Percent is { } percent)
        {
            line += $"（{percent:0.#}%）";
        }

        if (progress.BytesReceived is { } received && progress.TotalBytes is { } total and > 0)
        {
            line += $" {FormatSize(received)}/{FormatSize(total)}";
        }

        if (progress.BytesPerSecond is { } speed and > 1)
        {
            line += $" · {FormatSize((long)speed)}/s";
            if (progress.Remaining is { } remaining)
            {
                line += $" · 剩余 {remaining:mm\\:ss}";
            }
        }

        // 进行中的进度走 ProgressIndicator：不入历史日志，界面折叠为单行实时刷新；
        // 失败/取消/完成作为固定行保留
        Line(line, progress.Stage switch
        {
            UpdateStage.Failed => LineType.Error,
            UpdateStage.Completed or UpdateStage.Canceled => LineType.Information,
            _ => LineType.ProgressIndicator,
        });
    }

    protected override void OnRunCompleted()
    {
        try
        {
            _completed();
        }
        catch
        {
            // 刷新失败不应影响操作结果
        }
    }

    protected override void ApplyRetryAction(string retryMode)
    {
        Line($"按 {retryMode} 重试", LineType.Information);
    }

    public override Task<Uri> GetOperationIcon() => Task.FromResult(new Uri("about:blank"));

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
        >= 1024 * 1024 => $"{bytes / (1024d * 1024):0.##} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B",
    };
}
