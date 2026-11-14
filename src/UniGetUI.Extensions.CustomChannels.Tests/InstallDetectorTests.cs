using FluentAssertions;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Detection;

namespace UniGetUI.Extensions.CustomChannels.Tests;

public sealed class InstallDetectorTests
{
    [Fact]
    public void MSI资产应识别为安装包()
    {
        var result = InstallDetector.FromAssetNames(["App-1.2.3-x64.msi", "App-1.2.3-x64.zip"]);

        result.Kind.Should().Be(InstallKind.Msi);
    }

    [Fact]
    public void 含Setup的EXE应识别为安装程序()
    {
        var result = InstallDetector.FromAssetNames(["App-Setup-1.2.3.exe", "App-win64.zip"]);

        result.Kind.Should().Be(InstallKind.Exe);
    }

    [Fact]
    public void 仅有单个EXE时应识别为单文件替换()
    {
        var result = InstallDetector.FromAssetNames(["App.exe"]);

        result.Kind.Should().Be(InstallKind.SingleFile);
        result.TargetFileName.Should().Be("App.exe");
        result.ExePath.Should().Be("App.exe");
    }

    [Fact]
    public void 无Setup字样但存在压缩包时EXE视为安装程序()
    {
        var result = InstallDetector.FromAssetNames(["App.exe", "App-portable-win64.zip"]);

        result.Kind.Should().Be(InstallKind.Exe);
    }

    [Fact]
    public void 只有压缩包时应识别为解压覆盖()
    {
        var result = InstallDetector.FromAssetNames(["App-1.0.0-win64.zip"]);

        result.Kind.Should().Be(InstallKind.ZipOverwrite);
    }

    [Fact]
    public void 源码包与符号文件应被忽略()
    {
        var result = InstallDetector.FromAssetNames(
            ["App-1.0.0-source.zip", "App-1.0.0-symbols.zip", "App-1.0.0-x64.zip"]);

        result.Kind.Should().Be(InstallKind.ZipOverwrite);
        result.Reason.Should().Contain("x64.zip");
    }

    [Theory]
    [InlineData("https://example.com/download/App-1.0.0.zip", InstallKind.ZipOverwrite)]
    [InlineData("https://example.com/download/App-Setup.exe", InstallKind.Exe)]
    [InlineData("https://example.com/download/App.msi", InstallKind.Msi)]
    public void 直链应按扩展名识别(string url, InstallKind expected)
    {
        InstallDetector.FromUrl(url).Kind.Should().Be(expected);
    }

    [Theory]
    [InlineData("microsoft/vscode", "vscode")]
    [InlineData("https://gitlab.com/gitlab-org/cli.git", "cli")]
    [InlineData("owner/My_App.v2", "my_app.v2")]
    public void 仓库地址应能规范化为标识(string input, string expected)
    {
        var name = input.Contains('/') ? input.Split('/')[^1] : input;
        InstallDetector.ToSlug(name).Should().Be(expected);
    }
}
