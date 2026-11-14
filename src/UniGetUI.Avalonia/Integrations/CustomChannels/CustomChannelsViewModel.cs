using System.Collections.ObjectModel;
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
        Versions.Clear();
        SelectedVersion = null;
        if (value is null)
        {
            return;
        }

        foreach (var candidate in value.Candidates)
        {
            Versions.Add(new CustomChannelVersionViewModel(candidate));
        }

        SelectedVersion = Versions.FirstOrDefault(v => v.IsSelectable) ?? Versions.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedCardNotes));
        OnPropertyChanged(nameof(SelectedCardReadme));
        OnPropertyChanged(nameof(HasSelectedCard));
    }

    partial void OnSelectedVersionChanged(CustomChannelVersionViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedCardNotes));
        OnPropertyChanged(nameof(SelectedCardReadme));
        OnPropertyChanged(nameof(SelectedVersionNotesTitle));
    }

    public bool HasSelectedCard => SelectedCard is not null;

    public string SelectedCardNotes => SelectedVersion?.Notes ?? SelectedCard?.Error ?? "该版本没有提供更新说明。";

    public string SelectedVersionNotesTitle =>
        SelectedVersion is null ? "更新说明" : $"{SelectedVersion.VersionText} 更新说明";

    public string SelectedCardReadme => SelectedVersion?.ReadmeText ?? string.Empty;

    /// <summary>页面首次加载时调用（只执行一次）。</summary>
    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        if (_service.Warnings.Count > 0)
        {
            ConfigError = string.Join("；", _service.Warnings);
        }

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

    public async Task CheckCardAsync(CustomChannelCardViewModel card, bool forceRefresh, CancellationToken ct)
    {
        await CheckGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => card.IsChecking = true);
            var result = await _service.CheckAsync(card.Target, forceRefresh, ct).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => card.Apply(result));
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

        var created = await CustomChannelEditorDialog.ShowAsync(owner, null);
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
    }

    public async Task EditTargetAsync(Window? owner, CustomChannelCardViewModel card)
    {
        if (owner is null)
        {
            return;
        }

        var edited = await CustomChannelEditorDialog.ShowAsync(owner, card.Target);
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

    public void ReloadFromConfig()
    {
        _service.Reload();
        Cards.Clear();
        foreach (var target in _service.Config.Targets)
        {
            Cards.Add(new CustomChannelCardViewModel(target));
        }

        SelectedCard = Cards.FirstOrDefault();
        OnPropertyChanged(nameof(StatusSummary));
    }

    private void PersistAndReload()
    {
        try
        {
            _service.SaveConfig();
            ConfigError = null;
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
