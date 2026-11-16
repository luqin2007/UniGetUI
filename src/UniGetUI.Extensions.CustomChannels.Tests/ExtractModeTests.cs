using FluentAssertions;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Packages;
using Xunit;

namespace UniGetUI.Extensions.CustomChannels.Tests;

/// <summary>解压后处理方式：直接解压 / 智能剥离单顶层目录 / 自定义子目录。</summary>
public sealed class ExtractModeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cc-extract-mode-test-" + Guid.NewGuid().ToString("N"));

    public string ExtractDir
    {
        get
        {
            var dir = Path.Combine(_root, "extract");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    /// <summary>模拟解压结果：zip 内只有 "MRS助手_26.11.14.1/" 一个顶层目录。</summary>
    private string ExtractSingleDir()
    {
        var dir = ExtractDir;
        Directory.CreateDirectory(Path.Combine(dir, "MRS助手_26.11.14.1"));
        File.WriteAllText(Path.Combine(dir, "MRS助手_26.11.14.1", "MRS助手.exe"), "MZ");
        return dir;
    }

    /// <summary>模拟解压结果：压缩包内容直接铺在顶层。</summary>
    private string ExtractFlat()
    {
        var dir = ExtractDir;
        File.WriteAllText(Path.Combine(dir, "MRS助手.exe"), "MZ");
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "hi");
        return dir;
    }

    [Fact]
    public void 智能解压剥离单个顶层目录()
    {
        var dir = ExtractSingleDir();

        var contentRoot = ArchiveExtractor.ResolveContentRoot(dir, new ArchiveConfig());

        Path.GetFileName(contentRoot).Should().Be("MRS助手_26.11.14.1");
    }

    [Fact]
    public void 智能解压对多顶层条目不剥离()
    {
        var dir = ExtractFlat();

        ArchiveExtractor.ResolveContentRoot(dir, new ArchiveConfig()).Should().Be(dir);
    }

    [Fact]
    public void 直接解压保持原样()
    {
        var dir = ExtractSingleDir();

        ArchiveExtractor.ResolveContentRoot(
            dir, new ArchiveConfig { ExtractMode = ExtractMode.Direct }).Should().Be(dir);
    }

    [Fact]
    public void 自定义路径以压缩包子目录为根()
    {
        var dir = ExtractSingleDir();

        var contentRoot = ArchiveExtractor.ResolveContentRoot(
            dir, new ArchiveConfig { ExtractMode = ExtractMode.Custom, ExtractSubPath = "MRS助手_26.11.14.1" });

        Path.GetFileName(contentRoot).Should().Be("MRS助手_26.11.14.1");
    }

    [Fact]
    public void 自定义路径子目录不存在时报清晰错误()
    {
        var dir = ExtractFlat();

        var act = () => ArchiveExtractor.ResolveContentRoot(
            dir, new ArchiveConfig { ExtractMode = ExtractMode.Custom, ExtractSubPath = "missing" });

        act.Should().Throw<DirectoryNotFoundException>()
            .WithMessage("*压缩包内不存在子目录*");
    }

    [Fact]
    public void 自定义路径未填写时报清晰错误()
    {
        var dir = ExtractFlat();

        var act = () => ArchiveExtractor.ResolveContentRoot(
            dir, new ArchiveConfig { ExtractMode = ExtractMode.Custom });

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*未填写压缩包子目录*");
    }

    [Fact]
    public void 旧配置兼容_仅ExtractSubPath时按自定义路径处理()
    {
        // 旧版本 JSON 没有 extractMode 字段，反序列化后 Mode 为默认 Smart，
        // 但 ExtractSubPath 非空必须继续按自定义路径生效
        var dir = ExtractSingleDir();

        var contentRoot = ArchiveExtractor.ResolveContentRoot(
            dir, new ArchiveConfig { ExtractSubPath = "MRS助手_26.11.14.1" });

        Path.GetFileName(contentRoot).Should().Be("MRS助手_26.11.14.1");
    }

    [Fact]
    public void 默认配置即智能解压()
    {
        new ArchiveConfig().ExtractMode.Should().Be(ExtractMode.Smart);
    }
}
