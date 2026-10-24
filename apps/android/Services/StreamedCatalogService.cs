using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamedTv.Models;
using StreamedTv.Platform;

namespace StreamedTv.Services
{
    public sealed class StreamedCatalogService
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _json;
        private readonly Dictionary<string, CacheEntry> _cache;

        /// <summary>The most recent network/parse error encountered, or null if the last attempt
        /// succeeded. An empty array from the API counts as success — the UI surfaces "No matches"
        /// instead of falling back to bundled data, since stale demo content was misleading
        /// users into thinking streams were available when the upstream catalog was actually empty.</summary>
        public string LastNetworkError { get; private set; }

        public StreamedCatalogService(HttpClient http = null)
        {
            _http = http ?? CreateDefaultHttpClient();
            _json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            _cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        }

        private static HttpClient CreateDefaultHttpClient()
        {
            // Use the platform trust store and normal hostname validation.
            var handler = new HttpClientHandler
            {
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamedTv/1.0 (Tizen TV)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return client;
        }

        public async Task<string> DiagnoseAsync()
        {
            try
            {
                using (var response = await _http.GetAsync(StreamedEndpoints.Sports()))
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        return "ERROR: HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + " (" + bytes.Length + " bytes)";
                    }

                    return "OK: " + bytes.Length + " bytes";
                }
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        public async Task<IReadOnlyList<Sport>> GetSportsAsync(CancellationToken cancellationToken)
        {
            return await GetOrLoadAsync("sports", async () =>
            {
                try
                {
                    var sports = await GetArrayAsync<Sport>(StreamedEndpoints.Sports(), "sports", cancellationToken);
                    if (sports.Count > 0)
                    {
                        LastNetworkError = null;
                        return sports;
                    }
                    LastNetworkError = "API returned 0 sports";
                }
                catch (Exception ex)
                {
                    LastNetworkError = "GetSports: " + ex.GetType().Name + ": " + ex.Message;
                    AppLog.Error("StreamedTv", "GetSportsAsync failed: " + ex.GetType().Name + ": " + ex.Message);
                }

                // Empty list — UI shows "No matches". No bundled-demo fallback;
                // see the LastNetworkError docstring for why.
                return new List<Sport>();
            });
        }

        /// <summary>True if a fresh in-memory cache entry exists for this sport's matches.
        /// Lets the UI skip the "loading" skeleton for snappy back-and-forth tab switching.</summary>
        public bool HasCachedMatchesForSport(string sport)
        {
            string key = "matches:" + sport;
            CacheEntry entry;
            if (!_cache.TryGetValue(key, out entry)) return false;
            return DateTimeOffset.UtcNow - entry.CreatedAt < CacheDuration;
        }

        public async Task<IReadOnlyList<CatalogMatch>> GetMatchesBySportAsync(string sport, IEnumerable<string> favoriteTeams, CancellationToken cancellationToken)
        {
            var cacheKey = "matches:" + sport;
            var matches = await GetOrLoadAsync(cacheKey, async () =>
            {
                try
                {
                    var apiMatches = await GetArrayAsync<ApiMatch>(StreamedEndpoints.MatchesBySport(sport), "matches", cancellationToken);
                    var mapped = apiMatches.Select(m => MapApiMatch(m, null)).ToList();
                    if (mapped.Count > 0)
                    {
                        LastNetworkError = null;
                        return mapped;
                    }
                    LastNetworkError = "API returned 0 matches for " + sport;
                }
                catch (Exception ex)
                {
                    LastNetworkError = "GetMatches(" + sport + "): " + ex.GetType().Name + ": " + ex.Message;
                    AppLog.Error("StreamedTv", "GetMatchesBySportAsync(" + sport + ") failed: " + ex.GetType().Name + ": " + ex.Message);
                }

                return new List<CatalogMatch>();
            });

            return new StreamRanker(favoriteTeams).RankMatches(matches);
        }

        public async Task<IReadOnlyList<CatalogMatch>> GetLiveMatchesAsync(IEnumerable<string> favoriteTeams, CancellationToken cancellationToken)
        {
            var matches = await GetOrLoadAsync("matches:live", async () =>
            {
                try
                {
                    var apiMatches = await GetArrayAsync<ApiMatch>(StreamedEndpoints.LiveMatches(), "matches", cancellationToken);
                    var mapped = apiMatches.Select(m => MapApiMatch(m, "live")).ToList();
                    if (mapped.Count > 0)
                    {
                        return mapped;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error("StreamedTv", "GetLiveMatchesAsync failed: " + ex.GetType().Name + ": " + ex.Message);
                }

                return new List<CatalogMatch>();
            });

            return new StreamRanker(favoriteTeams).RankMatches(matches);
        }

        public async Task<IReadOnlyList<StreamOption>> GetStreamsAsync(CatalogMatch match, CancellationToken cancellationToken)
        {
            // Stream URLs from streamed.pk include short-lived signed manifest tokens
            // and expire fast (observed ~5-15 min). The player surfaces this as
            // `hls:networkError_manifestParsingError` because the .m3u8 fetch returns
            // 403/410 by the time the user actually presses play. Bypass the catalog's
            // 1-hour cache for streams specifically — refetch every time so the user
            // always gets fresh, working URLs.
            var resolved = new List<StreamOption>();

            foreach (var local in match.Streams)
            {
                if (!string.IsNullOrWhiteSpace(local.Url))
                {
                    resolved.Add(new StreamOption
                    {
                        Id = match.Id + ":" + resolved.Count,
                        StreamNo = resolved.Count + 1,
                        Language = local.Label,
                        Hd = true,
                        EmbedUrl = local.Url,
                        Source = local.Type
                    });
                }
            }

            foreach (var source in match.StreamSources)
            {
                if (string.IsNullOrWhiteSpace(source.Source) || string.IsNullOrWhiteSpace(source.Id))
                {
                    continue;
                }

                try
                {
                    var sourceStreams = await GetArrayAsync<StreamOption>(
                        StreamedEndpoints.StreamsBySource(source.Source, source.Id),
                        "streams",
                        cancellationToken);
                    resolved.AddRange(sourceStreams);
                }
                catch
                {
                    // Keep trying the other available source endpoints.
                }
            }

            return new StreamRanker(Array.Empty<string>()).RankStreams(resolved);
        }

        private static CatalogMatch MapApiMatch(ApiMatch match, string forcedStatus)
        {
            return new CatalogMatch
            {
                Id = match.Id,
                Title = match.Title,
                Sport = match.Category,
                Category = match.Category,
                Competition = match.Category,
                Date = match.Date,
                Status = forcedStatus ?? InferStatus(match.Date),
                Popular = match.Popular,
                Poster = match.Poster,
                Teams = match.Teams,
                StreamSources = match.Sources ?? new List<StreamSourceRef>()
            };
        }

        private static string InferStatus(long date)
        {
            var start = date > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(date) : DateTimeOffset.UtcNow;
            var delta = start - DateTimeOffset.UtcNow;
            if (Math.Abs(delta.TotalHours) < 3)
            {
                return "live";
            }

            return delta.TotalMilliseconds < 0 ? "recent" : "scheduled";
        }

        private async Task<List<T>> GetArrayAsync<T>(Uri uri, string preferredProperty, CancellationToken cancellationToken)
        {
            using (var response = await _http.GetAsync(uri, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                using (var document = JsonDocument.Parse(json))
                {
                    return ReadArray<T>(document.RootElement, preferredProperty);
                }
            }
        }

        private List<T> ReadArray<T>(JsonElement root, string preferredProperty)
        {
            if (root.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<T>>(root.GetRawText(), _json) ?? new List<T>();
            }

            var keys = new[] { preferredProperty, "data", "matches", "streams", "sports", "results" };
            foreach (var key in keys)
            {
                if (root.TryGetProperty(key, out var child) && child.ValueKind == JsonValueKind.Array)
                {
                    return JsonSerializer.Deserialize<List<T>>(child.GetRawText(), _json) ?? new List<T>();
                }
            }

            return new List<T>();
        }

        private async Task<IReadOnlyList<T>> GetOrLoadAsync<T>(string key, Func<Task<List<T>>> loader)
        {
            if (_cache.TryGetValue(key, out var entry) && DateTimeOffset.UtcNow - entry.CreatedAt < CacheDuration)
            {
                return (IReadOnlyList<T>)entry.Value;
            }

            var value = await loader();
            _cache[key] = new CacheEntry(value);
            return value;
        }

        private sealed class CacheEntry
        {
            public CacheEntry(object value)
            {
                Value = value;
                CreatedAt = DateTimeOffset.UtcNow;
            }

            public object Value { get; }
            public DateTimeOffset CreatedAt { get; }
        }
    }
}
