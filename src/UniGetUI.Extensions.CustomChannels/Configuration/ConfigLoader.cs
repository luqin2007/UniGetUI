using System.Text.Json;
using UniGetUI.Extensions.CustomChannels.Infrastructure;

namespace UniGetUI.Extensions.CustomChannels.Configuration;

public sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class ConfigLoadResult(
    UpdaterConfig config,
    string path,
    List<string> warnings,
    bool created,
    bool loadFailed = false)
{
    public UpdaterConfig Config { get; } = config;

    public string Path { get; } = path;

    public List<string> Warnings { get; } = warnings;

    public bool CreatedFromSample { get; } = created;

    /// <summary>配置加载/校验失败时为 true，此时 Config 只是占位的空配置，不应覆盖已有状态。</summary>
    public bool LoadFailed { get; } = loadFailed;
}

public static class ConfigLoader
{
    public const string DefaultFileName = "targets.json";

    public static string DefaultPath(AppPaths paths) => Path.Combine(paths.Config, DefaultFileName);

    public static ConfigLoadResult Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException(
                $"未找到配置文件：{path}\r\n请将示例配置另存为该路径，或在界面中选择其他配置文件。");
        }

        var warnings = new List<string>();
        UpdaterConfig? config;

        try
        {
            using var stream = File.OpenRead(path);
            config = JsonSerializer.Deserialize(stream, AppJsonContext.Default.UpdaterConfig);
        }
        catch (JsonException ex)
        {
            var location = string.IsNullOrEmpty(ex.Path) ? "根节点" : ex.Path;
            throw new ConfigException($"配置文件解析失败，位置 {location}：{ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new ConfigException($"读取配置文件失败：{ex.Message}", ex);
        }

        if (config is null)
        {
            throw new ConfigException($"配置文件内容为空：{path}");
        }

        config.Download.Concurrency = Math.Clamp(config.Download.Concurrency, 1, 8);
        if (config.Download.Concurrency != 4)
        {
            warnings.Add($"下载并发已规范化为 {config.Download.Concurrency}（允许范围 1-8）。");
        }

        foreach (var source in config.Targets.SelectMany(t => t.Sources))
        {
            source.Token = ResolveEnvironment(source.Token);

            if (string.IsNullOrWhiteSpace(source.Token) && !string.IsNullOrWhiteSpace(source.TokenEnv))
            {
                source.Token = ResolveEnvironment($"%{source.TokenEnv}%");
                if (string.IsNullOrWhiteSpace(source.Token))
                {
                    warnings.Add($"环境变量 {source.TokenEnv} 未设置，相关渠道将以匿名方式访问。");
                    source.Token = null;
                }
            }
        }

        config.OpenList.Password = ResolveEnvironment(config.OpenList.Password);

        var issues = ConfigValidator.Validate(config);
        warnings.AddRange(issues.Where(i => !i.IsError).Select(i => i.ToString()));

        var errors = issues.Where(i => i.IsError).ToList();
        if (errors.Count > 0)
        {
            throw new ConfigException(
                "配置校验未通过：\r\n" + string.Join("\r\n", errors.Select(e => " - " + e)));
        }

        return new ConfigLoadResult(config, Path.GetFullPath(path), warnings, created: false);
    }

    /// <summary>配置文件不存在时生成一份可运行的示例配置。</summary>
    public static ConfigLoadResult LoadOrCreateSample(AppPaths paths)
    {
        paths.EnsureCreated();
        var path = DefaultPath(paths);
        if (File.Exists(path))
        {
            return Load(path);
        }

        var config = CreateSampleConfig();
        JsonFileStore.Write(path, config, AppJsonContext.Default.UpdaterConfig);
        return new ConfigLoadResult(config, path, ["已生成示例配置，请按需修改后重新加载。"], created: true);
    }

    public static void Save(string path, UpdaterConfig config)
    {
        JsonFileStore.Write(path, config, AppJsonContext.Default.UpdaterConfig);
    }

    public static UpdaterConfig CreateSampleConfig() => new()
    {
        Download = new DownloadConfig
        {
            Concurrency = 4,
            KeepBackupCount = 1,
            ReleaseCacheMinutes = 5,
        },
        Transfer = new TransferConfig
        {
            Enabled = true,
            TransferPath = "/AppUpdater",
            CleanupPolicy = TransferCleanupPolicy.DeleteAfterSuccess,
        },
        OpenList = new OpenListConfig
        {
            Mode = OpenListMode.Disabled,
            BaseUrl = "http://127.0.0.1:5244",
            Username = "admin",
            LocalPort = 5244,
            RootPath = "/",
        },
        Targets =
        [
            new TargetConfig
            {
                Id = "demo-app",
                DisplayName = "Demo 应用",
                Install = new InstallConfig
                {
                    Kind = InstallKind.ZipOverwrite,
                    RootPath = @"D:\Apps\Demo",
                    ExePath = "Demo.exe",
                    LaunchAfterUpdate = true,
                    StopRunningProcess = ProcessStopMode.PromptUser,
                },
                Version = new LocalVersionConfig
                {
                    Mode = LocalVersionMode.FileVersion,
                    Path = "Demo.exe",
                },
                Archive = new ArchiveConfig
                {
                    Passwords = [],
                    PreserveExistingExtraFiles = true,
                    PromptForPassword = true,
                },
                Policy = new UpdatePolicyConfig { AllowDowngrade = false },
                Sources =
                [
                    new SourceConfig
                    {
                        Type = SourceType.GithubRelease,
                        Repo = "owner/demo",
                        AssetPattern = "*-win-x64.zip",
                        IncludePrerelease = false,
                        TokenEnv = "GITHUB_TOKEN",
                    },
                    new SourceConfig
                    {
                        Type = SourceType.GitlabRelease,
                        BaseUrl = "https://gitlab.com",
                        ProjectId = "group/demo",
                        AssetPattern = "*.zip",
                    },
                ],
            },
            new TargetConfig
            {
                Id = "demo-installer",
                DisplayName = "Demo 安装版",
                Install = new InstallConfig
                {
                    Kind = InstallKind.Msi,
                    RootPath = @"C:\Program Files\Demo",
                    ExePath = "Demo.exe",
                    RequiresElevation = true,
                    LaunchAfterUpdate = false,
                },
                Version = new LocalVersionConfig
                {
                    Mode = LocalVersionMode.FileVersion,
                    Path = "Demo.exe",
                },
                Policy = new UpdatePolicyConfig { AllowDowngrade = true },
                Sources =
                [
                    new SourceConfig
                    {
                        Type = SourceType.QuarkPan,
                        ShareUrl = "https://pan.quark.cn/s/xxxxxxxx",
                        Pwd = string.Empty,
                        PathPattern = "/releases/Demo-*.msi",
                        VersionPattern = @"v?(\d+\.\d+\.\d+)",
                        CleanupPolicy = TransferCleanupPolicy.DeleteAfterSuccess,
                    },
                    new SourceConfig
                    {
                        Type = SourceType.DirectUrl,
                        Url = "https://example.com/Demo-latest.msi",
                    },
                ],
            },
        ],
    };

    /// <summary>支持 %VAR% 形式的环境变量引用。</summary>
    public static string? ResolveEnvironment(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var resolved = Environment.ExpandEnvironmentVariables(value.Trim());
        return resolved.Contains('%') && resolved == value ? null : resolved;
    }
}
