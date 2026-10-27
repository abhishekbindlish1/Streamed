namespace StreamedTv.Controls;

internal static class RemoteButtonStyle
{
    public static bool TryFocus(Button button)
    {
        if (!button.IsLoaded || !button.IsVisible || !double.IsFinite(button.Width) ||
            !double.IsFinite(button.Height) || button.Width <= 0 || button.Height <= 0)
            return false;
#if ANDROID
        if (button.Handler?.PlatformView is not global::Android.Views.View view ||
            !view.IsLaidOut || view.IsLayoutRequested)
            return false;
        var metrics = view.Resources?.DisplayMetrics;
        if (metrics is null || view.Width <= 0 || view.Height <= 0 ||
            view.Width > metrics.WidthPixels || view.Height > metrics.HeightPixels)
            return false;
#endif
        return button.Focus();
    }

    public static void Apply(Button button)
    {
        button.BorderWidth = 2;
        button.BorderColor = Color.FromArgb("#3a3a3a");
        button.Focused += (_, _) =>
        {
            button.BackgroundColor = Color.FromArgb("#263e60");
            button.BorderColor = Color.FromArgb("#8ebcff");
        };
        button.Unfocused += (_, _) =>
        {
            button.BackgroundColor = Color.FromArgb("#1e1e1e");
            button.BorderColor = Color.FromArgb("#3a3a3a");
        };
    }
}
