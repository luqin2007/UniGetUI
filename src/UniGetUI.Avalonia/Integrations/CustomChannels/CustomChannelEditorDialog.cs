using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Detection;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Models;

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
    private readonly TextBlock _detectStatus = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly Func<TargetConfig, Task<TargetCheckResult>>? _checkAsync;
    private Control? _exeField;
    private Control? _targetFileField;
    private Control? _silentField;

    private CustomChannelEditorDialog(
        TargetConfig target,
        Func<TargetConfig, Task<TargetCheckResult>>? checkAsync)
    {
        _target = target;
        _checkAsync = checkAsync;
        Title = string.IsNullOrWhiteSpace(target.Id) ? "添加应用" : $"编辑 {target.DisplayName}";
        Width = 760;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        _installKind.ItemsSource = Enum.GetValues<InstallKind>();
        _versionMode.ItemsSource = Enum.GetValues<LocalVersionMode>();
        _installKind.SelectionChanged += (_, _) => UpdateFieldVisibility();

        _id.Text = target.Id;
        _id.Watermark = "唯一标识；留空时 GitHub/GitLab 渠道自动取仓库名";
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

        // 一个应用只保留一个渠道：编辑历史多渠道路配置时以第一个为准
        _rows.Add(new SourceRow(target.Sources.FirstOrDefault() ?? new SourceConfig { Type = SourceType.GithubRelease }));

        RebuildSourcePanel();

        Content = BuildLayout();
        UpdateFieldVisibility();
        Opened += (_, _) => _id.Focus();
    }

    public static async Task<TargetConfig?> ShowAsync(
        Window owner,
        TargetConfig? existing,
        Func<TargetConfig, Task<TargetCheckResult>>? checkAsync = null)
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

        var dialog = new CustomChannelEditorDialog(copy, checkAsync);
        return await dialog.ShowDialog<TargetConfig?>(owner).ConfigureAwait(true);
    }

    private Control BuildLayout()
    {
        var save = new Button { Content = "保存", Padding = new Thickness(18, 6) };
        save.Click += (_, _) => TrySave();

        var cancel = new Button { Content = "取消", Padding = new Thickness(18, 6) };
        cancel.Click += (_, _) => Close(null);

        _exeField = FieldWithBrowse("可执行文件", _exePath, BrowseExeAsync);
        _targetFileField = Field("目标文件名（单文件模式）", _targetFileName);
        _silentField = Field("静默参数（EXE）", _silentArgs);

        var body = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                Section("渠道"),
                _sourcePanel,
                _detectStatus,
                Section("基础信息"),
                Field("标识", _id),
                Field("显示名称", _displayName),
                Section("安装方式"),
                Field("安装类型", _installKind),
                FieldWithBrowse("安装目录", _rootPath, BrowseFolderAsync),
                _exeField,
                _targetFileField,
                _silentField,
                _interactive,
                _launchAfter,
                _elevated,
                Section("版本与策略"),
                Field("本地版本读取方式", _versionMode),
                Field("版本文件路径（可选）", _versionPath),
                _allowDowngrade,
                Field("压缩包候选密码", _archivePasswords),
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

    /// <summary>按安装类型隐藏无意义的字段，减少误填。</summary>
    private void UpdateFieldVisibility()
    {
        var kind = (InstallKind)(_installKind.SelectedItem ?? InstallKind.ZipOverwrite);

        if (_exeField is not null)
        {
            // 只有解压覆盖与单文件模式需要知道主程序位置
            _exeField.IsVisible = kind is InstallKind.ZipOverwrite or InstallKind.SingleFile;
        }

        if (_targetFileField is not null)
        {
            _targetFileField.IsVisible = kind == InstallKind.SingleFile;
        }

        if (_silentField is not null)
        {
            _silentField.IsVisible = kind == InstallKind.Exe;
        }

        _interactive.IsVisible = kind == InstallKind.Exe;
    }

    private void RebuildSourcePanel()
    {
        _sourcePanel.Children.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var detect = new Button { Content = "识别", Padding = new Thickness(12, 2) };
            detect.Click += async (_, _) => await DetectAsync();
            row.PrimaryCommitted += OnSourcePrimaryCommitted;
            _sourcePanel.Children.Add(row.Build(detect));
        }
    }

    private async Task DetectAsync()
    {
        if (_rows.Count == 0 || _checkAsync is null)
        {
            return;
        }

        var source = _rows[0].ToConfig();
        if (source is null)
        {
            ShowError("请先填写渠道信息（仓库 owner/repo、分享链接或直链）。");
            return;
        }

        _detectStatus.Text = "正在识别…";
        try
        {
            var probe = new TargetConfig
            {
                Id = string.IsNullOrWhiteSpace(_id.Text) ? "detect" : _id.Text!.Trim(),
                DisplayName = _displayName.Text?.Trim(),
                Install = new InstallConfig
                {
                    Kind = (InstallKind)(_installKind.SelectedItem ?? InstallKind.ZipOverwrite),
                    RootPath = _rootPath.Text?.Trim() ?? string.Empty,
                },
                Version = new LocalVersionConfig { Mode = LocalVersionMode.FileVersion },
                Sources = [source],
            };

            var result = await _checkAsync(probe);
            var candidate = result.Best ?? result.Candidates.FirstOrDefault();
            if (candidate is null)
            {
                _detectStatus.Text = result.Error is { Length: > 0 } error
                    ? $"识别失败：{error}"
                    : "识别失败：渠道未返回任何版本。";
                return;
            }

            var detection = source.Type is SourceType.GithubRelease or SourceType.GitlabRelease
                ? InstallDetector.FromAssetNames(candidate.Version.Assets.Select(a => a.Name))
                : InstallDetector.FromUrl(source.Url ?? source.ShareUrl ?? source.OpenListPath);

            ApplyDetection(source, candidate, detection);
        }
        catch (Exception ex)
        {
            _detectStatus.Text = $"识别失败：{ex.Message}";
        }
    }

    private void ApplyDetection(SourceConfig source, VersionCandidate candidate, InstallDetection detection)
    {
        // 标识用小写 slug；显示名称保留原始大小写（例如 HybridFileXfer）
        string rawName = source.Type is SourceType.GithubRelease or SourceType.GitlabRelease
            ? (source.Repo ?? source.ProjectId ?? string.Empty).Trim().Split('/')[^1].Trim()
            : InstallDetector.NameFromUrl(source.Url ?? source.ShareUrl ?? source.OpenListPath);
        if (rawName.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            rawName = rawName[..^4];
        }

        string suggested = InstallDetector.ToSlug(rawName);

        if (string.IsNullOrWhiteSpace(_id.Text) && suggested.Length > 0)
        {
            _id.Text = suggested;
        }

        if (string.IsNullOrWhiteSpace(_displayName.Text) && rawName.Length > 0)
        {
            _displayName.Text = rawName;
        }

        _installKind.SelectedItem = detection.Kind;
        if (!string.IsNullOrWhiteSpace(detection.TargetFileName))
        {
            _targetFileName.Text = detection.TargetFileName;
        }

        if (!string.IsNullOrWhiteSpace(detection.ExePath))
        {
            _exePath.Text = detection.ExePath;
        }

        // 版本策略默认从文件读取；单文件模式直接指向识别到的可执行文件
        _versionMode.SelectedItem = LocalVersionMode.FileVersion;
        if (detection.Kind == InstallKind.SingleFile && !string.IsNullOrWhiteSpace(detection.ExePath))
        {
            _versionPath.Text = detection.ExePath;
        }

        _detectStatus.Text =
            $"{detection.Reason}（最新版本 {candidate.Version.DisplayVersion}，共 {candidate.Version.Assets.Count} 个资产）";
    }

    private async Task BrowseFolderAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择安装目录",
            AllowMultiple = false,
            SuggestedStartLocation = await TryGetFolderAsync(_rootPath.Text),
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { Length: > 0 } path)
        {
            _rootPath.Text = path;
            AutoFillExecutable(path);
        }
    }

    /// <summary>选定安装目录后，优先选择与标识/显示名称同名的 exe，否则取第一个 exe。</summary>
    private void AutoFillExecutable(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            var exes = Directory.EnumerateFiles(root, "*.exe", SearchOption.TopDirectoryOnly).ToList();
            if (exes.Count == 0)
            {
                return;
            }

            string id = _id.Text?.Trim() ?? string.Empty;
            string name = _displayName.Text?.Trim() ?? string.Empty;

            string chosen = exes.FirstOrDefault(f => string.Equals(
                                Path.GetFileNameWithoutExtension(f), id, StringComparison.OrdinalIgnoreCase))
                            ?? exes.FirstOrDefault(f => string.Equals(
                                Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase))
                            ?? exes[0];

            _exePath.Text = Path.GetFileName(chosen);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"扫描目录失败：{ex.Message}");
        }
    }

    private async Task BrowseExeAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择可执行文件",
            AllowMultiple = false,
            SuggestedStartLocation = await TryGetFolderAsync(_rootPath.Text),
            FileTypeFilter = [new FilePickerFileType("可执行文件") { Patterns = ["*.exe"] }],
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { Length: > 0 } fullPath)
        {
            return;
        }

        string root = _rootPath.Text?.Trim() ?? string.Empty;
        if (root.Length > 0 && fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            _exePath.Text = Path.GetRelativePath(root, fullPath);
        }
        else
        {
            _exePath.Text = fullPath;
        }
    }

    private static async Task<IStorageFolder?> TryGetFolderAsync(string? path)
    {
        // 起始目录由调用方忽略（无 owner 时无法安全解析），保持系统默认位置
        await Task.CompletedTask;
        return null;
    }

    /// <summary>标识留空时，从 GitHub/GitLab 渠道的仓库名推导一个可用标识。</summary>
    private string? DeriveIdFromSources()
    {
        foreach (var row in _rows)
        {
            var suggested = row.SuggestId();
            if (!string.IsNullOrWhiteSpace(suggested))
            {
                return suggested;
            }
        }

        return null;
    }

    private void OnSourcePrimaryCommitted(object? sender, EventArgs e)
    {
        // 用户填完仓库地址、且尚未填写标识时，自动补上仓库名，省一次手动输入
        if (!string.IsNullOrWhiteSpace(_id.Text) || sender is not SourceRow row)
        {
            return;
        }

        if (row.SuggestId() is { Length: > 0 } suggested)
        {
            _id.Text = suggested;
            if (string.IsNullOrWhiteSpace(_displayName.Text) && row.SuggestName() is { Length: > 0 } display)
            {
                _displayName.Text = display;
            }
        }
    }

    private void TrySave()
    {
        string id = _id.Text?.Trim() ?? string.Empty;
        if (id.Length == 0)
        {
            id = DeriveIdFromSources() ?? string.Empty;
            if (id.Length > 0)
            {
                _id.Text = id;
            }
        }

        if (id.Length == 0)
        {
            ShowError("请填写应用标识（或先选择 GitHub/GitLab 仓库，标识会自动取仓库名）。");
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

    /// <summary>带「浏览」按钮的输入行（目录用文件夹选择器，文件用文件选择器）。</summary>
    private Control FieldWithBrowse(string label, Control input, Func<Task> browse)
    {
        var button = new Button { Content = "浏览…", Padding = new Thickness(10, 2), Margin = new Thickness(6, 0, 0, 0) };
        button.Click += async (_, _) =>
        {
            try
            {
                await browse();
            }
            catch (Exception ex)
            {
                ShowError($"选择失败：{ex.Message}");
            }
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(input, 0);
        Grid.SetColumn(button, 1);
        row.Children.Add(input);
        row.Children.Add(button);

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = label, FontSize = 12, Opacity = 0.75 },
                row,
            },
        };
    }

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

        /// <summary>仓库/项目输入框失焦时触发，供外层自动填充标识。</summary>
        public event EventHandler? PrimaryCommitted;

        public SourceRow(SourceConfig source)
        {
            _original = source;
            _type.ItemsSource = Enum.GetValues<SourceType>();
            _type.SelectedItem = source.Type;
            _prerelease.IsChecked = source.IncludePrerelease;
            _type.SelectionChanged += (_, _) => ApplyLabelsAndValues();
            _primary.LostFocus += (_, _) => PrimaryCommitted?.Invoke(this, EventArgs.Empty);

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

        /// <summary>从仓库/项目地址推导标识：取最后一段并规范化为小写 slug。</summary>
        public string? SuggestId() => InstallDetector.ToSlug(SuggestName()) is { Length: > 0 } slug ? slug : null;

        /// <summary>从仓库/项目地址取显示名称：保留原始大小写（例如 HybridFileXfer）。</summary>
        public string? SuggestName()
        {
            var type = (SourceType)(_type.SelectedItem ?? SourceType.GithubRelease);
            if (type is not (SourceType.GithubRelease or SourceType.GitlabRelease))
            {
                return null;
            }

            string value = _primary.Text?.Trim() ?? string.Empty;
            if (value.Length == 0)
            {
                return null;
            }

            // 支持直接粘贴完整地址：https://github.com/owner/repo(.git)
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                value = uri.AbsolutePath;
            }

            var segments = value.Trim('/', '\\').Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return null;
            }

            string name = segments[^1];
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }

            return name;
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
