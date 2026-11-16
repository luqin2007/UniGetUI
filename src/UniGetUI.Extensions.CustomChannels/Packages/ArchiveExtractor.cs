using System.IO.Compression;
using System.Text;
using UniGetUI.Extensions.CustomChannels.Configuration;
using ICSharpCode.SharpZipLib.Zip;
using SharpZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace UniGetUI.Extensions.CustomChannels.Packages;

public sealed record ExtractResult(IReadOnlyList<string> Files, bool PasswordUsed, string? Password);

/// <summary>
/// 压缩包解压：普通 zip 走 System.IO.Compression 快速路径；
/// 加密 zip（ZipCrypto/AES）走 SharpZipLib 并支持候选密码与运行时弹窗输入。
/// 所有条目均做 Zip Slip 校验。
/// </summary>
public sealed class ArchiveExtractor
{
    private const int MaxPromptAttempts = 3;

    static ArchiveExtractor()
    {
        // codepage 936（GBK）在 .NET Core 需注册扩展码页提供程序
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // SharpZipLib 加密 zip 路径：非 UTF-8 标志条目按 GBK 解码（中文压缩包）
        ZipStrings.CodePage = 936;
    }

    public async Task<ExtractResult> ExtractAsync(
        string archivePath,
        string destinationRoot,
        ArchiveConfig config,
        IPasswordProvider? passwordProvider,
        string displayName,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationRoot);

        var extension = Path.GetExtension(archivePath).ToLowerInvariant();
        if (extension is ".7z" or ".rar" or ".tar" or ".gz" or ".tgz")
        {
            throw new InvalidDataException(
                $"暂不支持 {extension} 格式，请使用 zip 压缩包（加密 zip 亦支持）。");
        }

        var encrypted = DetectZipEncryption(archivePath);
        if (encrypted == false)
        {
            await Task.Run(() => ExtractPlainZip(archivePath, destinationRoot, ct), ct).ConfigureAwait(false);
            return new ExtractResult(EnumerateFiles(destinationRoot), false, null);
        }

        var candidates = config.Passwords
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct()
            .ToList();

        string? usedPassword = null;
        string? lastError = null;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (TryExtractEncryptedZip(archivePath, destinationRoot, candidate, out lastError))
            {
                usedPassword = candidate;
                break;
            }

