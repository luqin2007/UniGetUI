using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Models;

namespace UniGetUI.Extensions.CustomChannels.Packages;

/// <summary>单文件替换：备份旧文件后原子替换，必要时经提权助手。</summary>
public sealed class SingleFileInstaller(ElevationClient elevation) : IPackageInstaller
{
    public InstallKind Kind => InstallKind.SingleFile;

    public async Task<InstallResult> InstallAsync(InstallContext context, CancellationToken ct = default)
    {
        var install = context.Install;
        var root = context.InstallRoot;

        var relative = string.IsNullOrWhiteSpace(install.TargetFileName)
            ? Path.GetFileName(context.PackagePath)
            : install.TargetFileName!;

        var destination = ArchiveExtractor.SafeCombine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        context.Progress?.Report(new StageProgress(UpdateStage.BackingUp, "正在备份旧文件…"));
        var session = context.Backup.Begin(context.Target.Id, root);
        context.Backup.AddFile(session, relative);
        context.Backup.Commit(session);

        context.Progress?.Report(new StageProgress(UpdateStage.Applying, $"正在替换 {relative}…"));

        if (install.RequiresElevation)
        {
            var plan = new ApplyPlanDocument { TargetId = context.Target.Id };
            plan.Steps.Add(new ApplyStep
            {
                Kind = ApplyStepKind.CopyFile,
                Name = $"替换 {relative}",
                Source = context.PackagePath,
                Destination = destination,
            });

            var status = await elevation.ExecuteAsync(plan, ct).ConfigureAwait(false);
            if (!status.Success)
            {
                context.Backup.Rollback(session);
                return new InstallResult(false, status.Message ?? "提权替换失败。", []);
            }

            return new InstallResult(true, $"已替换 {relative}", [relative]);
        }

        var temp = destination + ".new";
        try
        {
            File.Copy(context.PackagePath, temp, overwrite: true);

            if (File.Exists(destination))
            {
                File.SetAttributes(destination, FileAttributes.Normal);
                File.Replace(temp, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, destination);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }

            context.Backup.Rollback(session);
            return new InstallResult(false, $"替换文件失败：{ex.Message}", []);
        }

        return new InstallResult(true, $"已替换 {relative}", [relative]);
    }
}
