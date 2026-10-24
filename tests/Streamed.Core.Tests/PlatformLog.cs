// Only the logging adapter is substituted. Tests compile the Android product
// catalog, models, ranking, and endpoints directly, without MAUI/Tizen devices.
namespace StreamedTv.Platform;
internal static class AppLog
{
    public static void Info(string tag, string message) { }
    public static void Warn(string tag, string message) { }
    public static void Error(string tag, string message) { }
}
