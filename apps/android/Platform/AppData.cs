// Platform/AppData.cs
using Microsoft.Maui.Storage;

namespace StreamedTv.Platform
{
    // Replaces Tizen.Applications.Application.Current?.DirectoryInfo?.Data
    // across all ported services. Returns the app's private data directory.
    internal static class AppData
    {
        public static string? DataDirectory
        {
            get
            {
                try { return FileSystem.AppDataDirectory; }
                catch { return null; }
            }
        }
    }
}
