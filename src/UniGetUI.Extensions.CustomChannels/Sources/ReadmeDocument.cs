using System.Security.Cryptography;
using System.Text;

namespace UniGetUI.Extensions.CustomChannels.Sources;

/// <summary>单份 README（按文件区分语言），内容已修正图片地址。</summary>
public sealed record ReadmeSection(string File, string Language, string Markdown);

/// <summary>
/// 与某个版本 tag 匹配的项目说明文档：保留 README.md / README_CN.md 各自独立，
/// 由界面按语言分别展示，图片地址均改写为可访问的绝对地址。
/// </summary>
public sealed record ReadmeDocument(
    string Ref,
    IReadOnlyList<ReadmeSection> Sections,
    string Sha256,
    bool HasChanged = false)
{
    public string FileListText => Sections.Count == 0
        ? "README"
        : string.Join(" + ", Sections.Select(section => section.File));

    /// <summary>按固定顺序保存多份 README，并计算整体内容哈希（用于判断文档是否有更新）。</summary>
    public static ReadmeDocument Create(
        string refName,
        IReadOnlyList<(string File, string Text)> sections,
        string rawBaseUrl,
        bool hasChanged = false)
    {
        var list = new List<ReadmeSection>(sections.Count);
        var builder = new StringBuilder();

        foreach (var (file, text) in sections)
        {
            var markdown = MarkdownImageLinkFixer.Fix(text, rawBaseUrl);
            list.Add(new ReadmeSection(file, DetectLanguage(file), markdown));

            // 哈希覆盖文件名与内容，任一文件变化都能被识别
            builder.AppendLine(file);
            builder.Append(markdown);
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();

        return new ReadmeDocument(refName, list, hash, hasChanged);
    }

    /// <summary>按文件名判断语言：README_CN.md / README_zh-CN.md 等视为中文，其余视为英文。</summary>
    public static string DetectLanguage(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
        return name.Contains("_CN", StringComparison.Ordinal) ||
               name.Contains("_ZH", StringComparison.Ordinal) ||
               name.Contains("-ZH", StringComparison.Ordinal) ||
               name.Contains("_CHS", StringComparison.Ordinal) ||
               name.Contains("_CHT", StringComparison.Ordinal)
            ? "zh"
            : "en";
    }
}

/// <summary>
/// 修正 Markdown/HTML 中的相对图片地址：README 位于仓库根目录，
/// 因此相对路径按「仓库根 + 版本引用」解析为绝对地址。
/// </summary>
public static partial class MarkdownImageLinkFixer
{
    // ![alt](url "title")，url 可能带尖括号
    [System.Text.RegularExpressions.GeneratedRegex(
        @"!\[(?<alt>[^\]]*)\]\(\s*(?<url><[^>]*>|[^)\s]+)(?<rest>[^)]*)\)",
        System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex MarkdownImageRegex();

    // <img src="...">
    [System.Text.RegularExpressions.GeneratedRegex(
        @"<img\b[^>]*?\bsrc\s*=\s*(?<quote>[""'])(?<url>.*?)\k<quote>",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex HtmlImageRegex();

    /// <summary>
    /// 把相对图片地址改写为 <paramref name="rawBaseUrl"/> 下的绝对地址；
    /// 已是绝对地址、锚点或 data URI 时保持原样。
    /// </summary>
    public static string Fix(string? markdown, string rawBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return markdown ?? string.Empty;
        }

        if (!Uri.TryCreate(rawBaseUrl, UriKind.Absolute, out var baseUri))
        {
            return markdown;
        }

        var result = MarkdownImageRegex().Replace(markdown, match =>
        {
            var group = match.Groups["url"];
            var replacement = Rewrite(group.Value.Trim().Trim('<', '>'), baseUri);
            if (replacement is null)
            {
                return match.Value;
            }

            // 统一用尖括号包裹，避免路径中的空格/括号破坏 Markdown 语义
            return ReplaceAt(match, group, $"<{replacement}>");
        });

        return HtmlImageRegex().Replace(result, match =>
        {
            var group = match.Groups["url"];
            var replacement = Rewrite(group.Value, baseUri);
            return replacement is null ? match.Value : ReplaceAt(match, group, replacement);
        });
    }

    /// <summary>
    /// 统一改写图片地址：代码托管页的 blob 链接（github.com/.../blob/...、GitLab 的 /-/blob/）
    /// 转为可直接加载的 raw 地址；相对路径按仓库根解析为绝对地址。
    /// </summary>
    private static string? Rewrite(string url, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }

        if (TryNormalizeHostedBlob(url, out var rawUrl))
        {
            return rawUrl;
        }

        return ToAbsolute(url, baseUri);
    }

    /// <summary>把 GitHub/GitLab 的网页 blob 地址规范化为 raw 地址。</summary>
    private static bool TryNormalizeHostedBlob(string url, out string rawUrl)
    {
        rawUrl = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // https://{github-host}/{owner}/{repo}/blob/{ref}/{path}
        if (segments.Length >= 5 && segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase))
        {
            var tail = string.Join('/', segments[4..]);
            rawUrl = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                ? $"https://raw.githubusercontent.com/{segments[0]}/{segments[1]}/{segments[3]}/{tail}"
                : $"{uri.Scheme}://{uri.Authority}/{segments[0]}/{segments[1]}/raw/{segments[3]}/{tail}";
            return true;
        }

        // https://{gitlab-host}/{group}/{project}/-/blob/{ref}/{path}
        var blobIndex = Array.FindLastIndex(
            segments, s => s.Equals("blob", StringComparison.OrdinalIgnoreCase)) - 1;
        if (blobIndex > 0 &&
            segments[blobIndex] == "-" &&
            blobIndex + 2 < segments.Length)
        {
            var prefix = string.Join('/', segments[..blobIndex]);
            var tail = string.Join('/', segments[(blobIndex + 2)..]);
            rawUrl = $"{uri.Scheme}://{uri.Authority}/{prefix}/-/raw/{tail}";
            return true;
        }

        return false;
    }

    /// <summary>只替换匹配中 url 分组所在片段，避免误改 alt 文本或标题。</summary>
    private static string ReplaceAt(
        System.Text.RegularExpressions.Match match,
        System.Text.RegularExpressions.Group group,
        string replacement)
    {
        var start = group.Index - match.Index;
        return string.Concat(
            match.Value.AsSpan(0, start),
            replacement,
            match.Value.AsSpan(start + group.Length));
    }

    private static string? ToAbsolute(string url, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (url.StartsWith('#'))
        {
            return null;
        }

        // 协议相对地址补全协议
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + url;
        }

        // 已是绝对地址（http/https/data/mailto/file 等），保持原样
        if (Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return null;
        }

        var relative = url.TrimStart('/');
        if (relative.Length == 0)
        {
            return null;
        }

        try
        {
            var combined = new Uri(baseUri, relative);
            return combined.AbsoluteUri;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
