using UniGetUI.Extensions.CustomChannels.Configuration;

namespace UniGetUI.Extensions.CustomChannels.Packages;

/// <summary>按扩展名与文件头识别更新包类型。</summary>
public static class PackageDetector
{
    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz"];
    private static readonly string[] InstallerExtensions = [".msi", ".msix", ".msp"];

    /// <summary>文件名中出现这些词时视为 exe 安装程序，而非单文件应用本体。</summary>
    private static readonly string[] InstallerTokens = ["setup", "installer", "install"];

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

    /// <summary>配置声明的安装方式与包实际类型是否冲突；返回 null 表示兼容。</summary>
    public static string? Validate(InstallKind configured, InstallKind detected, string? fileName = null)
    {
        var mode = InstallKinds.Normalize(configured);

        if (mode == detected)
        {
            if (mode == InstallKind.Exe && LooksLikeExeInstaller(fileName))
            {
                return "下载的 exe 名称包含安装程序字样（setup/installer），但它被配置为「EXE 单文件应用」。"
                       + "如果它需要安装，请把安装方式改为「Msi/Exe 安装程序」。";
            }

            return null;
        }

        // Msi/Exe 安装程序同时接受 msi 与 exe 安装包
        if (mode == InstallKind.Msi && detected is InstallKind.Exe or InstallKind.SingleFile)
        {
            return null;
        }

        // 单文件应用也接受无法识别的文件类型（直接复制）
        if (mode == InstallKind.Exe && detected == InstallKind.SingleFile)
        {
            return null;
        }

        return mode switch
        {
            InstallKind.Msi => "配置为「Msi/Exe 安装程序」，但下载的更新包不是 msi 或 exe 安装包。",
            InstallKind.Exe => detected == InstallKind.Msi
                ? "配置为「EXE 单文件应用」，但下载的更新包是 msi 安装包；请把安装方式改为「Msi/Exe 安装程序」。"
                : "配置为「EXE 单文件应用」，但下载的更新包是压缩包；请把安装方式改为「ZIP 解压覆盖」。",
            InstallKind.ZipOverwrite => "配置为「ZIP 解压覆盖」，但下载的更新包不是压缩包。",
            _ => null,
        };
    }

    /// <summary>文件名是否像 exe 安装程序（而不是程序本体）。</summary>
    public static bool LooksLikeExeInstaller(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        return InstallerTokens.Any(name.Contains);
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
