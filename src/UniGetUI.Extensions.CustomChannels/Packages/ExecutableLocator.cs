using UniGetUI.Extensions.CustomChannels.Configuration;

namespace UniGetUI.Extensions.CustomChannels.Packages;

/// <summary>
/// 定位目标程序的可执行文件。单文件应用的文件名通常带版本号（例如 App-v1.4.0.exe），
/// 更新后会变化，不能写死名称：优先使用配置路径，其次目标文件名，
/// 最后在安装目录中挑选最像主程序的 exe。
/// </summary>
public static class ExecutableLocator
{
    /// <summary>这些词出现在文件名里通常意味着它是安装/卸载程序，而不是应用本体。</summary>
    private static readonly string[] NonAppTokens =
        ["unins", "setup", "installer", "install", "update", "updater"];

    public static string? Resolve(TargetConfig target)
    {
        var install = target.Install;
        var root = install.RootPath ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(install.ExePath))
        {
            var configured = Path.IsPathRooted(install.ExePath)
                ? install.ExePath
                : Path.Combine(root, install.ExePath);
            if (File.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }
        }

        if (!InstallKinds.IsPortableSingleFile(install.Kind) ||
            string.IsNullOrWhiteSpace(root) ||
            !Directory.Exists(root))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(install.TargetFileName))
        {
            string named;
            try
            {
                named = ArchiveExtractor.SafeCombine(root, install.TargetFileName!);
            }
            catch (ArgumentException)
            {
                named = string.Empty;
            }

            if (named.Length > 0 && File.Exists(named))
            {
                return named;
            }
        }

        List<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(root, "*.exe", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        string id = target.Id?.Trim() ?? string.Empty;
        string name = target.DisplayName?.Trim() ?? string.Empty;

        var exact = candidates.FirstOrDefault(file => MatchesExactly(file, id) || MatchesExactly(file, name));
        if (exact is not null)
        {
            return exact;
        }

        var partial = candidates.FirstOrDefault(file => ContainsName(file, id) || ContainsName(file, name));
        if (partial is not null)
        {
            return partial;
        }

        var apps = candidates.Where(file => !LooksLikeArtifact(file)).ToList();
        var pool = apps.Count > 0 ? apps : candidates;
        return pool.OrderByDescending(LastWrite).First();
    }

    private static bool MatchesExactly(string file, string name) =>
        name.Length > 0 &&
        string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsName(string file, string name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        var fileName = Path.GetFileName(file);
        return fileName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
               fileName.Replace(" ", string.Empty).Contains(name.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeArtifact(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        return NonAppTokens.Any(name.Contains);
    }

    private static DateTime LastWrite(string file)
    {
        try
        {
            return File.GetLastWriteTimeUtc(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
