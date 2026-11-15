using System.Net;
using System.Text;

namespace UniGetUI.Extensions.CustomChannels.Tests;

/// <summary>
/// 按入队顺序返回预置响应的 HttpMessageHandler 桩，并记录全部请求，
/// 用于在不联网的前提下验证渠道与 OpenList 客户端的请求构造与响应解析。
/// </summary>
internal sealed class TestHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();

    public List<string> RequestUris { get; } = new();

    public List<string?> AuthorizationHeaders { get; } = new();

    public List<string?> PrivateTokenHeaders { get; } = new();

    public int RequestCount => RequestUris.Count;

    public TestHttpHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responders.Enqueue(responder);
        return this;
    }

    public TestHttpHandler EnqueueJson(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    public TestHttpHandler EnqueueText(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(text, Encoding.UTF8, "text/plain"),
        });

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestUris.Add(request.RequestUri?.ToString() ?? string.Empty);
        AuthorizationHeaders.Add(
            request.Headers.TryGetValues("Authorization", out var values) ? values.FirstOrDefault() : null);
        PrivateTokenHeaders.Add(
            request.Headers.TryGetValues("PRIVATE-TOKEN", out var tokens) ? tokens.FirstOrDefault() : null);

        if (_responders.Count == 0)
        {
            throw new InvalidOperationException($"没有更多预置响应，未预期的请求：{request.RequestUri}");
        }

        return Task.FromResult(_responders.Dequeue()(request));
    }
}
