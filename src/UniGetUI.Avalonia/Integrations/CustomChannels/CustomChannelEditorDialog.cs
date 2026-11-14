using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;

namespace UniGetUI.Avalonia.Integrations.CustomChannels;

/// <summary>
/// 应用与渠道编辑器。比 WPF 版简化：只保留常用字段，其余高级项仍可通过配置文件编辑。
/// 保存时返回一份深拷贝，取消不会修改原对象。
/// </summary>
internal sealed class CustomChannelEditorDialog : Window
{
    private readonly TargetConfig _target;
    private readonly TextBox _id = new();
    private readonly TextBox _displayName = new();
    private readonly ComboBox _installKind = new();
    private readonly TextBox _rootPath = new();
    private readonly TextBox _exePath = new();
    private readonly TextBox _targetFileName = new();
    private readonly TextBox _silentArgs = new();
    private readonly CheckBox _interactive = new() { Content = "安装程序交互运行（不静默）" };
    private readonly CheckBox _launchAfter = new() { Content = "更新后启动程序" };
    private readonly CheckBox _elevated = new() { Content = "需要管理员权限" };
    private readonly CheckBox _allowDowngrade = new() { Content = "允许安装低于本地的版本" };
    private readonly ComboBox _versionMode = new();
    private readonly TextBox _versionPath = new();
    private readonly TextBox _archivePasswords = new() { Watermark = "多个密码用英文逗号分隔" };
    private readonly StackPanel _sourcePanel = new() { Spacing = 8 };
    private readonly List<SourceRow> _rows = [];
    private readonly TextBlock _error = new() { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap };

    private CustomChannelEditorDialog(TargetConfig target)
    {
        _target = target;
        Title = string.IsNullOrWhiteSpace(target.Id) ? "添加应用" : $"编辑 {target.DisplayName}";
        Width = 760;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        _installKind.ItemsSource = Enum.GetValues<InstallKind>();
        _versionMode.ItemsSource = Enum.GetValues<LocalVersionMode>();

        _id.Text = target.Id;
        _id.Watermark = "唯一标识，例如 vscode-portable";
        _displayName.Text = target.DisplayName;
        _displayName.Watermark = "显示名称";
        _installKind.SelectedItem = target.Install.Kind;
        _rootPath.Text = target.Install.RootPath;
        _rootPath.Watermark = @"C:\Apps\MyApp";
        _exePath.Text = target.Install.ExePath;
        _exePath.Watermark = @"MyApp.exe（相对安装目录）";
        _targetFileName.Text = target.Install.TargetFileName;
        _targetFileName.Watermark = "单文件模式的目标文件名";
        _silentArgs.Text = target.Install.SilentArgs;
        _silentArgs.Watermark = "/S 或 /SILENT";
        _interactive.IsChecked = target.Install.Interactive;
        _launchAfter.IsChecked = target.Install.LaunchAfterUpdate;
        _elevated.IsChecked = target.Install.RequiresElevation;
        _allowDowngrade.IsChecked = target.Policy.AllowDowngrade;
        _versionMode.SelectedItem = target.Version.Mode;
        _versionPath.Text = target.Version.Path;
        _archivePasswords.Text = string.Join(",", target.Archive.Passwords);

        foreach (var source in target.Sources)
        {
            _rows.Add(new SourceRow(source));
        }

        RebuildSourcePanel();

        Content = BuildLayout();
        Opened += (_, _) => _id.Focus();
    }

    public static async Task<TargetConfig?> ShowAsync(Window owner, TargetConfig? existing)
    {
        var copy = existing is null
            ? new TargetConfig
            {
                Sources =
                [
                    new SourceConfig { Type = SourceType.GithubRelease },
                ],
            }
            : JsonSerializer.Deserialize(
                JsonSerializer.Serialize(existing, AppJsonContext.Default.TargetConfig),
                AppJsonContext.Default.TargetConfig)!;

        var dialog = new CustomChannelEditorDialog(copy);
        return await dialog.ShowDialog<TargetConfig?>(owner).ConfigureAwait(true);
    }

