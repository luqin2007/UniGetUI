using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Core.Logging;
using UniGetUI.Extensions.CustomChannels.Pan;

namespace UniGetUI.Avalonia.Integrations.CustomChannels;

/// <summary>
/// 网盘登录窗口：内置浏览器登录在跨平台下需要 WebView 运行时，这里采用更稳妥的方式——
/// 用系统浏览器打开登录页，用户登录后把请求头里的 Cookie 粘贴回来（与浏览器扩展导出的格式一致）。
/// </summary>
internal sealed class PanLoginDialog : Window
{
    private readonly TextBox _cookie = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 140,
        Watermark = "粘贴完整 Cookie，例如：BDUSS=...; STOKEN=...",
    };

    public PanLoginDialog(string platform)
    {
        string display = PanPlatform.DisplayName(platform);
        Title = $"登录{display}";
        Width = 620;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var steps = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"1. 点击「在浏览器中登录」，在打开的页面用{display}账号登录；\n" +
                   "2. 按 F12 打开开发者工具，在 Network 面板任选一个请求；\n" +
                   "3. 复制请求头里的 Cookie 整行内容；\n" +
                   "4. 粘贴到下方并点击「保存」。",
        };

        var open = new Button { Content = "在浏览器中登录", Padding = new Thickness(16, 6) };
        open.Click += (_, _) => OpenLoginPage(platform);

        var save = new Button { Content = "保存", Padding = new Thickness(16, 6) };
        save.Click += (_, _) => Close(string.IsNullOrWhiteSpace(_cookie.Text) ? null : _cookie.Text.Trim());

        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 6) };
        cancel.Click += (_, _) => Close(null);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { open, save, cancel },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "凭据仅保存在本机，使用 Windows DPAPI 加密（仅当前用户可解密）。",
                    FontSize = 11,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                },
                steps,
                _cookie,
                buttons,
            },
        };
    }

    private static void OpenLoginPage(string platform)
    {
        string url = platform == PanPlatform.Baidu ? "https://pan.baidu.com/" : "https://pan.quark.cn/";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }
}
