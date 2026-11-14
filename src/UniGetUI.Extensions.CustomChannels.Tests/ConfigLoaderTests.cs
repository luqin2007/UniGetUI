using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using FluentAssertions;

namespace UniGetUI.Extensions.CustomChannels.Tests;

public sealed class ConfigLoaderTests : IDisposable
{
    private readonly string _root;

    public ConfigLoaderTests()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "test-artifacts", "config-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void 应能解析示例配置的全部渠道与字段()
    {
        var path = Path.Combine(_root, "targets.json");
        ConfigLoader.Save(path, ConfigLoader.CreateSampleConfig());

        var result = ConfigLoader.Load(path);

        result.Config.Targets.Should().HaveCount(2);
        result.Config.Targets[0].Sources.Should().Contain(s => s.Type == SourceType.GithubRelease);
        result.Config.Targets[1].Sources.Should().Contain(s => s.Type == SourceType.QuarkPan);
        result.Config.Transfer.CleanupPolicy.Should().Be(TransferCleanupPolicy.DeleteAfterSuccess);
    }

    [Fact]
    public void 应支持小驼峰枚举字符串与注释()
    {
        var path = Path.Combine(_root, "targets.json");
        File.WriteAllText(path, """
        {
          // 注释允许存在
          "download": { "concurrency": 3 },
          "targets": [
            {
              "id": "demo",
              "displayName": "Demo",
              "install": { "kind": "singleFile", "rootPath": "C:\\Apps\\Demo", "targetFileName": "Demo.exe" },
              "version": { "mode": "versionFile", "path": "version.txt" },
              "sources": [
                { "type": "directUrl", "url": "https://example.com/Demo.exe", "version": "1.2.3" }
              ]
            }
          ]
        }
        """);

        var result = ConfigLoader.Load(path);

        result.Config.Download.Concurrency.Should().Be(3);
        result.Config.Targets[0].Install.Kind.Should().Be(InstallKind.SingleFile);
        result.Config.Targets[0].Sources[0].Type.Should().Be(SourceType.DirectUrl);
    }

    [Fact]
    public void 非法正则与缺失字段应抛出配置异常()
    {
        var path = Path.Combine(_root, "bad.json");
        File.WriteAllText(path, """
        {
          "targets": [
            {
              "id": "",
              "install": { "kind": "zipOverwrite", "rootPath": "relative/path" },
              "version": { "mode": "versionFile", "path": "v.txt", "pattern": "([" },
              "sources": [ { "type": "githubRelease", "repo": "invalid" } ]
            }
          ]
        }
        """);

        var act = () => ConfigLoader.Load(path);

        act.Should().Throw<ConfigException>().WithMessage("*校验未通过*");
    }

    [Fact]
    public void 文件不存在时给出明确错误()
    {
        var act = () => ConfigLoader.Load(Path.Combine(_root, "missing.json"));

        act.Should().Throw<ConfigException>().WithMessage("*未找到配置文件*");
    }

    [Fact]
    public void 配置缺失时应在程序目录生成示例()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();

        var result = ConfigLoader.LoadOrCreateSample(paths);

        result.CreatedFromSample.Should().BeTrue();
        File.Exists(Path.Combine(paths.Config, ConfigLoader.DefaultFileName)).Should().BeTrue();
        result.Config.Targets.Should().NotBeEmpty();
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
        }
    }
}
