using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace UniGetUI.Extensions.CustomChannels.Pan;

internal static class PanHttp
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

    public static StringContent Json(object? payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

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
            throw new PanApiException(
                $"{operation} 失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
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

    public static void ApplyAccept(HttpRequestMessage request, string accept = "application/json, text/plain, */*")
    {
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(accept));
    }
}
