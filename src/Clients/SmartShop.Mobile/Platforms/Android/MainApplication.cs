using Android.App;
using Android.OS;
using Android.Runtime;

namespace SmartShop.Mobile;

[Application]
public class MainApplication(IntPtr handle, JniHandleOwnership ownership) : MauiApplication(handle, ownership)
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override void OnCreate()
    {
        base.OnCreate();
        // New orders must be noticed: a high-importance channel (sound + heads-up). FCM uses it by default (manifest).
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && GetSystemService(NotificationService) is NotificationManager manager)
            manager.CreateNotificationChannel(new NotificationChannel("orders", "ออเดอร์และสถานะ", NotificationImportance.High));
    }
}
