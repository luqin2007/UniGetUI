using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Models;
using FluentAssertions;

namespace UniGetUI.Extensions.CustomChannels.Tests;

public sealed class SampleConfigTests
{
    [Fact]
    public void 仓库示例配置应能通过解析与校验()
    {
        var path = FindSampleConfig();
        path.Should().NotBeNull("示例配置应随仓库提供，用于校验配置格式文档");

        var result = ConfigLoader.Load(path!);

        result.Config.Targets.Should().NotBeEmpty();
        result.Config.Targets.Select(t => t.Id).Should().OnlyHaveUniqueItems();

        // 示例需要覆盖四类安装方式与全部渠道类型
        result.Config.Targets.Select(t => t.Install.Kind).Distinct()
            .Should().BeEquivalentTo(
                [InstallKind.ZipOverwrite, InstallKind.SingleFile, InstallKind.Msi, InstallKind.Exe]);

        result.Config.Targets.SelectMany(t => t.Sources).Select(s => s.Type).Distinct()
            .Should().BeEquivalentTo(
            [
                SourceType.GithubRelease,
                SourceType.GitlabRelease,
                SourceType.OpenList,
                SourceType.BaiduPan,
                SourceType.QuarkPan,
                SourceType.DirectUrl,
            ]);
    }

    private static string? FindSampleConfig()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            foreach (var relative in new[]
                     {
                         Path.Combine("config", "custom-channels", "targets.sample.json"),
                         Path.Combine("config", "targets.sample.json"),
                     })
            {
                var candidate = Path.Combine(directory.FullName, relative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            directory = directory.Parent;
        }

        return null;
    }
}
