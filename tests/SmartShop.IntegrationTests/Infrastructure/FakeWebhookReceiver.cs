using System.Collections.Concurrent;
using System.Net;

namespace SmartShop.IntegrationTests.Infrastructure;

/// <summary>Captures outgoing webhook requests instead of sending them. URLs ending in "/fail" answer 500.</summary>
public sealed class FakeWebhookReceiver : HttpMessageHandler
{
    public sealed record Received(Uri Url, string Event, string Signature, string Body);

    public static ConcurrentQueue<Received> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue(new Received(request.RequestUri!, request.Headers.GetValues("X-SmartShop-Event").Single(),
            request.Headers.GetValues("X-SmartShop-Signature").Single(), body));
        return new HttpResponseMessage(request.RequestUri!.AbsolutePath.EndsWith("/fail", StringComparison.Ordinal)
            ? HttpStatusCode.InternalServerError
            : HttpStatusCode.OK);
    }

    public static IReadOnlyList<Received> For(string url) => Requests.Where(r => r.Url.ToString().StartsWith(url, StringComparison.Ordinal)).ToList();
}
