using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Core.Logging;

namespace UniGetUI.Avalonia.Integrations.CustomChannels;

/// <summary>
/// 「自定义渠道」页面。所有控件用代码构建、显式订阅属性变更，不依赖 XAML/反射绑定，
/// 从而兼容上游的 NativeAOT + 全量裁剪发布。
/// </summary>
internal sealed class CustomChannelsPage : UserControl
{
    private readonly CustomChannelsViewModel _viewModel;
    private readonly StackPanel _cardsPanel = new() { Spacing = 6 };
    private readonly StackPanel _detailsPanel = new() { Spacing = 10 };
    private readonly ListBox _versionsList = new();
    private readonly TextBlock _statusText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Opacity = 0.8 };
    private readonly TextBlock _configError = new() { FontSize = 11, Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _detailsTitle = new() { FontSize = 16, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _detailsSubtitle = new() { FontSize = 12, Opacity = 0.75 };
    private readonly TextBlock _notesHeader = new() { FontSize = 13, FontWeight = FontWeight.SemiBold };
    private readonly SelectableTextBlock _notesText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _readmeHeader = new() { FontSize = 13, FontWeight = FontWeight.SemiBold };
    private readonly SelectableTextBlock _readmeText = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
    private readonly Button _installButton;
    private readonly Button _editButton;
    private readonly Button _removeButton;
    private readonly Button _manualVersionButton;

    public CustomChannelsPage(CustomChannelsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        _installButton = MakeButton("安装选中版本", () =>
        {
            if (_viewModel.SelectedCard is { } card && _viewModel.SelectedVersion is { } version)
            {
                _viewModel.Install(card, version);
            }
        });
        _editButton = MakeAsyncButton("编辑", async () =>
        {
            if (_viewModel.SelectedCard is { } card)
            {
                await _viewModel.EditTargetAsync(TopLevel.GetTopLevel(this) as Window, card);
            }
        });
        _removeButton = MakeButton("删除", () =>
        {
            if (_viewModel.SelectedCard is { } card)
            {
                _viewModel.RemoveTarget(card);
            }
        });
        _manualVersionButton = MakeAsyncButton("填写当前版本", async () =>
        {
            if (_viewModel.SelectedCard is not { } card)
            {
                return;
            }

            var value = await VersionInputDialog.ShowAsync(
                TopLevel.GetTopLevel(this) as Window,
                card.DisplayName,
                card.LocalVersion);
            if (!string.IsNullOrWhiteSpace(value))
            {
                await _viewModel.SetManualVersionAsync(card, value);
            }
        });

        Content = BuildLayout();
        HookEvents();
        RefreshCards();
        RefreshDetails();
        _viewModel.Initialize();
    }

    private Control BuildLayout()
    {
        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var checkAll = MakeAsyncButton("检查全部", async () => await _viewModel.CheckAllAsync(forceRefresh: true));
        var add = MakeAsyncButton("添加应用", async () =>
            await _viewModel.AddTargetAsync(TopLevel.GetTopLevel(this) as Window));

        Grid.SetColumn(checkAll, 0);
        Grid.SetColumn(add, 1);
        Grid.SetColumn(_statusText, 2);
        Grid.SetColumn(_configError, 3);
        toolbar.Children.Add(checkAll);
        toolbar.Children.Add(add);
        toolbar.Children.Add(_statusText);
        toolbar.Children.Add(_configError);

        var listPane = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Child = new ScrollViewer { Content = _cardsPanel, Padding = new Thickness(0, 2, 8, 2) },
        };
        listPane.Bind(Border.BorderBrushProperty, this.GetResourceObservable("AppBorderBrush"));
        listPane.Bind(Border.BackgroundProperty, this.GetResourceObservable("SettingsCardBackground"));

        var detailsPane = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = new ScrollViewer { Content = _detailsPanel, Padding = new Thickness(4, 0, 0, 4) },
        };
        detailsPane.Bind(Border.BorderBrushProperty, this.GetResourceObservable("AppBorderBrush"));
        detailsPane.Bind(Border.BackgroundProperty, this.GetResourceObservable("SettingsCardBackground"));

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,12,3*") };
        Grid.SetColumn(listPane, 0);
        Grid.SetColumn(detailsPane, 2);
        content.Children.Add(listPane);
        content.Children.Add(detailsPane);

        _versionsList.Height = 200;
        _versionsList.SelectionChanged += (_, _) =>
            _viewModel.SelectedVersion = _versionsList.SelectedItem as CustomChannelVersionViewModel;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _installButton, _manualVersionButton, _editButton, _removeButton },
        };

        _detailsPanel.Children.Add(_detailsTitle);
        _detailsPanel.Children.Add(_detailsSubtitle);
        _detailsPanel.Children.Add(new TextBlock { Text = "可用版本", FontSize = 13, FontWeight = FontWeight.SemiBold });
        _detailsPanel.Children.Add(_versionsList);
        _detailsPanel.Children.Add(_notesHeader);
        _detailsPanel.Children.Add(Boxed(_notesText));
        _detailsPanel.Children.Add(_readmeHeader);
        _detailsPanel.Children.Add(Boxed(_readmeText));
        _detailsPanel.Children.Add(buttons);

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Thickness(16),
        };
        Grid.SetRow(toolbar, 0);
        Grid.SetRow(content, 1);
        root.Children.Add(toolbar);
        root.Children.Add(content);
        return root;
    }

    private static Control Boxed(Control content) => new Border
    {
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(8),
        Margin = new Thickness(0, 0, 0, 4),
        Child = content,
    };

    private void HookEvents()
    {
        _viewModel.Cards.CollectionChanged += OnCardsChanged;
        _viewModel.Versions.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(RefreshDetails);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateStatus();
    }

    private void OnCardsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(RefreshCards);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CustomChannelsViewModel.StatusText):
                UpdateStatus();
                break;
            case nameof(CustomChannelsViewModel.ConfigError):
                _configError.Text = _viewModel.ConfigError;
                _configError.IsVisible = !string.IsNullOrWhiteSpace(_viewModel.ConfigError);
                break;
            case nameof(CustomChannelsViewModel.SelectedCard):
            case nameof(CustomChannelsViewModel.SelectedVersion):
                Dispatcher.UIThread.Post(RefreshDetails);
                break;
        }
    }

    private void UpdateStatus() => _statusText.Text = _viewModel.StatusText;

    private void RefreshCards()
    {
        _cardsPanel.Children.Clear();

        if (_viewModel.Cards.Count == 0)
        {
            _cardsPanel.Children.Add(new TextBlock
            {
                Text = "还没有应用。点击「添加应用」选择 GitHub/GitLab Release、OpenList、网盘分享或直链。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                Margin = new Thickness(4, 8),
            });
            return;
        }

        foreach (var card in _viewModel.Cards)
        {
            _cardsPanel.Children.Add(new CustomChannelCardControl(card, _viewModel));
        }
    }

    private void RefreshDetails()
    {
        _versionsList.ItemsSource = _viewModel.Versions;
        _versionsList.SelectedItem = _viewModel.SelectedVersion;

        var card = _viewModel.SelectedCard;
        _detailsTitle.Text = card?.DisplayName ?? "未选择应用";
        _detailsSubtitle.Text = card is null
            ? string.Empty
            : $"{card.SourceBadges} · {card.InstallKindText}";
        _notesHeader.Text = _viewModel.SelectedVersionNotesTitle;
        _notesText.Text = _viewModel.SelectedCardNotes;
        _readmeHeader.Text = _viewModel.SelectedVersion?.ReadmeTitle ?? "README";
        _readmeText.Text = _viewModel.SelectedCardReadme;

        _installButton.IsEnabled = card is not null && _viewModel.SelectedVersion?.IsSelectable == true;
        _editButton.IsEnabled = card is not null;
        _removeButton.IsEnabled = card is not null;

        if (_viewModel.SelectedVersion is { IsSelectable: false, BlockReason: { } reason })
        {
            _notesText.Text = $"{_notesText.Text}\n\n（不可安装：{reason}）";
        }
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(6) };
        button.Click += (_, _) =>
        {
            try
            {
                onClick();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        };
        return button;
    }

    private static Button MakeAsyncButton(string text, Func<Task> onClick)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(6) };
        button.Click += async (_, _) =>
        {
            try
            {
                await onClick();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        };
        return button;
    }

    /// <summary>单个应用卡片：显式订阅属性变更，避免反射绑定。</summary>
    private sealed class CustomChannelCardControl : Border
    {
        private readonly CustomChannelCardViewModel _card;
        private readonly TextBlock _name = new()
        {
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        private readonly TextBlock _version = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly TextBlock _sources = new()
        {
            FontSize = 11,
            Opacity = 0.7,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        private readonly TextBlock _kind = new() { FontSize = 11, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly TextBlock _error = new()
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brushes.OrangeRed,
        };

        private readonly Button _check;

        public CustomChannelCardControl(CustomChannelCardViewModel card, CustomChannelsViewModel viewModel)
        {
            _card = card;
            Background = Brushes.Transparent;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(8);
            Padding = new Thickness(10, 8);
            Cursor = new Cursor(StandardCursorType.Hand);

            var titleStack = new StackPanel { Spacing = 2, Children = { _name, _sources } };
            var versionStack = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { _version, _kind },
            };

            _check = new Button { Content = "检查", Padding = new Thickness(10, 2), Margin = new Thickness(8, 0, 0, 0) };
            _check.Click += async (_, e) =>
            {
                e.Handled = true;
                await viewModel.CheckSingleAsync(card);
            };

            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            Grid.SetColumn(versionStack, 1);
            Grid.SetColumn(_check, 2);
            header.Children.Add(titleStack);
            header.Children.Add(versionStack);
            header.Children.Add(_check);

            Child = new StackPanel { Spacing = 4, Children = { header, _error } };
            PointerPressed += (_, _) => viewModel.SelectedCard = card;

            _card.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(Render);
            Render();
        }

        private void Render()
        {
            _name.Text = _card.DisplayName;
            _version.Text = _card.VersionText;
            _sources.Text = _card.SourceBadges;
            _kind.Text = _card.InstallKindText;
            _error.Text = _card.Error ?? string.Empty;
            _error.IsVisible = !string.IsNullOrWhiteSpace(_card.Error);
            Opacity = _card.IsChecking ? 0.6 : 1;
            _check.Content = _card.IsChecking ? "检查中…" : "检查";
            _check.IsEnabled = !_card.IsChecking;
        }
    }
}
