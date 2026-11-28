using System.Text;
using FluentAssertions;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Packages;

namespace UniGetUI.Extensions.CustomChannels.Tests;

public sealed class InstallKindTests : IDisposable
{
    private readonly string _root;

    public InstallKindTests()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "test-artifacts", "install-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(_root);
    }

    [Theory]
    [InlineData("神金版[电脑版]v1.4.0.exe")]
    [InlineData("App-v1.2.3.exe")]
    public void 单文件应用应接受普通exe(string fileName)
    {
        PackageDetector.Validate(InstallKind.Exe, InstallKind.Exe, fileName).Should().BeNull();
    }

    [Fact]
    public void 单文件应用应提示改用安装程序模式()
    {
        var message = PackageDetector.Validate(InstallKind.Exe, InstallKind.Exe, "App-2.0.1-x64-setup.exe");

        message.Should().NotBeNull();
        message.Should().Contain("Msi/Exe 安装程序");
    }

    [Fact]
    public void 安装程序模式应接受exe安装包与msi()
    {
        PackageDetector.Validate(InstallKind.Msi, InstallKind.Exe, "App-Setup.exe").Should().BeNull();
        PackageDetector.Validate(InstallKind.Msi, InstallKind.Msi, "App.msi").Should().BeNull();
    }

    [Fact]
    public void 安装方式与包类型冲突时应给出提示()
    {
        PackageDetector.Validate(InstallKind.Msi, InstallKind.ZipOverwrite, "App.zip")
            .Should().NotBeNull();
        PackageDetector.Validate(InstallKind.Exe, InstallKind.ZipOverwrite, "App.zip")
            .Should().NotBeNull();
        PackageDetector.Validate(InstallKind.Exe, InstallKind.Msi, "App.msi")
            .Should().Contain("Msi/Exe 安装程序");
    }

    [Fact]
    public void 历史单文件类型应与单文件应用等价()
    {
        PackageDetector.Validate(InstallKind.SingleFile, InstallKind.Exe, "App.exe").Should().BeNull();
    }

    [Fact]
    public void 单文件应用应能定位带版本号的可执行文件()
    {
        var exe = Path.Combine(_root, "神金版[电脑版]v1.4.0.exe");
        File.WriteAllText(exe, "stub");

        var target = CreateTarget(InstallKind.Exe);

        ExecutableLocator.Resolve(target).Should().Be(exe);
    }

    [Fact]
    public void 存在多个exe时应忽略卸载程序()
    {
        File.WriteAllText(Path.Combine(_root, "unins000.exe"), "stub");
        var app = Path.Combine(_root, "App-v2.0.0.exe");
        File.WriteAllText(app, "stub");

        var target = CreateTarget(InstallKind.Exe);

        ExecutableLocator.Resolve(target).Should().Be(app);
    }

    [Fact]
    public void 安装程序模式未填写可执行文件时不应猜测()
    {
        File.WriteAllText(Path.Combine(_root, "App.exe"), "stub");

        ExecutableLocator.Resolve(CreateTarget(InstallKind.Msi)).Should().BeNull();
    }

    [Fact]
    public void 快捷方式应写入Unicode目标路径()
    {
        const string target = @"D:\Games\PVZ\神金版\神金版[电脑版]v1.4.0.exe";

        var bytes = ShellShortcut.Build(target, @"D:\Games\PVZ\神金版", "PVZ 神金版", target);

        BitConverter.ToInt32(bytes, 0).Should().Be(0x4C);
        // LinkInfoHeaderSize 位于固定头之后，为 0x24 时才带 Unicode 路径偏移
        BitConverter.ToInt32(bytes, 0x4C + 4).Should().Be(0x24);
        // LinkInfoSize 必须正好指向 StringData 起点，否则偏移计算有误
        int linkInfoSize = BitConverter.ToInt32(bytes, 0x4C);
        BitConverter.ToUInt16(bytes, 0x4C + linkInfoSize).Should().Be((ushort)"PVZ 神金版".Length);
        bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(target)).Should().BeGreaterThan(0);
    }

    private TargetConfig CreateTarget(InstallKind kind) => new()
    {
        Id = "test-app",
        DisplayName = "测试应用",
        Install = new InstallConfig
        {
            Kind = kind,
            RootPath = _root,
        },
    };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
