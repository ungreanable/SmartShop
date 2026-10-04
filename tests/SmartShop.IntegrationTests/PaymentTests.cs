using System.Net.Http.Json;
using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

public sealed record ProofDto(Guid Id, string? Url, string ContentType, string? DuplicateOfOrderNo, bool HasSlipReference, bool? SystemVerified, string? VerificationMessage);
public sealed record PaymentDto(Guid Id, Guid OrderId, decimal Amount, string MethodType, string? PromptPayQrDataUrl, string Status,
    string? RejectReason, List<ProofDto> Proofs, bool CanUploadProof, bool CanVerify, bool RefundRequired);

public class PaymentTests(SmartShopFactory factory)
{
    private async Task<(TestClient Owner, TestClient Customer, Guid ShopId, Guid PromptPayId, Guid ItemId, PlantDto Plant, TestClient Admin)> SetupAsync()
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/status", new { action = "open" });
        var promptPay = await owner.PostAsync<PaymentMethodDto>($"/api/merchant/shops/{shopId}/payment-methods",
            new { type = "PromptPayQr", displayName = "พร้อมเพย์", promptPayId = "0812345678" });
        var item = await owner.PostAsync<MerchantItemDto>($"/api/merchant/shops/{shopId}/catalog/items", new { kind = "Product", stockMode = "Untracked", name = "ขนม", price = 45 });
        var customer = await factory.JoinAsync(plant, admin);
        return (owner, customer, shopId, promptPay.Id, item.Item.Id, plant, admin);
    }

    private static async Task<OrderDto> OrderAsync(TestClient customer, Guid shopId, Guid itemId, Guid methodId)
    {
        await customer.PostOkAsync("/api/cart/items", new { shopId, itemId, quantity = 2 });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new { fulfillmentType = "Pickup", paymentMethodId = methodId }, options: TestClient.Json),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await TestClient.ReadAsync<OrderDto>(await customer.Http.SendAsync(request));
    }

    private static async Task<PaymentDto> PaymentAsync(TestClient client, Guid orderId)
    {
        PaymentDto? payment = null;
        await FactoryExtensions.EventuallyAsync(async () => payment = await client.GetAsync<PaymentDto>($"/api/orders/{orderId}/payment"));
        return payment!;
    }

    [Fact]
    public async Task PromptPay_order_gets_qr_with_amount_and_slip_verification_flow()
    {
        var s = await SetupAsync();
        var order = await OrderAsync(s.Customer, s.ShopId, s.ItemId, s.PromptPayId);

        var payment = await PaymentAsync(s.Customer, order.Id);
        payment.Amount.ShouldBe(90);
        payment.Status.ShouldBe("Unpaid");
        payment.PromptPayQrDataUrl.ShouldStartWith("data:image/png;base64,");
        payment.CanUploadProof.ShouldBeTrue();

        var slip = await s.Customer.UploadAsync("PaymentSlip", Scenario.Png(300, 500, SkiaSharp.SKColors.LightGreen));
        var uploaded = await s.Customer.PostAsync<PaymentDto>($"/api/orders/{order.Id}/payment/proofs", new { mediaId = slip.Id });
        uploaded.Status.ShouldBe("PendingVerification");

        var rejected = await s.Owner.PostAsync<PaymentDto>($"/api/merchant/orders/{order.Id}/payment/reject", new { reason = "ยอดไม่ตรง" });
        rejected.Status.ShouldBe("Rejected");
        rejected.RejectReason.ShouldBe("ยอดไม่ตรง");

        var slip2 = await s.Customer.UploadAsync("PaymentSlip", Scenario.Png(300, 500, SkiaSharp.SKColors.LightBlue));
        await s.Customer.PostOkAsync($"/api/orders/{order.Id}/payment/proofs", new { mediaId = slip2.Id });
        (await s.Owner.PostAsync<PaymentDto>($"/api/merchant/orders/{order.Id}/payment/verify")).Status.ShouldBe("Paid");

        // The order view reflects the payment status (event-driven).
        await FactoryExtensions.EventuallyAsync(async () =>
            (await s.Customer.GetAsync<OrderDto>($"/api/orders/{order.Id}")).PaymentStatus.ShouldBe("Paid"));
    }

    [Fact]
    public async Task Reusing_the_same_slip_for_another_order_is_flagged()
    {
        var s = await SetupAsync();
        var first = await OrderAsync(s.Customer, s.ShopId, s.ItemId, s.PromptPayId);
        var second = await OrderAsync(s.Customer, s.ShopId, s.ItemId, s.PromptPayId);
        await PaymentAsync(s.Customer, first.Id);
        await PaymentAsync(s.Customer, second.Id);

        var bytes = Scenario.Png(320, 480, SkiaSharp.SKColors.Pink);
        var slipA = await s.Customer.UploadAsync("PaymentSlip", bytes);
        await s.Customer.PostOkAsync($"/api/orders/{first.Id}/payment/proofs", new { mediaId = slipA.Id });
        var slipB = await s.Customer.UploadAsync("PaymentSlip", bytes);
        var flagged = await s.Customer.PostAsync<PaymentDto>($"/api/orders/{second.Id}/payment/proofs", new { mediaId = slipB.Id });

        flagged.Proofs.Single().DuplicateOfOrderNo.ShouldBe(first.OrderNo);
    }

    [Fact]
    public async Task Cash_is_marked_received_by_the_shop()
    {
        var s = await SetupAsync();
        var settings = await s.Owner.GetAsync<ShopSettingsDto>($"/api/merchant/shops/{s.ShopId}");
        var cash = settings.Shop.PaymentMethods.Single(p => p.Type == "Cash").Id;
        var order = await OrderAsync(s.Customer, s.ShopId, s.ItemId, cash);

        var payment = await PaymentAsync(s.Owner, order.Id);
        payment.CanUploadProof.ShouldBeFalse();
        (await s.Owner.PostAsync<PaymentDto>($"/api/merchant/orders/{order.Id}/payment/received")).Status.ShouldBe("Paid");
    }

    [Fact]
    public async Task Shop_can_require_payment_before_preparing()
    {
        var s = await SetupAsync();
        await s.Owner.PutOkAsync($"/api/merchant/shops/{s.ShopId}/order-settings", new
        {
            acceptMode = "Manual",
            acceptTimeoutMinutes = 15,
            reminderAfterMinutes = 3,
            autoCompleteHours = 12,
            allowPreorderWhenClosed = false,
            prepTimeMinutes = 10,
            slotIntervalMinutes = 30,
            requirePaymentBeforePreparing = true,
        });
        var order = await OrderAsync(s.Customer, s.ShopId, s.ItemId, s.PromptPayId);
        await s.Owner.PostOkAsync($"/api/merchant/orders/{order.Id}/accept");

        var blocked = await s.Owner.PostRawAsync($"/api/merchant/orders/{order.Id}/start");
        (await TestClient.ProblemCodeAsync(blocked)).ShouldBe("payment_required");
    }

    [Fact]
    public async Task Cancelled_unpaid_order_voids_its_payment()
    {
        var s = await SetupAsync();
        var order = await OrderAsync(s.Customer, s.ShopId, s.ItemId, s.PromptPayId);
        await PaymentAsync(s.Customer, order.Id);
        await s.Customer.PostOkAsync($"/api/orders/{order.Id}/cancel", new { });

        await FactoryExtensions.EventuallyAsync(async () =>
            (await s.Customer.GetAsync<PaymentDto>($"/api/orders/{order.Id}/payment")).Status.ShouldBe("Voided"));
    }

    private static byte[] SlipWithQr(string reference) => new QRCoder.PngByteQRCode(
        new QRCoder.QRCodeGenerator().CreateQrCode(reference, QRCoder.QRCodeGenerator.ECCLevel.M)).GetGraphic(8);

    [Fact]
    public async Task Slip_verifier_plugin_auto_confirms_genuine_slips_and_flags_wrong_ones()
    {
        var s = await SetupAsync();
        var genuine = await OrderAsync(s.Customer, s.ShopId, s.ItemId, s.PromptPayId);
        var wrong = await OrderAsync(s.Customer, s.ShopId, s.ItemId, s.PromptPayId);
        await PaymentAsync(s.Customer, genuine.Id);
        await PaymentAsync(s.Customer, wrong.Id);

        var slip = await s.Customer.UploadAsync("PaymentSlip", SlipWithQr("VALID-" + Guid.NewGuid().ToString("N")));
        (await s.Customer.PostAsync<PaymentDto>($"/api/orders/{genuine.Id}/payment/proofs", new { mediaId = slip.Id })).Proofs.Single().HasSlipReference.ShouldBeTrue();
        await FactoryExtensions.EventuallyAsync(async () =>
        {
            var payment = await s.Owner.GetAsync<PaymentDto>($"/api/orders/{genuine.Id}/payment");
            payment.Status.ShouldBe("Paid");
            payment.Proofs.Single().SystemVerified.ShouldBe(true);
            payment.Proofs.Single().VerificationMessage.ShouldBe("ตรวจสอบกับธนาคารแล้ว");
        });

        var badSlip = await s.Customer.UploadAsync("PaymentSlip", SlipWithQr("WRONGAMOUNT-" + Guid.NewGuid().ToString("N")));
        await s.Customer.PostOkAsync($"/api/orders/{wrong.Id}/payment/proofs", new { mediaId = badSlip.Id });
        await FactoryExtensions.EventuallyAsync(async () =>
        {
            var payment = await s.Owner.GetAsync<PaymentDto>($"/api/orders/{wrong.Id}/payment");
            payment.Proofs.Single().SystemVerified.ShouldBe(false);
            payment.Proofs.Single().VerificationMessage!.ShouldContain("ไม่ตรง");
            payment.Status.ShouldBe("PendingVerification"); // the shop decides
        });
    }
}
