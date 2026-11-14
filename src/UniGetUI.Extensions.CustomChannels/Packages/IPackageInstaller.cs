using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Models;
using UniGetUI.Extensions.CustomChannels.Update;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Packages;

public sealed record PasswordRequest(string ArchiveName, int Attempt, string? LastError);

/// <summary>由界面实现的密码交互回调。</summary>
public interface IPasswordProvider
{
    Task<string?> RequestPasswordAsync(PasswordRequest request, CancellationToken ct = default);
}

public sealed class InstallContext
{
    public required TargetConfig Target { get; init; }

    public required AppPaths Paths { get; init; }

    public required string PackagePath { get; init; }

    public required VersionInfo Version { get; init; }

    public required BackupManager Backup { get; init; }

    public required ILogger Logger { get; init; }

    public IPasswordProvider? PasswordProvider { get; init; }

    public IProgress<StageProgress>? Progress { get; init; }

    public InstallConfig Install => Target.Install;

    public string InstallRoot => Path.GetFullPath(Target.Install.RootPath);
}

public interface IPackageInstaller
{
    InstallKind Kind { get; }

    Task<InstallResult> InstallAsync(InstallContext context, CancellationToken ct = default);
}

public sealed class ArchivePasswordException(string message, Exception? inner = null) : Exception(message, inner);
