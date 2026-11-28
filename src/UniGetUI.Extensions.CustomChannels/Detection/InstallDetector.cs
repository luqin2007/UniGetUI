using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Packages;

namespace UniGetUI.Extensions.CustomChannels.Detection;

/// <summary>根据渠道资产/文件名推断出的安装方式。</summary>
public sealed record InstallDetection(
    InstallKind Kind,
    string? TargetFileName,
    string? ExePath,
    string Reason);

/// <summary>
/// 从资产名或下载链接推断安装方式。纯函数实现，便于单测与复用（编辑器「识别」按钮）。
/// 规则：
///  * 忽略源码包、符号文件、校验文件；
///  * msi/msix 以及含 setup/installer 的 exe 归入「Msi/Exe 安装程序」；
///  * 存在压缩包时按解压覆盖处理（含 portable 标记的优先）；
///  * 只有一个 exe 且不带安装程序字样时，按「EXE 单文件应用」处理。
/// </summary>
public static class InstallDetector
{
    private static readonly string[] IgnoredTokens =
    [
        "source", "src", "symbols", "pdb", "debug", "checksums", "sha256", "sha512",
        ".sig", ".asc", ".txt", ".json", ".md", "licence", "license",
    ];

    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".xz"];

    public static InstallDetection FromAssetNames(IEnumerable<string>? assetNames)
    {
        var names = (assetNames ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Where(IsUsable)
            .ToList();

        string? msi = names.FirstOrDefault(n => Extension(n) is ".msi" or ".msix" or ".msixbundle");
        if (msi is not null)
        {
            return new InstallDetection(InstallKind.Msi, null, null, $"识别到安装包 {msi}");
        }

        var exes = names.Where(n => Extension(n) == ".exe").ToList();
        var archives = names.Where(n => ArchiveExtensions.Contains(Extension(n))).ToList();

        string? setup = exes.FirstOrDefault(n => PackageDetector.LooksLikeExeInstaller(n));
        if (setup is not null)
        {
            return new InstallDetection(InstallKind.Msi, null, null, $"识别到 EXE 安装程序 {setup}");
        }

        if (archives.Count > 0 && exes.Count == 0)
        {
            string chosen = archives.FirstOrDefault(n => ContainsAny(n, "portable", "win64", "win-x64", "windows"))
                            ?? archives[0];
            return new InstallDetection(InstallKind.ZipOverwrite, null, null, $"识别到压缩包 {chosen}");
        }

        if (exes.Count > 0 && archives.Count > 0)
        {
            // 同时提供 EXE 与独立压缩包时，EXE 视为安装程序（压缩包通常是便携版）
            return new InstallDetection(
                InstallKind.Msi,
                null,
                null,
                $"识别到安装程序 {exes[0]}（同时提供压缩包 {archives[0]}）");
        }

        if (exes.Count == 1)
        {
            // 单文件应用的文件名可能带版本号，因此不写死目标文件名与可执行文件名
            return new InstallDetection(
                InstallKind.Exe,
                null,
                null,
                $"仅有单个可执行文件 {exes[0]}，按 EXE 单文件应用处理");
        }

        if (exes.Count > 1)
        {
            return new InstallDetection(InstallKind.Msi, null, null, $"识别到 {exes.Count} 个可执行文件，按 Msi/Exe 安装程序处理");
        }

        return new InstallDetection(InstallKind.ZipOverwrite, null, null, "未识别到明确的安装包，默认按压缩包解压覆盖处理");
    }

    public static InstallDetection FromUrl(string? url)
    {
        string name = FileNameFromUrl(url);
        if (name.Length == 0)
        {
            return new InstallDetection(InstallKind.ZipOverwrite, null, null, "无法从地址识别文件名，默认按压缩包解压覆盖处理");
        }

        return Extension(name) switch
        {
            ".msi" or ".msix" or ".msixbundle" =>
                new InstallDetection(InstallKind.Msi, null, null, $"识别到安装包 {name}"),
            ".exe" when PackageDetector.LooksLikeExeInstaller(name) =>
                new InstallDetection(InstallKind.Msi, null, null, $"识别到 EXE 安装程序 {name}"),
            ".exe" =>
                new InstallDetection(InstallKind.Exe, null, null, $"识别到单文件应用 {name}"),
            var ext when ArchiveExtensions.Contains(ext) =>
                new InstallDetection(InstallKind.ZipOverwrite, null, null, $"识别到压缩包 {name}"),
            _ => new InstallDetection(InstallKind.ZipOverwrite, null, null, $"未识别的文件类型 {name}，按压缩包解压覆盖处理"),
        };
    }

    /// <summary>从 URL 或路径取文件名（去掉查询参数与扩展名）。</summary>
    public static string NameFromUrl(string? value)
    {
        string name = FileNameFromUrl(value);
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>规范化为可用作标识/显示名称的字符串（小写 slug）。</summary>
    public static string ToSlug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var slug = new System.Text.StringBuilder(value.Length);
        foreach (char c in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        string result = slug.ToString().Trim('-', '.', '_');
        if (result.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            result = result[..^4];
        }

        return result.Trim('-', '.', '_');
    }

    private static string FileNameFromUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string path = value.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            path = uri.IsFile ? uri.LocalPath : uri.AbsolutePath;
        }

        int query = path.IndexOfAny(['?', '#']);
        if (query >= 0)
        {
            path = path[..query];
        }

        path = path.Replace('\\', '/');
        int slash = path.LastIndexOf('/');
        return (slash >= 0 ? path[(slash + 1)..] : path).Trim();
    }

    private static bool IsUsable(string name)
    {
        var lower = name.ToLowerInvariant();
        return !IgnoredTokens.Any(lower.Contains);
    }

    private static bool ContainsAny(string name, params string[] tokens)
    {
        var lower = name.ToLowerInvariant();
        return tokens.Any(lower.Contains);
    }

    private static string Extension(string name)
    {
        var lower = name.ToLowerInvariant();
        int dot = lower.LastIndexOf('.');
        return dot >= 0 ? lower[dot..] : string.Empty;
    }
}
