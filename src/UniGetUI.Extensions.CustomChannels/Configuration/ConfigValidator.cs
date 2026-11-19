using System.Text.RegularExpressions;
using UniGetUI.Extensions.CustomChannels.Sources;

namespace UniGetUI.Extensions.CustomChannels.Configuration;

public sealed record ConfigIssue(string Path, string Message, bool IsError)
{
    public override string ToString() => $"{(IsError ? "错误" : "警告")} [{Path}] {Message}";
}

public static class ConfigValidator
{
    public static IReadOnlyList<ConfigIssue> Validate(UpdaterConfig config)
    {
        var issues = new List<ConfigIssue>();

        if (config.Download.Concurrency is < 1 or > 8)
        {
            issues.Add(new ConfigIssue("download.concurrency", "并发数必须在 1-8 之间。", true));
        }

        if (config.Download.KeepBackupCount < 0)
        {
            issues.Add(new ConfigIssue("download.keepBackupCount", "备份保留份数不能为负数。", true));
        }

        if (!string.IsNullOrWhiteSpace(config.Download.Proxy) &&
            !Uri.TryCreate(config.Download.Proxy, UriKind.Absolute, out _))
        {
            issues.Add(new ConfigIssue("download.proxy", "代理地址不是合法的绝对 URI。", true));
        }

        if (config.OpenList.Mode != OpenListMode.Disabled)
        {
            ValidateOpenList(config, issues);
        }

        // 空列表是合法状态（用户可能刚删掉最后一个应用）；仅提示，不阻断加载
        if (config.Targets.Count == 0)
        {
            issues.Add(new ConfigIssue("targets", "尚未配置任何目标应用。", false));
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < config.Targets.Count; i++)
        {
            ValidateTarget(config.Targets[i], $"targets[{i}]", ids, issues);
        }

        return issues;
    }

    private static void ValidateOpenList(UpdaterConfig config, List<ConfigIssue> issues)
    {
        if (config.OpenList.Mode == OpenListMode.External)
        {
            if (!Uri.TryCreate(config.OpenList.BaseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && !IsLoopback(uri)))
            {
                issues.Add(new ConfigIssue(
                    "openList.baseUrl",
                    "外部 OpenList 地址必须是 HTTPS（回环地址可用 HTTP）。",
                    true));
            }
        }

        if (config.OpenList.LocalPort is < 1024 or > 65535)
        {
            issues.Add(new ConfigIssue("openList.localPort", "本地端口必须在 1024-65535 之间。", true));
        }

        if (!config.OpenList.RootPath.StartsWith('/'))
        {
            issues.Add(new ConfigIssue("openList.rootPath", "OpenList 根路径必须以 / 开头。", true));
        }
    }

    private static void ValidateTarget(
        TargetConfig target,
        string prefix,
        HashSet<string> ids,
        List<ConfigIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(target.Id))
        {
            issues.Add(new ConfigIssue($"{prefix}.id", "目标标识不能为空。", true));
        }
        else if (!ids.Add(target.Id))
        {
            issues.Add(new ConfigIssue($"{prefix}.id", $"目标标识 '{target.Id}' 重复。", true));
        }

        if (string.IsNullOrWhiteSpace(target.DisplayName))
        {
            issues.Add(new ConfigIssue($"{prefix}.displayName", "显示名称建议填写。", false));
        }

