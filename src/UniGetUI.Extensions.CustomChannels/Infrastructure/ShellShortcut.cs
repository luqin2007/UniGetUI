using System.Text;

namespace UniGetUI.Extensions.CustomChannels.Infrastructure;

/// <summary>
/// 纯托管写入 Windows 快捷方式（.lnk，MS-SHLLINK）。在 NativeAOT 进程里创建快捷方式
/// 需要 IShellLink COM，这里改为直接写文件，避免引入 COM 互操作与反射。
/// </summary>
public static class ShellShortcut
{
    private const uint LinkFlags =
        0x00000002 | // HasLinkInfo
        0x00000004 | // HasName
        0x00000008 | // HasRelativePath
        0x00000010 | // HasWorkingDir
        0x00000040 | // HasIconLocation
        0x00000080; // IsUnicode

    private const int LinkInfoHeaderSize = 0x24;
    private const int MaxStringChars = 259;

    /// <summary>
    /// 创建（或覆盖）一个指向本地文件的快捷方式。目标路径可以包含非 ASCII 字符。
    /// </summary>
    /// <returns>写入成功时为 true；目录不可写等情况返回 false。</returns>
    public static bool TryCreate(
        string shortcutPath,
        string targetPath,
        string? workingDirectory,
        string? description,
        string? iconPath = null)
    {
        try
        {
            var fullShortcut = Path.GetFullPath(shortcutPath);
            var directory = Path.GetDirectoryName(fullShortcut);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(fullShortcut, Build(
                Path.GetFullPath(targetPath),
                workingDirectory,
                description,
                iconPath ?? targetPath));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>生成 .lnk 文件内容（公开以便单元测试校验结构）。</summary>
    public static byte[] Build(
        string targetPath,
        string? workingDirectory,
        string? description,
        string? iconLocation)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);

        // ShellLinkHeader（76 字节）
        writer.Write(0x0000004Cu);
        writer.Write(new byte[]
        {
            0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46,
        });
        writer.Write(LinkFlags);
        writer.Write(0u); // FileAttributes
        writer.Write(0L); // CreationTime
        writer.Write(0L); // AccessTime
        writer.Write(0L); // WriteTime
        writer.Write(0u); // FileSize
        writer.Write(0); // IconIndex
        writer.Write(1u); // ShowCommand = SW_SHOWNORMAL
        writer.Write((ushort)0); // HotKey
        writer.Write((ushort)0); // Reserved1
        writer.Write(0u); // Reserved2
        writer.Write(0u); // Reserved3

        writer.Write(BuildLinkInfo(targetPath));

        // StringData：按 HasName、HasRelativePath、HasWorkingDir、HasIconLocation 的顺序
        WriteStringData(writer, description);
        WriteStringData(writer, targetPath);
        WriteStringData(writer, workingDirectory);
        WriteStringData(writer, iconLocation);

        // ExtraData 终止块
        writer.Write(0u);
        writer.Flush();

        return stream.ToArray();
    }

    /// <summary>
    /// LinkInfo：优先提供 Unicode 路径（中文等非 ASCII 路径不会因 ANSI 编码丢失），
    /// 纯 ASCII 路径同时写入 ANSI 副本。
    /// </summary>
    private static byte[] BuildLinkInfo(string targetPath)
    {
        var ansiPath = ToAnsiOrEmpty(targetPath);
        var ansiBytes = Encoding.ASCII.GetBytes(ansiPath);
        var unicodeBytes = Encoding.Unicode.GetBytes(targetPath);

        var volumeId = BuildVolumeId();

        int volumeIdOffset = LinkInfoHeaderSize;
        int localBasePathOffset = volumeIdOffset + volumeId.Length;
        int commonPathSuffixOffset = localBasePathOffset + ansiBytes.Length + 1;
        int localBasePathUnicodeOffset = commonPathSuffixOffset + 1;
        int commonPathSuffixUnicodeOffset = localBasePathUnicodeOffset + unicodeBytes.Length + 2;
        int size = commonPathSuffixUnicodeOffset + 2;

        using var stream = new MemoryStream(size);
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);

        writer.Write(size);
        writer.Write(LinkInfoHeaderSize);
        writer.Write(1); // VolumeIDAndLocalBasePath
        writer.Write(volumeIdOffset);
        writer.Write(localBasePathOffset);
        writer.Write(0); // 没有网络路径
        writer.Write(commonPathSuffixOffset);
        writer.Write(localBasePathUnicodeOffset);
        writer.Write(commonPathSuffixUnicodeOffset);

        writer.Write(volumeId);
        writer.Write(ansiBytes);
        writer.Write((byte)0);
        writer.Write((byte)0); // CommonPathSuffix = ""
        writer.Write(unicodeBytes);
        writer.Write((ushort)0); // LocalBasePathUnicode 终止符
        writer.Write((ushort)0); // CommonPathSuffixUnicode = ""
        writer.Flush();

        return stream.ToArray();
    }

    private static byte[] BuildVolumeId()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);

        writer.Write(17); // VolumeIDSize
        writer.Write(3); // DRIVE_FIXED
        writer.Write(0u); // DriveSerialNumber
        writer.Write(0x10); // VolumeLabelOffset
        writer.Write((byte)0); // 空卷标（ANSI）
        writer.Flush();

        return stream.ToArray();
    }

    private static void WriteStringData(BinaryWriter writer, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.Write((ushort)0);
            return;
        }

        var text = value.Length > MaxStringChars ? value[..MaxStringChars] : value;
        writer.Write((ushort)text.Length);
        writer.Write(Encoding.Unicode.GetBytes(text));
    }

    private static string ToAnsiOrEmpty(string value) =>
        value.All(static c => c < 128) ? value : string.Empty;
}
