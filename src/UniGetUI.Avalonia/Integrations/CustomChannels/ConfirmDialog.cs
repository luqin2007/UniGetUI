using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace UniGetUI.Avalonia.Integrations.CustomChannels;

/// <summary>简单的是/否确认框。</summary>
internal sealed class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string yesText, string noText)
    {
        Title = title;
        Width = 460;
        Height = 200;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var yes = new Button { Content = yesText, Padding = new Thickness(18, 6), IsDefault = true };
        yes.Click += (_, _) => Close(true);

        var no = new Button { Content = noText, Padding = new Thickness(18, 6), IsCancel = true };
        no.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { yes, no },
                },
            },
        };
    }

    public static async Task<bool> ShowAsync(
        Window? owner,
        string title,
        string message,
        string yesText = "确定",
        string noText = "取消")
    {
        if (owner is null)
        {
            return false;
        }

        var dialog = new ConfirmDialog(title, message, yesText, noText);
        return await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
    }
}
