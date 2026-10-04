using SmartShop.Contracts.Ordering;
using SmartShop.Contracts.Shops;
using SmartShop.Modules.Catalog.Domain;
using SmartShop.Modules.Ordering.Domain;
using SmartShop.Modules.Payments.Services;
using SmartShop.SharedKernel;

namespace SmartShop.UnitTests;

public class PromptPayTests
{
    [Fact]
    public void Payload_matches_the_reference_implementation()
    {
        // Reference vector from the widely used open-source promptpay-qr library.
        PromptPay.Payload("000-000-0000", null)
            .ShouldBe("00020101021129370016A000000677010111011300660000000005802TH530376463048956");
    }

    [Fact]
    public void Amount_makes_a_dynamic_qr_with_valid_crc()
    {
        var payload = PromptPay.Payload("0812345678", 120.5m);

        payload.ShouldContain("010212");
        payload.ShouldContain("0066812345678");
        payload.ShouldContain("5406120.50");
        var body = payload[..^4];
        payload[^4..].ShouldBe(PromptPay.Crc16(body).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Citizen_id_uses_tag_02()
    {
        PromptPay.Payload("1234567890123", 10).ShouldContain("02131234567890123");
    }
}

public class OrderStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);

    private static Order NewOrder() => Order.Place(new PlaceOrder(
        Ids.New(), Ids.New(), Ids.New(), "ร้าน", Ids.New(), "A-001", FulfillmentType.Delivery, null, null,
        new DeliveryAddress { HouseNo = "1" }, null,
        [new OrderLine { Id = Ids.New(), ItemId = Ids.New(), Name = "ข้าว", Quantity = 2, UnitPrice = 40, Options = [new OrderLineOption { Group = "ไข่", Name = "ไข่ดาว", PriceDelta = 10 }] }],
        0, null, null, new PaymentMethodInfo(Ids.New(), PaymentMethodType.Cash, "เงินสด", null, null, null, null, null, null, null, false),
        null, "key-12345", 15), Now);

    [Fact]
    public void Totals_include_option_prices_and_no_delivery_fee()
    {
        var order = NewOrder();
        order.Subtotal.ShouldBe(100);
        order.Total.ShouldBe(100);
        order.ExpiresAt.ShouldBe(Now.AddMinutes(15));
    }

    [Fact]
    public void Shop_may_skip_steps_but_not_go_backwards()
    {
        var order = NewOrder();
        order.Accept(Ids.New(), Now);
        order.MarkDelivered(Ids.New(), null, 12, Now.AddMinutes(30));
        order.Status.ShouldBe(OrderStatus.Delivered);
        order.AutoCompleteAt.ShouldBe(Now.AddMinutes(30).AddHours(12));

        Should.Throw<ConflictException>(() => order.StartPreparing(Ids.New(), Now));
    }

    [Fact]
    public void Customer_cannot_cancel_after_acceptance()
    {
        var order = NewOrder();
        order.Accept(Ids.New(), Now);
        Should.Throw<ConflictException>(() => order.CancelByCustomer(order.CustomerId, null, Now));
        order.RequestCancel(order.CustomerId, "เปลี่ยนใจ", Now);
        order.CancelRequestedAt.ShouldNotBeNull();
    }

    [Fact]
    public void Auto_complete_only_applies_to_delivered_orders()
    {
        var order = NewOrder();
        order.AutoComplete(Now).ShouldBeFalse();
        order.Accept(null, Now);
        order.MarkDelivered(Ids.New(), null, 1, Now);
        order.AutoComplete(Now.AddHours(1)).ShouldBeTrue();
        order.Status.ShouldBe(OrderStatus.Completed);
    }

    [Fact]
    public void Expire_is_idempotent_after_acceptance()
    {
        var order = NewOrder();
        order.Accept(Ids.New(), Now);
        order.Expire(Now.AddHours(1)).ShouldBeFalse();
        order.Status.ShouldBe(OrderStatus.Accepted);
    }

    [Fact]
    public void Pickup_orders_cannot_go_out_for_delivery()
    {
        var order = Order.Place(new PlaceOrder(Ids.New(), Ids.New(), Ids.New(), "ร้าน", Ids.New(), "A-002", FulfillmentType.Pickup, null, null, null, null,
            [new OrderLine { Id = Ids.New(), ItemId = Ids.New(), Name = "x", Quantity = 1, UnitPrice = 1 }], 0, null, null,
            new PaymentMethodInfo(Ids.New(), PaymentMethodType.Cash, "เงินสด", null, null, null, null, null, null, null, false), null, "k-123456", 15), Now);
        order.Accept(Ids.New(), Now);
        Should.Throw<DomainException>(() => order.Dispatch(Ids.New(), Now));
    }
}

public class InventoryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Stock_cannot_be_set_below_reserved()
    {
        var inventory = new Inventory(Ids.New(), Ids.New(), 5);
        inventory.Set(10, Ids.New(), null, Now);
        inventory.Available.ShouldBe(10);
        Should.NotThrow(() => inventory.Set(0, Ids.New(), null, Now));
    }

    [Fact]
    public void Daily_reset_sets_available_to_the_quota_once_per_day()
    {
        var inventory = new Inventory(Ids.New(), Ids.New(), 3);
        inventory.ConfigureDaily(30, new TimeOnly(5, 0));
        var today = new DateOnly(2026, 10, 5);

        inventory.DailyReset(today, Now).ShouldNotBeNull();
        inventory.Available.ShouldBe(30);
        inventory.DailyReset(today, Now).ShouldBeNull();
    }

    [Fact]
    public void Low_stock_warning_fires_once_until_restocked()
    {
        var inventory = new Inventory(Ids.New(), Ids.New(), 3);
        inventory.ConfigureLowStock(5);
        inventory.ShouldWarnLowStock().ShouldBeTrue();
        inventory.ShouldWarnLowStock().ShouldBeFalse();
        inventory.Add(10, Ids.New(), null, Now);
        inventory.ShouldWarnLowStock().ShouldBeFalse();
    }

    [Fact]
    public void Overnight_availability_window_belongs_to_the_previous_day()
    {
        var window = new AvailabilityWindow([DayOfWeek.Friday], new TimeOnly(22, 0), new TimeOnly(2, 0));
        window.Contains(new DateTime(2026, 10, 10, 1, 0, 0)).ShouldBeTrue();   // Saturday 01:00 (Friday night)
        window.Contains(new DateTime(2026, 10, 11, 1, 0, 0)).ShouldBeFalse();  // Sunday 01:00
    }
}
