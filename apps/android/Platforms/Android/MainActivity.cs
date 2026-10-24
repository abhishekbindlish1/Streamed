using Android.App;
using Android.Content;
using Android.Content.PM;

namespace StreamedTv.Platforms.Android
{
    [Activity(
        Label = "StreamedTV",
        Theme = "@style/Maui.SplashTheme",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
    [IntentFilter(
        new[] { Intent.ActionMain },
        Categories = new[] { "android.intent.category.LEANBACK_LAUNCHER" })]
    public class MainActivity : MauiAppCompatActivity
    {
    }
}
