using Android.Gms.Tasks;
using Firebase;
using Firebase.Messaging;

namespace SmartShop.Mobile;

internal sealed partial class FcmPushRegistrar
{
    // Firebase initialises itself only when google-services.json was bundled at build time.
    private static partial bool PlatformSupported() =>
        FirebaseApp.GetApps(Android.App.Application.Context).Count > 0;

    private static partial System.Threading.Tasks.Task<string?> GetTokenAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        FirebaseMessaging.Instance.GetToken().AddOnCompleteListener(new Listener(tcs));
        return tcs.Task;
    }

    private sealed class Listener(TaskCompletionSource<string?> tcs) : Java.Lang.Object, IOnCompleteListener
    {
        public void OnComplete(Android.Gms.Tasks.Task task) =>
            tcs.TrySetResult(task.IsSuccessful ? task.Result?.ToString() : null);
    }
}
