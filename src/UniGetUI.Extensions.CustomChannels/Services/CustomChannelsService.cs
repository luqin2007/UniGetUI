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
    private readonly TransferManager _transfers;
    private readonly VersionRecordStore _versionRecords;
    private readonly VersionCache _cache = new();
    private readonly ReadmeStore _readmeStore;

    private HttpClient _http;
    private UpdateSourceFactory _sourceFactory;
    private UpdateChecker _checker;
    private UpdateOrchestrator _orchestrator;

    public CustomChannelsService(AppPaths paths, ILoggerFactory loggerFactory)
    {
        Paths = paths;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CustomChannelsService>();
        paths.EnsureCreated();

        _secrets = new SecretStore(paths);
        _panSessions = new PanSessionStore(_secrets);
        _transfers = new TransferManager(paths, Config.Transfer, loggerFactory.CreateLogger<TransferManager>());
        _versionRecords = new VersionRecordStore(paths, loggerFactory.CreateLogger<VersionRecordStore>());
        _readmeStore = new ReadmeStore(paths);

        var loaded = LoadConfig();
        Config = loaded.Config;
        ConfigPath = loaded.Path;
        Warnings = loaded.Warnings;

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

    public PanSessionStore PanSessions => _panSessions;

    public TransferManager Transfers => _transfers;

    public OpenListRuntime OpenList => _openList;

    public VersionRecordStore VersionRecords => _versionRecords;

    public UpdateSourceFactory Sources => _sourceFactory;

    /// <summary>读取配置；缺失时生成示例配置，保证页面首次打开可用。</summary>
    private ConfigLoadResult LoadConfig()
    {
        try
        {
            return ConfigLoader.LoadOrCreateSample(Paths);
        }
        catch (ConfigException ex)
        {
            _logger.LogError(ex, "自定义渠道配置加载失败");
            var fallback = ConfigLoader.CreateSampleConfig();
            var path = ConfigLoader.DefaultPath(Paths);
            return new ConfigLoadResult(fallback, path, [ex.Message], created: true);
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
            new ElevationClient(Paths, _loggerFactory.CreateLogger<ElevationClient>()));

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
        var previousHttp = _http;
        var loaded = LoadConfig();
        Config = loaded.Config;
        ConfigPath = loaded.Path;
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

    public void Dispose()
    {
        _openList.Invalidate();
        _http.Dispose();
    }
}
