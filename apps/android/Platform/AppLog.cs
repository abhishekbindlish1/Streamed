// Platform/AppLog.cs
using Android.Util;

namespace StreamedTv.Platform
{
    // Drop-in replacement for Tizen.Log. Services call AppLog.Info/Warn/Error
    // identically so porting is a global find-replace.
    internal static class AppLog
    {
        public static void Info(string tag, string msg)
        {
            try { Log.Info(tag, msg ?? ""); } catch { }
        }

        public static void Warn(string tag, string msg)
        {
            try { Log.Warn(tag, msg ?? ""); } catch { }
        }

        public static void Error(string tag, string msg)
        {
            try { Log.Error(tag, msg ?? ""); } catch { }
        }
    }
}
