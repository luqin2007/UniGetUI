using Microsoft.Extensions.Logging;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Models;

namespace UniGetUI.Extensions.CustomChannels.Packages;

/// <summary>
/// EXE 单文件应用：下载的 exe 就是程序本体，复制到安装目录即可运行。
/// 文件名通常带版本号，因此安装后记录实际文件名，更新时清理上一版文件，
/// 并按配置创建/清理桌面与开始菜单快捷方式。
/// </summary>
public sealed class SingleFileInstaller(ElevationClient elevation, InstalledFileStore installed) : IPackageInstaller
{
    public InstallKind Kind => InstallKind.Exe;

    public async Task<InstallResult> InstallAsync(InstallContext context, CancellationToken ct = default)
    {
        var install = context.Install;
        var root = context.InstallRoot;

        var relative = string.IsNullOrWhiteSpace(install.TargetFileName)
            ? Path.GetFileName(context.PackagePath)
            : install.TargetFileName!;

        var destination = ArchiveExtractor.SafeCombine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var previous = installed.Read(context.Target.Id);
        var previousFile = ResolvePreviousFile(context, previous, relative);

        context.Progress?.Report(new StageProgress(UpdateStage.BackingUp, "正在备份旧文件…"));
        var session = context.Backup.Begin(context.Target.Id, root);
        context.Backup.AddFile(session, relative);
        context.Backup.Commit(session);

        context.Progress?.Report(new StageProgress(UpdateStage.Applying, $"正在安装 {relative}…"));

        if (install.RequiresElevation)
        {
            var plan = new ApplyPlanDocument { TargetId = context.Target.Id };
            plan.Steps.Add(new ApplyStep
            {
                Kind = ApplyStepKind.CopyFile,
                Name = $"安装 {relative}",
                Source = context.PackagePath,
                Destination = destination,
            });

            if (previousFile is not null)
            {
                plan.Steps.Add(new ApplyStep
                {
                    Kind = ApplyStepKind.DeleteFile,
                    Name = $"清理上一版 {previousFile}",
                    Destination = ArchiveExtractor.SafeCombine(root, previousFile),
                    Optional = true,
                });
            }

            var status = await elevation.ExecuteAsync(plan, ct).ConfigureAwait(false);
            if (!status.Success)
            {
                context.Backup.Rollback(session);
                return new InstallResult(false, status.Message ?? "提权安装失败。", []);
            }
        }
        else
        {
            var copyError = TryCopy(context.PackagePath, destination);
            if (copyError is not null)
            {
                context.Backup.Rollback(session);
                return new InstallResult(false, copyError, []);
            }

            if (previousFile is not null)
            {
                TryDelete(Path.Combine(root, previousFile), context);
            }
        }

        var shortcuts = CreateShortcuts(context, destination, previous);

        installed.Save(new InstalledFileRecord(
            context.Target.Id,
            relative,
            shortcuts,
            DateTimeOffset.Now));

        var message = previousFile is not null
            ? $"已安装 {relative}（已清理上一版 {previousFile}）"
            : $"已安装 {relative}";

        if (shortcuts.Count > 0)
        {
            message += "，快捷方式已创建";
        }

        return new InstallResult(true, message, [relative]);
    }

    /// <summary>上一版安装的文件名；与本次相同时无需清理。</summary>
    private static string? ResolvePreviousFile(
        InstallContext context,
        InstalledFileRecord? previous,
        string current)
    {
        var name = previous?.FileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            // 首次由本版本接管：安装目录里已有的 exe 视为上一版程序
            var resolved = ExecutableLocator.Resolve(context.Target);
            if (resolved is not null)
            {
                name = Path.GetRelativePath(context.InstallRoot, resolved);
                if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
                {
                    name = null;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(name) ||
            string.Equals(name, current, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        bool exists;
        try
        {
            exists = File.Exists(ArchiveExtractor.SafeCombine(context.InstallRoot, name!));
        }
        catch (ArgumentException)
        {
            return null;
        }

        return exists ? name : null;
    }

    private static string? TryCopy(string source, string destination)
    {
        var temp = destination + ".new";
        try
        {
            File.Copy(source, temp, overwrite: true);

            if (File.Exists(destination))
            {
                File.SetAttributes(destination, FileAttributes.Normal);
                File.Replace(temp, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, destination);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                }
            }

            return $"安装文件失败：{ex.Message}";
        }
    }

    private static void TryDelete(string path, InstallContext context)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Logger.LogWarning("清理上一版文件失败：{Path} - {Message}", path, ex.Message);
        }
    }

    /// <summary>按配置创建桌面/开始菜单快捷方式，并删除不再需要的旧快捷方式。</summary>
    private static List<string> CreateShortcuts(
        InstallContext context,
        string targetPath,
        InstalledFileRecord? previous)
    {
        var created = new List<string>();
        var stale = new List<string>(previous?.ShortcutPaths ?? []);

        if (OperatingSystem.IsWindows())
        {
            string name = ShortcutName(context.Target);
            string workdir = Path.GetDirectoryName(targetPath) ?? context.InstallRoot;

            var locations = new (Environment.SpecialFolder Folder, bool Enabled)[]
            {
                (Environment.SpecialFolder.DesktopDirectory, context.Install.CreateDesktopShortcut),
                (Environment.SpecialFolder.Programs, context.Install.CreateStartMenuShortcut),
            };

            foreach (var (folder, enabled) in locations)
            {
                if (!enabled)
                {
                    continue;
                }

                string directory = Environment.GetFolderPath(folder);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                string linkPath = Path.Combine(directory, name + ".lnk");
                if (ShellShortcut.TryCreate(linkPath, targetPath, workdir, context.Target.DisplayName, targetPath))
                {
                    created.Add(linkPath);
                    stale.RemoveAll(p => string.Equals(p, linkPath, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    context.Logger.LogWarning("创建快捷方式失败：{Path}", linkPath);
                }
            }
        }

        foreach (var path in stale)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                context.Logger.LogWarning("删除旧快捷方式失败：{Path} - {Message}", path, ex.Message);
            }
        }

        return created;
    }

    private static string ShortcutName(TargetConfig target)
    {
        string name = string.IsNullOrWhiteSpace(target.DisplayName) ? target.Id : target.DisplayName;
        name = AppPaths.Sanitize(name);
        return name.Length == 0 ? "App" : name;
    }
}
