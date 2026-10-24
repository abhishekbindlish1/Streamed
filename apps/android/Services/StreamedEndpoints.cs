using System;
using System.Linq;

namespace StreamedTv.Services
{
    public static class StreamedEndpoints
    {
        public const string DefaultBaseUrl = "https://streamed.pk";

        public static string BaseUrl { get; set; } = DefaultBaseUrl;

        public static Uri Sports()
        {
            return Build("/api/sports");
        }

        public static Uri MatchesBySport(string sport)
        {
            return Build("/api/matches/" + Uri.EscapeDataString(sport));
        }

        public static Uri PopularMatchesBySport(string sport)
        {
            return Build("/api/matches/" + Uri.EscapeDataString(sport) + "/popular");
        }

        public static Uri AllMatches()
        {
            return Build("/api/matches/all");
        }

        public static Uri AllPopularMatches()
        {
            return Build("/api/matches/all/popular");
        }

        public static Uri TodaysMatches()
        {
            return Build("/api/matches/all-today");
        }

        public static Uri TodaysPopularMatches()
        {
            return Build("/api/matches/all-today/popular");
        }

        public static Uri LiveMatches()
        {
            return Build("/api/matches/live");
        }

        public static Uri LivePopularMatches()
        {
            return Build("/api/matches/live/popular");
        }

        public static Uri StreamsBySource(string source, string id)
        {
            return Build("/api/stream/" + Uri.EscapeDataString(source) + "/" + Uri.EscapeDataString(id));
        }

        public static Uri TeamBadge(string id)
        {
            return Build("/api/images/badge/" + CleanImageId(id) + ".webp");
        }

        public static Uri MatchPoster(string homeBadge, string awayBadge)
        {
            return Build("/api/images/poster/" + CleanImageId(homeBadge) + "/" + CleanImageId(awayBadge) + ".webp");
        }

        public static Uri ProxiedImage(string poster)
        {
            return Build("/api/images/proxy/" + Uri.EscapeDataString(CleanProxyImageId(poster)) + ".webp");
        }

        private static Uri Build(string path)
        {
            // IMPORTANT: do NOT call Uri.TryCreate(path, UriKind.Absolute, ...) here. On Tizen the
            // runtime parses leading-slash paths like "/api/matches/football" as a valid absolute
            // file:// URI, then HttpClient.GetAsync rejects it with "The file scheme is not supported."
            // Only treat the input as already-absolute if it explicitly carries an http(s) scheme.
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri(path);
            }

            return new Uri(BaseUrl.TrimEnd('/') + path);
        }

        private static string CleanImageId(string value)
        {
            var cleaned = (value ?? "").Trim();
            cleaned = StripKnownImagePrefix(cleaned);
            cleaned = cleaned.TrimStart('/');
            if (cleaned.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(0, cleaned.Length - ".webp".Length);
            }

            return string.Join("/", cleaned.Split('/').Select(Uri.EscapeDataString));
        }

        private static string CleanProxyImageId(string value)
        {
            var cleaned = (value ?? "").Trim().TrimStart('/');
            if (cleaned.StartsWith("api/images/proxy/", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring("api/images/proxy/".Length);
            }

            if (cleaned.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(0, cleaned.Length - ".webp".Length);
            }

            return cleaned;
        }

        private static string StripKnownImagePrefix(string value)
        {
            var marker = "/api/images/";
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                value = value.Substring(index + marker.Length);
            }

            if (value.StartsWith("api/images/", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring("api/images/".Length);
            }

            if (value.StartsWith("badge/", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring("badge/".Length);
            }

            if (value.StartsWith("poster/", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring("poster/".Length);
            }

            return value;
        }
    }
}
