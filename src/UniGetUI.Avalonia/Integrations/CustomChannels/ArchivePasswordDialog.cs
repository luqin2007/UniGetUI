using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace UniGetUI.Avalonia.Integrations.CustomChannels;

/// <summary>加密压缩包的密码输入窗口：候选密码全部失败时弹出，允许重试。</summary>
internal sealed class ArchivePasswordDialog : Window
{
    private readonly TextBox _password = new() { PasswordChar = '●', MinWidth = 320 };

    public ArchivePasswordDialog(string archiveName, int attempt, string? lastError)
    {
        Title = "需要解压密码";
        Width = 460;
        Height = 240;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var hint = new TextBlock
        {
            Text = attempt <= 1
                ? $"压缩包「{archiveName}」已加密，请输入密码。"
                : $"密码不正确，请重新输入（第 {attempt} 次尝试）。",
            TextWrapping = TextWrapping.Wrap,
        };

        var error = new TextBlock
        {
            Text = lastError ?? string.Empty,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.7,
            IsVisible = !string.IsNullOrWhiteSpace(lastError),
        };

        var ok = new Button { Content = "确定", Padding = new Thickness(16, 6) };
        ok.Click += (_, _) => Close(string.IsNullOrWhiteSpace(_password.Text) ? null : _password.Text);

        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 6) };
        cancel.Click += (_, _) => Close(null);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children = { hint, _password, error, buttons },
        };

        Opened += (_, _) => _password.Focus();
    }
}
