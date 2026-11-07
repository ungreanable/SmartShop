using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace SmartShop.UI.Services;

/// <summary>Small cross-page state: unread badge, cart badge.</summary>
public sealed class AppState(Api api, Realtime realtime, Session session)
{
    public int UnreadNotifications { get; private set; }
    public int CartItems { get; private set; }
    public event Action? Changed;

    public void Attach()
    {
        realtime.Notification -= OnNotification;
        realtime.Resynced -= OnResynced;
        realtime.Notification += OnNotification;
        realtime.Resynced += OnResynced;
    }

    // Notifications sent while the connection was down were not counted.
    private async void OnResynced()
    {
        try { await RefreshUnreadAsync(); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
    }

    public async Task RefreshUnreadAsync()
    {
        if (!session.IsAuthenticated) return;
        var result = await api.Get<Dictionary<string, int>>("api/notifications/unread-count", silent: true);
        UnreadNotifications = result?.GetValueOrDefault("count") ?? 0;
        Changed?.Invoke();
    }

    /// <summary>Join requests waiting for a village admin (badge in the admin navigation); null until known.</summary>
    public int? PendingMembers { get; private set; }

    public async Task RefreshPendingMembersAsync()
    {
        if (await api.Get<PlantCounts>("api/plant/admin/counts", silent: true) is { } counts) SetPendingMembers(counts.Pending);
    }

    public void SetPendingMembers(int count)
    {
        if (PendingMembers == count) return;
        PendingMembers = count;
        Changed?.Invoke();
    }

    public void SetCartItems(int count)
    {
        CartItems = count;
        Changed?.Invoke();
    }

    public void MarkAllRead()
    {
        UnreadNotifications = 0;
        Changed?.Invoke();
    }

    private void OnNotification(RealtimeNotification _)
    {
        UnreadNotifications++;
        Changed?.Invoke();
    }
}

public static class UiSetup
{
    public static IServiceCollection AddSmartShopUi(this IServiceCollection services, AppConfig config)
    {
        services.AddSingleton(config);
        services.AddMudServices(o =>
        {
            o.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.TopCenter;
            o.SnackbarConfiguration.VisibleStateDuration = 4000;
            o.SnackbarConfiguration.ShowCloseIcon = true;
            o.SnackbarConfiguration.PreventDuplicates = true;
            // A phone screen fits about two: more would cover the page when several arrive while the app is in the background.
            o.SnackbarConfiguration.MaxDisplayedSnackbars = 2;
        });
        services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(config.ApiBaseUrl) });
        services.AddScoped<Loc>();
        services.AddScoped<ThemeSettings>();
        services.AddScoped<Session>();
        services.AddScoped<PlantContextAccessor>();
        services.AddScoped<Api>();
        services.AddScoped<PlantContext>();
        services.AddScoped<PendingJoin>();
        services.AddScoped<PushEvents>();
        services.AddScoped<PushPrompt>();
        services.AddScoped<Realtime>();
        services.AddScoped<Browser>();
        services.AddScoped<AppState>();
        services.AddScoped<OrderAlarm>();
        services.AddScoped<Analytics>();
        return services;
    }

    public static MudTheme Theme { get; } = new()
    {
        // Contrast (WCAG AA, 4.5:1 for text): white on #00A851 was only 3.1:1, hard to read for older eyes in sunlight.
        // #00803E keeps the brand green and gives 5.1:1 for white button text, 4.7:1 as text on the page background.
        // Warning stays bright amber for borders and chips (with dark text, 7.4:1); warning TEXT uses the dark amber
        // WarningDarken (5.3:1 on white, 4.9:1 on the page; see .ss-warn-text and MudAlert).
        PaletteLight = new PaletteLight
        {
            Primary = "#00803E",
            PrimaryDarken = "#006B34",
            PrimaryLighten = "#00A851",
            Secondary = "#FF6A13",
            Tertiary = "#2E7DFF",
            Success = "#00803E",
            Warning = "#F59F00",
            WarningDarken = "#A85600",
            WarningContrastText = "#1F2328",
            Error = "#E03131",
            Background = "#F6F7F9",
            Surface = "#FFFFFF",
            AppbarBackground = "#FFFFFF",
            AppbarText = "#1F2328",
            TextPrimary = "#1F2328",
            TextSecondary = "#5C6370",
            ActionDefault = "#5C6370",
            LinesDefault = "#E6E8EB",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#2BC46E",
            // Dark text on the light dark-mode green (8.2:1); white on it is only 2.3:1.
            PrimaryContrastText = "#111315",
            // Amber text (WarningDarken, used by .ss-warn-text and alerts) must be light on the dark surface: 9.6:1.
            Warning = "#FFB545",
            WarningDarken = "#FFB545",
            WarningContrastText = "#111315",
            Secondary = "#FF8A45",
            Tertiary = "#5C9BFF",
            Background = "#111315",
            Surface = "#1A1D20",
            AppbarBackground = "#1A1D20",
            TextPrimary = "#ECEFF1",
            TextSecondary = "#A7AEB7",
            LinesDefault = "#2B2F33",
        },
        Typography = new Typography
        {
            // Thai script needs more room than Latin; sizes are deliberately larger than Material defaults
            // so the app stays comfortable for older villagers on small phones.
            Default = new DefaultTypography
            {
                FontFamily = ["IBM Plex Sans Thai", "Noto Sans Thai", "system-ui", "sans-serif"],
                FontSize = "1.0625rem",
                LineHeight = "1.6",
            },
            H4 = new H4Typography { FontSize = "2rem", FontWeight = "700", LineHeight = "1.3" },
            H5 = new H5Typography { FontSize = "1.625rem", FontWeight = "700", LineHeight = "1.35" },
            H6 = new H6Typography { FontSize = "1.3125rem", FontWeight = "600", LineHeight = "1.4" },
            Subtitle1 = new Subtitle1Typography { FontSize = "1.125rem", LineHeight = "1.5" },
            Subtitle2 = new Subtitle2Typography { FontSize = "1.0625rem", FontWeight = "600", LineHeight = "1.5" },
            Body1 = new Body1Typography { FontSize = "1.0625rem", LineHeight = "1.6" },
            Body2 = new Body2Typography { FontSize = "1rem", LineHeight = "1.55" },
            Button = new ButtonTypography { FontSize = "1rem", FontWeight = "600", LineHeight = "1.5", TextTransform = "none" },
            Caption = new CaptionTypography { FontSize = "0.9375rem", LineHeight = "1.5" },
            Overline = new OverlineTypography { FontSize = "0.875rem", LineHeight = "1.5" },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "12px" },
    };
}
