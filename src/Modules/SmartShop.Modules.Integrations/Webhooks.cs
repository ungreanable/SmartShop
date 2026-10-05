using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Integrations;

public sealed class WebhookOptions
{
    public const string Section = "Integrations:Webhooks";

    /// <summary>Feature switch (Features:Webhooks). Off: no webhook endpoints and no deliveries; API keys still work.</summary>
    public const string FeatureKey = "Features:Webhooks";

    public bool Enabled { get; set; }

    /// <summary>Allow plain http:// URLs (development only).</summary>
    public bool AllowInsecure { get; set; }

    /// <summary>Allow targets on loopback / private networks. Off in production to prevent SSRF into the cluster.</summary>
    public bool AllowPrivateNetworks { get; set; }

    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>Rejects webhook targets that could be used to reach internal services (SSRF).</summary>
public static class WebhookUrlPolicy
{
    public static Uri Validate(string url, WebhookOptions options)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
            throw new DomainException("webhook_url_invalid", "Enter a full URL such as https://example.com/hook.");
        var schemeOk = uri.Scheme == Uri.UriSchemeHttps || (options.AllowInsecure && uri.Scheme == Uri.UriSchemeHttp);
        if (!schemeOk) throw new DomainException("webhook_url_insecure", "Webhook URLs must use https://.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new DomainException("webhook_url_invalid", "Credentials in the URL are not allowed.");
        if (!options.AllowPrivateNetworks && (uri.IsLoopback || (IPAddress.TryParse(uri.DnsSafeHost, out var ip) && !IsPublic(ip))))
            throw new DomainException("webhook_url_private", "Webhook URLs must point to a public internet address.");
        return uri;
    }

    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast);
        var b = ip.GetAddressBytes();
        return !(b[0] == 10
                 || (b[0] == 172 && b[1] is >= 16 and <= 31)
                 || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 169 && b[1] == 254) // link-local incl. cloud metadata 169.254.169.254
                 || (b[0] == 100 && b[1] is >= 64 and <= 127) // carrier-grade NAT
                 || b[0] == 0 || b[0] >= 224);
    }

    /// <summary>
    /// Connection factory that checks the resolved address at connect time, so DNS names that resolve
    /// (or later re-resolve) to private addresses are blocked too.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(IOptions<WebhookOptions> options) => new()
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var allowed = options.Value.AllowPrivateNetworks ? addresses : addresses.Where(IsPublic).ToArray();
            if (allowed.Length == 0) throw new HttpRequestException("Webhook target resolves to a non-public address.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}

/// <summary>Posts one delivery. Receivers verify <c>X-SmartShop-Signature: t=..,v1=hex(HMAC-SHA256(secret, t + "." + body))</c>.</summary>
public sealed class WebhookSender(IHttpClientFactory factory, IOptions<WebhookOptions> options, TimeProvider clock)
{
    public const string ClientName = "webhooks";

    public async Task<(bool Ok, int? Status, string? Error)> SendAsync(WebhookEndpoint endpoint, WebhookDelivery delivery, CancellationToken ct)
    {
        Uri uri;
        try
        {
            uri = WebhookUrlPolicy.Validate(endpoint.Url, options.Value);
        }
        catch (DomainException e)
        {
            return (false, null, e.Message);
        }

        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-SmartShop-Event", delivery.EventType);
        request.Headers.Add("X-SmartShop-Delivery", delivery.Id.ToString());
        request.Headers.Add("X-SmartShop-Signature", $"t={timestamp},v1={Sign(endpoint.Secret, timestamp, delivery.Payload)}");
        request.Headers.UserAgent.ParseAdd("SmartShop-Webhooks/1.0");

        try
        {
            var http = factory.CreateClient(ClientName);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            return response.IsSuccessStatusCode ? (true, status, null) : (false, status, $"HTTP {status}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return (false, null, e is TaskCanceledException ? "timeout" : e.Message);
        }
    }

    public static string Sign(string secret, string timestamp, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
