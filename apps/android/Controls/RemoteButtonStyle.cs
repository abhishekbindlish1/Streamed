namespace StreamedTv.Controls;

internal static class RemoteButtonStyle
{
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
