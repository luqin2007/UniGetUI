using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UniGetUI.Extensions.CustomChannels.Pan;

public static class PanHttp
{
    public static HttpRequestMessage Create(
        HttpMethod method,
        string url,
        string? cookie,
        string referer,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = content,
        };

        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        request.Headers.TryAddWithoutValidation("Referer", referer);
        request.Headers.TryAddWithoutValidation("Origin", new Uri(referer).GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("User-Agent", PanApiEndpoints.BrowserUserAgent);
        return request;
    }

    /// <summary>用 JsonNode 构造请求体：NativeAOT 下禁用反射序列化，DOM 直写是安全路径。</summary>
    public static StringContent Json(JsonNode? payload) =>
        new(payload?.ToJsonString() ?? "null", Encoding.UTF8, "application/json");

    public static FormUrlEncodedContent Form(IEnumerable<KeyValuePair<string, string>> pairs) =>
        new(pairs);

    public static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = TryExtractJsonMessage(body);
            throw new PanApiException(
                detail is null
                    ? $"{operation} 失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
                    : $"{operation} 失败：HTTP {(int)response.StatusCode}，{detail}",
                Truncate(body));
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new PanApiException($"{operation} 返回了无法解析的内容，接口可能已变更。", Truncate(body), ex);
        }
    }

    public static string Truncate(string? text, int max = 500) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length > max ? text[..max] + "…" : text;

    /// <summary>从错误响应体中提取服务端的 message/error 提示（夸克 message / 百度 errmsg）。</summary>
    private static string? TryExtractJsonMessage(string body)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var root = document.RootElement;
            foreach (var name in new[] { "message", "error_msg", "error_message", "errmsg" })
            {
                if (root.TryGetProperty(name, out var value) &&
                    value.ValueKind == System.Text.Json.JsonValueKind.String &&
                    value.GetString() is { Length: > 0 } text)
                {
                    return Truncate(text, 120);
                }
            }

            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static void ApplyAccept(HttpRequestMessage request, string accept = "application/json, text/plain, */*")
    {
        // 默认值是逗号分隔的多值 Accept，无法用 MediaTypeWithQualityHeaderValue 解析为单个值，
        // 改用不验证模式原样写入，避免 FormatException
        request.Headers.Accept.Clear();
        request.Headers.TryAddWithoutValidation("Accept", accept);
    }
}
