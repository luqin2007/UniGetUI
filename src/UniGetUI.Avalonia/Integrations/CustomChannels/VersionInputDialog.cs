using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace UniGetUI.Avalonia.Integrations.CustomChannels;

/// <summary>版本未知时手动填写本地版本的小对话框。</summary>
internal sealed class VersionInputDialog : Window
{
    private readonly TextBox _input = new();

    private VersionInputDialog(string appName, string? currentVersion)
    {
        Title = $"填写当前版本 - {appName}";
        Width = 420;
        Height = 190;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _input.Text = currentVersion ?? string.Empty;
        _input.Watermark = "例如 1.5.2";

        var ok = new Button { Content = "保存", Padding = new Thickness(18, 6), IsDefault = true };
        ok.Click += (_, _) => Close(_input.Text?.Trim());

        var cancel = new Button { Content = "取消", Padding = new Thickness(18, 6), IsCancel = true };
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "读取不到本地版本，请手动填写（只影响版本比较，不会修改程序文件）。",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.8,
                },
                _input,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { ok, cancel },
                },
            },
        };

        Opened += (_, _) => _input.Focus();
    }

    public static async Task<string?> ShowAsync(Window? owner, string appName, string? currentVersion)
    {
        if (owner is null)
        {
            return null;
        }

        var dialog = new VersionInputDialog(appName, currentVersion);
        return await dialog.ShowDialog<string?>(owner).ConfigureAwait(true);
    }
}
