using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Models;

namespace UniGetUI.Extensions.CustomChannels.Packages;

/// <summary>msi 静默安装：msiexec /i /qn /norestart，日志写入程序目录 logs。</summary>
public sealed class MsiInstaller(ElevationClient elevation) : IPackageInstaller
{
    public InstallKind Kind => InstallKind.Msi;

    public async Task<InstallResult> InstallAsync(InstallContext context, CancellationToken ct = default)
    {
        var logPath = Path.Combine(
            context.Paths.Logs,
            $"msi-{AppPaths.Sanitize(context.Target.Id)}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        var msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        var arguments =
            $"/i \"{context.PackagePath}\" /qn /norestart /l*v \"{logPath}\"";

        context.Progress?.Report(new StageProgress(UpdateStage.Applying, "正在执行 msi 静默安装…"));

        if (context.Install.RequiresElevation)
        {
            var plan = new ApplyPlanDocument { TargetId = context.Target.Id };
            plan.Steps.Add(new ApplyStep
            {
                Kind = ApplyStepKind.RunProcess,
                Name = "msi 静默安装",
                Source = msiexec,
                Arguments = arguments,
            });

            var status = await elevation.ExecuteAsync(plan, ct).ConfigureAwait(false);
            if (!status.Success)
            {
                return new InstallResult(false, status.Message ?? "msi 安装失败。", []);
            }

            return new InstallResult(true, "msi 安装完成", []);
        }

        var result = await ProcessRunner.RunAsync(
            msiexec,
            arguments,
            AppContext.BaseDirectory,
            TimeSpan.FromMinutes(30),
            ct).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            return new InstallResult(
                false,
                $"msi 安装失败（退出码 {result.ExitCode}）。日志：{logPath} {Truncate(detail)}",
                []);
        }

        var rebootHint = result.ExitCode == 3010 ? "（需要重启才能完成）" : string.Empty;
        return new InstallResult(true, $"msi 安装完成{rebootHint}", []);
    }

    private static string Truncate(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length > 300 ? text[..300] + "…" : text;
}
