using System.Net;
using System.Net.Http.Headers;
using UniGetUI.Extensions.CustomChannels.Configuration;

namespace UniGetUI.Extensions.CustomChannels.Infrastructure;

/// <summary>统一 HttpClient 构造：连接池复用、代理、超时与 UA。</summary>
public static class UpdaterHttp
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/131.0.0.0 Safari/537.36";

    public const string AppUserAgent = "UniGetUI.CustomChannels/1.0";

    public static HttpClient Create(DownloadConfig config, string? userAgent = null, bool allowAutoRedirect = true)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = allowAutoRedirect,
            MaxAutomaticRedirections = 10,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(config.TimeoutSeconds, 30)),
            UseCookies = false,
        };

        if (!string.IsNullOrWhiteSpace(config.Proxy) &&
            Uri.TryCreate(config.Proxy, UriKind.Absolute, out var proxyUri))
        {
            handler.Proxy = new WebProxy(proxyUri);
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(config.TimeoutSeconds, 10)),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent ?? AppUserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        return client;
    }

    public static string RedactUrl(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Query))
        {
            return uri.ToString();
        }

        return uri.GetLeftPart(UriPartial.Path);
    }
}
