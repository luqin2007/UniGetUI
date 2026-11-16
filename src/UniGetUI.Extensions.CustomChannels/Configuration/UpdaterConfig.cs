using System.Text.Json.Serialization;

namespace UniGetUI.Extensions.CustomChannels.Configuration;

/// <summary>根配置。</summary>
public sealed class UpdaterConfig
{
    public DownloadConfig Download { get; set; } = new();

    public TransferConfig Transfer { get; set; } = new();

    public OpenListConfig OpenList { get; set; } = new();

    public List<TargetConfig> Targets { get; set; } = new();
}

public sealed class DownloadConfig
{
    /// <summary>分片并发数，1-8。</summary>
    public int Concurrency { get; set; } = 4;

    /// <summary>全局限速，单位 KB/s，0 表示不限速。</summary>
    public int MaxSpeedKBps { get; set; }

    /// <summary>备份保留份数。</summary>
    public int KeepBackupCount { get; set; } = 1;

    /// <summary>可选 HTTP 代理，例如 http://127.0.0.1:7890。</summary>
    public string? Proxy { get; set; }

    /// <summary>版本列表缓存时间（分钟）。</summary>
    public int ReleaseCacheMinutes { get; set; } = 5;

    /// <summary>单次 HTTP 请求超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 100;
}

public enum TransferCleanupPolicy
{
    /// <summary>保留转存文件。</summary>
    Keep,

    /// <summary>下载完成后立即删除。</summary>
    DeleteAfterDownload,

    /// <summary>安装成功后删除（安装失败则保留，便于排查与重试）。</summary>
    DeleteAfterSuccess,
}

public sealed class TransferConfig
{
    /// <summary>是否允许自动转存分享文件到当前账号。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>转存到当前账号的目标目录。</summary>
    public string TransferPath { get; set; } = "/AppUpdater";

    /// <summary>全局默认清理策略，单个渠道可覆盖。</summary>
    public TransferCleanupPolicy CleanupPolicy { get; set; } = TransferCleanupPolicy.DeleteAfterSuccess;
}

public enum OpenListMode
{
    Disabled,
    External,
    Local,
}

public sealed class OpenListConfig
{
    public OpenListMode Mode { get; set; } = OpenListMode.Disabled;

    /// <summary>外部实例地址，本地模式时只需端口。</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:5244";

    public string? Username { get; set; } = "admin";

    /// <summary>外部实例密码；也可写 %ENV_VAR% 引用环境变量。</summary>
    public string? Password { get; set; }

    /// <summary>已登录 token（可选，优先于账号密码）。</summary>
    public string? Token { get; set; }

    /// <summary>本地实例端口。</summary>
    public int LocalPort { get; set; } = 5244;

    /// <summary>OpenList 中视为根目录的路径。</summary>
    public string RootPath { get; set; } = "/";

    /// <summary>本地实例缺失时是否自动下载官方二进制（不随本工具分发）。</summary>
    public bool AutoDownloadBinary { get; set; } = true;

    /// <summary>
    /// 手动指定 openlist.exe 路径（相对路径按程序目录解析）。
    /// 留空时自动探测 PATH、常见安装目录与程序目录内的托管副本。
    /// </summary>
    public string? BinaryPath { get; set; }
}

public sealed class TargetConfig
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? IconPath { get; set; }

    public InstallConfig Install { get; set; } = new();

    public LocalVersionConfig Version { get; set; } = new();

    public ArchiveConfig Archive { get; set; } = new();

    public UpdatePolicyConfig Policy { get; set; } = new();

    public List<SourceConfig> Sources { get; set; } = new();
}

public enum InstallKind
{
    /// <summary>zip 解压覆盖安装目录。</summary>
    ZipOverwrite,

    /// <summary>单文件替换。</summary>
    SingleFile,

    /// <summary>msi 安装程序。</summary>
    Msi,

    /// <summary>exe 安装程序。</summary>
    Exe,
}

public enum ProcessStopMode
{
    /// <summary>不处理，占用时直接失败。</summary>
    None,

    /// <summary>提示用户手动关闭。</summary>
    PromptUser,

    /// <summary>自动结束进程。</summary>
    Kill,
}

public sealed class InstallConfig
{
    public InstallKind Kind { get; set; } = InstallKind.ZipOverwrite;

    /// <summary>目标安装根目录，绝对路径。</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>启动/版本读取使用的可执行文件，相对 RootPath。</summary>
    public string? ExePath { get; set; }

    /// <summary>单文件模式下的目标文件名，相对 RootPath；留空则使用下载文件名。</summary>
    public string? TargetFileName { get; set; }

    /// <summary>更新完成后启动目标程序。</summary>
    public bool LaunchAfterUpdate { get; set; }

    /// <summary>强制通过提权助手执行（写入 Program Files 等受保护目录）。</summary>
    public bool RequiresElevation { get; set; }

    /// <summary>安装程序是否交互运行（false 时使用静默参数）。</summary>
    public bool Interactive { get; set; }

    /// <summary>exe 安装程序静默参数，留空时依次尝试 /S、/SILENT。</summary>
    public string? SilentArgs { get; set; }

    public ProcessStopMode StopRunningProcess { get; set; } = ProcessStopMode.PromptUser;

    /// <summary>zip 内部路径到目标相对路径的重映射，可用于只取子目录。</summary>
    public List<PathMapping> PathMappings { get; set; } = new();
}

