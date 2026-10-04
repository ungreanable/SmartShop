namespace SmartShop.UI.Services;

public static class ShopStatusExtensions
{
    public static bool IsOpen(this ShopStatus status) => status.State is "Open" or "Busy";
}
