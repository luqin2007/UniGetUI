using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Download;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Models;
using UniGetUI.Extensions.CustomChannels.OpenList;
using UniGetUI.Extensions.CustomChannels.Packages;
using UniGetUI.Extensions.CustomChannels.Pan;
using UniGetUI.Extensions.CustomChannels.Sources;
using UniGetUI.Extensions.CustomChannels.Update;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Services;

/// <summary>
/// 自定义渠道的服务门面：把配置、渠道、下载、安装与网盘/OpenList 依赖装配在一起，
/// 界面层只与这个类交互，避免在 UI 中重复拼装依赖。配置保存后调用 <see cref="Reload"/> 生效。
/// </summary>
public sealed class CustomChannelsService : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CustomChannelsService> _logger;
    private readonly SecretStore _secrets;
    private readonly OpenListRuntime _openList;
    private readonly PanSessionStore _panSessions;
    private readonly GitTokenStore _gitTokens;
    private readonly TransferManager _transfers;
    private readonly VersionRecordStore _versionRecords;
    private readonly InstalledFileStore _installedFiles;
    private readonly VersionCache _cache = new();
    private readonly ReadmeStore _readmeStore;

    private HttpClient _http;
    private UpdateSourceFactory _sourceFactory;
    private UpdateChecker _checker;
    private UpdateOrchestrator _orchestrator;
    private string? _configPath;

    public CustomChannelsService(AppPaths paths, ILoggerFactory loggerFactory)
    {
        Paths = paths;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CustomChannelsService>();
        paths.EnsureCreated();

        // 先加载配置，后续组件都依赖其中的下载/转存参数
        var loaded = LoadConfig();
        Config = loaded.Config;
        ConfigPath = loaded.Path;
        _configPath = loaded.Path;
        Warnings = loaded.Warnings;

        _secrets = new SecretStore(paths);
        _panSessions = new PanSessionStore(_secrets);
        _gitTokens = new GitTokenStore(_secrets);
        _transfers = new TransferManager(paths, Config.Transfer, loggerFactory.CreateLogger<TransferManager>());
        _versionRecords = new VersionRecordStore(paths, loggerFactory.CreateLogger<VersionRecordStore>());
        _installedFiles = new InstalledFileStore(paths, loggerFactory.CreateLogger<InstalledFileStore>());
        _readmeStore = new ReadmeStore(paths);

        _http = UpdaterHttp.Create(Config.Download);
        _openList = new OpenListRuntime(
            paths,
            () => Config.OpenList,
            Config.Download,
            loggerFactory.CreateLogger<OpenListRuntime>());

        (_sourceFactory, _checker, _orchestrator) = BuildPipeline();
    }

    public AppPaths Paths { get; }

    public string ConfigPath { get; private set; }

    public UpdaterConfig Config { get; private set; }

    public IReadOnlyList<string> Warnings { get; private set; }

    /// <summary>最近一次配置加载失败的原因；加载成功后为 null。界面据此提示用户。</summary>
    public string? LastLoadError { get; private set; }

    public PanSessionStore PanSessions => _panSessions;

    public GitTokenStore GitTokens => _gitTokens;

    public TransferManager Transfers => _transfers;

    public OpenListRuntime OpenList => _openList;

    public VersionRecordStore VersionRecords => _versionRecords;

    public UpdateSourceFactory Sources => _sourceFactory;

    /// <summary>读取配置；缺失时生成示例配置，保证页面首次打开可用。</summary>
    private ConfigLoadResult LoadConfig()
    {
        var path = _configPath ?? ConfigLoader.DefaultPath(Paths);
        try
        {
            // 已有配置文件时优先加载它（即使不是默认路径），避免保存与读取指向不同文件
            var result = File.Exists(path) ? ConfigLoader.Load(path) : ConfigLoader.LoadOrCreateSample(Paths);
            LastLoadError = null;
            return result;
        }
        catch (ConfigException ex)
        {
            // 关键：不要用示例配置覆盖用户数据。加载失败时退回空配置并标记失败，
            // 让 Reload 保留旧配置（否则界面会清空，且后续保存会用空配置覆盖磁盘）。
            _logger.LogError(ex, "自定义渠道配置加载失败");
            LastLoadError = ex.Message;
            return new ConfigLoadResult(
                new UpdaterConfig(),
                path,
                [ex.Message],
                created: false,
                loadFailed: true);
        }
    }

    private (UpdateSourceFactory, UpdateChecker, UpdateOrchestrator) BuildPipeline()
    {
        var factory = new UpdateSourceFactory(
            _http,
            Config,
            Paths,
            _panSessions,
            _transfers,
            _openList,
            _gitTokens,
            _loggerFactory);

        var resolver = new VersionResolver(_versionRecords, _loggerFactory.CreateLogger<VersionResolver>());
        var checker = new UpdateChecker(
            factory,
            resolver,
            _cache,
            Config.Download,
            _loggerFactory.CreateLogger<UpdateChecker>(),
            _readmeStore);

        var downloader = new SegmentedDownloader(
            Config.Download,
            _loggerFactory.CreateLogger<SegmentedDownloader>());
        var backup = new BackupManager(Paths, _loggerFactory.CreateLogger<BackupManager>());
        var installers = new PackageInstallerFactory(
            new ArchiveExtractor(),
            new ElevationClient(Paths, _loggerFactory.CreateLogger<ElevationClient>()),
            _installedFiles);

        var orchestrator = new UpdateOrchestrator(
            Paths,
            Config.Download,
            downloader,
            installers,
            backup,
            _transfers,
            _versionRecords,
            _loggerFactory.CreateLogger<UpdateOrchestrator>());

        return (factory, checker, orchestrator);
    }

    /// <summary>重新读取配置文件并重建运行期管线。</summary>
    public void Reload()
    {
        var loaded = LoadConfig();
        if (loaded.LoadFailed)
        {
            // 加载失败时保留当前配置与管线，避免界面清空、
            // 以及后续任何保存把占位的空配置写回磁盘造成数据丢失。
            Warnings = loaded.Warnings;
            _cache.Invalidate();
            _openList.Invalidate();
            return;
        }

        var previousHttp = _http;
        Config = loaded.Config;
        ConfigPath = loaded.Path;
        _configPath = loaded.Path;
        Warnings = loaded.Warnings;

        _http = UpdaterHttp.Create(Config.Download);
        (_sourceFactory, _checker, _orchestrator) = BuildPipeline();
        _openList.Invalidate();
        _cache.Invalidate();
        previousHttp.Dispose();
    }

    /// <summary>把内存中的配置写回磁盘（界面编辑后调用）。</summary>
    public void SaveConfig()
    {
        // 保存前先校验：写入一个自己都加载不了的配置文件，会让下一次加载失败
        var errors = ConfigValidator.Validate(Config).Where(i => i.IsError).ToList();
        if (errors.Count > 0)
        {
            throw new ConfigException(
                "配置校验未通过，未保存：\r\n" + string.Join("\r\n", errors.Select(e => " - " + e)));
        }

        ConfigLoader.Save(ConfigPath, Config);
        Reload();
    }

    public Task<TargetCheckResult> CheckAsync(TargetConfig target, bool forceRefresh = false, CancellationToken ct = default) =>
        _checker.CheckAsync(target, forceRefresh, ct);

    /// <summary>为选中的版本执行完整更新流程（下载/校验/解压/备份/安装/清理）。</summary>
    public async Task<UpdateResult> InstallAsync(
        TargetConfig target,
        VersionCandidate candidate,
        IPasswordProvider? passwordProvider,
        IProgress<StageProgress>? progress,
        CancellationToken ct)
    {
        var sourceConfig = target.Sources.FirstOrDefault(source =>
        {
            if (!source.Enabled)
            {
                return false;
            }

            var instance = _sourceFactory.Create(source, target);
            return instance is not null &&
                   string.Equals(instance.SourceId, candidate.Version.SourceId, StringComparison.OrdinalIgnoreCase);
        });

        if (sourceConfig is null)
        {
            return UpdateResult.Fail("找不到该版本对应的渠道配置，可能已被删除。", []);
        }

        var source = _sourceFactory.Create(sourceConfig, target);
        if (source is null)
        {
            return UpdateResult.Fail("渠道不可用，请检查配置。", []);
        }

        AssetRef? asset;
        try
        {
            asset = await source
                .ResolveAssetAsync(candidate.Version, sourceConfig.AssetPattern, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "解析下载地址失败：{Target}", target.Id);
            return UpdateResult.Fail($"解析下载地址失败：{ex.Message}", [UpdateStage.Resolving], ex);
        }

        if (asset?.DirectUri is null)
        {
            return UpdateResult.Fail("渠道未返回可下载地址。", [UpdateStage.Resolving]);
        }

        var request = new UpdateRequest(target, candidate.Version, asset, source, passwordProvider);
        return await _orchestrator.ExecuteAsync(request, progress, ct).ConfigureAwait(false);
    }

    /// <summary>清理过期的下载/暂存残留。</summary>
    public (int Files, long Bytes) CleanupStale(TimeSpan maxAge) => WorkspaceGuard.CleanupStale(Paths, maxAge);

    /// <summary>校验 GitHub / GitLab 全局令牌（未配置时返回 Skipped）。</summary>
    public Task<CredentialCheckResult> CheckGithubTokenAsync(CancellationToken ct = default) =>
        CheckTokenAsync(() => _credentialChecker.CheckGithubTokenAsync(_gitTokens.GetGithubToken(), ct));

    public Task<CredentialCheckResult> CheckGitlabTokenAsync(CancellationToken ct = default) =>
        CheckTokenAsync(() => _credentialChecker.CheckGitlabTokenAsync(_gitTokens.GetGitlabToken(), ct: ct));

    /// <summary>校验百度网盘登录状态（未登录返回 Skipped）。</summary>
    public async Task<CredentialCheckResult> CheckBaiduSessionAsync(CancellationToken ct = default)
    {
        if (!_panSessions.HasSession(PanPlatform.Baidu))
        {
            return CredentialCheckResult.Skip("百度网盘", "未登录，跳过。");
        }

        try
        {
            await _sourceFactory.Baidu.ValidateSessionAsync(ct).ConfigureAwait(false);
            return CredentialCheckResult.Pass("百度网盘", "登录有效。");
        }
        catch (Exception ex) when (ex is PanApiException or HttpRequestException or TaskCanceledException)
        {
            return CredentialCheckResult.Fail("百度网盘", ex.Message);
        }
    }

    /// <summary>校验夸克网盘登录状态（未登录返回 Skipped）。</summary>
    public async Task<CredentialCheckResult> CheckQuarkSessionAsync(CancellationToken ct = default)
    {
        if (!_panSessions.HasSession(PanPlatform.Quark))
        {
            return CredentialCheckResult.Skip("夸克网盘", "未登录，跳过。");
        }

        try
        {
            await _sourceFactory.Quark.ValidateSessionAsync(ct).ConfigureAwait(false);
            return CredentialCheckResult.Pass("夸克网盘", "登录有效。");
        }
        catch (Exception ex) when (ex is PanApiException or HttpRequestException or TaskCanceledException)
        {
            return CredentialCheckResult.Fail("夸克网盘", ex.Message);
        }
    }

    /// <summary>
    /// 校验 OpenList 连通性与凭据。<paramref name="probe"/> 传入设置页当前填写值（未保存也可检测）；
    /// 传 null 时使用已保存的配置。
    /// </summary>
    public async Task<CredentialCheckResult> CheckOpenListAsync(
        OpenListConfig? probe = null,
        CancellationToken ct = default)
    {
        var effective = probe ?? Config.OpenList;
        if (effective.Mode == OpenListMode.Disabled)
        {
            return CredentialCheckResult.Skip("OpenList", "运行模式为 disabled，跳过。");
        }

        try
        {
            // 用临时客户端探测：不影响正在运行的登录缓存，也不触发本地实例下载
            var client = new OpenListClient(
                _http,
                () => effective,
                _loggerFactory.CreateLogger<OpenListClient>());

            var ping = await client.PingAsync(ct).ConfigureAwait(false);
            if (ping != "在线")
            {
                return CredentialCheckResult.Fail("OpenList", $"实例不可达：{ping}");
            }

            await client.LoginAsync(ct).ConfigureAwait(false);
            return CredentialCheckResult.Pass("OpenList", $"服务在线（{client.BaseUrl}），凭据有效。");
        }
        catch (Exception ex) when (ex is PanApiException or HttpRequestException or TaskCanceledException)
        {
            return CredentialCheckResult.Fail("OpenList", ex.Message);
        }
    }

    private CredentialChecker _credentialChecker => new(_http, _loggerFactory.CreateLogger<CredentialChecker>());

    private async Task<CredentialCheckResult> CheckTokenAsync(Func<Task<CredentialCheckResult>> check)
    {
        try
        {
            return await check().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "令牌校验失败");
            return CredentialCheckResult.Fail("令牌校验", $"网络错误：{ex.Message}");
        }
    }

    public void Dispose()
    {
        _openList.Invalidate();
        _http.Dispose();
    }
}
