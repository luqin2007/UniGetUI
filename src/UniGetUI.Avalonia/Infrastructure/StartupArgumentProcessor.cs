using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using UniGetUI.Avalonia.Integrations.CustomChannels;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Logging;
using UniGetUI.Shared;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class StartupArgumentProcessor
{
    public static void WarnIfBundlesIgnored(IReadOnlyList<string> args, string reason)
    {
        if (args.Count == 0)
            return;

        List<string> bundles = StartupBundleArguments.Resolve(args, Environment.CurrentDirectory);
        foreach (string bundle in bundles)
        {
            Logger.Warn($"Ignoring the package bundle {bundle}: {reason}");
        }
    }

    /// <summary>
    /// Returns the first <c>unigetui://package</c> link in <paramref name="args"/>, logging and
    /// skipping malformed ones.
    /// </summary>
    internal static PackageLink? FindPackageLink(IReadOnlyList<string> args)
    {
        foreach (string arg in args)
        {
            if (!PackageLink.IsPackageLink(arg))
                continue;

            if (PackageLink.TryParse(arg) is { } link)
                return link;

            Logger.Warn($"Ignoring a malformed package link: {arg}");
        }

        return null;
    }

    public static async Task ProcessAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return;
        }

        // A protocol launch carries nothing but its link, so a link and a bundle never come together.
        PackageLink? link = FindPackageLink(args);
        List<string> bundles = link is null
            ? StartupBundleArguments.Resolve(args, Environment.CurrentDirectory)
            : [];

        // 自动化/调试参数（供程序化 UI 验证使用，无需人工点击）：
        //   --open=<PageType 名称>   导航到指定页面
        //   --auto-check             触发自定义渠道的「检查全部」
        //   --screen=<0 起始索引>     把主窗口移到指定显示器（默认 0 = 主屏）
        string? openArg = FindArg(args, "--open=");
        string? screenArg = FindArg(args, "--screen=");
        bool autoCheck = HasArg(args, "--auto-check");
        bool hasUiAutomation = openArg is not null || screenArg is not null || autoCheck;
        if (link is null && bundles.Count == 0 && !hasUiAutomation)
        {
            return;
        }

        string request = link is not null
            ? $"the package link {link.ToUri()}"
            : bundles.Count > 0
                ? $"the package bundle {bundles[0]}"
                : "the UI automation request";

        if (bundles.Count > 1)
        {
            Logger.Warn(
                $"{bundles.Count} package bundles were passed on the command line; only {bundles[0]} will be loaded");
        }

        if (!await AvaloniaBootstrapper.Initialized)
        {
            Logger.Warn($"Could not open {request}: UniGetUI failed to finish initializing");
            return;
        }

        if (MainWindow.Instance is not { } window)
        {
            Logger.Warn($"Could not open {request}: the main window is not available");
            return;
        }

        if (window.DataContext is not MainWindowViewModel viewModel)
        {
            Logger.Warn($"Could not open {request}: the main window has no view model");
            return;
        }

        if (!window.IsVisible)
        {
            window.ShowFromTray();
        }

        if (screenArg is not null)
        {
            MoveWindowToScreen(window, screenArg["--screen=".Length..]);
        }

        if (openArg is not null)
        {
            // 允许连字符/空格分隔的写法（--open=custom-channels ↔ 枚举名 CustomChannels）
            string pageName = openArg["--open=".Length..].Replace("-", "").Replace(" ", "");
            if (Enum.TryParse(pageName, ignoreCase: true, out PageType page)
                && page is not PageType.Quit and not PageType.Null)
            {
                Logger.ImportantInfo($"UI automation: navigating to {page}");
                await viewModel.NavigateToAsync(page);
            }
            else
            {
                Logger.Warn($"UI automation: unknown page '{pageName}'");
            }
        }

        if (autoCheck)
        {
            // 稍等页面完成初始化（卡片加载），再触发「检查全部」
            await Task.Delay(1500);
            var customChannels = CustomChannelsIntegration.Current;
            if (customChannels is not null)
            {
                Logger.ImportantInfo("UI automation: running custom channels check-all");
                await customChannels.CheckAllAsync(forceRefresh: false);
            }
            else
            {
                Logger.Warn("UI automation: custom channels extension is not active");
            }
        }

        if (link is not null)
        {
            await PackageLinkHandler.OpenAsync(link, window);
            return;
        }

        if (bundles.Count > 0)
        {
            Logger.ImportantInfo($"Loading the package bundle {bundles[0]} requested on the command line");
            await viewModel.LoadBundleFromFileAsync(bundles[0]);
        }
    }

    /// <summary>把主窗口移到指定显示器（0 起始索引）并最大化，供自动化截屏使用。</summary>
    private static void MoveWindowToScreen(Window window, string indexText)
    {
        if (!int.TryParse(indexText, out int index) || index < 0)
        {
            Logger.Warn($"UI automation: invalid --screen index '{indexText}'");
            return;
        }

        var screens = window.Screens.All;
        if (index >= screens.Count)
        {
            Logger.Warn($"UI automation: screen {index} not found ({screens.Count} screens available)");
            return;
        }

        var screen = screens[index];
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        // Screen.Bounds 与 Window.Position 同为物理像素坐标。
        // 先设置一次，再在布局完成后重设一次：避免启动期的几何恢复把窗口拉回主屏
        window.Position = screen.Bounds.Position;
        window.WindowState = WindowState.Maximized;
        Dispatcher.UIThread.Post(
            () =>
            {
                window.Position = screen.Bounds.Position;
                window.WindowState = WindowState.Maximized;
            },
            DispatcherPriority.Background);
        Logger.ImportantInfo($"UI automation: moved window to screen {index} ({screen.Bounds.Width}x{screen.Bounds.Height})");
    }

    private static string? FindArg(IReadOnlyList<string> args, string prefix)
    {
        foreach (string arg in args)
        {
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg;
            }
        }

        return null;
    }

    private static bool HasArg(IReadOnlyList<string> args, string name)
    {
        foreach (string arg in args)
        {
            if (arg.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
