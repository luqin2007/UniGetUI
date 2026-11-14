using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Update;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace UniGetUI.Extensions.CustomChannels.Tests;

public sealed class VersionRecordTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly VersionRecordStore _store;

    public VersionRecordTests()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        _store = new VersionRecordStore(_paths, NullLogger<VersionRecordStore>.Instance);
    }

    [Fact]
    public void 版本记录应写入状态目录并可读回()
    {
        var saved = _store.Save("demo-app", "1.5.2", "github:Justsenger/ExHyperV", "安装成功");

        saved.Version.Should().Be("1.5.2");
        File.Exists(_store.GetRecordPath("demo-app")).Should().BeTrue();
        _store.GetRecordPath("demo-app").Should().Contain("state");

        var record = _store.Read("demo-app");
        record.Should().NotBeNull();
        record!.Version.Should().Be("1.5.2");
        record.SourceId.Should().Be("github:Justsenger/ExHyperV");
        record.Note.Should().Be("安装成功");
    }

    [Fact]
    public void 未记录过的目标读取为空()
    {
        _store.Read("missing").Should().BeNull();
    }

    [Fact]
    public void 目标标识中的非法字符应被替换()
    {
        var path = _store.GetRecordPath("a/b:c*d");
        Path.GetFileName(path).Should().Be("a_b_c_d.json");
    }

    [Fact]
    public void 删除记录后读取为空()
    {
        _store.Save("demo", "1.0.0", null);
        _store.Delete("demo").Should().BeTrue();
        _store.Read("demo").Should().BeNull();
    }

    [Fact]
    public void 记录模式下无记录时返回空()
    {
        var resolver = CreateResolver();
        var target = Target(LocalVersionMode.Recorded);

        resolver.GetLocalVersion(target).Should().BeNull();
    }

    [Fact]
    public void 记录模式读取更新流程写入的版本()
    {
        _store.Save("demo", "1.5.2", "github:x");
        var resolver = CreateResolver();

        resolver.GetLocalVersion(Target(LocalVersionMode.Recorded)).Should().Be("1.5.2");
    }

    [Fact]
    public void 命令模式应从输出中解析版本()
    {
        var resolver = CreateResolver();
        var target = Target(LocalVersionMode.Command);
        target.Version.Command = "echo ExHyperV v2.3.4";

        resolver.GetLocalVersion(target).Should().Be("2.3.4");
    }

    [Fact]
    public void 命令模式应支持自定义正则()
    {
        var resolver = CreateResolver();
        var target = Target(LocalVersionMode.Command);
        target.Version.Command = "echo build=7.8.9";
        target.Version.Pattern = @"build=(\d+\.\d+\.\d+)";

        resolver.GetLocalVersion(target).Should().Be("7.8.9");
    }

    [Fact]
    public void 命令为空时返回空()
    {
        var resolver = CreateResolver();
        var target = Target(LocalVersionMode.Command);

        resolver.GetLocalVersion(target).Should().BeNull();
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

    private VersionResolver CreateResolver() =>
        new(_store, NullLogger<VersionResolver>.Instance);

    private TargetConfig Target(LocalVersionMode mode) => new()
    {
        Id = "demo",
        DisplayName = "Demo",
        Install = new InstallConfig { RootPath = _root },
        Version = new LocalVersionConfig { Mode = mode },
    };
}
