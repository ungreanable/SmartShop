using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace SmartShop.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        OpenNotificationLink(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        OpenNotificationLink(intent);
    }

    // Tapping a push notification starts the app with the FCM data payload as extras.
    private static void OpenNotificationLink(Intent? intent)
    {
        if (intent?.GetStringExtra("url") is { Length: > 0 } url) DeepLinks.Open(url);
    }
}

/// <summary>Receives smartshop://auth?code=...&amp;state=... after LINE login (bounced by the API).</summary>
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = MobileLoginLauncher.CallbackScheme, DataHost = "auth")]
public class WebAuthenticationCallbackActivity : WebAuthenticatorCallbackActivity;