public sealed class PathMapping
{
    /// <summary>压缩包内相对路径前缀，例如 "app/"。</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>目标相对路径前缀，例如 ""。</summary>
    public string To { get; set; } = string.Empty;
}

public enum LocalVersionMode
{
    /// <summary>读取可执行文件的 FileVersion。</summary>
    FileVersion,

    /// <summary>读取可执行文件的 ProductVersion。</summary>
    ProductVersion,

    /// <summary>读取版本文件（txt/json 中的版本号）。</summary>
    VersionFile,

    /// <summary>
    /// 使用本工具记录的版本：每次更新成功后自动把远端版本写入程序目录内的记录，
    /// 首次使用前可在界面填写初始版本。
    /// </summary>
    Recorded,

    /// <summary>执行命令并解析输出（在安装目录下运行）。</summary>
    Command,

    /// <summary>无法自动读取，需在界面手动指定。</summary>
    None,
}

public sealed class LocalVersionConfig
{
    public LocalVersionMode Mode { get; set; } = LocalVersionMode.FileVersion;

    /// <summary>相对 RootPath 的文件路径。</summary>
    public string? Path { get; set; }

    /// <summary>从文件内容提取版本号的正则，默认取首个 x.y.z 形式的串。</summary>
    public string? Pattern { get; set; }

    /// <summary>Command 模式下执行的命令行，工作目录为安装目录。</summary>
    public string? Command { get; set; }

    /// <summary>Command 模式的超时时间（秒）。</summary>
    public int CommandTimeoutSeconds { get; set; } = 15;
}

public sealed class ArchiveConfig
{
    /// <summary>候选密码列表，按顺序尝试。</summary>
    public List<string> Passwords { get; set; } = new();

    /// <summary>解压后处理方式（默认智能解压）。</summary>
    public ExtractMode ExtractMode { get; set; } = ExtractMode.Smart;

    /// <summary>只使用压缩包内该子目录作为安装内容（解压方式为自定义路径时使用）。</summary>
    public string? ExtractSubPath { get; set; }

    /// <summary>覆盖时是否保留目标目录中压缩包未包含的既有文件。</summary>
    public bool PreserveExistingExtraFiles { get; set; } = true;

    /// <summary>密码错误时是否允许弹窗让用户输入。</summary>
    public bool PromptForPassword { get; set; } = true;
}

/// <summary>压缩包解压后处理方式。</summary>
public enum ExtractMode
{
    /// <summary>直接解压：压缩包内容原样作为安装内容。</summary>
    Direct,

    /// <summary>智能解压（默认）：压缩包只有一个顶层目录时忽略该目录层级，取其内容作为安装内容。</summary>
    Smart,

    /// <summary>自定义路径：以压缩包内指定子目录（ExtractSubPath）作为安装内容根。</summary>
    Custom,
}

public sealed class UpdatePolicyConfig
{
    /// <summary>允许安装低于本地版本的版本。</summary>
    public bool AllowDowngrade { get; set; }

    /// <summary>用户主动跳过的版本，不再提示。</summary>
    public List<string> SkippedVersions { get; set; } = new();

    /// <summary>固定版本，设置后仅提示该版本。</summary>
    public string? PinnedVersion { get; set; }
}

public enum SourceType
{
    GithubRelease,
    GitlabRelease,
    BaiduPan,
    QuarkPan,
    OpenList,
    DirectUrl,
}

/// <summary>
/// 渠道配置。为便于校验与错误定位采用扁平结构，由 Type 决定实际使用的字段。
/// </summary>
public sealed class SourceConfig
{
    public SourceType Type { get; set; }

    public bool Enabled { get; set; } = true;

    public string? DisplayName { get; set; }

    // ---- Git 仓库 ----

    /// <summary>GitHub: owner/repo。</summary>
    public string? Repo { get; set; }

    /// <summary>GitLab 实例地址或 GitHub Enterprise 地址。</summary>
    public string? BaseUrl { get; set; }

    /// <summary>GitLab: 项目 ID 或 group/project。</summary>
    public string? ProjectId { get; set; }

    /// <summary>访问令牌明文（不推荐）。</summary>
    public string? Token { get; set; }

    /// <summary>访问令牌来源环境变量名。</summary>
    public string? TokenEnv { get; set; }

    /// <summary>资产名通配符，例如 *-win-x64.zip。</summary>
    public string? AssetPattern { get; set; }

    public bool IncludePrerelease { get; set; }

    // ---- 网盘 ----

    public string? ShareUrl { get; set; }

    public string? Pwd { get; set; }

    /// <summary>网盘目录内匹配文件的通配符，例如 /latest/App-*.zip。</summary>
    public string? PathPattern { get; set; }

    /// <summary>从文件名提取版本号的正则。</summary>
    public string? VersionPattern { get; set; }

    /// <summary>覆盖全局转存目录。</summary>
    public string? TransferPath { get; set; }

    /// <summary>覆盖全局清理策略。</summary>
    public TransferCleanupPolicy? CleanupPolicy { get; set; }

    // ---- OpenList ----

    /// <summary>OpenList 中的目录路径，例如 /quark/releases。</summary>
    public string? OpenListPath { get; set; }

    /// <summary>OpenList 目录下的文件匹配模式。</summary>
    public string? FilePattern { get; set; }

    // ---- 直链 ----

    public string? Url { get; set; }

    public string? Sha256 { get; set; }

    public long? Size { get; set; }

    /// <summary>直链渠道的显式版本号（用于比较）。</summary>
    public string? Version { get; set; }
}
