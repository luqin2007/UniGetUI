using UniGetUI.Extensions.CustomChannels.Configuration;

namespace UniGetUI.Extensions.CustomChannels.Packages;

/// <summary>按扩展名与文件头识别更新包类型。</summary>
public static class PackageDetector
{
    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz"];
    private static readonly string[] InstallerExtensions = [".msi", ".msix", ".msp"];

    public static InstallKind Detect(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        if (InstallerExtensions.Contains(extension))
        {
            return InstallKind.Msi;
        }

        if (extension == ".exe")
        {
            return IsZipContainer(filePath) ? InstallKind.ZipOverwrite : InstallKind.Exe;
        }

        if (ArchiveExtensions.Contains(extension) || IsZipContainer(filePath))
        {
            return InstallKind.ZipOverwrite;
        }

        return InstallKind.SingleFile;
    }

    /// <summary>配置声明的安装方式与包实际类型是否冲突。</summary>
    public static string? Validate(InstallKind configured, InstallKind detected)
    {
        if (configured == detected)
        {
            return null;
        }

        if (configured == InstallKind.ZipOverwrite && detected == InstallKind.ZipOverwrite)
        {
            return null;
        }

        return configured switch
        {
            InstallKind.Msi => "配置为 msi 安装，但下载的更新包不是 msi 文件。",
            InstallKind.Exe => "配置为 exe 安装程序，但下载的更新包不是 exe 文件。",
            InstallKind.SingleFile => "配置为单文件替换，但下载的更新包是压缩包或安装程序。",
            InstallKind.ZipOverwrite => "配置为压缩包覆盖，但下载的更新包不是压缩包。",
            _ => null,
        };
    }

    public static bool IsArchive(string filePath) =>
        ArchiveExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant()) || IsZipContainer(filePath);

    private static bool IsZipContainer(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            Span<byte> header = stackalloc byte[4];
            var read = stream.Read(header);
            return read >= 4 && header[0] == 0x50 && header[1] == 0x4B &&
                   (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
