using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SmartShop.Modules.Payments.Services;

public sealed class SlipVerifierOptions
{
    public const string Section = "Payments:SlipVerifier";

    /// <summary>Endpoint implementing the contract on <see cref="HttpSlipVerifier"/>. Empty = manual verification only.</summary>
    public string? Url { get; set; }

    /// <summary>Sent as <c>Authorization: Bearer ...</c>.</summary>
    public string? ApiKey { get; set; }

    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Mark the payment as paid automatically when the service confirms the slip (the shop still sees the verdict).</summary>
    public bool AutoConfirm { get; set; }
}

/// <summary>
/// Generic HTTP slip verification plug-in. Contract (adapt a vendor such as a bank API or slip-checking service with a tiny proxy):
/// <code>
/// POST {Url}   { "reference": "&lt;QR text from the slip&gt;", "amount": 90.00 }
/// 200          { "valid": true, "amount": 90.00, "message": "optional text shown to the shop" }
/// </code>
/// A slip is only accepted when <c>valid</c> is true and, if the service returns an amount, it matches the order total.
/// </summary>
internal sealed class HttpSlipVerifier(HttpClient http, IOptions<SlipVerifierOptions> options, ILogger<HttpSlipVerifier> logger) : ISlipVerifier
{
    private sealed record Request(string Reference, decimal Amount);
    private sealed record Response(bool Valid, decimal? Amount, string? Message);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.Url);

    public async Task<SlipVerification> VerifyAsync(string slipReference, decimal expectedAmount, CancellationToken ct)
    {
        if (!Enabled) return new SlipVerification(false, false, null);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.Url)
            {
                Content = JsonContent.Create(new Request(slipReference, expectedAmount), options: Json),
            };
            if (!string.IsNullOrEmpty(options.Value.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ApiKey);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Slip verifier returned {Status}", (int)response.StatusCode);
                return new SlipVerification(false, false, null);
            }

            var body = await response.Content.ReadFromJsonAsync<Response>(Json, timeout.Token);
            if (body is null) return new SlipVerification(false, false, null);
            if (body.Valid && body.Amount is { } amount && amount != expectedAmount)
                return new SlipVerification(true, false, $"ยอดในสลิป {amount:N2} ไม่ตรงกับยอดออเดอร์ {expectedAmount:N2}");
            return new SlipVerification(true, body.Valid, body.Message);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Unreachable service = no verdict; the shop verifies manually as usual.
            logger.LogWarning(e, "Slip verifier unavailable");
            return new SlipVerification(false, false, null);
        }
    }
}
