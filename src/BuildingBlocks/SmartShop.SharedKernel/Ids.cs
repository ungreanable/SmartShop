namespace SmartShop.SharedKernel;

public static class Ids
{
    /// <summary>Time-ordered UUID v7: index friendly and sortable by creation time.</summary>
    public static Guid New() => Guid.CreateVersion7();
}
