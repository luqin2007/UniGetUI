using UniGetUI.Extensions.CustomChannels.Configuration;

namespace UniGetUI.Extensions.CustomChannels.Packages;

public interface IPackageInstallerFactory
{
    IPackageInstaller Get(InstallKind kind);
}

public sealed class PackageInstallerFactory : IPackageInstallerFactory
{
    private readonly Dictionary<InstallKind, IPackageInstaller> _installers;

    public PackageInstallerFactory(
        ArchiveExtractor extractor,
        ElevationClient elevation,
        InstalledFileStore installedFiles)
    {
        var singleFile = new SingleFileInstaller(elevation, installedFiles);
        _installers = new Dictionary<InstallKind, IPackageInstaller>
        {
            [InstallKind.ZipOverwrite] = new ZipPackageInstaller(extractor, elevation),
            // 历史配置里的 SingleFile 与单文件应用行为一致
            [InstallKind.SingleFile] = singleFile,
            [InstallKind.Msi] = new MsiInstaller(elevation),
            [InstallKind.Exe] = singleFile,
        };
    }

    public IPackageInstaller Get(InstallKind kind) =>
        _installers.TryGetValue(kind, out var installer)
            ? installer
            : throw new NotSupportedException($"未支持的安装类型：{kind}");
}
