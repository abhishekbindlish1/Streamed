using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StreamedTv.Services
{
    /// <summary>
    /// Process-wide cache of which posters / logos the backend has generated.
    /// Loaded once at app startup (and on demand) from <c>GET /v1/posters</c>; views
    /// consult <see cref="HasPoster"/> / <see cref="HasLogo"/> synchronously when
    /// resolving image URLs so they don't have to do network IO per tile.
    ///
    /// When the backend is unreachable (cron disabled, dev machine off) the manifest
    /// stays empty and every lookup returns false — callers naturally fall through
    /// to the streamed.pk poster pipeline.
    /// </summary>
    public static class BackendAssets
    {
        private static readonly object _lock = new object();
        private static HashSet<string> _posterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> _logoSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static SportsDataService _service;
        private static DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

        /// <summary>Inject the SportsDataService at startup. The first refresh kicks off
        /// fire-and-forget; subsequent calls reuse the same service.</summary>
        public static void Configure(SportsDataService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        /// <summary>True iff the backend has generated a poster for this match. Returns
        /// false when the manifest hasn't been loaded yet — that's intentional: callers
        /// should always have a streamed.pk fallback ready.</summary>
        public static bool HasPoster(string matchId)
        {
            if (string.IsNullOrWhiteSpace(matchId)) return false;
            lock (_lock)
            {
                return _posterIds.Contains(matchId);
            }
        }

        /// <summary>True iff the backend has generated a crest for this team name. The
        /// name is slugified via <see cref="SportsDataService.Slugify"/> and looked up.</summary>
        public static bool HasLogo(string teamName)
        {
            string slug = SportsDataService.Slugify(teamName);
            if (string.IsNullOrEmpty(slug)) return false;
            lock (_lock)
            {
                return _logoSlugs.Contains(slug);
            }
        }

        public static DateTimeOffset LastRefresh
        {
            get { lock (_lock) return _lastRefresh; }
        }

        public static int PosterCount
        {
            get { lock (_lock) return _posterIds.Count; }
        }

        public static int LogoCount
        {
            get { lock (_lock) return _logoSlugs.Count; }
        }

        /// <summary>Pull the latest manifest. Safe to call on a background task; updates
        /// the in-memory hash sets atomically. Returns true on success, false on any
        /// failure (transport, parse, service not configured).</summary>
        public static async Task<bool> RefreshAsync(CancellationToken ct = default)
        {
            SportsDataService svc = _service;
            if (svc == null) return false;

            AssetsManifest manifest = await svc.GetAssetsManifestAsync(ct);
            if (manifest == null) return false;

            HashSet<string> posters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> logos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (manifest.Posters != null)
            {
                foreach (PosterEntry p in manifest.Posters)
                {
                    if (!string.IsNullOrWhiteSpace(p.MatchId))
                    {
                        posters.Add(p.MatchId);
                    }
                }
            }
            if (manifest.Logos != null)
            {
                foreach (LogoEntry l in manifest.Logos)
                {
                    if (!string.IsNullOrWhiteSpace(l.TeamSlug))
                    {
                        logos.Add(l.TeamSlug);
                    }
                }
            }

            lock (_lock)
            {
                _posterIds = posters;
                _logoSlugs = logos;
                _lastRefresh = DateTimeOffset.UtcNow;
            }
            return true;
        }

        /// <summary>Return the absolute backend poster URL for a match if the backend has
        /// one cached, else null. Convenience wrapper for view code.</summary>
        public static string ResolvePosterUrl(string matchId)
        {
            if (_service == null) return null;
            if (!HasPoster(matchId)) return null;
            return _service.PosterUrl(matchId);
        }

        /// <summary>Return the absolute backend team-logo URL if a crest is cached for
        /// this team name, else null.</summary>
        public static string ResolveLogoUrl(string teamName)
        {
            if (_service == null) return null;
            if (!HasLogo(teamName)) return null;
            return _service.TeamLogoUrl(teamName);
        }
    }
}