        var root = target.Install.RootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            issues.Add(new ConfigIssue($"{prefix}.install.rootPath", "安装根目录不能为空。", true));
        }
        else
        {
            if (!Path.IsPathFullyQualified(root))
            {
                issues.Add(new ConfigIssue($"{prefix}.install.rootPath", "安装根目录必须是绝对路径。", true));
            }
            else if (!Directory.Exists(root))
            {
                issues.Add(new ConfigIssue($"{prefix}.install.rootPath", $"目录不存在：{root}", false));
            }
        }

        if (target.Install.Kind == InstallKind.SingleFile && string.IsNullOrWhiteSpace(target.Install.TargetFileName))
        {
            issues.Add(new ConfigIssue(
                $"{prefix}.install.targetFileName",
                "单文件安装未指定目标文件名时，将使用下载文件名。",
                false));
        }

        if (target.Install.Kind == InstallKind.Exe && target.Install.Interactive && !string.IsNullOrEmpty(target.Install.SilentArgs))
        {
            issues.Add(new ConfigIssue(
                $"{prefix}.install.silentArgs",
                "交互模式下游标参数将被忽略。",
                false));
        }

        ValidateVersion(target, prefix, issues);

        if (!string.IsNullOrWhiteSpace(target.Archive.ExtractSubPath) &&
            target.Archive.ExtractSubPath.Contains("..", StringComparison.Ordinal))
        {
            issues.Add(new ConfigIssue($"{prefix}.archive.extractSubPath", "子路径不能包含 ..。", true));
        }

        if (target.Sources.Count == 0)
        {
            issues.Add(new ConfigIssue($"{prefix}.sources", "至少需要配置一个更新渠道。", true));
        }

        var enabled = target.Sources.Where(s => s.Enabled).ToList();
        if (target.Sources.Count > 0 && enabled.Count == 0)
        {
            issues.Add(new ConfigIssue($"{prefix}.sources", "所有渠道均被禁用。", true));
        }

        for (var i = 0; i < target.Sources.Count; i++)
        {
            ValidateSource(target.Sources[i], $"{prefix}.sources[{i}]", target, issues);
        }
    }

    private static void ValidateVersion(TargetConfig target, string prefix, List<ConfigIssue> issues)
    {
        var version = target.Version;

        // 不读取 / 记录版本模式都不需要文件路径
        if (version.Mode is LocalVersionMode.None or LocalVersionMode.Recorded)
        {
            return;
        }

        if (version.Mode == LocalVersionMode.Command)
        {
            if (string.IsNullOrWhiteSpace(version.Command))
            {
                issues.Add(new ConfigIssue(
                    $"{prefix}.version.command",
                    "选择命令获取版本时必须填写命令。",
                    true));
            }

            if (version.CommandTimeoutSeconds is < 3 or > 120)
            {
                issues.Add(new ConfigIssue(
                    $"{prefix}.version.commandTimeoutSeconds",
                    "命令超时时间需在 3-120 秒之间。",
                    false));
            }

            if (!string.IsNullOrWhiteSpace(version.Pattern) && !IsValidRegex(version.Pattern))
            {
                issues.Add(new ConfigIssue($"{prefix}.version.pattern", "版本提取正则无效。", true));
            }

            return;
        }

        var path = version.Path ?? target.Install.ExePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            // 降级为警告：VersionResolver 对缺失路径会记录日志并回退到手动填写的版本记录，
            // 不应让整个配置文件无法加载（曾导致添加应用后界面清空）。
            issues.Add(new ConfigIssue(
                $"{prefix}.version.path",
                "未指定版本文件路径，无法自动读取本地版本（可在应用卡片手动填写版本）。",
                false));
        }

        if (!string.IsNullOrWhiteSpace(version.Pattern) && !IsValidRegex(version.Pattern))
        {
            issues.Add(new ConfigIssue($"{prefix}.version.pattern", "版本提取正则无效。", true));
        }
    }

    private static void ValidateSource(
        SourceConfig source,
        string prefix,
        TargetConfig target,
        List<ConfigIssue> issues)
    {
        var name = source.Type.ToString();

        switch (source.Type)
        {
            case SourceType.GithubRelease:
                // 允许直接粘贴仓库地址，校验前先归一化为 owner/repo
                var repo = SourceUrlHelper.NormalizeGithubRepo(source.Repo);
                if (string.IsNullOrWhiteSpace(repo) || !repo.Contains('/'))
                {
                    issues.Add(new ConfigIssue(
                        $"{prefix}.repo",
                        $"{name} 需要 owner/repo 形式的仓库，也可直接粘贴仓库地址。",
                        true));
                }

                break;

            case SourceType.GitlabRelease:
                if (string.IsNullOrWhiteSpace(SourceUrlHelper.NormalizeGitlabProject(source.ProjectId)))
                {
                    issues.Add(new ConfigIssue(
                        $"{prefix}.projectId",
                        $"{name} 需要项目 ID 或 group/project（也可粘贴项目地址）。",
                        true));
                }

                if (!string.IsNullOrWhiteSpace(source.BaseUrl) &&
                    (!Uri.TryCreate(source.BaseUrl, UriKind.Absolute, out _) || !source.BaseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
                {
                    issues.Add(new ConfigIssue($"{prefix}.baseUrl", $"{name} 的 baseUrl 不是合法地址。", true));
                }

                break;

            case SourceType.BaiduPan:
            case SourceType.QuarkPan:
                ValidatePanSource(source, prefix, name, issues);
                break;

            case SourceType.OpenList:
                if (string.IsNullOrWhiteSpace(source.OpenListPath))
                {
                    issues.Add(new ConfigIssue($"{prefix}.openListPath", $"{name} 需要指定目录路径。", true));
                }
                else if (!source.OpenListPath.StartsWith('/'))
                {
                    issues.Add(new ConfigIssue($"{prefix}.openListPath", "OpenList 路径必须以 / 开头。", true));
                }

                break;

            case SourceType.DirectUrl:
                if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var directUri) ||
                    (directUri.Scheme != Uri.UriSchemeHttps && !IsLoopback(directUri)))
                {
                    issues.Add(new ConfigIssue($"{prefix}.url", $"{name} 必须使用 HTTPS（回环地址可用 HTTP）。", true));
                }

                if (!string.IsNullOrWhiteSpace(source.Sha256) &&
                    (source.Sha256.Length != 64 || !source.Sha256.All(Uri.IsHexDigit)))
                {
                    issues.Add(new ConfigIssue($"{prefix}.sha256", "SHA256 应为 64 位十六进制字符串。", true));
                }

                break;
        }

        if (source.Type is SourceType.GithubRelease or SourceType.GitlabRelease &&
            !string.IsNullOrWhiteSpace(source.AssetPattern) && source.AssetPattern.IndexOfAny(['/', '\\']) >= 0)
        {
            issues.Add(new ConfigIssue($"{prefix}.assetPattern", "资产匹配只支持文件名通配符，不能包含路径分隔符。", true));
        }

        if (source.Type is SourceType.BaiduPan or SourceType.QuarkPan &&
            source.PathPattern?.IndexOfAny(['+', '(', ')', '|', '{', '}']) >= 0)
        {
            issues.Add(new ConfigIssue(
                $"{prefix}.pathPattern",
                "pathPattern 使用通配符 * ? [abc]，+ ( ) | { } 会被当成普通字符；要同时匹配 _ 与 - 请写 [-_] 或直接写 *。",
                false));
        }

        if (!string.IsNullOrWhiteSpace(source.VersionPattern) && !IsValidRegex(source.VersionPattern))
        {
            issues.Add(new ConfigIssue($"{prefix}.versionPattern", "版本提取正则无效。", true));
        }

        if (source.Type is SourceType.BaiduPan or SourceType.QuarkPan && target.Install.Kind == InstallKind.SingleFile &&
            string.IsNullOrWhiteSpace(source.PathPattern))
        {
            issues.Add(new ConfigIssue($"{prefix}.pathPattern", "网盘渠道建议配置 pathPattern 以便定位正确的更新包。", false));
        }
    }

    private static void ValidatePanSource(
        SourceConfig source,
        string prefix,
        string name,
        List<ConfigIssue> issues)
    {
        if (!Uri.TryCreate(source.ShareUrl, UriKind.Absolute, out var shareUri) ||
            !shareUri.Host.Contains(
                source.Type == SourceType.BaiduPan ? "baidu.com" : "quark.cn",
                StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ConfigIssue(
                $"{prefix}.shareUrl",
                $"{name} 的分享链接不是有效的 {name} 地址。",
                true));
        }
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsLoopback(Uri uri) => uri.IsLoopback;
}
