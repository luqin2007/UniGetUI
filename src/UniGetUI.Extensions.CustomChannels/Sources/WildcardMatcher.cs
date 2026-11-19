using System.Text;
using System.Text.RegularExpressions;

namespace UniGetUI.Extensions.CustomChannels.Sources;

/// <summary>
/// 文件名通配符匹配（*、? 与 [abc] 字符类），大小写不敏感。
/// 支持 <c>[-_]</c> 这类写法以同时匹配两种分隔符；正则量词（如 +）不生效。
/// </summary>
public static class WildcardMatcher
{
    public static bool IsMatch(string? name, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return true;
        }

        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return Regex.IsMatch(name, ToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static string? FirstMatch(IEnumerable<string> names, string? pattern)
    {
        var list = names.ToList();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return list.FirstOrDefault();
        }

        return list.FirstOrDefault(n => IsMatch(n, pattern));
    }

    public static string ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            switch (ch)
            {
                case '*':
                    sb.Append(".*");
                    break;
                case '?':
                    sb.Append('.');
                    break;
                case '[' when TryReadCharacterClass(pattern, i, out var characterClass, out var close):
                    sb.Append(characterClass);
                    i = close;
                    break;
                default:
                    sb.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }

        sb.Append('$');
        return sb.ToString();
    }

    /// <summary>
    /// 解析 <c>[abc]</c> / <c>[a-z]</c> / <c>[!abc]</c> 字符类并转成等价正则片段。
    /// 缺少右括号或写法不合法时返回 false，交给调用方按普通字符处理。
    /// </summary>
    private static bool TryReadCharacterClass(string pattern, int start, out string regex, out int end)
    {
        regex = string.Empty;
        end = start;

        var close = pattern.IndexOf(']', start + 2);
        if (close < 0)
        {
            return false;
        }

        var body = pattern[(start + 1)..close];
        var negate = body.Length > 0 && body[0] is '!' or '^';
        if (negate)
        {
            body = body[1..];
        }

        if (body.Length == 0)
        {
            return false;
        }

        var candidate = "[" + (negate ? "^" : string.Empty) + body.Replace(@"\", @"\\").Replace("[", @"\[") + "]";
        try
        {
            _ = new Regex(candidate, RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            // 例如 [z-a] 这种非法区间：整体当作普通字符
            return false;
        }

        regex = candidate;
        end = close;
        return true;
    }
}
