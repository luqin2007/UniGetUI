using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Models;
using UniGetUI.Extensions.CustomChannels.Update;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Packages;

/// <summary>
/// 压缩包覆盖安装：解压到程序目录内暂存区 → 应用路径映射生成 applied 树 →
/// 备份将被覆盖的文件 → 覆盖写入（必要时经提权助手）。
/// </summary>
public sealed class ZipPackageInstaller(ArchiveExtractor extractor, ElevationClient elevation) : IPackageInstaller
{
    public InstallKind Kind => InstallKind.ZipOverwrite;

    public async Task<InstallResult> InstallAsync(InstallContext context, CancellationToken ct = default)
    {
        var install = context.Install;
        var root = context.InstallRoot;
        Directory.CreateDirectory(root);

        var staging = context.Paths.CreateStagingDirectory(context.Target.Id, context.Version.DisplayVersion);
        var extractDir = Path.Combine(staging, "extract");
        if (Directory.Exists(extractDir))
        {
            Directory.Delete(extractDir, recursive: true);
        }

        Directory.CreateDirectory(extractDir);

        context.Progress?.Report(new StageProgress(UpdateStage.Extracting, "正在解压更新包…"));
        var extract = await extractor.ExtractAsync(
            context.PackagePath,
            extractDir,
            context.Target.Archive,
            context.PasswordProvider,
            Path.GetFileName(context.PackagePath),
            ct).ConfigureAwait(false);

        if (extract.PasswordUsed)
        {
            context.Logger.LogInformation("压缩包使用密码解压成功。");
        }

        var contentRoot = extractDir;
        if (!string.IsNullOrWhiteSpace(context.Target.Archive.ExtractSubPath))
        {
            contentRoot = ArchiveExtractor.SafeCombine(extractDir, context.Target.Archive.ExtractSubPath);
            if (!Directory.Exists(contentRoot))
            {
                throw new DirectoryNotFoundException(
                    $"压缩包内不存在子目录：{context.Target.Archive.ExtractSubPath}");
            }
        }

        var appliedDir = Path.Combine(staging, "applied");
        if (Directory.Exists(appliedDir))
        {
            Directory.Delete(appliedDir, recursive: true);
        }

        Directory.CreateDirectory(appliedDir);

        var mappings = install.PathMappings
            .Where(m => !string.IsNullOrWhiteSpace(m.From) || !string.IsNullOrWhiteSpace(m.To))
            .ToList();

        var relativeFiles = new List<string>();
        foreach (var file in Directory.EnumerateFiles(contentRoot, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(contentRoot, file).Replace('\\', '/');
            var mapped = MapPath(relative, mappings);
            if (mapped is null)
            {
                continue;
            }

            var destination = ArchiveExtractor.SafeCombine(appliedDir, mapped);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            relativeFiles.Add(mapped);
        }

        if (relativeFiles.Count == 0)
        {
            throw new InvalidDataException("压缩包内没有匹配的可安装文件，请检查 pathMappings 配置。");
        }

        // 清理目标目录中不属于新版本的文件（可选）
        var deletions = new List<string>();
        if (!context.Target.Archive.PreserveExistingExtraFiles)
        {
            deletions = FindExtraFiles(root, relativeFiles);
        }

        // 备份
        context.Progress?.Report(new StageProgress(UpdateStage.BackingUp, $"正在备份 {relativeFiles.Count} 个文件…"));
        var session = context.Backup.Begin(context.Target.Id, root);
        context.Backup.AddFiles(session, relativeFiles);
        context.Backup.Commit(session);

        context.Progress?.Report(new StageProgress(UpdateStage.Applying, "正在写入更新文件…"));

        if (install.RequiresElevation)
        {
            var plan = new ApplyPlanDocument { TargetId = context.Target.Id };
            plan.Steps.Add(new ApplyStep
            {
                Kind = ApplyStepKind.CopyTree,
                Name = "覆盖安装目录",
                Source = appliedDir,
                Destination = root,
            });

            foreach (var deletion in deletions)
            {
                plan.Steps.Add(new ApplyStep
                {
                    Kind = ApplyStepKind.DeleteFile,
                    Name = $"删除多余文件 {deletion}",
                    Destination = Path.Combine(root, deletion),
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
            try
            {
                foreach (var relative in relativeFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var source = ArchiveExtractor.SafeCombine(appliedDir, relative);
                    var destination = ArchiveExtractor.SafeCombine(root, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                    if (File.Exists(destination))
                    {
                        File.SetAttributes(destination, FileAttributes.Normal);
                    }

                    File.Copy(source, destination, overwrite: true);
                }

                foreach (var deletion in deletions)
                {
                    var target = ArchiveExtractor.SafeCombine(root, deletion);
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                context.Backup.Rollback(session);
                return new InstallResult(false, $"写入文件失败：{ex.Message}", relativeFiles);
            }
        }

        return new InstallResult(true, $"已覆盖 {relativeFiles.Count} 个文件", relativeFiles);
    }

    private static string? MapPath(string relative, List<PathMapping> mappings)
    {
        if (mappings.Count == 0)
        {
            return relative;
        }

        foreach (var mapping in mappings)
        {
            var from = mapping.From.Replace('\\', '/').Trim('/');
            var to = mapping.To.Replace('\\', '/').Trim('/');

            if (from.Length == 0)
            {
                return CombineRelative(to, relative);
            }

            if (string.Equals(relative, from, StringComparison.OrdinalIgnoreCase))
            {
                return CombineRelative(to, Path.GetFileName(relative));
            }

            if (relative.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase))
            {
                return CombineRelative(to, relative[(from.Length + 1)..]);
            }
        }

        return null;
    }

    private static string CombineRelative(string prefix, string tail) =>
        prefix.Length == 0 ? tail : $"{prefix}/{tail}";

    private static List<string> FindExtraFiles(string root, List<string> relativeFiles)
    {
        var keep = new HashSet<string>(relativeFiles, StringComparer.OrdinalIgnoreCase);
        var topDirectories = relativeFiles
            .Select(r => r.Contains('/') ? r[..r.IndexOf('/')] : string.Empty)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (topDirectories.Count == 0)
        {
            return [];
        }

        var extras = new List<string>();
        foreach (var directory in topDirectories)
        {
            var full = Path.Combine(root, directory);
            if (!Directory.Exists(full))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!keep.Contains(relative))
                {
                    extras.Add(relative);
                }

                if (extras.Count > 5000)
                {
                    return extras;
                }
            }
        }

        return extras;
    }
}
