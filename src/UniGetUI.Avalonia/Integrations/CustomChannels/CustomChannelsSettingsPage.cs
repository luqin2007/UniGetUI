using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using UniGetUI.Core.Logging;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Pan;
using UniGetUI.Extensions.CustomChannels.Services;
using AppPaths = UniGetUI.Extensions.CustomChannels.Infrastructure.AppPaths;

namespace UniGetUI.Avalonia.Integrations.CustomChannels;

/// <summary>
/// 「设置 &gt; 扩展 &gt; 自定义渠道」：OpenList、网盘账号、下载与安装选项。
/// 保存后热重载正在运行的页面，无需重启应用。
/// </summary>
internal sealed class CustomChannelsSettingsPage : UserControl, ISettingsPage
{
    private readonly CustomChannelsService _service;
    private readonly ComboBox _openListMode = new();
    private readonly TextBox _openListUrl = new();
    private readonly TextBox _openListUser = new();
    private readonly TextBox _openListPassword = new() { PasswordChar = '●' };
    private readonly TextBox _openListRoot = new();
    private readonly CheckBox _openListAutoDownload = new() { Content = "本地实例缺失时自动下载官方 OpenList 二进制" };
    private readonly TextBox _githubToken = new() { PasswordChar = '●', Watermark = "ghp_…（留空清除，未配置时匿名访问）" };
    private readonly TextBox _gitlabToken = new() { PasswordChar = '●', Watermark = "glpat-…（留空清除）" };
    private readonly TextBlock _credStatus = new() { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _concurrency = new();
    private readonly TextBox _speedLimit = new();
    private readonly TextBox _cacheMinutes = new();
    private readonly TextBox _proxy = new() { Watermark = "http://127.0.0.1:7890（留空直连）" };
    private readonly CheckBox _transferEnabled = new() { Content = "允许自动转存分享文件到当前网盘账号" };
    private readonly TextBox _transferPath = new();
    private readonly ComboBox _cleanupPolicy = new();
    private readonly TextBlock _panStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8 };
    private readonly TextBlock _saveHint = new() { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };

    public CustomChannelsSettingsPage()
    {
        ShortTitle = "自定义渠道";
        _service = new CustomChannelsService(AppPaths.Default, CustomChannelsIntegration.CreateLoggerFactory());

        _openListMode.ItemsSource = Enum.GetValues<OpenListMode>();
        _cleanupPolicy.ItemsSource = Enum.GetValues<TransferCleanupPolicy>();

        LoadFrom(_service.Config);
        Content = BuildLayout();
    }

    public bool CanGoBack => false;

    public string ShortTitle { get; }

    public event EventHandler? RestartRequired
    {
        add { }
        remove { }
    }

    public event EventHandler<Type>? NavigationRequested
    {
        add { }
        remove { }
    }

    private void LoadFrom(UpdaterConfig config)
    {
        _openListMode.SelectedItem = config.OpenList.Mode;
        _openListUrl.Text = config.OpenList.BaseUrl;
        _openListUser.Text = config.OpenList.Username;
        _openListPassword.Text = config.OpenList.Password;
        _openListRoot.Text = config.OpenList.RootPath;
        _openListAutoDownload.IsChecked = config.OpenList.AutoDownloadBinary;
        _githubToken.Text = _service.GitTokens.GetGithubToken() ?? string.Empty;
        _gitlabToken.Text = _service.GitTokens.GetGitlabToken() ?? string.Empty;

        _concurrency.Text = config.Download.Concurrency.ToString();
        _speedLimit.Text = config.Download.MaxSpeedKBps.ToString();
        _cacheMinutes.Text = config.Download.ReleaseCacheMinutes.ToString();
        _proxy.Text = config.Download.Proxy;

        _transferEnabled.IsChecked = config.Transfer.Enabled;
        _transferPath.Text = config.Transfer.TransferPath;
        _cleanupPolicy.SelectedItem = config.Transfer.CleanupPolicy;

        RefreshPanStatus();
    }

