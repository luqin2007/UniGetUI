using System.Security.Cryptography;
using System.Text;

namespace UniGetUI.Extensions.CustomChannels.Infrastructure;

/// <summary>
/// 所有运行时目录的唯一来源。根目录固定为可执行文件所在目录，
/// 禁止使用系统临时目录（Path.GetTempPath / TEMP）。
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = Path.GetFullPath(root);
        Cache = Path.Combine(Root, "cache");
        Downloads = Path.Combine(Root, "downloads");
        Staging = Path.Combine(Root, "staging");
        Backup = Path.Combine(Root, "backup");
        Logs = Path.Combine(Root, "logs");
        Locks = Path.Combine(Root, "locks");
        State = Path.Combine(Root, "state");
        Tools = Path.Combine(Root, "tools");
        WebView = Path.Combine(Root, "webview2");
        Config = Path.Combine(Root, "config");
    }

    /// <summary>
    /// 默认数据根目录。UniGetUI 安装目录不可写，因此固定放在用户数据目录下；
    /// 可通过环境变量 <c>UNIGETUI_CUSTOMCHANNELS_ROOT</c> 覆盖（测试用）。
    /// </summary>
    public static AppPaths Default { get; } = new(GetDefaultRoot());

    private static string GetDefaultRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("UNIGETUI_CUSTOMCHANNELS_ROOT");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = AppContext.BaseDirectory;
        }

        return Path.Combine(local, "UniGetUI", "custom-channels");
    }

    public string Root { get; }

    public string Cache { get; }

    public string Downloads { get; }

    public string Staging { get; }

    public string Backup { get; }

    public string Logs { get; }

    public string Locks { get; }

    public string State { get; }

    public string Tools { get; }

    public string WebView { get; }

    public string Config { get; }

    public IReadOnlyList<string> AllDirectories =>
    [
        Cache, Downloads, Staging, Backup, Logs, Locks, State, Tools, WebView, Config
    ];

    public void EnsureCreated()
    {
        foreach (var dir in AllDirectories)
        {
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>在根目录下拼接路径，并断言结果仍位于根目录内。</summary>
    public string Under(params string[] parts)
    {
        var relative = Path.Combine(parts);
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        return WorkspaceGuard.AssertInside(Root, full);
    }

    /// <summary>为某个目标创建独立的暂存目录。</summary>
    public string CreateStagingDirectory(string targetId, string version)
    {
        var safeTarget = Sanitize(targetId);
        var safeVersion = Sanitize(version);
        var path = Under("staging", safeTarget, safeVersion);
        Directory.CreateDirectory(path);
        return path;
    }

    public static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "_";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        return sb.ToString().Trim().TrimEnd('.');
    }

    /// <summary>计算目录占用（用于清理提示）。</summary>
    public static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // 忽略被占用的文件
            }
        }

        return total;
    }

    public static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
