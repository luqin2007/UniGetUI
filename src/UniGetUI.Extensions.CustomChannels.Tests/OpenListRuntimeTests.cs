using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.OpenList;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace UniGetUI.Extensions.CustomChannels.Tests;

public sealed class OpenListRuntimeTests
{
    private readonly AppPaths _paths;

    public OpenListRuntimeTests()
    {
        _paths = new AppPaths(Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N")));
        _paths.EnsureCreated();
    }

    [Fact]
    public void 启动时为非本地模式也应能在切换后创建本地实例()
    {
        var config = new OpenListConfig { Mode = OpenListMode.Disabled };
        using var runtime = CreateRuntime(() => config);

        runtime.Host.Should().BeNull();

        config.Mode = OpenListMode.Local;

        runtime.Host.Should().NotBeNull();
        runtime.IsLocalMode.Should().BeTrue();
    }

    [Fact]
    public void 配置对象被替换后应使用新的配置实例()
    {
        var root = new UpdaterConfig
        {
            OpenList = new OpenListConfig { Mode = OpenListMode.Local, LocalPort = 5244 },
        };
        using var runtime = CreateRuntime(() => root.OpenList);

        var first = runtime.Host;

        root.OpenList = new OpenListConfig { Mode = OpenListMode.Local, LocalPort = 5245 };
        var second = runtime.Host;

        second.Should().NotBeNull();
        second.Should().NotBeSameAs(first);
    }

    [Fact]
    public void 配置失效后应清除本地实例状态()
    {
        var root = new UpdaterConfig
        {
            OpenList = new OpenListConfig { Mode = OpenListMode.Local },
        };
        using var runtime = CreateRuntime(() => root.OpenList);

        runtime.Host.Should().NotBeNull();

        root.OpenList.Mode = OpenListMode.Disabled;
        runtime.Invalidate();

        runtime.Host.Should().BeNull();
        runtime.IsLocalMode.Should().BeFalse();
    }

    private OpenListRuntime CreateRuntime(Func<OpenListConfig> accessor) =>
        new(_paths, accessor, new DownloadConfig(), NullLogger<OpenListRuntime>.Instance);
}
