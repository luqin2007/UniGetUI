using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Logging;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Models;
using UniGetUI.Extensions.CustomChannels.Packages;
using UniGetUI.Extensions.CustomChannels.Pan;
using UniGetUI.Extensions.CustomChannels.Services;
using AppPaths = UniGetUI.Extensions.CustomChannels.Infrastructure.AppPaths;

namespace UniGetUI.Avalonia.Integrations.CustomChannels;

/// <summary>
/// 「自定义渠道」页面 ViewModel：应用卡片、检查、版本列表、安装任务与编辑入口。
/// 所有耗时操作都在后台线程执行，界面状态通过 <see cref="Dispatcher"/> 回到 UI 线程。
/// </summary>
internal sealed partial class CustomChannelsViewModel : ViewModelBase, IDisposable
{
    private const string SyncStatusUrl =
        "https://raw.githubusercontent.com/luqin2007/UniGetUI/sync-status/sync-status.json";

    private static readonly SemaphoreSlim CheckGate = new(3, 3);

    private readonly CustomChannelsService _service;
    private readonly IShellHost _shell;
    private bool _initialized;
    private CancellationTokenSource _checks = new();

    public CustomChannelsViewModel(ILoggerFactory loggerFactory)
    {
        LoggerFactory = loggerFactory;
        _service = new CustomChannelsService(AppPaths.Default, loggerFactory);
        _shell = new AvaloniaShellHost();

        foreach (var target in _service.Config.Targets)
        {
            Cards.Add(new CustomChannelCardViewModel(target));
        }
    }

    public ILoggerFactory LoggerFactory { get; }

    public ObservableCollection<CustomChannelCardViewModel> Cards { get; } = [];

    public ObservableCollection<CustomChannelVersionViewModel> Versions { get; } = [];

    public CustomChannelsService Service => _service;

    [ObservableProperty]
    private CustomChannelCardViewModel? _selectedCard;

    [ObservableProperty]
    private CustomChannelVersionViewModel? _selectedVersion;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "尚未检查";

    [ObservableProperty]
    private string? _configError;

    public string StatusSummary
    {
        get
        {
            int updates = Cards.Count(c => c.HasUpdate);
            return updates > 0 ? $"{updates} 个应用可更新" : string.Empty;
        }
    }

    partial void OnSelectedCardChanged(CustomChannelCardViewModel? value)
    {
        RebuildVersions();
        OnPropertyChanged(nameof(SelectedCardNotes));
        OnPropertyChanged(nameof(SelectedCardReadme));
        OnPropertyChanged(nameof(SelectedReadmeTitle));
        OnPropertyChanged(nameof(HasSelectedCard));
    }

    /// <summary>用当前选中卡片的候选版本重建版本列表（检查完成后也会调用）。</summary>
    private void RebuildVersions()
    {
        string? previous = SelectedVersion?.Version.Key;
        Versions.Clear();
        if (SelectedCard is null)
        {
            SelectedVersion = null;
            return;
        }

        foreach (var candidate in SelectedCard.Candidates)
        {
            Versions.Add(new CustomChannelVersionViewModel(candidate));
        }

        SelectedVersion = previous is null
            ? Versions.FirstOrDefault(v => v.IsSelectable) ?? Versions.FirstOrDefault()
            : Versions.FirstOrDefault(v => v.Version.Key == previous) ?? Versions.FirstOrDefault();
    }

