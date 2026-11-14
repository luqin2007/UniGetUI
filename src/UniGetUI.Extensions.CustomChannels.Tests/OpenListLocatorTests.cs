using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.OpenList;
using FluentAssertions;

namespace UniGetUI.Extensions.CustomChannels.Tests;

public sealed class OpenListLocatorTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;

    public OpenListLocatorTests()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
    }

    [Fact]
    public void 配置路径应优先于PATH与常见安装目录()
    {
        var configured = CreateBinary("custom", "openlist.exe");
        var pathBinary = CreateBinary("path-dir", "openlist.exe");
        var commonBinary = CreateBinary("common-dir", "openlist.exe");

        var result = OpenListLocator.Locate(
            new OpenListConfig { BinaryPath = configured },
            _paths,
            Path.GetDirectoryName(pathBinary),
            [Path.GetDirectoryName(commonBinary)!]);

        result.Should().NotBeEmpty();
        result[0].Path.Should().Be(configured);
        result[0].Source.Should().Be(OpenListBinarySource.Configured);
        result.Should().Contain(item => item.Path == pathBinary && item.Source == OpenListBinarySource.Path);
        result.Should().Contain(item => item.Path == commonBinary && item.Source == OpenListBinarySource.CommonInstall);
    }

    [Fact]
    public void 未配置时应先使用PATH中的版本()
    {
        var pathBinary = CreateBinary("path-dir", "openlist.exe");
        var commonBinary = CreateBinary("common-dir", "openlist.exe");

        var result = OpenListLocator.Locate(
            new OpenListConfig(),
            _paths,
            Path.GetDirectoryName(pathBinary),
            [Path.GetDirectoryName(commonBinary)!]);

        result[0].Path.Should().Be(pathBinary);
        result[0].Source.Should().Be(OpenListBinarySource.Path);
    }

    [Fact]
    public void 只有托管副本存在时应回落到程序目录()
    {
        var managed = CreateBinary("tools", "openlist", "openlist.exe");

        var result = OpenListLocator.Locate(new OpenListConfig(), _paths, null, []);

        result.Should().ContainSingle();
        result[0].Path.Should().Be(managed);
        result[0].Source.Should().Be(OpenListBinarySource.Bundled);
    }

    [Fact]
    public void 相对配置路径应按程序目录解析()
    {
        var managed = CreateBinary("tools", "openlist", "openlist.exe");
        var config = new OpenListConfig
        {
            BinaryPath = Path.Combine("tools", "openlist", "openlist.exe"),
        };

        var result = OpenListLocator.Locate(config, _paths, null, []);

        result[0].Path.Should().Be(managed);
        result[0].Source.Should().Be(OpenListBinarySource.Configured);
    }

    [Fact]
    public void 配置路径不存在时应忽略并继续查找()
    {
        CreateBinary("tools", "openlist", "openlist.exe");
        var config = new OpenListConfig { BinaryPath = @"Z:\definitely-missing\openlist.exe" };

        var result = OpenListLocator.Locate(config, _paths, null, []);

        result.Should().ContainSingle();
        result[0].Source.Should().Be(OpenListBinarySource.Bundled);
    }

    [Fact]
    public void 同一路径不应重复出现()
    {
        var binary = CreateBinary("shared", "openlist.exe");
        var directory = Path.GetDirectoryName(binary)!;

        var result = OpenListLocator.Locate(new OpenListConfig(), _paths, directory, [directory]);

        result.Should().ContainSingle();
        result[0].Source.Should().Be(OpenListBinarySource.Path);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试目录清理失败不影响断言结果
        }
    }

    private string CreateBinary(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
