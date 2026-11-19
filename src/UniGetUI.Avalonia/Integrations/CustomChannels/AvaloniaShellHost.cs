using Avalonia.Controls;
using Avalonia.Threading;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Logging;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Pan;
using AppPaths = UniGetUI.Extensions.CustomChannels.Infrastructure.AppPaths;

namespace UniGetUI.Avalonia.Integrations.CustomChannels;

/// <summary>界面宿主动作的 Avalonia 实现：网盘登录、压缩包密码、横幅提示。</summary>
internal sealed class AvaloniaShellHost : IShellHost
{
    public async Task<bool> ShowPanLoginAsync(string platform, CancellationToken ct = default)
    {
        if (MainWindow.Instance is not { } owner)
        {
            return false;
        }

        var dialog = new PanLoginDialog(platform);
        var cookie = await dialog.ShowDialog<string?>(owner).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return false;
        }

        try
        {
            var sessions = new PanSessionStore(new SecretStore(AppPaths.Default));
            sessions.SaveCookie(platform, cookie);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            Notify("保存登录信息失败", ex.Message, isError: true);
            return false;
        }
    }

    public Task<string?> AskArchivePasswordAsync(
        string archiveName,
        int attempt,
        string? lastError,
        CancellationToken ct = default)
    {
        // 解压跑在后台线程，窗口必须在 UI 线程上创建；否则 Avalonia 会抛
        // 「The calling thread cannot access this object because a different thread owns it」，
        // 整个安装阶段被判定为失败
        if (Dispatcher.UIThread.CheckAccess())
        {
            return ShowPasswordDialogAsync(archiveName, attempt, lastError);
        }

        return Dispatcher.UIThread.InvokeAsync(() => ShowPasswordDialogAsync(archiveName, attempt, lastError));
    }

    private static async Task<string?> ShowPasswordDialogAsync(string archiveName, int attempt, string? lastError)
    {
        if (MainWindow.Instance is not { } owner)
        {
            return null;
        }

        var dialog = new ArchivePasswordDialog(archiveName, attempt, lastError);
        return await dialog.ShowDialog<string?>(owner).ConfigureAwait(true);
    }

    public void Notify(string title, string message, bool isError)
    {
        MainWindow.Instance?.ShowBanner(
            title,
            message,
            isError ? MainWindow.RuntimeNotificationLevel.Error : MainWindow.RuntimeNotificationLevel.Success);
    }
}
