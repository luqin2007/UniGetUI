using System.Text.Json;
using System.Text.Json.Nodes;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Pan;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.OpenList;

public sealed record OpenListEntry(string Name, string Path, long Size, bool IsDir, DateTimeOffset? Modified);

public sealed record OpenListFile(string Name, string Path, long Size, string? RawUrl, string? Sha256);

/// <summary>
/// OpenList HTTP 客户端：登录、列目录、取直链。
/// OpenList 是独立的 AGPL 服务，本工具仅通过其公开 HTTP API 调用，不链接其源码。
/// </summary>
/// <remarks>
/// 通过委托读取配置，保证应用内修改或重新加载配置后（同一实例被替换）仍能使用最新地址与凭据，
/// 不需要重建客户端。
/// </remarks>
public sealed class OpenListClient(HttpClient http, Func<OpenListConfig> configAccessor, ILogger<OpenListClient> logger)
{
    private string? _token;

    private OpenListConfig Config => configAccessor();

    public string BaseUrl => (Config.BaseUrl ?? "http://127.0.0.1:5244").TrimEnd('/');

    public bool HasToken => !string.IsNullOrWhiteSpace(_token ?? Config.Token);

    /// <summary>清除已缓存的登录状态（配置变更或本地实例重启后调用）。</summary>
    public void ResetSession() => _token = null;

    /// <summary>探测服务是否在线。</summary>
    public async Task<string?> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync($"{BaseUrl}/ping", ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode && text.Trim().Contains("pong", StringComparison.OrdinalIgnoreCase)
                ? "在线"
                : $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"不可达：{ex.Message}";
        }
    }

    public async Task<string?> LoginAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(Config.Token))
        {
            _token = Config.Token;
            return _token;
        }

        if (string.IsNullOrWhiteSpace(Config.Username) || string.IsNullOrWhiteSpace(Config.Password))
        {
            throw new PanApiException("OpenList 需要用户名与密码（或直接配置 token）。");
        }

        using var document = await PostAsync(
            "/api/auth/login",
            new JsonObject
            {
                ["username"] = Config.Username,
                ["password"] = Config.Password,
            },
            "OpenList 登录",
            ct,
            requireAuth: false).ConfigureAwait(false);

        var data = document.RootElement.TryGetProperty("data", out var d) ? d : default;
        var token = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("token", out var t)
            ? t.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new PanApiException("OpenList 登录失败：未返回 token。");
        }

        _token = token;
        logger.LogInformation("OpenList 登录成功：{BaseUrl}", BaseUrl);
        return token;
    }

    public async Task<IReadOnlyList<OpenListEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct).ConfigureAwait(false);

        using var document = await PostAsync(
            "/api/fs/list",
            new JsonObject
            {
                ["path"] = path,
                ["password"] = string.Empty,
                ["page"] = 1,
                ["per_page"] = 200,
                ["refresh"] = false,
                ["order"] = "name",
                ["order_by"] = "asc",
            },
            "OpenList 列目录",
            ct).ConfigureAwait(false);

        var entries = new List<OpenListEntry>();
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return entries;
        }

        foreach (var item in content.EnumerateArray())
        {
            var name = GetString(item, "name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var modified = GetString(item, "modified");
            entries.Add(new OpenListEntry(
                name,
                Combine(path, name),
                GetLong(item, "size") ?? 0,
                item.TryGetProperty("is_dir", out var dir) && dir.ValueKind == JsonValueKind.True,
                DateTimeOffset.TryParse(modified, out var dt) ? dt : null));
        }

        return entries;
    }

    public async Task<OpenListFile?> GetFileAsync(string path, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct).ConfigureAwait(false);

        using var document = await PostAsync(
            "/api/fs/get",
            new JsonObject
            {
                ["path"] = path,
                ["password"] = string.Empty,
            },
            "OpenList 取文件信息",
            ct).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = GetString(data, "name") ?? Path.GetFileName(path);
        var rawUrl = GetString(data, "raw_url");
        string? sha256 = null;
        if (data.TryGetProperty("hash_info", out var hash) && hash.ValueKind == JsonValueKind.Object)
        {
            sha256 = GetString(hash, "sha256");
        }

        return new OpenListFile(name, path, GetLong(data, "size") ?? 0, rawUrl, sha256);
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (HasToken && _token is null)
        {
            _token = Config.Token;
        }

        if (string.IsNullOrWhiteSpace(_token))
        {
            await LoginAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<JsonDocument> PostAsync(
        string relativePath,
        JsonNode payload,
        string operation,
        CancellationToken ct,
        bool requireAuth = true)
    {
        if (requireAuth && string.IsNullOrWhiteSpace(_token) && !string.IsNullOrWhiteSpace(Config.Token))
        {
            _token = Config.Token;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + relativePath)
        {
            Content = PanHttp.Json(payload),
        };

        if (!string.IsNullOrWhiteSpace(_token))
        {
            request.Headers.TryAddWithoutValidation("Authorization", _token);
        }

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

        // 文档所有权交给调用方（调用处以 using 释放），此处不可 using，否则返回后已被释放
        var document = await PanHttp.ReadJsonAsync(response, operation, ct).ConfigureAwait(false);

        var code = document.RootElement.TryGetProperty("code", out var c) && c.TryGetInt32(out var value)
            ? value
            : 200;

        if (code != 200)
        {
            var message = GetString(document.RootElement, "message") ?? "未知错误";
            if (code is 401 or 403)
            {
                _token = null;
            }

            document.Dispose();
            throw new PanApiException($"{operation}失败：code={code}, message={message}");
        }

        return document;
    }

    private static string Combine(string path, string name)
    {
        var normalized = path.EndsWith('/') ? path : path + "/";
        return normalized + name;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }
}
