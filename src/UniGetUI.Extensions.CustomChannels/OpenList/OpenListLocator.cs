using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;

namespace UniGetUI.Extensions.CustomChannels.OpenList;

public enum OpenListBinarySource
{
    /// <summary>配置中显式指定。</summary>
    Configured,

    /// <summary>系统 PATH 中找到（用户自行安装并加入环境变量）。</summary>
    Path,

    /// <summary>常见安装目录中找到（安装版 / 便携版解压目录）。</summary>
    CommonInstall,

    /// <summary>程序目录 tools/openlist 内的托管副本（此前下载或手动放置）。</summary>
    Bundled,
}

public sealed record OpenListBinary(string Path, OpenListBinarySource Source)
{
    public string SourceText => Source switch
    {
        OpenListBinarySource.Configured => "配置指定",
        OpenListBinarySource.Path => "系统 PATH",
        OpenListBinarySource.CommonInstall => "本机安装目录",
        OpenListBinarySource.Bundled => "程序目录托管副本",
        _ => Source.ToString(),
    };
}

/// <summary>
/// 查找本机可用的 OpenList 可执行文件。优先使用用户已安装的版本，
/// 只有全部候选都不可用时才回落到程序目录托管副本或按需下载。
/// </summary>
public static class OpenListLocator
{
    public const string BinaryName = "openlist.exe";

    /// <summary>按优先级返回已存在的候选可执行文件（去重，仅返回真实存在的文件）。</summary>
    public static IReadOnlyList<OpenListBinary> Locate(OpenListConfig config, AppPaths paths) =>
        Locate(config, paths, Environment.GetEnvironmentVariable("PATH"), EnumerateCommonDirectories());

    /// <summary>可注入 PATH 与候选目录的重载，便于单元测试。</summary>
    public static IReadOnlyList<OpenListBinary> Locate(
        OpenListConfig config,
        AppPaths paths,
        string? pathVariable,
        IEnumerable<string> commonDirectories)
    {
        var results = new List<OpenListBinary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 显式配置优先，相对路径按程序目录解析
        Add(results, seen, ResolveConfigured(config.BinaryPath, paths.Root), OpenListBinarySource.Configured);

        // 2. 系统 PATH（用户把 openlist.exe 加进了环境变量）
        foreach (var directory in SplitPathVariable(pathVariable))
        {
            Add(results, seen, Path.Combine(directory, BinaryName), OpenListBinarySource.Path);
        }

        // 3. 常见安装目录
        foreach (var directory in commonDirectories)
        {
            Add(results, seen, Path.Combine(directory, BinaryName), OpenListBinarySource.CommonInstall);
        }

        // 4. 程序目录内的托管副本
        Add(
            results,
            seen,
            Path.Combine(paths.Tools, "openlist", BinaryName),
            OpenListBinarySource.Bundled);

        return results;
    }

    /// <summary>常规安装位置：安装版默认目录、便携版常见解压位置。</summary>
    public static IEnumerable<string> EnumerateCommonDirectories()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            yield return Path.Combine(root, "OpenList");
            yield return Path.Combine(root, "openlist");
            yield return Path.Combine(root, "Programs", "OpenList");
            yield return Path.Combine(root, "Programs", "openlist");
            yield return Path.Combine(root, "Apps", "OpenList");
            yield return Path.Combine(root, "scoop", "apps", "openlist", "current");
        }

        // %LOCALAPPDATA%\Programs 下形如 OpenList-1.2.3 的便携解压目录
        var programs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs");
        foreach (var directory in SafeEnumerateDirectories(programs))
        {
            if (Path.GetFileName(directory).Contains("openlist", StringComparison.OrdinalIgnoreCase))
            {
                yield return directory;
            }
        }
    }

    private static string ResolveConfigured(string? configured, string root)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return string.Empty;
        }

        var value = configured.Trim().Trim('"');
        return Path.IsPathRooted(value) ? value : Path.Combine(root, value);
    }

    private static IEnumerable<string> SplitPathVariable(string? pathVariable)
    {
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            yield break;
        }

        foreach (var part in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = part.Trim().Trim('"');
            if (directory.Length > 0)
            {
                yield return directory;
            }
        }
    }

    private static void Add(
        List<OpenListBinary> results,
        HashSet<string> seen,
        string path,
        OpenListBinarySource source)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        if (!File.Exists(full) || !seen.Add(full))
        {
            return;
        }

        results.Add(new OpenListBinary(full, source));
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        if (!Directory.Exists(path))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
