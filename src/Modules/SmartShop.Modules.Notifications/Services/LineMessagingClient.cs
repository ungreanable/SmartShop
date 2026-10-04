using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace SmartShop.Modules.Notifications.Services;

public sealed class NotificationOptions
{
    /// <summary>LINE Messaging API (Official Account). Must be under the same provider as the LINE Login channel.</summary>
    public string? LineAccessToken { get; set; }
    public string? LineChannelSecret { get; set; }
    public string? LineAddFriendUrl { get; set; }
    public int LineMonthlyQuota { get; set; } = 300;

    /// <summary>When fewer messages than this remain, only high-priority notifications use LINE.</summary>
    public int LineReserveForHighPriority { get; set; } = 50;

    public string? VapidSubject { get; set; }
    public string? VapidPublicKey { get; set; }
    public string? VapidPrivateKey { get; set; }
    public string? FirebaseCredentialsJson { get; set; }

    public string PublicUrl { get; set; } = "http://localhost:5080";
    public string? LiffId { get; set; }
    public string TimeZoneId { get; set; } = "Asia/Bangkok";

    public bool LineConfigured => !string.IsNullOrWhiteSpace(LineAccessToken);
    public bool WebPushConfigured => !string.IsNullOrWhiteSpace(VapidPublicKey) && !string.IsNullOrWhiteSpace(VapidPrivateKey);
    public bool FcmConfigured => !string.IsNullOrWhiteSpace(FirebaseCredentialsJson);

    /// <summary>Links open the LIFF app inside LINE when configured, otherwise the web app.</summary>
    public string AbsoluteLink(string? path) =>
        string.IsNullOrWhiteSpace(LiffId)
            ? PublicUrl.TrimEnd('/') + (path ?? "/")
            : $"https://liff.line.me/{LiffId}{path ?? ""}";
}

public enum LinePushResult { Sent, NotFriend, QuotaExceeded, InvalidRequest }

public interface ILineMessagingClient
{
    Task<LinePushResult> PushAsync(string lineUserId, JsonNode message, Guid retryKey, CancellationToken ct);

    /// <summary>True when the user follows the Official Account (profile is only visible to friends).</summary>
    Task<bool?> IsFriendAsync(string lineUserId, CancellationToken ct);

    bool VerifySignature(string body, string? signature);
}

internal sealed class LineMessagingClient(HttpClient http, IOptions<NotificationOptions> options) : ILineMessagingClient
{
    private readonly NotificationOptions _options = options.Value;

    public async Task<LinePushResult> PushAsync(string lineUserId, JsonNode message, Guid retryKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.line.me/v2/bot/message/push")
        {
            Content = JsonContent.Create(new JsonObject { ["to"] = lineUserId, ["messages"] = new JsonArray(message) }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.LineAccessToken);
        // LINE de-duplicates retries with the same key, so a retried delivery is never sent twice.
        request.Headers.Add("X-Line-Retry-Key", retryKey.ToString());

        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict) return LinePushResult.Sent;

        var body = await response.Content.ReadAsStringAsync(ct);
        return response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests when body.Contains("monthly limit", StringComparison.OrdinalIgnoreCase) => LinePushResult.QuotaExceeded,
            HttpStatusCode.TooManyRequests => throw new HttpRequestException("LINE rate limit", null, response.StatusCode),
            HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                when body.Contains("not found", StringComparison.OrdinalIgnoreCase) || body.Contains("Failed to send", StringComparison.OrdinalIgnoreCase)
                => LinePushResult.NotFriend,
            >= HttpStatusCode.InternalServerError => throw new HttpRequestException($"LINE {response.StatusCode}", null, response.StatusCode),
            _ => LinePushResult.InvalidRequest,
        };
    }

    public async Task<bool?> IsFriendAsync(string lineUserId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.line.me/v2/bot/profile/{Uri.EscapeDataString(lineUserId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.LineAccessToken);
        using var response = await http.SendAsync(request, ct);
        return response.StatusCode switch
        {
            HttpStatusCode.OK => true,
            HttpStatusCode.NotFound => false,
            _ => null,
        };
    }

    public bool VerifySignature(string body, string? signature)
    {
        if (string.IsNullOrEmpty(_options.LineChannelSecret) || string.IsNullOrEmpty(signature)) return false;
        var expected = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(_options.LineChannelSecret), Encoding.UTF8.GetBytes(body)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature));
    }
}

/// <summary>LINE Flex Message bubble with a button that opens the related page (LIFF inside LINE).</summary>
internal static class FlexMessages
{
    public static JsonNode Bubble(string title, string body, string url, bool urgent)
    {
        var color = urgent ? "#E8590C" : "#00B14F";
        return new JsonObject
        {
            ["type"] = "flex",
            ["altText"] = $"{title} - {body}",
            ["contents"] = new JsonObject
            {
                ["type"] = "bubble",
                ["size"] = "kilo",
                ["header"] = new JsonObject
                {
                    ["type"] = "box", ["layout"] = "vertical", ["backgroundColor"] = color, ["paddingAll"] = "12px",
                    ["contents"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text", ["text"] = title, ["color"] = "#FFFFFF", ["weight"] = "bold", ["size"] = "md", ["wrap"] = true,
                    }),
                },
                ["body"] = new JsonObject
                {
                    ["type"] = "box", ["layout"] = "vertical",
                    ["contents"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = body, ["wrap"] = true, ["size"] = "sm", ["color"] = "#333333" }),
                },
                ["footer"] = new JsonObject
                {
                    ["type"] = "box", ["layout"] = "vertical",
                    ["contents"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "button", ["style"] = "primary", ["color"] = color, ["height"] = "sm",
                        ["action"] = new JsonObject { ["type"] = "uri", ["label"] = "เปิดดู", ["uri"] = url },
                    }),
                },
            },
        };
    }
}
