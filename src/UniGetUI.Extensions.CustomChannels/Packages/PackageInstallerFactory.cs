using UniGetUI.Extensions.CustomChannels.Configuration;

namespace UniGetUI.Extensions.CustomChannels.Packages;

public interface IPackageInstallerFactory
{
    IPackageInstaller Get(InstallKind kind);
}

public sealed class PackageInstallerFactory : IPackageInstallerFactory
{
    private readonly Dictionary<InstallKind, IPackageInstaller> _installers;

    public PackageInstallerFactory(ArchiveExtractor extractor, ElevationClient elevation)
    {
        _installers = new Dictionary<InstallKind, IPackageInstaller>
        {
            [InstallKind.ZipOverwrite] = new ZipPackageInstaller(extractor, elevation),
            [InstallKind.SingleFile] = new SingleFileInstaller(elevation),
            [InstallKind.Msi] = new MsiInstaller(elevation),
            [InstallKind.Exe] = new ExeInstaller(elevation),
        };
    }

    public IPackageInstaller Get(InstallKind kind) =>
        _installers.TryGetValue(kind, out var installer)
            ? installer
            : throw new NotSupportedException($"未支持的安装类型：{kind}");
}
