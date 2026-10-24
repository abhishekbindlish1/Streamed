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
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode |
            ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    [IntentFilter(
        new[] { Intent.ActionMain },
        Categories = new[] { "android.intent.category.LEANBACK_LAUNCHER" })]
    public class MainActivity : MauiAppCompatActivity
    {
        public override bool OnKeyDown(global::Android.Views.Keycode keyCode, global::Android.Views.KeyEvent? e)
        {
            if (Shell.Current?.CurrentPage is StreamedTv.Views.PlayerPage player)
            {
                bool handled = keyCode switch
                {
                    global::Android.Views.Keycode.MediaPlayPause => player.HandlePlaybackKey(),
                    global::Android.Views.Keycode.MediaPlay => player.HandlePlaybackKey(true),
                    global::Android.Views.Keycode.MediaPause => player.HandlePlaybackKey(false),
                    _ => false
                };
                if (handled) return true;
            }
            return base.OnKeyDown(keyCode, e);
        }
    }
}
