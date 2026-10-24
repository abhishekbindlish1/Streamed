using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StreamedTv.Services
{
    /// <summary>
    /// Disk-backed cache for remote poster / team-badge images.
    ///
    /// Why this exists:
    /// Tizen NUI's ImageView, when given a remote URL, calls into the system
    /// HTTP layer with NO disk cache — every assignment of <c>ResourceUrl</c>
    /// for the same URL re-downloads the bytes. On a TV switching between
    /// sport tabs (which rebuilds TileViews from scratch), that's an avoidable
    /// network round-trip per tile every time. This cache:
    ///
    ///   1. <see cref="TryGetLocalPath"/> — synchronous, returns the local
    ///      cached file path for a URL if we've already downloaded it. Use
    ///      this from view construction so the ImageView immediately gets a
    ///      file:// path (NUI loads local files fast and caches the decoded
    ///      texture by URL).
    ///   2. <see cref="EnsureCachedAsync"/> — async, downloads the URL bytes
    ///      to disk if not already cached. Call as fire-and-forget after
    ///      assigning the original URL on a cache miss, so the NEXT load is
    ///      a hit.
    ///
    /// Local file paths and file:// URLs are passed through unchanged — they
    /// don't need caching (NUI handles those efficiently).
    ///
    /// Bundled assets in shared/res/ are also passed through. Those decode
    /// from the .tpk's resource dir which is already on local disk.
    /// </summary>
    public static class PosterCache
    {
        private const string CacheDirName = "poster-cache";
        private const int MaxConcurrentDownloads = 3;

        private static readonly Dictionary<string, string> _urlToFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Task<string>> _inflight = new Dictionary<string, Task<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new object();
        private static readonly SemaphoreSlim _downloadGate = new SemaphoreSlim(MaxConcurrentDownloads);
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        private static string _cacheDir;
        private static bool _initialized;

        /// <summary>One-time setup. Call from Program.OnCreate after the Tizen
        /// app context is ready so we can resolve the data directory.</summary>
        public static void Init()
        {
            if (_initialized) return;
            try
            {
                string dataDir = Tizen.Applications.Application.Current?.DirectoryInfo?.Data;
                if (string.IsNullOrEmpty(dataDir)) return;
                _cacheDir = Path.Combine(dataDir, CacheDirName);
                if (!Directory.Exists(_cacheDir)) Directory.CreateDirectory(_cacheDir);
                // Hydrate the URL->file map from previously cached files. We
                // store a metadata file alongside each cached image with the
                // original URL so we can rebuild the map across launches.
                HydrateFromDisk();
                _initialized = true;
            }
            catch
            {
                // Cache disabled — TryGetLocalPath returns null, EnsureCachedAsync
                // becomes a no-op. ImageViews fall back to the original URL.
            }
        }

        /// <summary>Synchronous fast path: returns the local file path for
        /// <paramref name="url"/> if we've cached it, else null. Local-file
        /// inputs are returned unchanged (no caching needed).</summary>
        public static string TryGetLocalPath(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            // Local paths or file:// URLs — NUI loads these directly, no need
            // to indirect through our cache.
            if (IsLocalUrl(url)) return url;
            if (!_initialized) return null;
            lock (_lock)
            {
                if (_urlToFile.TryGetValue(url, out string path))
                {
                    if (File.Exists(path)) return path;
                    // Stale — file was deleted underneath us
                    _urlToFile.Remove(url);
                }
            }
            return null;
        }

        /// <summary>Download the URL to the cache directory if not already
        /// present. Returns the local path on success, or the original URL
        /// on failure (caller can still pass it to ImageView). Re-entrant:
        /// concurrent calls for the same URL share one download.</summary>
        public static async Task<string> EnsureCachedAsync(string url, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (IsLocalUrl(url)) return url;
            if (!_initialized) return url;

            string existing = TryGetLocalPath(url);
            if (existing != null) return existing;

            Task<string> inflight;
            lock (_lock)
            {
                if (!_inflight.TryGetValue(url, out inflight))
                {
                    inflight = DownloadToCacheAsync(url, ct);
                    _inflight[url] = inflight;
                }
            }
            try
            {
                return await inflight.ConfigureAwait(false);
            }
            finally
            {
                lock (_lock)
                {
                    _inflight.Remove(url);
                }
            }
        }

        private static async Task<string> DownloadToCacheAsync(string url, CancellationToken ct)
        {
            await _downloadGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                byte[] bytes;
                using (HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return url;
                    bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
                if (bytes == null || bytes.Length == 0) return url;

                string ext = GuessExtension(url);
                string baseName = ComputeHash(url);
                string path = Path.Combine(_cacheDir, baseName + ext);
                File.WriteAllBytes(path, bytes);
                // Persist URL alongside the bytes so HydrateFromDisk can rebuild
                // the map across launches without re-downloading.
                File.WriteAllText(path + ".url", url);
                lock (_lock)
                {
                    _urlToFile[url] = path;
                }
                return path;
            }
            catch
            {
                return url;
            }
            finally
            {
                _downloadGate.Release();
            }
        }

        private static void HydrateFromDisk()
        {
            try
            {
                foreach (string urlFile in Directory.EnumerateFiles(_cacheDir, "*.url"))
                {
                    try
                    {
                        string imgPath = urlFile.Substring(0, urlFile.Length - ".url".Length);
                        if (!File.Exists(imgPath)) continue;
                        string url = File.ReadAllText(urlFile).Trim();
                        if (string.IsNullOrEmpty(url)) continue;
                        _urlToFile[url] = imgPath;
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static bool IsLocalUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            // Tizen .tpk resource paths and bundled file paths are all absolute,
            // OS-style. Treat anything that doesn't start with http(s):// as local.
            return !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                  || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        private static string GuessExtension(string url)
        {
            int q = url.IndexOf('?');
            string clean = q > 0 ? url.Substring(0, q) : url;
            if (clean.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return ".png";
            if (clean.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return ".webp";
            if (clean.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return ".gif";
            return ".jpg";
        }

        private static string ComputeHash(string url)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
                StringBuilder sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
