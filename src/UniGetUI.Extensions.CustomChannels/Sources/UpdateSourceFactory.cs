using System.Collections.Concurrent;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Models;
using UniGetUI.Extensions.CustomChannels.OpenList;
using UniGetUI.Extensions.CustomChannels.Pan;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Sources;

/// <summary>按配置构造渠道实例。网盘 API 复用同一实例以缓存分享 token。</summary>
public sealed class UpdateSourceFactory
{
    private readonly HttpClient _http;
    private readonly UpdaterConfig _root;
    private readonly AppPaths _paths;
    private readonly PanSessionStore _sessions;
    private readonly TransferManager _transfers;
    private readonly OpenListRuntime _openList;
    private readonly ILoggerFactory _loggerFactory;
    private readonly BaiduPanApi _baidu;
    private readonly QuarkPanApi _quark;

    public UpdateSourceFactory(
        HttpClient http,
        UpdaterConfig root,
        AppPaths paths,
        PanSessionStore sessions,
        TransferManager transfers,
        OpenListRuntime openList,
        ILoggerFactory loggerFactory)
    {
        _http = http;
        _root = root;
        _paths = paths;
        _sessions = sessions;
        _transfers = transfers;
        _openList = openList;
        _loggerFactory = loggerFactory;
        _baidu = new BaiduPanApi(http, sessions, loggerFactory.CreateLogger<BaiduPanApi>());
        _quark = new QuarkPanApi(http, sessions, loggerFactory.CreateLogger<QuarkPanApi>());
    }

    public BaiduPanApi Baidu => _baidu;

    public QuarkPanApi Quark => _quark;

    public IUpdateSource? Create(SourceConfig config, TargetConfig target)
    {
        if (!config.Enabled)
        {
            return null;
        }

        return config.Type switch
        {
            SourceType.GithubRelease => new GithubReleaseSource(
                _http, config, _loggerFactory.CreateLogger<GithubReleaseSource>()),
            SourceType.GitlabRelease => new GitlabReleaseSource(
                _http, config, _loggerFactory.CreateLogger<GitlabReleaseSource>()),
            SourceType.BaiduPan => new BaiduPanSource(
                config, _baidu, _sessions, _transfers, _root.Transfer,
                _loggerFactory.CreateLogger<BaiduPanSource>()),
            SourceType.QuarkPan => new QuarkPanSource(
                config, _quark, _sessions, _transfers, _root.Transfer,
                _loggerFactory.CreateLogger<QuarkPanSource>()),
            SourceType.OpenList => new OpenListSource(
                config, _openList, _loggerFactory.CreateLogger<OpenListSource>()),
            SourceType.DirectUrl => new DirectUrlSource(
                _http, config, _loggerFactory.CreateLogger<DirectUrlSource>()),
            _ => null,
        };
    }

    public IReadOnlyList<IUpdateSource> CreateAll(TargetConfig target) =>
        target.Sources
            .Where(s => s.Enabled)
            .Select(s => Create(s, target))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();
}

/// <summary>版本列表短期缓存，避免频繁请求远程接口。</summary>
public sealed class VersionCache
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<VersionInfo> Items)> _cache = new();

    public bool TryGet(string key, TimeSpan ttl, out IReadOnlyList<VersionInfo> items)
    {
        if (_cache.TryGetValue(key, out var entry) && DateTimeOffset.UtcNow - entry.At < ttl)
        {
            items = entry.Items;
            return true;
        }

        items = [];
        return false;
    }

    public void Set(string key, IReadOnlyList<VersionInfo> items) =>
        _cache[key] = (DateTimeOffset.UtcNow, items);

    public void Invalidate(string? key = null)
    {
        if (key is null)
        {
            _cache.Clear();
        }
        else
        {
            _cache.TryRemove(key, out _);
        }
    }
}