    private Control BuildLayout()
    {
        var addSource = new Button { Content = "+ 添加渠道", Padding = new Thickness(12, 6) };
        addSource.Click += (_, _) =>
        {
            _rows.Add(new SourceRow(new SourceConfig { Type = SourceType.GithubRelease }));
            RebuildSourcePanel();
        };

        var save = new Button { Content = "保存", Padding = new Thickness(18, 6) };
        save.Click += (_, _) => TrySave();

        var cancel = new Button { Content = "取消", Padding = new Thickness(18, 6) };
        cancel.Click += (_, _) => Close(null);

        var body = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                Section("基础信息"),
                Field("标识", _id),
                Field("显示名称", _displayName),
                Section("安装方式"),
                Field("安装类型", _installKind),
                Field("安装目录", _rootPath),
                Field("可执行文件", _exePath),
                Field("目标文件名（单文件模式）", _targetFileName),
                Field("静默参数（EXE）", _silentArgs),
                _interactive,
                _launchAfter,
                _elevated,
                Section("版本与策略"),
                Field("本地版本读取方式", _versionMode),
                Field("版本文件路径（可选）", _versionPath),
                _allowDowngrade,
                Field("压缩包候选密码", _archivePasswords),
                Section("渠道"),
                _sourcePanel,
                addSource,
                _error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { save, cancel },
                },
            },
        };

        return new ScrollViewer { Content = body };
    }

    private void RebuildSourcePanel()
    {
        _sourcePanel.Children.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            int index = i;
            var row = _rows[i];
            var remove = new Button { Content = "移除", Padding = new Thickness(8, 2) };
            remove.Click += (_, _) =>
            {
                _rows.RemoveAt(index);
                RebuildSourcePanel();
            };
            _sourcePanel.Children.Add(row.Build(remove));
        }
    }

    private void TrySave()
    {
        string id = _id.Text?.Trim() ?? string.Empty;
        if (id.Length == 0)
        {
            ShowError("请填写应用标识。");
            return;
        }

        string root = _rootPath.Text?.Trim() ?? string.Empty;
        if (root.Length == 0 || !Path.IsPathFullyQualified(root))
        {
            ShowError(@"安装目录必须是绝对路径，例如 C:\Apps\MyApp。");
            return;
        }

        var rows = _rows.Select(r => r.ToConfig()).Where(s => s is not null).Select(s => s!).ToList();
        if (rows.Count == 0)
        {
            ShowError("至少需要一个可用渠道（填写了必填参数的渠道）。");
            return;
        }

        _target.Id = id;
        _target.DisplayName = string.IsNullOrWhiteSpace(_displayName.Text) ? id : _displayName.Text.Trim();
        _target.Install.Kind = (InstallKind)(_installKind.SelectedItem ?? InstallKind.ZipOverwrite);
        _target.Install.RootPath = root;
        _target.Install.ExePath = NullIfEmpty(_exePath.Text);
        _target.Install.TargetFileName = NullIfEmpty(_targetFileName.Text);
        _target.Install.SilentArgs = NullIfEmpty(_silentArgs.Text);
        _target.Install.Interactive = _interactive.IsChecked == true;
        _target.Install.LaunchAfterUpdate = _launchAfter.IsChecked == true;
        _target.Install.RequiresElevation = _elevated.IsChecked == true;
        _target.Version.Mode = (LocalVersionMode)(_versionMode.SelectedItem ?? LocalVersionMode.FileVersion);
        _target.Version.Path = NullIfEmpty(_versionPath.Text);
        _target.Policy.AllowDowngrade = _allowDowngrade.IsChecked == true;
        _target.Archive.Passwords = (_archivePasswords.Text ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        _target.Sources = rows;

        Close(_target);
    }

    private void ShowError(string message) => _error.Text = message;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        FontSize = 13,
        Margin = new Thickness(0, 8, 0, 0),
    };

    private static Control Field(string label, Control input) => new StackPanel
    {
        Spacing = 4,
        Children =
        {
            new TextBlock { Text = label, FontSize = 12, Opacity = 0.75 },
            input,
        },
    };

    /// <summary>一行渠道编辑：类型 + 主参数 + 资产匹配 + 可选口令/密码。</summary>
    private sealed class SourceRow
    {
        private readonly ComboBox _type = new();
        private readonly TextBox _primary = new();
        private readonly TextBox _secondary = new();
        private readonly TextBox _pattern = new();
        private readonly CheckBox _prerelease = new() { Content = "包含预发布版本" };
        private readonly TextBlock _primaryLabel = new() { FontSize = 12, Opacity = 0.75 };
        private readonly TextBlock _secondaryLabel = new() { FontSize = 12, Opacity = 0.75 };
        private readonly TextBlock _patternLabel = new() { FontSize = 12, Opacity = 0.75 };
        private readonly SourceConfig _original;

        public SourceRow(SourceConfig source)
        {
            _original = source;
            _type.ItemsSource = Enum.GetValues<SourceType>();
            _type.SelectedItem = source.Type;
            _prerelease.IsChecked = source.IncludePrerelease;
            _type.SelectionChanged += (_, _) => ApplyLabelsAndValues();

            ApplyLabelsAndValues();
            _primary.Text = PrimaryValue(source);
            _secondary.Text = source.BaseUrl ?? source.Pwd ?? source.BaseUrl;
            _pattern.Text = PatternValue(source);
            _prerelease.IsChecked = source.IncludePrerelease;
        }

        public Control Build(Control removeButton)
        {
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*,Auto") };
            Grid.SetColumn(_type, 0);
            var spacer = new Border { Width = 8 };
            Grid.SetColumn(spacer, 1);
            Grid.SetColumn(removeButton, 2);
            header.Children.Add(_type);
            header.Children.Add(spacer);
            header.Children.Add(removeButton);

            return new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 4),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        header,
                        _primaryLabel,
                        _primary,
                        _secondaryLabel,
                        _secondary,
                        _patternLabel,
                        _pattern,
                        _prerelease,
                    },
                },
            };
        }

        public SourceConfig? ToConfig()
        {
            var type = (SourceType)(_type.SelectedItem ?? SourceType.GithubRelease);
            var config = new SourceConfig
            {
                Type = type,
                Enabled = true,
                AssetPattern = NullIfEmpty(_pattern.Text),
                IncludePrerelease = _prerelease.IsChecked == true,
                DisplayName = _original.DisplayName,
            };

            string primary = _primary.Text?.Trim() ?? string.Empty;
            string secondary = _secondary.Text?.Trim() ?? string.Empty;
            switch (type)
            {
                case SourceType.GithubRelease:
                    if (primary.Length == 0)
                    {
                        return null;
                    }

                    config.Repo = primary;
                    if (secondary.Length > 0)
                    {
                        config.BaseUrl = secondary;
                    }

                    break;
                case SourceType.GitlabRelease:
                    if (primary.Length == 0)
                    {
                        return null;
                    }

                    config.ProjectId = primary;
                    if (secondary.Length > 0)
                    {
                        config.BaseUrl = secondary;
                    }

                    break;
                case SourceType.BaiduPan:
                case SourceType.QuarkPan:
                    if (primary.Length == 0)
                    {
                        return null;
                    }

                    config.ShareUrl = primary;
                    config.Pwd = secondary.Length > 0 ? secondary : null;
                    config.PathPattern = NullIfEmpty(_pattern.Text);
                    break;
                case SourceType.OpenList:
                    if (primary.Length == 0)
                    {
                        return null;
                    }

                    config.OpenListPath = primary;
                    config.FilePattern = NullIfEmpty(_pattern.Text);
                    break;
                case SourceType.DirectUrl:
                    if (primary.Length == 0)
                    {
                        return null;
                    }

                    config.Url = primary;
                    config.Sha256 = secondary.Length > 0 ? secondary : null;
                    break;
            }

            return config;
        }

        private void ApplyLabelsAndValues()
        {
            var type = (SourceType)(_type.SelectedItem ?? SourceType.GithubRelease);
            switch (type)
            {
                case SourceType.GithubRelease:
                    _primaryLabel.Text = "仓库（owner/repo）";
                    _secondaryLabel.Text = "API 地址（GitHub Enterprise 可选）";
                    _patternLabel.Text = "资产匹配（可选，留空自动选择）";
                    _secondaryLabel.IsVisible = _secondary.IsVisible = true;
                    break;
                case SourceType.GitlabRelease:
                    _primaryLabel.Text = "项目 ID 或 group/project";
                    _secondaryLabel.Text = "GitLab 实例地址（自建时填写）";
                    _patternLabel.Text = "资产匹配（可选）";
                    _secondaryLabel.IsVisible = _secondary.IsVisible = true;
                    break;
                case SourceType.BaiduPan:
                    _primaryLabel.Text = "分享链接";
                    _secondaryLabel.Text = "提取码（可选）";
                    _patternLabel.Text = "目录内文件匹配（如 /latest/App-*.zip）";
                    _secondaryLabel.IsVisible = _secondary.IsVisible = true;
                    break;
                case SourceType.QuarkPan:
                    _primaryLabel.Text = "分享链接";
                    _secondaryLabel.Text = "提取码（可选）";
                    _patternLabel.Text = "目录内文件匹配";
                    _secondaryLabel.IsVisible = _secondary.IsVisible = true;
                    break;
                case SourceType.OpenList:
                    _primaryLabel.Text = "OpenList 目录路径";
                    _secondaryLabel.Text = "（不使用）";
                    _patternLabel.Text = "文件匹配（如 App-*.zip）";
                    _secondaryLabel.IsVisible = _secondary.IsVisible = false;
                    break;
                case SourceType.DirectUrl:
                    _primaryLabel.Text = "下载直链（HTTPS）";
                    _secondaryLabel.Text = "SHA256（可选）";
                    _patternLabel.Text = "（不使用）";
                    _secondaryLabel.IsVisible = _secondary.IsVisible = true;
                    _pattern.IsVisible = false;
                    break;
            }

            if (type != SourceType.DirectUrl)
            {
                _pattern.IsVisible = true;
            }

            _prerelease.IsVisible = type is SourceType.GithubRelease or SourceType.GitlabRelease;
        }

        private static string? PrimaryValue(SourceConfig source) => source.Type switch
        {
            SourceType.GithubRelease => source.Repo,
            SourceType.GitlabRelease => source.ProjectId,
            SourceType.BaiduPan or SourceType.QuarkPan => source.ShareUrl,
            SourceType.OpenList => source.OpenListPath,
            _ => source.Url,
        };

        private static string? PatternValue(SourceConfig source) => source.Type switch
        {
            SourceType.BaiduPan or SourceType.QuarkPan => source.PathPattern,
            SourceType.OpenList => source.FilePattern,
            _ => source.AssetPattern,
        };

        private static string? NullIfEmpty(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
