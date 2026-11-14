using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Models;
using UniGetUI.Extensions.CustomChannels.Sources;

namespace UniGetUI.Avalonia.Integrations.CustomChannels;

/// <summary>「自定义渠道」页面上的单个应用卡片。</summary>
internal sealed partial class CustomChannelCardViewModel : ObservableObject
{
    public CustomChannelCardViewModel(TargetConfig target)
    {
        Target = target;
    }

    public TargetConfig Target { get; }

    public string Id => Target.Id;

    public string DisplayName => string.IsNullOrWhiteSpace(Target.DisplayName) ? Target.Id : Target.DisplayName;

    public string InstallKindText => Target.Install.Kind switch
    {
        InstallKind.ZipOverwrite => "ZIP 解压覆盖",
        InstallKind.SingleFile => "单文件替换",
        InstallKind.Msi => "MSI 安装",
        InstallKind.Exe => "EXE 安装",
        _ => Target.Install.Kind.ToString(),
    };

    public string SourceBadges => string.Join(
        " · ",
        Target.Sources.Where(s => s.Enabled).Select(SourceBadge));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionText))]
    private string? _localVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionText))]
    private string? _latestVersion;

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string? _warnings;

    [ObservableProperty]
    private bool _isChecked;

    public List<VersionCandidate> Candidates { get; } = [];

    public string VersionText => LocalVersion is null && LatestVersion is null
        ? "尚未检查"
        : $"{LocalVersion ?? "未知"} → {LatestVersion ?? "未知"}";

    public void Apply(TargetCheckResult result)
    {
        LocalVersion = result.LocalVersion;
        var best = result.Best;
        LatestVersion = best?.Version.DisplayVersion;
        HasUpdate = result.HasUpdate;
        Error = result.Error;
        Warnings = result.Warnings is { Count: > 0 } warnings ? string.Join("；", warnings) : null;
        IsChecked = true;

        Candidates.Clear();
        Candidates.AddRange(result.Candidates);
    }

    private static string SourceBadge(SourceConfig source) => source.Type switch
    {
        SourceType.GithubRelease => "GitHub",
        SourceType.GitlabRelease => "GitLab",
        SourceType.BaiduPan => "百度网盘",
        SourceType.QuarkPan => "夸克网盘",
        SourceType.OpenList => "OpenList",
        SourceType.DirectUrl => "直链",
        _ => source.Type.ToString(),
    };
}

/// <summary>详情面板中的一行版本。</summary>
internal sealed class CustomChannelVersionViewModel(VersionCandidate candidate)
{
    public VersionCandidate Candidate { get; } = candidate;

    public VersionInfo Version => Candidate.Version;

    public string VersionText => Version.DisplayVersion;

    public bool IsPrerelease => Version.IsPrerelease;

    public string PublishedText => Version.PublishedAt is { } at
        ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "未知发布时间";

    public string SizeText => Version.TotalSize is { } size ? FormatSize(size) : "大小未知";

    public string RelationText => Candidate.Relation switch
    {
        VersionRelation.Newer => "可升级",
        VersionRelation.Same => "当前版本",
        VersionRelation.Older => "旧版本",
        _ => "未知",
    };

    public bool IsSelectable => Candidate.IsSelectable;

    public string? BlockReason => Candidate.BlockReason;

    public bool IsSkipped => Candidate.IsSkipped;

    public string? Notes => Version.ReleaseNotes;

    public string ReadmeText => Version.Readme is { Sections.Count: > 0 } readme
        ? string.Join(
            "\n\n",
            readme.Sections.Select(section => $"## {section.File}\n\n{section.Markdown}"))
        : string.Empty;

    public string ReadmeTitle => Version.Readme?.FileListText ?? "README";

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
        >= 1024 * 1024 => $"{bytes / (1024d * 1024):0.##} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B",
    };
}
