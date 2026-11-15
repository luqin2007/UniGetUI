using System.Reflection;
using UniGetUI.Extensions.CustomChannels.Sources;
using FluentAssertions;

namespace UniGetUI.Extensions.CustomChannels.Tests;

/// <summary>
/// 夸克网盘 pathPattern 解析：目录前缀（支持通配符）与文件名模式分离，
/// 目录前缀用于在分享内逐级进入子目录。
/// </summary>
public sealed class QuarkPathPatternTests
{
    private static (string FilePattern, IReadOnlyList<string> Dirs) Parse(string? pathPattern)
    {
        var type = typeof(QuarkPanSource);
        var file = (string?)type
            .GetMethod("ExtractFileNamePattern", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [pathPattern]);
        var dirs = (IReadOnlyList<string>)type
            .GetMethod("ExtractDirectorySegments", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [pathPattern])!;
        return (file ?? "*", dirs);
    }

    [Fact]
    public void 空模式_匹配根目录全部文件()
    {
        var (file, dirs) = Parse(null);
        file.Should().Be("*");
        dirs.Should().BeEmpty();
    }

    [Fact]
    public void 纯文件名_留在根目录()
    {
        var (file, dirs) = Parse("App-*.zip");
        file.Should().Be("App-*.zip");
        dirs.Should().BeEmpty();
    }

    [Fact]
    public void 单层目录_取目录段与文件名()
    {
        var (file, dirs) = Parse("releases/App-*.zip");
        file.Should().Be("App-*.zip");
        dirs.Should().BeEquivalentTo(["releases"], o => o.WithStrictOrdering());
    }

    [Fact]
    public void 多层目录含通配符_逐段保留()
    {
        var (file, dirs) = Parse("MRS助手/*26*/MRS助手_*.zip");
        file.Should().Be("MRS助手_*.zip");
        dirs.Should().BeEquivalentTo(["MRS助手", "*26*"], o => o.WithStrictOrdering());
    }

    [Fact]
    public void 反斜杠与首部斜杠_归一化处理()
    {
        var (file, dirs) = Parse(@"\a\b\c.zip");
        file.Should().Be("c.zip");
        dirs.Should().BeEquivalentTo(["a", "b"], o => o.WithStrictOrdering());
    }
}
