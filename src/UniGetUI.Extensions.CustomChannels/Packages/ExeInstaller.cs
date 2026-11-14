using System.Diagnostics;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Models;

namespace UniGetUI.Extensions.CustomChannels.Packages;

/// <summary>exe 安装程序：支持静默参数（默认 /S）与交互运行，必要时提权。</summary>
public sealed class ExeInstaller(ElevationClient elevation) : IPackageInstaller
{
    public InstallKind Kind => InstallKind.Exe;

    public async Task<InstallResult> InstallAsync(InstallContext context, CancellationToken ct = default)
    {
        var install = context.Install;

        if (install.Interactive)
        {
            return await RunInteractiveAsync(context, ct).ConfigureAwait(false);
        }

        var arguments = string.IsNullOrWhiteSpace(install.SilentArgs) ? "/S" : install.SilentArgs!;
        context.Progress?.Report(new StageProgress(
            UpdateStage.Applying,
            $"正在运行安装程序（参数：{arguments}）…"));

        if (install.RequiresElevation)
        {
            var plan = new ApplyPlanDocument { TargetId = context.Target.Id };
            plan.Steps.Add(new ApplyStep
            {
                Kind = ApplyStepKind.RunProcess,
                Name = "运行安装程序",
                Source = context.PackagePath,
                Arguments = arguments,
            });

            var status = await elevation.ExecuteAsync(plan, ct).ConfigureAwait(false);
            if (!status.Success)
            {
                return new InstallResult(false, status.Message ?? "安装程序执行失败。", []);
            }

            return new InstallResult(true, "安装程序执行完成", []);
        }

        var result = await ProcessRunner.RunAsync(
            context.PackagePath,
            arguments,
            Path.GetDirectoryName(context.PackagePath) ?? AppContext.BaseDirectory,
            TimeSpan.FromMinutes(30),
            ct).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            var hint = result.ExitCode == 2
                ? "（部分安装程序不识别 /S，可在配置中设置 install.silentArgs）"
                : string.Empty;
            return new InstallResult(
                false,
                $"安装程序返回退出码 {result.ExitCode}{hint}。{Truncate(detail)}",
                []);
        }

        return new InstallResult(true, "安装程序执行完成", []);
    }

    private static async Task<InstallResult> RunInteractiveAsync(InstallContext context, CancellationToken ct)
    {
        context.Progress?.Report(new StageProgress(UpdateStage.Applying, "等待安装程序完成…"));

        var startInfo = new ProcessStartInfo(context.PackagePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(context.PackagePath) ?? AppContext.BaseDirectory,
        };

        if (context.Install.RequiresElevation)
        {
            startInfo.Verb = "runas";
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new InstallResult(false, "无法启动安装程序。", []);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new InstallResult(false, "等待安装程序超时。", []);
        }

        return process.ExitCode is 0 or 3010 or 1641
            ? new InstallResult(true, "安装程序执行完成", [])
            : new InstallResult(false, $"安装程序返回退出码 {process.ExitCode}。", []);
    }

    private static string Truncate(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length > 300 ? text[..300] + "…" : text;
}
