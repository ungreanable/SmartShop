using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SmartShop.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for a slip verification service. The QR text decides the answer:
/// "VALID-*" = genuine slip for the requested amount, "WRONGAMOUNT-*" = genuine but 1 baht, anything else = not found.
/// </summary>
internal sealed class FakeSlipVerifierHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is not null) return new HttpResponseMessage(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var reference = body.RootElement.GetProperty("reference").GetString()!;
        var amount = body.RootElement.GetProperty("amount").GetDecimal();
        object answer = reference switch
        {
            _ when reference.StartsWith("VALID-", StringComparison.Ordinal) => new { valid = true, amount, message = "ตรวจสอบกับธนาคารแล้ว" },
            _ when reference.StartsWith("WRONGAMOUNT-", StringComparison.Ordinal) => new { valid = true, amount = 1m },
            _ => new { valid = false, message = "ไม่พบรายการโอน" },
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(answer) };
    }
}
