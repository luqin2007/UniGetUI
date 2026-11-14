using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.OpenList;

/// <summary>
/// OpenList 运行时：外部实例直接连接，本地模式按需下载并启动实例。
/// </summary>
public sealed class OpenListRuntime : IDisposable
{
    private readonly AppPaths _paths;
    private readonly Func<OpenListConfig> _configAccessor;
    private readonly DownloadConfig _download;
    private readonly ILogger<OpenListRuntime> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OpenListHost? _host;
    private OpenListConfig? _hostConfig;
    private bool _ready;

    /// <summary>
    /// 通过委托读取配置：设置页保存或配置重新加载后无需重建服务，
    /// 本地实例也会按最新的模式/端口按需创建，避免停留在启动时的快照上。
    /// </summary>
    public OpenListRuntime(
        AppPaths paths,
        Func<OpenListConfig> configAccessor,
        DownloadConfig download,
        ILogger<OpenListRuntime> logger)
    {
        _paths = paths;
        _configAccessor = configAccessor;
        _download = download;
        _logger = logger;

        var http = UpdaterHttp.Create(download, UpdaterHttp.AppUserAgent, allowAutoRedirect: true);
        http.Timeout = TimeSpan.FromSeconds(60);
        Client = new OpenListClient(http, configAccessor, logger: LoggerFactoryAdapter.Create<OpenListClient>(logger));
    }

    public OpenListClient Client { get; }

    private OpenListConfig Config => _configAccessor();

    /// <summary>本地模式下的托管实例；非本地模式或未使用本地模式时为 null（按需创建）。</summary>
    public OpenListHost? Host
    {
        get
        {
            var config = Config;
            if (config.Mode != OpenListMode.Local)
            {
                return null;
            }

            if (_host is null || !ReferenceEquals(_hostConfig, config))
            {
                _host?.Stop();
                _host = new OpenListHost(
                    _paths, config, _download, LoggerFactoryAdapter.Create<OpenListHost>(_logger));
                _hostConfig = config;
            }

            return _host;
        }
    }

    public bool IsEnabled => Config.Mode != OpenListMode.Disabled;

    public bool IsLocalMode => Config.Mode == OpenListMode.Local;

    /// <summary>配置被修改或重新加载后调用：丢弃已就绪状态与登录缓存，便于按新配置重新检测。</summary>
    public void Invalidate()
    {
        _ready = false;
        Client.ResetSession();

        if (_host is null)
        {
            return;
        }

        if (Config.Mode == OpenListMode.Local)
        {
            _host.RefreshCandidates();
            return;
        }

        _host.Stop();
        _host = null;
        _hostConfig = null;
    }

    public async Task<OpenListClient> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (Config.Mode == OpenListMode.Disabled)
        {
            throw new Pan.PanApiException("OpenList 渠道已启用，但配置中的 openList.mode 为 disabled。");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ready)
            {
                return Client;
            }

            var config = Config;
            if (config.Mode == OpenListMode.Local)
            {
                var port = config.LocalPort is > 0 and < 65536 ? config.LocalPort : 5244;

                // 端口上已有实例（用户自行启动的本地 OpenList）时直接复用，避免端口冲突
                if (await IsOnlineAsync(port, ct).ConfigureAwait(false))
                {
                    progress?.Report($"检测到端口 {port} 上已有 OpenList 在运行，直接使用该实例");
                    config.BaseUrl = $"http://127.0.0.1:{port}";
                    config.Username = string.IsNullOrWhiteSpace(config.Username) ? "admin" : config.Username;
                }
                else
                {
                    var host = Host ?? throw new InvalidOperationException("本地 OpenList 实例未能创建，请检查 openList.mode 配置。");
                    progress?.Report(host.DetectedBinary is { } detected
                        ? $"使用{detected.SourceText}的 OpenList：{detected.Path}"
                        : "首次使用，正在准备 OpenList 运行时…");

                    // 内部会先验证本机候选，只有全部不可用时才按需下载
                    await host.EnsureBinaryAsync(progress, ct).ConfigureAwait(false);

                    port = await host.StartAsync(ct).ConfigureAwait(false);
                    config.BaseUrl = $"http://127.0.0.1:{port}";
                    config.Username = "admin";
                    config.Password = host.AdminPassword;
                    progress?.Report($"OpenList 已在端口 {port} 启动");
                }
            }

            await Client.LoginAsync(ct).ConfigureAwait(false);
            _ready = true;
            return Client;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>探测指定端口上是否已有 OpenList 在运行。</summary>
    public static async Task<bool> IsOnlineAsync(int port, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/ping", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text.Contains("pong", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _host?.Stop();
        _gate.Dispose();
    }
}

/// <summary>在没有 DI 容器时按类别创建带前缀的日志器。</summary>
internal sealed class LoggerFactoryAdapter(ILogger inner) : ILoggerFactory
{
    public static ILogger<T> Create<T>(ILogger inner) => new PrefixedLogger<T>(inner);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => inner;

    public void Dispose()
    {
    }

    private sealed class PrefixedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
