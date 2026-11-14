using UniGetUI.Extensions.CustomChannels.Packages;

namespace UniGetUI.Avalonia.Integrations.CustomChannels;

/// <summary>
/// 核心库不引用界面框架，凡是需要窗口/WebView/密码框的动作都通过该接口回调界面实现。
/// </summary>
internal interface IShellHost
{
    /// <summary>打开网盘登录窗口；返回 true 表示已成功写入 Cookie。</summary>
    Task<bool> ShowPanLoginAsync(string platform, CancellationToken ct = default);

    /// <summary>加密压缩包密码输入；返回 null 表示用户取消。</summary>
    Task<string?> AskArchivePasswordAsync(string archiveName, int attempt, string? lastError, CancellationToken ct = default);

    /// <summary>在应用顶部显示一条横幅。</summary>
    void Notify(string title, string message, bool isError);
}

/// <summary>把界面回调适配成核心库的密码提供者。</summary>
internal sealed class ShellPasswordProvider(IShellHost host, string displayName) : IPasswordProvider
{
    public Task<string?> RequestPasswordAsync(PasswordRequest request, CancellationToken ct = default) =>
        host.AskArchivePasswordAsync(
            string.IsNullOrWhiteSpace(request.ArchiveName) ? displayName : request.ArchiveName,
            request.Attempt,
            request.LastError,
            ct);
}
