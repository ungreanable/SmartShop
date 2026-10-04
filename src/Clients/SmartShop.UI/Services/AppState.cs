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
        realtime.Notification += OnNotification;
    }

    public async Task RefreshUnreadAsync()
    {
        if (!session.IsAuthenticated) return;
        var result = await api.Get<Dictionary<string, int>>("api/notifications/unread-count", silent: true);
        UnreadNotifications = result?.GetValueOrDefault("count") ?? 0;
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
        });
        services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(config.ApiBaseUrl) });
        services.AddScoped<Loc>();
        services.AddScoped<Session>();
        services.AddScoped<PlantContextAccessor>();
        services.AddScoped<Api>();
        services.AddScoped<PlantContext>();
        services.AddScoped<Realtime>();
        services.AddScoped<Browser>();
        services.AddScoped<AppState>();
        services.AddScoped<OrderAlarm>();
        return services;
    }

    public static MudTheme Theme { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#00A851",
            PrimaryDarken = "#008C44",
            Secondary = "#FF6A13",
            Tertiary = "#2E7DFF",
            Success = "#00A851",
            Warning = "#F59F00",
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
