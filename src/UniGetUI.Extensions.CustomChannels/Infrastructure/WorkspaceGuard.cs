namespace UniGetUI.Extensions.CustomChannels.Infrastructure;

public sealed class WorkspaceViolationException(string message) : Exception(message);

/// <summary>
/// 路径归属守卫：确保所有写入都发生在程序目录内，杜绝系统临时目录。
/// </summary>
public static class WorkspaceGuard
{
    /// <summary>断言 candidate 位于 root 之内，返回规范化后的绝对路径。</summary>
    public static string AssertInside(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(candidate);

        var normalizedRoot = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedFull = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedFull, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return full;
        }

        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspaceViolationException(
                $"路径越界：'{full}' 不在程序目录 '{normalizedRoot}' 之内。所有临时文件必须位于程序目录内。");
        }

        return full;
    }

    /// <summary>断言路径不是系统临时目录。</summary>
    public static void AssertNotSystemTemp(string path)
    {
        var full = Path.GetFullPath(path);
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspaceViolationException(
                $"禁止使用系统临时目录：'{full}'。请改用 AppPaths 提供的程序目录内路径。");
        }
    }

    /// <summary>启动时清理残留的下载与暂存内容（保留日志、备份与状态）。</summary>
    public static (int Files, long Bytes) CleanupStale(AppPaths paths, TimeSpan maxAge)
    {
        var threshold = DateTimeOffset.UtcNow - maxAge;
        var files = 0;
        long bytes = 0;

        foreach (var dir in new[] { paths.Cache, paths.Staging })
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc > threshold.UtcDateTime)
                    {
                        continue;
                    }

                    bytes += info.Length;
                    info.Delete();
                    files++;
                }
                catch (IOException)
                {
                    // 文件被占用时跳过，下次启动再试
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(sub).Any())
                    {
                        Directory.Delete(sub);
                    }
                }
                catch (IOException)
                {
                }
            }
        }

        return (files, bytes);
    }

    /// <summary>预检磁盘剩余空间。</summary>
    public static void EnsureFreeSpace(string path, long requiredBytes, long reserveBytes = 64L * 1024 * 1024)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        var drive = new DriveInfo(root);
        var available = drive.AvailableFreeSpace;
        if (available < requiredBytes + reserveBytes)
        {
            throw new IOException(
                $"磁盘空间不足：需要约 {Format(requiredBytes + reserveBytes)}，可用 {Format(available)}。");
        }

        static string Format(long bytes) => bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024d / 1024 / 1024:F1} GB"
            : $"{bytes / 1024d / 1024:F0} MB";
    }
}