    partial void OnSelectedVersionChanged(CustomChannelVersionViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedCardNotes));
        OnPropertyChanged(nameof(SelectedCardReadme));
        OnPropertyChanged(nameof(SelectedReadmeTitle));
        OnPropertyChanged(nameof(SelectedVersionNotesTitle));
    }

    public bool HasSelectedCard => SelectedCard is not null;

    public string SelectedCardNotes => SelectedVersion?.Notes ?? SelectedCard?.Error ?? "该版本没有提供更新说明。";

    public string SelectedVersionNotesTitle =>
        SelectedVersion is null ? "更新说明" : $"{SelectedVersion.VersionText} 更新说明";

    /// <summary>README 只附加在最新版本上，选中历史版本时回退显示最新可用的 README。</summary>
    public string SelectedCardReadme => SelectedVersion?.ReadmeText is { Length: > 0 } text
        ? text
        : Versions.FirstOrDefault(v => v.ReadmeText.Length > 0)?.ReadmeText ?? string.Empty;

    public string SelectedReadmeTitle => SelectedVersion?.ReadmeText is { Length: > 0 }
        ? SelectedVersion.ReadmeTitle
        : Versions.FirstOrDefault(v => v.ReadmeText.Length > 0)?.ReadmeTitle ?? "README";

    /// <summary>页面首次加载时调用（只执行一次）。</summary>
    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        // 配置告警由 UniGetUI 顶部通知统一呈现，页面内不重复展示，避免占用工具栏空间
        ConfigError = _service.LastLoadError;
        SelectedCard = Cards.FirstOrDefault();
        _ = CheckAllAsync(forceRefresh: false);
        _ = CheckSyncStatusAsync();
    }

    public async Task CheckForUpdatesAsync()
    {
        await CheckAllAsync(forceRefresh: false).ConfigureAwait(false);
    }

    public async Task CheckAllAsync(bool forceRefresh)
    {
        if (Cards.Count == 0)
        {
            StatusText = "尚未添加应用";
            return;
        }

        IsBusy = true;
        StatusText = "正在检查全部渠道…";

        var token = ReplaceToken();
        try
        {
            await Task.WhenAll(Cards.Select(card => CheckCardAsync(card, forceRefresh, token)));
            StatusText = $"检查完成 · {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(StatusSummary));
        }
    }

    /// <summary>只检查单个应用（卡片上的「检查」按钮）。</summary>
    public async Task CheckSingleAsync(CustomChannelCardViewModel card)
    {
        IsBusy = true;
        StatusText = $"正在检查 {card.DisplayName}…";
        try
        {
            await CheckCardAsync(card, forceRefresh: true, CancellationToken.None).ConfigureAwait(true);
            StatusText = $"已检查 {card.DisplayName} · {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(StatusSummary));
        }
    }

    /// <summary>版本未知时由用户手动填写当前版本（写入版本记录并重新检查）。</summary>
    public async Task SetManualVersionAsync(CustomChannelCardViewModel card, string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        _service.VersionRecords.Save(
            card.Id,
            version.Trim(),
            card.Target.Sources.FirstOrDefault()?.Type.ToString(),
            "手动填写");
        await CheckSingleAsync(card).ConfigureAwait(true);
    }

    public async Task CheckCardAsync(CustomChannelCardViewModel card, bool forceRefresh, CancellationToken ct)
    {
        await CheckGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => card.IsChecking = true);
            var result = await _service.CheckAsync(card.Target, forceRefresh, ct).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                card.Apply(result);
                if (ReferenceEquals(card, SelectedCard))
                {
                    // 检查完成前版本列表是空的，完成后必须重建，否则详情面板一直为空
                    RebuildVersions();
                }
            });
        }
        catch (OperationCanceledException)
        {
            // 页面关闭或重新检查导致的取消：保持卡片原状态
        }
        catch (Exception ex)
        {
            Logger.Warn($"[CustomChannels] 检查 {card.Id} 失败：{ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(() => card.Error = ex.Message);
        }
        finally
        {
            CheckGate.Release();
            await Dispatcher.UIThread.InvokeAsync(() => card.IsChecking = false);
        }
    }

    /// <summary>为选中的版本创建操作并加入 UniGetUI 底部操作列表。</summary>
    public void Install(CustomChannelCardViewModel card, CustomChannelVersionViewModel version)
    {
        if (!version.IsSelectable)
        {
            _shell.Notify("无法安装", version.BlockReason ?? "该版本当前不可安装。", isError: true);
            return;
        }

        var operation = new CustomChannelsOperation(
            _service,
            card.Target,
            version.Candidate,
            new ShellPasswordProvider(_shell, card.DisplayName),
            () => _ = CheckCardSafelyAsync(card));

        AvaloniaOperationRegistry.Add(operation);
        _ = operation.MainThread();
    }

    private async Task CheckCardSafelyAsync(CustomChannelCardViewModel card)
    {
        try
        {
            await CheckCardAsync(card, forceRefresh: true, CancellationToken.None);
            await Dispatcher.UIThread.InvokeAsync(() => OnPropertyChanged(nameof(StatusSummary)));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[CustomChannels] 更新后刷新 {card.Id} 失败：{ex.Message}");
        }
    }

    public async Task AddTargetAsync(Window? owner)
    {
        if (owner is null)
        {
            return;
        }

        var created = await CustomChannelEditorDialog.ShowAsync(
            owner,
            null,
            target => _service.CheckAsync(target, forceRefresh: true));
        if (created is null)
        {
            return;
        }

        if (_service.Config.Targets.Any(t => string.Equals(t.Id, created.Id, StringComparison.OrdinalIgnoreCase)))
        {
            _shell.Notify("无法添加", $"已存在标识为 {created.Id} 的应用。", isError: true);
            return;
        }

        _service.Config.Targets.Add(created);
        PersistAndReload();

        // 安装目录不存在或为空（即应用未安装）时，添加完成后询问是否立即安装
        // （先检查一次拿到可用版本）
        var card = Cards.FirstOrDefault(c => c.Id == created.Id);
        if (card is null || !LooksNotInstalled(created.Install.RootPath))
        {
            return;
        }

        SelectedCard = card;
        await CheckSingleAsync(card).ConfigureAwait(true);

        var candidate = card.Candidates.FirstOrDefault(c => c.IsSelectable);
        if (candidate is null)
        {
            _shell.Notify(
                "暂时无法安装",
                card.Error is { Length: > 0 } error
                    ? $"检查未得到可安装的版本：{error}"
                    : "渠道尚未返回可安装的版本，可稍后在卡片上点「检查」重试。",
                isError: true);
            return;
        }

        bool installNow = await ConfirmDialog.ShowAsync(
            owner,
            "应用尚未安装",
            $"目录 {created.Install.RootPath} 不存在或为空，是否现在安装 {card.DisplayName} " +
            $"{candidate.Version.DisplayVersion}？也可以稍后在版本列表中自选任意版本安装。",
            "立即安装",
            "稍后").ConfigureAwait(true);

        if (!installNow)
        {
            return;
        }

        var selected = Versions.FirstOrDefault(v => ReferenceEquals(v.Candidate, candidate))
                       ?? new CustomChannelVersionViewModel(candidate);
        SelectedVersion = selected;
        Install(card, selected);
    }

    public async Task EditTargetAsync(Window? owner, CustomChannelCardViewModel card)
    {
        if (owner is null)
        {
            return;
        }

        var edited = await CustomChannelEditorDialog.ShowAsync(
            owner,
            card.Target,
            target => _service.CheckAsync(target, forceRefresh: true));
        if (edited is null)
        {
            return;
        }

        int index = _service.Config.Targets.FindIndex(t => t.Id == card.Target.Id);
        if (index >= 0)
        {
            _service.Config.Targets[index] = edited;
        }

        PersistAndReload();
    }

    public void RemoveTarget(CustomChannelCardViewModel card)
    {
        _service.Config.Targets.RemoveAll(t => t.Id == card.Target.Id);
        PersistAndReload();
    }

    /// <summary>启动应用主程序（要求已在编辑中填写可执行文件）。</summary>
    public void OpenApp(CustomChannelCardViewModel card)
    {
        try
        {
            var install = card.Target.Install;
            if (string.IsNullOrWhiteSpace(install.ExePath))
            {
                _shell.Notify("无法打开", "尚未填写可执行文件，请先在「编辑」中配置。", isError: true);
                return;
            }

            string fullPath = Path.IsPathRooted(install.ExePath)
                ? install.ExePath
                : Path.Combine(install.RootPath ?? string.Empty, install.ExePath);
            if (!File.Exists(fullPath))
            {
                _shell.Notify("无法打开", $"可执行文件不存在：{fullPath}", isError: true);
                return;
            }

            Process.Start(new ProcessStartInfo(fullPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty,
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            _shell.Notify("打开失败", ex.Message, isError: true);
        }
    }

    /// <summary>在浏览器中打开第一个启用渠道的页面（仓库 / 分享链接 / 直链）。</summary>
    public void OpenWeb(CustomChannelCardViewModel card)
    {
        try
        {
            var url = card.Target.Sources
                .Where(s => s.Enabled)
                .Select(SourcePageUrl)
                .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
            if (string.IsNullOrWhiteSpace(url))
            {
                _shell.Notify("无法打开", "该应用没有可打开的网页地址。", isError: true);
                return;
            }

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            _shell.Notify("打开失败", ex.Message, isError: true);
        }
    }

    /// <summary>在资源管理器中打开安装目录。</summary>
    public void OpenFolder(CustomChannelCardViewModel card)
    {
        try
        {
            var root = card.Target.Install.RootPath;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                _shell.Notify("无法打开", $"安装目录不存在：{root}", isError: true);
                return;
            }

            Process.Start("explorer.exe", root);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            _shell.Notify("打开失败", ex.Message, isError: true);
        }
    }

    private static string? SourcePageUrl(SourceConfig source) => source.Type switch
    {
        SourceType.GithubRelease => string.IsNullOrWhiteSpace(source.Repo)
            ? null
            : $"https://github.com/{source.Repo}/releases",
        SourceType.GitlabRelease => GitLabPageUrl(source),
        SourceType.BaiduPan or SourceType.QuarkPan => source.ShareUrl,
        SourceType.DirectUrl => source.Url,
        _ => null,
    };

    private static string? GitLabPageUrl(SourceConfig source)
    {
        if (string.IsNullOrWhiteSpace(source.ProjectId))
        {
            return null;
        }

        var host = string.IsNullOrWhiteSpace(source.BaseUrl) ? "https://gitlab.com" : source.BaseUrl!;
        return $"{host.TrimEnd('/')}/{source.ProjectId}/-/releases";
    }

    public void ReloadFromConfig()
    {
        string? previousId = SelectedCard?.Id;
        _service.Reload();
        Cards.Clear();
        foreach (var target in _service.Config.Targets)
        {
            Cards.Add(new CustomChannelCardViewModel(target));
        }

        SelectedCard = Cards.FirstOrDefault(c => c.Id == previousId) ?? Cards.FirstOrDefault();
        OnPropertyChanged(nameof(StatusSummary));
    }

    /// <summary>目录不存在，或存在但没有任何文件/子目录时，认为应用尚未安装。</summary>
    private static bool LooksNotInstalled(string? root) =>
        string.IsNullOrWhiteSpace(root) ||
        !Directory.Exists(root) ||
        !Directory.EnumerateFileSystemEntries(root).Any();

    private void PersistAndReload()
    {
        try
        {
            _service.SaveConfig();
            ConfigError = _service.LastLoadError;
        }
        catch (Exception ex)
        {
            ConfigError = ex.Message;
            Logger.Error(ex);
        }

        ReloadFromConfig();
        _ = CheckAllAsync(forceRefresh: true);
    }

    private CancellationToken ReplaceToken()
    {
        var previous = Interlocked.Exchange(ref _checks, new CancellationTokenSource());
        previous.Cancel();
        previous.Dispose();
        return _checks.Token;
    }

    /// <summary>读取上游同步状态；合并冲突/构建失败时在应用内提示一次。</summary>
    private async Task CheckSyncStatusAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("UniGetUI-CustomChannels/1.0");
            var json = await http.GetStringAsync(SyncStatusUrl).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            bool merged = root.TryGetProperty("merged", out var mergedValue) && mergedValue.GetBoolean();
            string? issue = root.TryGetProperty("issueUrl", out var issueValue) ? issueValue.GetString() : null;
            string? upstream = root.TryGetProperty("upstreamTag", out var tagValue) ? tagValue.GetString() : null;

            if (merged)
            {
                return;
            }

            string title = string.IsNullOrWhiteSpace(upstream)
                ? "上游同步失败"
                : $"上游 {upstream} 同步失败";

            await Dispatcher.UIThread.InvokeAsync(() =>
                MainWindow.Instance?.ShowBanner(
                    title,
                    issue is null
                        ? "自动合并上游更新失败，当前版本可能落后。详情见仓库的同步 Issue。"
                        : $"自动合并上游更新失败，当前版本可能落后。点击更多信息：{issue}",
                    MainWindow.RuntimeNotificationLevel.Error));
        }
        catch (Exception ex)
        {
            Logger.Debug($"[CustomChannels] 同步状态不可用：{ex.Message}");
        }
    }

    public void Stop()
    {
        _checks.Cancel();
        _service.Dispose();
    }

    public void Dispose() => Stop();
}
