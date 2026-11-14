namespace UniGetUI.Extensions.CustomChannels.Sources;

/// <summary>
/// 仓库/项目标识归一化：用户经常直接粘贴浏览器地址，
/// 这里把 https://github.com/owner/repo/、repo.git、.../releases 等形式统一成 API 需要的标识。
/// </summary>
public static class SourceUrlHelper
{
    /// <summary>归一化为 owner/repo。</summary>
    public static string NormalizeGithubRepo(string? repo)
    {
        if (string.IsNullOrWhiteSpace(repo))
        {
            return string.Empty;
        }

        var value = repo.Trim();

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            value = uri.AbsolutePath;
        }
        else if (value.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
        {
            value = value["github.com".Length..];
        }

        value = value.Trim('/');
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return string.Empty;
        }

        var owner = segments[0];
        var name = segments.Length >= 2 ? segments[1] : segments[0];

        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return segments.Length >= 2 ? $"{owner}/{name}" : name;
    }

    /// <summary>归一化为 group/project（支持多级 namespace 与自建实例地址）。</summary>
    public static string NormalizeGitlabProject(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return string.Empty;
        }

        var value = projectId.Trim();

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            value = uri.AbsolutePath;
        }
        else if (value.StartsWith("gitlab.com/", StringComparison.OrdinalIgnoreCase))
        {
            value = value["gitlab.com".Length..];
        }

        value = value.Trim('/');

        var marker = value.IndexOf("/-/", StringComparison.Ordinal);
        if (marker >= 0)
        {
            value = value[..marker];
        }

        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        return value.Trim('/');
    }

    /// <summary>从粘贴的浏览器地址推断实例根地址（GitLab 自建实例用）。</summary>
    public static string? InferGitlabBaseUrl(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId) ||
            !Uri.TryCreate(projectId.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return uri.Host.Contains("gitlab.com", StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{uri.Scheme}://{uri.Authority}";
    }
}
