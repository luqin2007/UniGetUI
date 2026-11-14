using System.IO.Compression;
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
        using var file = File.OpenRead(archivePath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
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
