using System.Text;
using System.Text.RegularExpressions;

namespace UniGetUI.Extensions.CustomChannels.Sources;

/// <summary>文件名通配符匹配（* 与 ?），大小写不敏感。</summary>
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
        foreach (var ch in pattern)
        {
            sb.Append(ch switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(ch.ToString()),
            });
        }

        sb.Append('$');
        return sb.ToString();
    }
}
