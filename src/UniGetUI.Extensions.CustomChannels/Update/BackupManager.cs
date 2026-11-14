using System.Text.Json.Serialization;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Update;

public sealed class BackupManifest
{
    public string TargetId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<BackupEntry> Entries { get; set; } = new();
}

public sealed class BackupEntry
{
    public string RelativePath { get; set; } = string.Empty;

    public bool Existed { get; set; }

    public string? BackupFile { get; set; }
}

public sealed class BackupSession
{
    public required string Id { get; init; }

    public required string Directory { get; init; }

    public required string TargetRoot { get; init; }

    public BackupManifest Manifest { get; } = new();

    public bool HasEntries => Manifest.Entries.Count > 0;
}

/// <summary>
/// 更新前快照与失败回滚。备份目录位于程序目录 backup/&lt;targetId&gt;/&lt;timestamp&gt;。
/// </summary>
public sealed class BackupManager(AppPaths paths, ILogger<BackupManager> logger)
{
    public BackupSession Begin(string targetId, string targetRoot)
    {
        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var directory = WorkspaceGuard.AssertInside(
            paths.Root,
            Path.Combine(paths.Backup, AppPaths.Sanitize(targetId), id));

        Directory.CreateDirectory(directory);

        var session = new BackupSession
        {
            Id = id,
            Directory = directory,
            TargetRoot = Path.GetFullPath(targetRoot),
        };
        session.Manifest.TargetId = targetId;
        return session;
    }

    /// <summary>把即将被覆盖的文件复制到备份目录。</summary>
    public void AddFile(BackupSession session, string relativePath)
    {
        var sourcePath = Path.Combine(session.TargetRoot, relativePath);
        var entry = new BackupEntry
        {
            RelativePath = relativePath,
            Existed = File.Exists(sourcePath),
        };

        if (entry.Existed)
        {
            var backupFile = Path.Combine(session.Directory, AppPaths.Sanitize(relativePath.Replace('/', '_')));
            Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
            File.Copy(sourcePath, backupFile, overwrite: true);
            entry.BackupFile = backupFile;
        }

        session.Manifest.Entries.Add(entry);
    }

    public void AddFiles(BackupSession session, IEnumerable<string> relativePaths)
    {
        foreach (var path in relativePaths)
        {
            AddFile(session, path);
        }
    }

    public void Commit(BackupSession session)
    {
        if (!session.HasEntries)
        {
            TryRemoveDirectory(session.Directory);
            return;
        }

        JsonFileStore.Write(
            Path.Combine(session.Directory, "manifest.json"),
            session.Manifest,
            AppJsonContext.Default.BackupManifest);

        logger.LogInformation("已备份 {Count} 个文件到 {Directory}", session.Manifest.Entries.Count, session.Directory);
    }

    /// <summary>回滚到备份状态。</summary>
    public void Rollback(BackupSession session)
    {
        var restored = 0;
        foreach (var entry in session.Manifest.Entries.AsEnumerable().Reverse())
        {
            var targetPath = Path.Combine(session.TargetRoot, entry.RelativePath);
            try
            {
                if (entry.Existed && entry.BackupFile is not null && File.Exists(entry.BackupFile))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    File.Copy(entry.BackupFile, targetPath, overwrite: true);
                    restored++;
                }
                else if (!entry.Existed && File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                    restored++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError("回滚 {Path} 失败：{Message}", targetPath, ex.Message);
            }
        }

        logger.LogWarning("已回滚 {Count} 个文件（备份 {Id}）", restored, session.Id);
    }

    /// <summary>保留最近 N 份备份，清理其余。</summary>
    public int Prune(string targetId, int keepCount)
    {
        if (keepCount < 0)
        {
            keepCount = 0;
        }

        var directory = Path.Combine(paths.Backup, AppPaths.Sanitize(targetId));
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var removed = 0;
        foreach (var sub in Directory.EnumerateDirectories(directory).OrderByDescending(d => d))
        {
            if (removed < keepCount)
            {
                removed++;
                continue;
            }

            TryRemoveDirectory(sub);
        }

        return Directory.EnumerateDirectories(directory).Count();
    }

    public int RemoveAll()
    {
        var count = 0;
        if (!Directory.Exists(paths.Backup))
        {
            return 0;
        }

        foreach (var sub in Directory.EnumerateDirectories(paths.Backup))
        {
            TryRemoveDirectory(sub);
            count++;
        }

        return count;
    }

    private static void TryRemoveDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
