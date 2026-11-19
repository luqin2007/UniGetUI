using UniGetUI.Extensions.CustomChannels.Infrastructure;

namespace UniGetUI.Extensions.CustomChannels.Pan;

public static class PanPlatform
{
    public const string Baidu = "baidu";
    public const string Quark = "quark";

    public static string DisplayName(string platform) => platform switch
    {
        Baidu => "百度网盘",
        Quark => "夸克网盘",
        _ => platform,
    };
}

/// <summary>网盘 Cookie 会话：DPAPI 加密后存于程序目录 state/secrets.json。</summary>
public sealed class PanSessionStore(SecretStore secrets)
{
    public static string CookieKey(string platform) => $"pan.{platform}.cookie";

    public string? GetCookie(string platform) => secrets.Get(CookieKey(platform));

    public bool HasSession(string platform) => !string.IsNullOrWhiteSpace(GetCookie(platform));

    public void SaveCookie(string platform, string cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie) || !cookie.Contains('='))
        {
            throw new ArgumentException("Cookie 内容无效。", nameof(cookie));
        }

        secrets.Set(CookieKey(platform), cookie.Trim());
    }

    /// <summary>把浏览器抓到的多个键值合并进已有 Cookie。</summary>
    public void MergeCookies(string platform, IReadOnlyDictionary<string, string> cookies)
    {
        var merged = CookieParser.Merge(GetCookie(platform), cookies);
        SaveCookie(platform, merged);
    }

    /// <summary>
    /// 用服务端 Set-Cookie 刷新已保存的 Cookie（同名覆盖），返回是否有变化。
    /// 夸克的 __puus 会随接口响应轮换：接口请求带旧值仍能成功，但直链 CDN 只认最新的，
    /// 于是「取链成功、下载 412」。因此每个响应里的新值都要落回会话。
    /// </summary>
    public bool MergeSetCookies(string platform, IEnumerable<string> setCookieHeaders)
    {
        var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in setCookieHeaders)
        {
            // Set-Cookie: name=value; Path=/; Domain=... —— 只取第一段键值
            var pair = header.Split(';', 2)[0];
            var index = pair.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var name = pair[..index].Trim();
            var value = pair[(index + 1)..].Trim();
            if (name.Length == 0 || value.Length == 0)
            {
                // 删除型 Set-Cookie（空值 / Max-Age=0）不覆盖已有会话
                continue;
            }

            updates[name] = value;
        }

        if (updates.Count == 0)
        {
            return false;
        }

        var current = GetCookie(platform);
        var merged = CookieParser.Merge(current, updates);
        if (string.Equals(merged, current, StringComparison.Ordinal))
        {
            return false;
        }

        secrets.Set(CookieKey(platform), merged);
        return true;
    }

    public string? GetCookieValue(string platform, string name) =>
        CookieParser.Parse(GetCookie(platform)).TryGetValue(name, out var value) ? value : null;

    public void Clear(string platform) => secrets.Remove(CookieKey(platform));
}

public static class CookieParser
{
    public static Dictionary<string, string> Parse(string? header)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(header))
        {
            return result;
        }

        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = part[..index].Trim();
            var value = part[(index + 1)..].Trim();
            if (key.Length > 0)
            {
                result[key] = value;
            }
        }

        return result;
    }

    public static string Merge(string? existing, IReadOnlyDictionary<string, string> updates)
    {
        var map = Parse(existing);
        foreach (var (key, value) in updates)
        {
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
            {
                map[key] = value;
            }
        }

        return string.Join("; ", map.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    public static string With(string? existing, string key, string value) =>
        Merge(existing, new Dictionary<string, string> { [key] = value });
}
