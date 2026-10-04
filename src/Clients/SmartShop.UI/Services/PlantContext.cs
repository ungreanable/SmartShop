namespace SmartShop.UI.Services;

/// <summary>The village the user is browsing; persisted so the app reopens in the same village.</summary>
public sealed class PlantContext(Api api, IAppStorage storage, PlantContextAccessor accessor)
{
    private const string Key = "smartshop.plant";

    public List<MyMembership> Memberships { get; private set; } = [];
    public MyMembership? Current { get; private set; }
    public List<MyShop> MyShops { get; private set; } = [];
    public bool Loaded { get; private set; }
    public event Action? Changed;

    public Guid? PlantId => Current?.PlantId;
    public bool IsAdmin => Current?.IsAdmin == true;

    public async Task LoadAsync(bool force = false)
    {
        if (Loaded && !force) return;
        Memberships = await api.Get<List<MyMembership>>("api/me/memberships", silent: true) ?? [];
        var saved = await storage.GetAsync(Key);
        var active = Memberships.Where(m => m.IsActive).ToList();
        Current = active.FirstOrDefault(m => m.PlantId.ToString() == saved) ?? active.FirstOrDefault();
        accessor.PlantId = Current?.PlantId;
        Loaded = true;
        await LoadShopsAsync();
        Changed?.Invoke();
    }

    public async Task SelectAsync(Guid plantId)
    {
        var membership = Memberships.FirstOrDefault(m => m.PlantId == plantId && m.IsActive);
        if (membership is null) return;
        Current = membership;
        accessor.PlantId = plantId;
        await storage.SetAsync(Key, plantId.ToString());
        await LoadShopsAsync();
        Changed?.Invoke();
    }

    public async Task LoadShopsAsync()
    {
        MyShops = Current is null ? [] : await api.Get<List<MyShop>>("api/me/shops", silent: true) ?? [];
    }

    public void Reset()
    {
        Memberships = [];
        Current = null;
        MyShops = [];
        accessor.PlantId = null;
        Loaded = false;
    }
}
