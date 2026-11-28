namespace UniGetUI.Extensions.CustomChannels.Configuration;

/// <summary>
/// 安装方式的展示名称与分类。枚举名保持稳定以兼容既有配置，
/// 界面统一通过这里取中文名称，避免出现裸枚举值。
/// </summary>
public static class InstallKinds
{
    /// <summary>编辑器中可选（并按下拉顺序排列）的安装方式。</summary>
    public static readonly IReadOnlyList<InstallKind> Editable =
    [
        InstallKind.ZipOverwrite,
        InstallKind.Msi,
        InstallKind.Exe,
    ];

    /// <summary>安装方式在界面上的名称。</summary>
    public static string Label(InstallKind kind) => kind switch
    {
        InstallKind.ZipOverwrite => "ZIP 解压覆盖",
        InstallKind.Msi => "Msi/Exe 安装程序",
        InstallKind.Exe or InstallKind.SingleFile => "EXE 单文件应用",
        _ => kind.ToString(),
    };

    /// <summary>下载的是安装包，需要定位安装目录下的主程序才能打开/读取版本。</summary>
    public static bool IsInstaller(InstallKind kind) => kind == InstallKind.Msi;

    /// <summary>下载的文件就是程序本体，安装即复制到安装目录。</summary>
    public static bool IsPortableSingleFile(InstallKind kind) =>
        kind is InstallKind.Exe or InstallKind.SingleFile;

    /// <summary>把历史枚举值归一到当前语义（SingleFile 与 Exe 行为一致）。</summary>
    public static InstallKind Normalize(InstallKind kind) =>
        kind == InstallKind.SingleFile ? InstallKind.Exe : kind;
}
