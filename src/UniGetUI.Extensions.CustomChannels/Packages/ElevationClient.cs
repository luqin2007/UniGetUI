using System.ComponentModel;
using System.Diagnostics;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Packages;

/// <summary>
/// 提权助手客户端：把受限操作计划写入程序目录 state，再通过 runas 启动自身执行，
/// 父进程轮询状态文件获取结果，避免整个程序常驻管理员权限。
/// </summary>
public sealed class ElevationClient(AppPaths paths, ILogger<ElevationClient> logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);

    public async Task<ApplyStatusDocument> ExecuteAsync(ApplyPlanDocument plan, CancellationToken ct = default)
    {
        Directory.CreateDirectory(paths.State);
        var token = Guid.NewGuid().ToString("N")[..8];
        var planPath = Path.Combine(paths.State, $"apply-plan-{token}.json");
        plan.StatusPath = Path.Combine(paths.State, $"apply-status-{token}.json");

        JsonFileStore.Write(planPath, plan, AppJsonContext.Default.ApplyPlanDocument);
        JsonFileStore.TryDelete(plan.StatusPath);

        var exePath = ResolveHostExecutable();
        logger.LogInformation("请求管理员权限执行 {Count} 个步骤：{Exe}", plan.Steps.Count, exePath);

        try
        {
            var startInfo = new ProcessStartInfo(exePath, $"--apply-elevated \"{planPath}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return Failure("无法启动提权进程。");
            }

            var deadline = DateTimeOffset.UtcNow + MaxWait;
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                var status = TryReadStatus(plan.StatusPath);
                if (status is not null)
                {
                    JsonFileStore.TryDelete(planPath);
                    return status;
                }

                if (process.HasExited)
                {
                    // 进程退出后再给文件系统一点时间
                    await Task.Delay(300, ct).ConfigureAwait(false);
                    var final = TryReadStatus(plan.StatusPath);
                    JsonFileStore.TryDelete(planPath);
                    return final ?? Failure($"提权进程已退出（代码 {process.ExitCode}），但未写入执行结果。");
                }

                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }

            return Failure("提权操作等待超时。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            logger.LogWarning("用户取消了管理员授权。");
            JsonFileStore.TryDelete(planPath);
            return Failure("用户取消了管理员授权，更新未执行。");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogError(ex, "启动提权进程失败。");
            JsonFileStore.TryDelete(planPath);
            return Failure($"启动提权进程失败：{ex.Message}");
        }
    }

    private static ApplyStatusDocument? TryReadStatus(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonFileStore.Read(path, AppJsonContext.Default.ApplyStatusDocument);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string ResolveHostExecutable()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) &&
            Path.GetFileName(processPath).StartsWith("AppUpdater", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, "AppUpdater.exe");
        return File.Exists(candidate) ? candidate : processPath ?? "AppUpdater.exe";
    }

    private static ApplyStatusDocument Failure(string message) => new()
    {
        Success = false,
        Message = message,
        FinishedAt = DateTimeOffset.UtcNow,
    };
}

/// <summary>提权模式下执行计划（在管理员进程中运行）。</summary>
public static class ElevatedApplier
{
    public static async Task<ApplyStatusDocument> ExecuteAsync(string planPath, CancellationToken ct = default)
    {
        var status = new ApplyStatusDocument();

        ApplyPlanDocument? plan;
        try
        {
            plan = JsonFileStore.Read(planPath, AppJsonContext.Default.ApplyPlanDocument);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return new ApplyStatusDocument { Success = false, Message = $"读取执行计划失败：{ex.Message}" };
        }

        if (plan is null)
        {
            return new ApplyStatusDocument { Success = false, Message = "执行计划为空。" };
        }

        foreach (var step in plan.Steps)
        {
            ct.ThrowIfCancellationRequested();
            var stepStatus = new ApplyStepStatus { Name = step.Name };

            try
            {
                switch (step.Kind)
                {
                    case ApplyStepKind.CopyTree:
                        CopyTree(step, ct);
                        break;
                    case ApplyStepKind.CopyFile:
                        CopyFile(step);
                        break;
                    case ApplyStepKind.DeleteFile:
                        if (step.Destination is not null && File.Exists(step.Destination))
                        {
                            File.Delete(step.Destination);
                        }

                        break;
                    case ApplyStepKind.RunProcess:
                        var result = await ProcessRunner.RunAsync(
                            step.Source ?? throw new InvalidOperationException("缺少可执行文件路径。"),
                            step.Arguments,
                            Path.GetDirectoryName(step.Source) ?? AppContext.BaseDirectory,
                            TimeSpan.FromMinutes(20),
                            ct).ConfigureAwait(false);

                        stepStatus.Message = $"退出码 {result.ExitCode}";
                        stepStatus.Success = result.IsSuccess;
                        break;
                    default:
                        throw new InvalidOperationException($"未知操作：{step.Kind}");
                }

                stepStatus.Success = step.Kind != ApplyStepKind.RunProcess ? true : stepStatus.Success;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stepStatus.Success = false;
                stepStatus.Message = ex.Message;
            }

            status.Steps.Add(stepStatus);
            if (!stepStatus.Success)
            {
                status.Success = false;
                status.Message = $"步骤「{step.Name}」失败：{stepStatus.Message}";
                status.FinishedAt = DateTimeOffset.UtcNow;
                TryWrite(plan, status);
                return status;
            }
        }

        status.Success = true;
        status.Message = "执行完成";
        status.FinishedAt = DateTimeOffset.UtcNow;
        TryWrite(plan, status);
        return status;
    }

    private static void CopyTree(ApplyStep step, CancellationToken ct)
    {
        var source = step.Source ?? throw new InvalidOperationException("缺少源目录。");
        var destination = step.Destination ?? throw new InvalidOperationException("缺少目标目录。");

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"源目录不存在：{source}");
        }

        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = ArchiveExtractor.SafeCombine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            if (File.Exists(target))
            {
                File.SetAttributes(target, FileAttributes.Normal);
            }

            File.Copy(file, target, overwrite: true);
        }
    }

    private static void CopyFile(ApplyStep step)
    {
        var source = step.Source ?? throw new InvalidOperationException("缺少源文件。");
        var destination = step.Destination ?? throw new InvalidOperationException("缺少目标文件。");

        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"源文件不存在：{source}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            File.SetAttributes(destination, FileAttributes.Normal);
        }

        File.Copy(source, destination, overwrite: true);
    }

    private static void TryWrite(ApplyPlanDocument plan, ApplyStatusDocument status)
    {
        if (string.IsNullOrWhiteSpace(plan.StatusPath))
        {
            return;
        }

        try
        {
            JsonFileStore.Write(plan.StatusPath, status, AppJsonContext.Default.ApplyStatusDocument);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