    private Control BuildLayout()
    {
        var body = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                Header("OpenList"),
                Field("运行模式", _openListMode),
                Field("实例地址", _openListUrl),
                Field("用户名", _openListUser),
                Field("密码", _openListPassword),
                Field("网盘根路径", _openListRoot),
                _openListAutoDownload,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { CheckButton("检测 OpenList", () => _service.CheckOpenListAsync(BuildOpenListProbe())) },
                },
                Header("GitHub / GitLab 访问令牌"),
                Field("GitHub 令牌（匿名访问限 60 次/小时，配置后 5000 次/小时）", _githubToken),
                Field("GitLab 令牌（对 gitlab.com 校验；自建实例请在各渠道的 BaseUrl 配置中填写同令牌）", _gitlabToken),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        CheckButton("检测 GitHub 令牌", () => _service.CheckGithubTokenAsync()),
                        CheckButton("检测 GitLab 令牌", () => _service.CheckGitlabTokenAsync()),
                    },
                },
                Header("网盘账号"),
                _panStatus,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        LoginButton("登录百度网盘", PanPlatform.Baidu),
                        LoginButton("登录夸克网盘", PanPlatform.Quark),
                        ClearButton(),
                        CheckButton("检测百度登录", () => _service.CheckBaiduSessionAsync()),
                        CheckButton("检测夸克登录", () => _service.CheckQuarkSessionAsync()),
                    },
                },
                _credStatus,
                Header("下载"),
                Field("分片并发数（1-8）", _concurrency),
                Field("全局限速 KB/s（0 为不限）", _speedLimit),
                Field("版本缓存时间（分钟）", _cacheMinutes),
                Field("HTTP 代理", _proxy),
                Header("网盘转存"),
                _transferEnabled,
                Field("转存目录", _transferPath),
                Field("清理策略", _cleanupPolicy),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(0, 8, 0, 0),
                    Children =
                    {
                        SaveButton(),
                        CleanupButton(),
                        OpenFolderButton(),
                    },
                },
                _saveHint,
            },
        };

        return new ScrollViewer { Content = body };
    }

    private Button SaveButton()
    {
        var save = new Button { Content = "保存设置", Padding = new Thickness(18, 6) };
        save.Click += (_, _) => Save();
        return save;
    }

    private Button CleanupButton()
    {
        var cleanup = new Button { Content = "清理下载残留", Padding = new Thickness(14, 6) };
        cleanup.Click += (_, _) =>
        {
            try
            {
                var (files, bytes) = _service.CleanupStale(TimeSpan.FromDays(2));
                _saveHint.Text = $"已清理 {files} 个文件，释放 {bytes / (1024d * 1024):0.##} MB。";
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                _saveHint.Text = $"清理失败：{ex.Message}";
            }
        };
        return cleanup;
    }

    private Button OpenFolderButton()
    {
        var open = new Button { Content = "打开数据目录", Padding = new Thickness(14, 6) };
        open.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.Default.Root)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        };
        return open;
    }

    /// <summary>凭据检测按钮：用设置页当前填写值探测，结果写状态行。</summary>
    private Button CheckButton(string text, Func<Task<CredentialCheckResult>> check)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 6) };
        button.Click += async (_, _) =>
        {
            _credStatus.Text = "正在检测…";
            try
            {
                var result = await check();
                _credStatus.Text = $"{result.Name}：{result.Message}";
                _credStatus.Opacity = result.Ok ? 0.85 : 1;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                _credStatus.Text = $"检测失败：{ex.Message}";
            }
        };
        return button;
    }

    /// <summary>把设置页当前填写的 OpenList 值构造成探测配置（无需先保存）。</summary>
    private OpenListConfig BuildOpenListProbe() => new()
    {
        Mode = (OpenListMode)(_openListMode.SelectedItem ?? OpenListMode.Disabled),
        BaseUrl = _openListUrl.Text?.Trim(),
        Username = _openListUser.Text?.Trim(),
        Password = _openListPassword.Text,
        Token = _service.Config.OpenList.Token,
        RootPath = _openListRoot.Text?.Trim(),
        LocalPort = _service.Config.OpenList.LocalPort,
    };

    private Button LoginButton(string text, string platform)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 6) };
        button.Click += async (_, _) =>
        {
            var host = new AvaloniaShellHost();
            if (await host.ShowPanLoginAsync(platform))
            {
                RefreshPanStatus();
            }
        };
        return button;
    }

    private Button ClearButton()
    {
        var clear = new Button { Content = "清除登录信息", Padding = new Thickness(14, 6) };
        clear.Click += (_, _) =>
        {
            try
            {
                _service.PanSessions.Clear(PanPlatform.Baidu);
                _service.PanSessions.Clear(PanPlatform.Quark);
                RefreshPanStatus();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        };
        return clear;
    }

    private void RefreshPanStatus()
    {
        bool baidu = _service.PanSessions.HasSession(PanPlatform.Baidu);
        bool quark = _service.PanSessions.HasSession(PanPlatform.Quark);
        _panStatus.Text = $"百度网盘：{(baidu ? "已登录" : "未登录")}　|　夸克网盘：{(quark ? "已登录" : "未登录")}";
    }

    private void Save()
    {
        try
        {
            var config = _service.Config;
            config.OpenList.Mode = (OpenListMode)(_openListMode.SelectedItem ?? OpenListMode.Disabled);
            config.OpenList.BaseUrl = _openListUrl.Text?.Trim() ?? config.OpenList.BaseUrl;
            config.OpenList.Username = _openListUser.Text?.Trim();
            config.OpenList.Password = string.IsNullOrWhiteSpace(_openListPassword.Text) ? null : _openListPassword.Text;
            config.OpenList.RootPath = string.IsNullOrWhiteSpace(_openListRoot.Text) ? "/" : _openListRoot.Text.Trim();
            config.OpenList.AutoDownloadBinary = _openListAutoDownload.IsChecked == true;

            // 令牌存 SecretStore（DPAPI 加密），不进 updater.json 明文；留空表示清除
            _service.GitTokens.SetGithubToken(_githubToken.Text);
            _service.GitTokens.SetGitlabToken(_gitlabToken.Text);

            config.Download.Concurrency = ParseInt(_concurrency.Text, config.Download.Concurrency, 1, 8);
            config.Download.MaxSpeedKBps = ParseInt(_speedLimit.Text, config.Download.MaxSpeedKBps, 0, 1024 * 1024);
            config.Download.ReleaseCacheMinutes = ParseInt(_cacheMinutes.Text, config.Download.ReleaseCacheMinutes, 1, 720);
            config.Download.Proxy = string.IsNullOrWhiteSpace(_proxy.Text) ? null : _proxy.Text.Trim();

            config.Transfer.Enabled = _transferEnabled.IsChecked == true;
            config.Transfer.TransferPath = string.IsNullOrWhiteSpace(_transferPath.Text)
                ? config.Transfer.TransferPath
                : _transferPath.Text.Trim();
            config.Transfer.CleanupPolicy = (TransferCleanupPolicy)(_cleanupPolicy.SelectedItem ?? TransferCleanupPolicy.DeleteAfterSuccess);

            _service.SaveConfig();
            CustomChannelsIntegration.Current?.ReloadFromConfig();
            _saveHint.Text = $"已保存到 {_service.ConfigPath}";
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            _saveHint.Text = $"保存失败：{ex.Message}";
        }
    }

    private static int ParseInt(string? text, int fallback, int min, int max) =>
        int.TryParse(text?.Trim(), out int value) ? Math.Clamp(value, min, max) : fallback;

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 10, 0, 2),
    };

    private static Control Field(string label, Control input) => new StackPanel
    {
        Spacing = 3,
        Children =
        {
            new TextBlock { Text = label, FontSize = 12, Opacity = 0.75 },
            input,
        },
    };
}
