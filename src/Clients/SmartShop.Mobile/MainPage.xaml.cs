using Microsoft.AspNetCore.Components;

namespace SmartShop.Mobile;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
        if (DeepLinks.TakePending() is { } start) blazorWebView.StartPath = start;
        DeepLinks.Requested += OnDeepLink;
    }

    private async void OnDeepLink(string url) =>
        await blazorWebView.TryDispatchAsync(sp => sp.GetRequiredService<NavigationManager>().NavigateTo(url));
}