            ClearDirectory(destinationRoot);
        }

        if (usedPassword is null && config.PromptForPassword && passwordProvider is not null)
        {
            for (var attempt = 1; attempt <= MaxPromptAttempts; attempt++)
            {
                var input = await passwordProvider
                    .RequestPasswordAsync(new PasswordRequest(displayName, attempt, lastError), ct)
                    .ConfigureAwait(false);

                if (string.IsNullOrEmpty(input))
                {
                    break;
                }

                if (TryExtractEncryptedZip(archivePath, destinationRoot, input, out lastError))
                {
                    usedPassword = input;
                    config.Passwords.Add(input);
                    break;
                }

                ClearDirectory(destinationRoot);
            }
        }

        if (usedPassword is null)
        {
            throw new ArchivePasswordException(
                $"压缩包已加密，未能使用给定密码解压：{displayName}" +
                (string.IsNullOrEmpty(lastError) ? "。" : $"。{lastError}"));
        }

        return new ExtractResult(EnumerateFiles(destinationRoot), true, usedPassword);
    }

    /// <summary>
    /// 根据解压后处理方式确定安装内容根目录。
    /// 旧配置兼容：ExtractSubPath 非空时优先按自定义路径处理（当时无 ExtractMode 字段）。
    /// </summary>
    public static string ResolveContentRoot(string extractDir, ArchiveConfig archive)
    {
        if (archive.ExtractMode == ExtractMode.Direct)
        {
            return extractDir;
        }

        if (!string.IsNullOrWhiteSpace(archive.ExtractSubPath))
        {
            var custom = SafeCombine(extractDir, archive.ExtractSubPath);
            if (!Directory.Exists(custom))
            {
                throw new DirectoryNotFoundException($"压缩包内不存在子目录：{archive.ExtractSubPath}");
            }

            return custom;
        }

        if (archive.ExtractMode == ExtractMode.Custom)
        {
            throw new InvalidDataException(
                "解压方式为「自定义路径」，但未填写压缩包子目录（ExtractSubPath）。");
        }

        // 智能解压：压缩包只有一个顶层目录时忽略该层级
        var top = Directory.GetFileSystemEntries(extractDir);
        if (top.Length == 1 && Directory.Exists(top[0]))
        {
            return top[0];
        }

        return extractDir;
    }

    /// <summary>判断 zip 是否加密；无法判断时返回 null。</summary>
    private static bool? DetectZipEncryption(string archivePath)
    {
        try
        {
            using var zip = new SharpZipFile(archivePath);
            foreach (ZipEntry entry in zip)
            {
                if (entry.IsCrypted)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is ZipException or IOException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    private static void ExtractPlainZip(string archivePath, string destinationRoot, CancellationToken ct)
    {
        // 中文压缩包（WinRAR/好压/360 等）常用 ANSI/GBK 存储条目名且不带 UTF-8 标志，
        // 默认按 UTF-8 解码会出现乱码；检测到替换符时改用 GBK 码页重开。
        // 显式指定编码不影响带 UTF-8 标志的条目（仍按 UTF-8 解码）。
        var encoding = DetectEntryEncoding(archivePath);

        using var file = File.OpenRead(archivePath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: encoding);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var target = SafeCombine(destinationRoot, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    /// <summary>默认解码出现乱码替换符时，判定条目名为 ANSI/GBK 存储。</summary>
    private static Encoding? DetectEntryEncoding(string archivePath)
    {
        try
        {
            using var file = File.OpenRead(archivePath);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Contains('\uFFFD'))
                {
                    return Encoding.GetEncoding(936);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static bool TryExtractEncryptedZip(
        string archivePath,
        string destinationRoot,
        string? password,
        out string? error)
    {
        error = null;
        try
        {
            using var zip = new SharpZipFile(archivePath);
            if (!string.IsNullOrEmpty(password))
            {
                zip.Password = password;
            }

            foreach (ZipEntry entry in zip)
            {
                if (!entry.IsFile || string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                var target = SafeCombine(destinationRoot, entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                using var input = zip.GetInputStream(entry);
                using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }

            return true;
        }
        catch (Exception ex) when (ex is ZipException or InvalidDataException or IOException or ArgumentException)
        {
            error = ex is ZipException ? $"密码错误或压缩包损坏：{ex.Message}" : ex.Message;
            return false;
        }
    }

    /// <summary>规范化并校验解压目标，阻断 Zip Slip 与绝对路径。</summary>
    public static string SafeCombine(string root, string entryName)
    {
        var raw = entryName.Replace('\\', '/');
        if (raw.Length == 0)
        {
            throw new InvalidDataException("压缩包内存在空路径条目。");
        }

        if (raw.StartsWith('/'))
        {
            throw new InvalidDataException($"压缩包内存在绝对路径条目：{entryName}");
        }

        var normalized = raw.TrimStart('/');
        if (normalized.Length == 0)
        {
            throw new InvalidDataException("压缩包内存在空路径条目。");
        }

        if (normalized.Length >= 2 && normalized[1] == ':')
        {
            throw new InvalidDataException($"压缩包内存在绝对路径条目：{entryName}");
        }

        var segments = normalized.Split('/');
        if (segments.Any(s => s == ".."))
        {
            throw new InvalidDataException($"压缩包内存在路径穿越条目：{entryName}");
        }

        var rootFull = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(rootFull, normalized));
        var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"压缩包条目越界：{entryName}");
        }

        return target;
    }

    private static IReadOnlyList<string> EnumerateFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

    private static void ClearDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }
}
