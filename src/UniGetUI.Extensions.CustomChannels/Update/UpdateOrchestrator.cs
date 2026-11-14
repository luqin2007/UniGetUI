using System.Diagnostics;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Download;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Models;
using UniGetUI.Extensions.CustomChannels.Packages;
using UniGetUI.Extensions.CustomChannels.Pan;
using UniGetUI.Extensions.CustomChannels.Sources;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Update;

public sealed record UpdateRequest(
    TargetConfig Target,
    VersionInfo Version,
    AssetRef Asset,
    IUpdateSource Source,
    IPasswordProvider? PasswordProvider = null);

/// <summary>
/// 更新编排状态机：检查 → 下载 → 校验 → 解压 → 备份 → 应用 → 清理 → 重启。
/// 任一步失败都会按策略回滚并保留可续传的下载分片。
/// </summary>
public sealed class UpdateOrchestrator(
    AppPaths paths,
    DownloadConfig downloadConfig,
    SegmentedDownloader downloader,
    IPackageInstallerFactory installerFactory,
    BackupManager backupManager,
    TransferManager transfers,
    VersionRecordStore versionRecords,
    ILogger<UpdateOrchestrator> logger)
{
    public async Task<UpdateResult> ExecuteAsync(
        UpdateRequest request,
        IProgress<StageProgress>? progress = null,
        CancellationToken ct = default)
    {
        // 阶段列表按调用隔离：多个目标可并行执行同一个编排器实例
        var stages = new List<UpdateStage>();
        var target = request.Target;
        var installSucceeded = false;

        void Report(UpdateStage stage, string message, double? percent = null,
            long? received = null, long? total = null, double? speed = null)
        {
            if (stages.Count == 0 || stages[^1] != stage)
            {
                stages.Add(stage);
            }

            progress?.Report(new StageProgress(stage, message, percent, received, total, speed));
        }

        try
        {
            // 1. 环境检查
            Report(UpdateStage.Checking, "正在检查安装环境…");
            Directory.CreateDirectory(target.Install.RootPath);
            EnsureTargetProcessStopped(target);

            if (request.Asset.Size is > 0)
            {
                WorkspaceGuard.EnsureFreeSpace(paths.Downloads, request.Asset.Size.Value);
            }

            // 2. 下载
            var fileName = AppPaths.Sanitize(Path.GetFileName(request.Asset.Name));
            if (string.IsNullOrWhiteSpace(fileName) || fileName == "_")
            {
                fileName = $"package{Path.GetExtension(request.Asset.Name)}";
            }

            var destination = paths.Under(
                "downloads",
                AppPaths.Sanitize(target.Id),
                fileName);

            logger.LogInformation("开始更新 {Target} → {Version}（{File}）", target.Id, request.Version.DisplayVersion, fileName);
            Report(UpdateStage.Resolving, "正在获取下载地址…");

            var downloadRequest = new DownloadRequest(
                async token =>
                {
                    var resolved = await request.Source
                        .ResolveAssetAsync(request.Version, request.Asset.Name, token)
                        .ConfigureAwait(false);

                    if (resolved?.DirectUri is null)
                    {
                        throw new InvalidOperationException("渠道未能返回可下载地址。");
                    }

                    return new DownloadHandle(
                        resolved.DirectUri,
                        resolved.Headers,
                        resolved.Size,
                        resolved.Sha256);
                },
                destination,
                downloadConfig.Concurrency,
                downloadConfig.MaxSpeedKBps * 1024L,
                request.Asset.Size,
                request.Asset.Sha256);

            var downloadProgress = new Progress<StageProgress>(value => progress?.Report(value));
            var downloadResult = await downloader
                .DownloadAsync(downloadRequest, downloadProgress, ct)
                .ConfigureAwait(false);

            transfers.MarkDownloadCompleted(PlatformOf(request.Source), fileName);

            // 3. 校验（下载器已校验，这里补报阶段）
            Report(UpdateStage.Verifying, "更新包校验通过", 100);

            // 4. 包类型识别
            var detected = PackageDetector.Detect(downloadResult.Path);
            var configured = target.Install.Kind;
            if (detected != configured)
            {
                var hint = PackageDetector.Validate(configured, detected) ??
                           $"配置的安装方式（{configured}）与实际包类型（{detected}）不一致。";
                throw new InvalidDataException(hint);
            }

            // 5. 安装
            var installer = installerFactory.Get(configured);
            var context = new InstallContext
            {
                Target = target,
                Paths = paths,
                PackagePath = downloadResult.Path,
                Version = request.Version,
                Backup = backupManager,
                Logger = logger,
                PasswordProvider = request.PasswordProvider,
                Progress = new Progress<StageProgress>(value => progress?.Report(value)),
            };

            var installResult = await installer.InstallAsync(context, ct).ConfigureAwait(false);
            if (!installResult.Success)
            {
                throw new InvalidOperationException(installResult.Message);
            }

            installSucceeded = true;

            // 记录本次安装的远端版本：「记录版本」模式据此判断本地版本
            versionRecords.Save(
                target.Id,
                request.Version.NormalizedVersion ?? request.Version.RawTag,
                request.Version.SourceId,
                $"安装成功：{target.Install.Kind}");

            // 6. 清理
            Report(UpdateStage.Cleaning, "正在清理临时文件…");
            await CleanupAsync(request.Source, target, installSucceeded, ct).ConfigureAwait(false);

            // 7. 重启
            if (target.Install.LaunchAfterUpdate)
            {
                Report(UpdateStage.Restarting, "正在启动目标程序…");
                TryLaunch(target);
            }

            Report(UpdateStage.Completed, "更新完成", 100);
            logger.LogInformation("更新完成：{Target} → {Version}", target.Id, request.Version.DisplayVersion);
            return UpdateResult.Ok(request.Version, stages, $"{target.DisplayName} 已更新到 {request.Version.DisplayVersion}");
        }
        catch (OperationCanceledException)
        {
            Report(UpdateStage.Canceled, "操作已取消");
            await SafeCleanupAsync(request.Source, target, installSucceeded).ConfigureAwait(false);
            return UpdateResult.Fail("更新已取消。", stages);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "更新失败：{Target}", target.Id);
            Report(UpdateStage.Failed, ex.Message);
            await SafeCleanupAsync(request.Source, target, installSucceeded).ConfigureAwait(false);
            return UpdateResult.Fail(ex.Message, stages, ex);
        }
    }

    private async Task CleanupAsync(
        IUpdateSource source,
        TargetConfig target,
        bool installSucceeded,
        CancellationToken ct)
    {
        await SafeCleanupAsync(source, target, installSucceeded).ConfigureAwait(false);
        backupManager.Prune(target.Id, downloadConfig.KeepBackupCount);
    }

    private async Task SafeCleanupAsync(IUpdateSource source, TargetConfig target, bool installSucceeded)
    {
        try
        {
            await source.CleanupAsync(installSucceeded, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("清理转存文件失败：{Message}", ex.Message);
        }

        try
        {
            var staging = Path.Combine(
                paths.Staging,
                AppPaths.Sanitize(target.Id));
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("清理暂存目录失败：{Message}", ex.Message);
        }
    }

    private void EnsureTargetProcessStopped(TargetConfig target)
    {
        if (string.IsNullOrWhiteSpace(target.Install.ExePath))
        {
            return;
        }

        var processName = Path.GetFileNameWithoutExtension(target.Install.ExePath);
        var processes = Process.GetProcessesByName(processName);
        if (processes.Length == 0)
        {
            return;
        }

        foreach (var process in processes)
        {
            process.Dispose();
        }

        switch (target.Install.StopRunningProcess)
        {
            case ProcessStopMode.Kill:
                logger.LogInformation("正在结束目标程序 {Name} 的 {Count} 个进程", processName, processes.Length);
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        logger.LogWarning("结束进程失败：{Message}", ex.Message);
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }

                break;

            case ProcessStopMode.PromptUser:
                throw new InvalidOperationException(
                    $"目标程序 {processName} 正在运行，请先关闭后再重试（或在配置中设置 stopRunningProcess=kill）。");

            case ProcessStopMode.None:
            default:
                throw new InvalidOperationException($"目标程序 {processName} 正在运行，无法继续更新。");
        }
    }

    private void TryLaunch(TargetConfig target)
    {
        if (string.IsNullOrWhiteSpace(target.Install.ExePath))
        {
            return;
        }

        var exePath = Path.Combine(target.Install.RootPath, target.Install.ExePath);
        if (!File.Exists(exePath))
        {
            logger.LogWarning("未找到启动目标：{Path}", exePath);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = target.Install.RootPath,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning("启动目标程序失败：{Message}", ex.Message);
        }
    }

    private static string PlatformOf(IUpdateSource source) => source.Type switch
    {
        SourceType.BaiduPan => PanPlatform.Baidu,
        SourceType.QuarkPan => PanPlatform.Quark,
        _ => source.Type.ToString(),
    };
}
