namespace SmartShop.Mobile;

// Desktop builds are for testing; they get notifications through LINE or the web app.
internal sealed partial class FcmPushRegistrar
{
    private static partial bool PlatformSupported() => false;

    private static partial Task<string?> GetTokenAsync() => Task.FromResult<string?>(null);
}
