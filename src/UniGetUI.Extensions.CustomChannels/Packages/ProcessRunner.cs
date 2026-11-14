using System.Diagnostics;
using System.Text;

namespace UniGetUI.Extensions.CustomChannels.Packages;

public sealed record ProcessResult(int ExitCode, string Output, string Error, bool TimedOut)
{
    public bool IsSuccess => !TimedOut && ExitCode is 0 or 3010 or 1641;
}

/// <summary>安装程序执行器：不使用 ShellExecute，避免弹窗与系统临时目录。</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string? arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments ?? string.Empty)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动进程：{fileName}");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return new ProcessResult(-1, string.Empty, "安装程序执行超时，已终止。", true);
        }

        return new ProcessResult(
            process.ExitCode,
            await SafeAwaitAsync(outputTask).ConfigureAwait(false),
            await SafeAwaitAsync(errorTask).ConfigureAwait(false),
            false);
    }

    private static async Task<string> SafeAwaitAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return string.Empty;
        }
    }
}
