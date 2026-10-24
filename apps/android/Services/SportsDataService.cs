using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace StreamedTv.Services
{
    /// <summary>HTTP client for the Streamed Go backend at /backend/. Wraps the team / league
    /// endpoints and is permissive about cert validation since the TV's HttpClient stack often
    /// rejects self-signed dev certs (matches StreamedCatalogService's policy).</summary>
    public sealed class SportsDataService
    {
        /// <summary>Default backend base URL.
        ///
        /// Set to the Cloud Run service deployed via <c>backend/deploy.sh</c> in project
        /// <c>streamed-495716</c>, region <c>us-central1</c>. Override at runtime by dropping
        /// a one-line <c>res/backend-url.txt</c> file in the app bundle (or beside the binary)
        /// containing a different URL — useful for swapping back to a laptop dev server
        /// without recompiling.</summary>
        public const string DefaultBaseUrl = "https://streamed-backend-1057053643049.us-central1.run.app";

        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _json;
        private readonly string _baseUrl;

        public string BaseUrl { get { return _baseUrl; } }
        public string LastError { get; private set; }

        public SportsDataService(string baseUrl = null, HttpClient http = null)
        {
            _baseUrl = (string.IsNullOrEmpty(baseUrl) ? ResolveBaseUrl() : baseUrl).TrimEnd('/');
            _http = http ?? CreateDefaultHttpClient();
            _json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        }

        /// <summary>Look for an on-disk override (<c>backend-url.txt</c>) before falling back
        /// to the compile-time default. Lets you reinstall the same .tpk and just swap the file
        /// to switch between Cloud Run and local dev.</summary>
        private static string ResolveBaseUrl()
        {
            string[] candidates = new[]
            {
                System.IO.Path.Combine(System.AppContext.BaseDirectory, "res", "backend-url.txt"),
                System.IO.Path.Combine(System.AppContext.BaseDirectory, "Resources", "Raw", "backend-url.txt"),
                System.IO.Path.Combine(System.AppContext.BaseDirectory, "backend-url.txt"),
            };
            foreach (string path in candidates)
            {
                try
                {
                    if (System.IO.File.Exists(path))
                    {
                        string contents = System.IO.File.ReadAllText(path).Trim();
                        if (!string.IsNullOrEmpty(contents) &&
                            (contents.StartsWith("http://") || contents.StartsWith("https://")))
                        {
                            return contents;
                        }
                    }
                }
                catch
                {
                    // Override file missing or unreadable — fall through to default.
                }
            }
            return DefaultBaseUrl;
        }

        private static HttpClient CreateDefaultHttpClient()
        {
            HttpClientHandler handler = new HttpClientHandler
            {
            };
            HttpClient client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(6),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamedTv/1.0 (Tizen TV)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return client;
        }

        public async Task<HealthResponse> GetHealthAsync(CancellationToken ct = default)
        {
            return await GetJsonAsync<HealthResponse>("/v1/health", ct);
        }

        public async Task<TeamStanding> GetTeamStandingsAsync(string teamId, CancellationToken ct = default)
        {
            return await GetJsonAsync<TeamStanding>("/v1/teams/" + Uri.EscapeDataString(teamId) + "/standings", ct);
        }

        public async Task<NextMatch> GetTeamNextMatchAsync(string teamId, CancellationToken ct = default)
        {
            return await GetJsonAsync<NextMatch>("/v1/teams/" + Uri.EscapeDataString(teamId) + "/next-match", ct);
        }

        public async Task<LeagueStandings> GetLeagueStandingsAsync(string leagueId, CancellationToken ct = default)
        {
            return await GetJsonAsync<LeagueStandings>("/v1/leagues/" + Uri.EscapeDataString(leagueId) + "/standings", ct);
        }

        /// <summary>Fetch the directory listing of every poster + team logo the backend has
        /// generated. The TV uses this at startup to know which match IDs / team slugs to
        /// serve from the backend vs the streamed.pk fallback. Returns null on transport failure.</summary>
        public async Task<AssetsManifest> GetAssetsManifestAsync(CancellationToken ct = default)
        {
            return await GetJsonAsync<AssetsManifest>("/v1/posters", ct);
        }

        /// <summary>Fetch the current list of live cricket matches with normalized scores.
        /// Backed by the backend's RapidAPI passthrough at /v1/live/cricket; returns null if
        /// backend is unreachable or live-score service isn't wired (returns 503 in that case).
        /// The backend caches upstream for 30s, so polling at 60s is safe.</summary>
        public async Task<LiveCricketResponse> GetLiveCricketAsync(CancellationToken ct = default)
        {
            return await GetJsonAsync<LiveCricketResponse>("/v1/live/cricket", ct);
        }

        /// <summary>Resolve a team name (e.g. "Manchester United", "Northern XI") to a backend
        /// teamId via the lookup endpoint. Returns null if backend is unreachable or no match.</summary>
        public async Task<TeamLookup> LookupTeamAsync(string teamName, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(teamName)) return null;
            return await GetJsonAsync<TeamLookup>("/v1/teams/lookup?name=" + Uri.EscapeDataString(teamName), ct);
        }

        /// <summary>URL of the backend-generated poster for a match. The cron creates these once
        /// per day; if the file isn't there yet the request 404s and the caller should fall back
        /// to the streamed.pk poster pipeline.</summary>
        public string PosterUrl(string matchId)
        {
            if (string.IsNullOrWhiteSpace(matchId)) return null;
            return _baseUrl + "/v1/posters/" + Uri.EscapeDataString(matchId) + ".png";
        }

        /// <summary>URL of the backend-generated team logo, keyed by slug. Returns null if name is empty.</summary>
        public string TeamLogoUrl(string teamName)
        {
            string slug = Slugify(teamName);
            if (string.IsNullOrEmpty(slug)) return null;
            return _baseUrl + "/v1/teams/" + slug + "/logo.png";
        }

        /// <summary>Mirror of the backend's slug helper. Lowercase, non-alphanumeric→hyphen,
        /// collapse runs of hyphens, trim. MUST stay in sync with backend/internal/posters/slug.go
        /// or the URLs won't resolve.</summary>
        public static string Slugify(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            StringBuilder sb = new StringBuilder(name.Length);
            bool lastWasDash = false;
            foreach (char c in name.Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    sb.Append(c);
                    lastWasDash = false;
                }
                else if (!lastWasDash && sb.Length > 0)
                {
                    sb.Append('-');
                    lastWasDash = true;
                }
            }
            string s = sb.ToString();
            return s.TrimEnd('-');
        }

        private async Task<T> GetJsonAsync<T>(string path, CancellationToken ct) where T : class
        {
            try
            {
                using (HttpResponseMessage response = await _http.GetAsync(_baseUrl + path, ct))
                {
                    string body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        LastError = "HTTP " + (int)response.StatusCode + " " + path;
                        return null;
                    }
                    LastError = null;
                    return JsonSerializer.Deserialize<T>(body, _json);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }
    }

    public sealed class HealthResponse
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("uptime")] public string Uptime { get; set; } = "";
    }

    public sealed class TeamLookup
    {
        [JsonPropertyName("query")]         public string Query { get; set; } = "";
        [JsonPropertyName("teamId")]        public string TeamId { get; set; } = "";
        [JsonPropertyName("canonicalName")] public string CanonicalName { get; set; } = "";
        [JsonPropertyName("league")]        public string League { get; set; } = "";
        [JsonPropertyName("confidence")]    public double Confidence { get; set; }
        [JsonPropertyName("source")]        public string Source { get; set; } = "";
    }

    public sealed class TeamStanding
    {
        [JsonPropertyName("teamId")]    public string TeamId { get; set; } = "";
        [JsonPropertyName("league")]    public string League { get; set; } = "";
        [JsonPropertyName("position")]  public int Position { get; set; }
        [JsonPropertyName("played")]    public int Played { get; set; }
        [JsonPropertyName("wins")]      public int Wins { get; set; }
        [JsonPropertyName("draws")]     public int Draws { get; set; }
        [JsonPropertyName("losses")]    public int Losses { get; set; }
        [JsonPropertyName("points")]    public int Points { get; set; }
        [JsonPropertyName("recentForm")] public List<string> RecentForm { get; set; } = new List<string>();
        [JsonPropertyName("source")]    public string Source { get; set; } = "";
        [JsonPropertyName("warning")]   public string Warning { get; set; } = "";
    }

    public sealed class NextMatchOpponent
    {
        [JsonPropertyName("id")]   public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }

    public sealed class NextMatch
    {
        [JsonPropertyName("teamId")]      public string TeamId { get; set; } = "";
        [JsonPropertyName("opponent")]    public NextMatchOpponent Opponent { get; set; }
        [JsonPropertyName("kickoff")]     public string Kickoff { get; set; } = "";
        [JsonPropertyName("competition")] public string Competition { get; set; } = "";
        [JsonPropertyName("venue")]       public string Venue { get; set; } = "";
        [JsonPropertyName("source")]      public string Source { get; set; } = "";
        [JsonPropertyName("warning")]     public string Warning { get; set; } = "";
    }

    public sealed class LeagueStandingRow
    {
        [JsonPropertyName("position")]  public int Position { get; set; }
        [JsonPropertyName("teamId")]    public string TeamId { get; set; } = "";
        [JsonPropertyName("teamName")]  public string TeamName { get; set; } = "";
        [JsonPropertyName("played")]    public int Played { get; set; }
        [JsonPropertyName("points")]    public int Points { get; set; }
    }

    public sealed class LeagueStandings
    {
        [JsonPropertyName("leagueId")] public string LeagueId { get; set; } = "";
        [JsonPropertyName("name")]     public string Name { get; set; } = "";
        [JsonPropertyName("season")]   public string Season { get; set; } = "";
        [JsonPropertyName("table")]    public List<LeagueStandingRow> Table { get; set; } = new List<LeagueStandingRow>();
        [JsonPropertyName("source")]   public string Source { get; set; } = "";
        [JsonPropertyName("warning")]  public string Warning { get; set; } = "";
    }

    public sealed class PosterEntry
    {
        [JsonPropertyName("matchId")]     public string MatchId { get; set; } = "";
        [JsonPropertyName("url")]         public string Url { get; set; } = "";
        [JsonPropertyName("generatedAt")] public string GeneratedAt { get; set; } = "";
        [JsonPropertyName("sizeBytes")]   public long SizeBytes { get; set; }
    }

    public sealed class LogoEntry
    {
        [JsonPropertyName("teamSlug")]    public string TeamSlug { get; set; } = "";
        [JsonPropertyName("teamName")]    public string TeamName { get; set; } = "";
        [JsonPropertyName("url")]         public string Url { get; set; } = "";
        [JsonPropertyName("generatedAt")] public string GeneratedAt { get; set; } = "";
        [JsonPropertyName("sizeBytes")]   public long SizeBytes { get; set; }
    }

    public sealed class AssetsManifest
    {
        [JsonPropertyName("posters")] public List<PosterEntry> Posters { get; set; } = new List<PosterEntry>();
        [JsonPropertyName("logos")]   public List<LogoEntry> Logos { get; set; } = new List<LogoEntry>();
    }

    public sealed class LiveTeam
    {
        [JsonPropertyName("name")]      public string Name { get; set; } = "";
        [JsonPropertyName("shortName")] public string ShortName { get; set; } = "";
        [JsonPropertyName("score")]     public string Score { get; set; } = "";
        [JsonPropertyName("overs")]     public string Overs { get; set; } = "";
    }

    public sealed class LiveCricketMatch
    {
        [JsonPropertyName("matchId")]   public string MatchId { get; set; } = "";
        [JsonPropertyName("series")]    public string Series { get; set; } = "";
        [JsonPropertyName("format")]    public string Format { get; set; } = "";
        [JsonPropertyName("status")]    public string Status { get; set; } = "";
        [JsonPropertyName("state")]     public string State { get; set; } = "";  // "live" | "finished" | "scheduled" | "unknown"
        [JsonPropertyName("team1")]     public LiveTeam Team1 { get; set; } = new LiveTeam();
        [JsonPropertyName("team2")]     public LiveTeam Team2 { get; set; } = new LiveTeam();
        [JsonPropertyName("venue")]     public string Venue { get; set; } = "";
        [JsonPropertyName("startTime")] public string StartTime { get; set; } = "";
        [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";
        [JsonPropertyName("source")]    public string Source { get; set; } = "";
    }

    public sealed class LiveCricketResponse
    {
        [JsonPropertyName("matches")]          public List<LiveCricketMatch> Matches { get; set; } = new List<LiveCricketMatch>();
        [JsonPropertyName("updatedAt")]        public string UpdatedAt { get; set; } = "";
        [JsonPropertyName("source")]           public string Source { get; set; } = "";
        [JsonPropertyName("cachedAgeSeconds")] public int CachedAgeSeconds { get; set; }
    }
}
