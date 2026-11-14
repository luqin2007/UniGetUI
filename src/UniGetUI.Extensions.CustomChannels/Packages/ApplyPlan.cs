namespace UniGetUI.Extensions.CustomChannels.Packages;

public enum ApplyStepKind
{
    CopyTree,
    CopyFile,
    DeleteFile,
    RunProcess,
}

/// <summary>提权助手执行计划，写入程序目录 state 目录后由 runas 子进程读取。</summary>
public sealed class ApplyPlanDocument
{
    public string TargetId { get; set; } = string.Empty;

    public string StatusPath { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ApplyStep> Steps { get; set; } = new();
}

public sealed class ApplyStep
{
    public ApplyStepKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Source { get; set; }

    public string? Destination { get; set; }

    public string? Arguments { get; set; }

    public bool PreserveExistingExtraFiles { get; set; } = true;
}

public sealed class ApplyStatusDocument
{
    public bool Success { get; set; }

    public string? Message { get; set; }

    public DateTimeOffset FinishedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ApplyStepStatus> Steps { get; set; } = new();
}

public sealed class ApplyStepStatus
{
    public string Name { get; set; } = string.Empty;

    public bool Success { get; set; }

    public string? Message { get; set; }
}

public sealed record InstallResult(bool Success, string Message, IReadOnlyList<string> AffectedFiles);
