using StreamedTv.Platform;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace StreamedTv.Services
{
    /// <summary>
    /// Owns the TV-side half of the pairing handshake + the JWT we get
    /// back. On launch the app calls <see cref="ValidateAsync"/> to
    /// decide whether to show the login screen or jump straight into
    /// the profile picker; if there's no JWT (or the existing one is
    /// rejected) the host shows <c>LoginView</c> which drives
    /// <see cref="PairInitAsync"/> + <see cref="PairPollAsync"/>.
    ///
    /// JWT is persisted to <c>{data}/auth.json</c> with the same
    /// pattern <see cref="ProfileService"/> uses for profiles. Logout
    /// just deletes the file (and the in-memory copy).
    /// </summary>
    public sealed class BackendAuthService
    {
        private const string FileName = "auth.json";
        private const string LogTag = "StreamedTv";

        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _json;
        private readonly string _baseUrl;
        private string _jwt;

        public string BaseUrl { get { return _baseUrl; } }
        public string CurrentJwt { get { return _jwt; } }
        public bool IsAuthenticated { get { return !string.IsNullOrEmpty(_jwt); } }

        public BackendAuthService(string baseUrl = null, HttpClient http = null)
        {
            _baseUrl = (string.IsNullOrEmpty(baseUrl) ? SportsDataService.DefaultBaseUrl : baseUrl).TrimEnd('/');
            _http = http ?? CreateDefaultHttpClient();
            _json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            // INTENTIONALLY NOT calling ReloadJwtFromDisk() here.
            // BackendAuthService is constructed as a field initializer
            // in Program.cs, which runs BEFORE OnCreate and therefore
            // before Tizen.Applications.Application.Current is wired
            // up. ResolvePath() would silently return null and the
            // persisted JWT would never be read — every restart looked
            // like a fresh install (user re-pairs every launch). The
            // host MUST call ReloadJwtFromDisk() from OnCreate, after
            // base.OnCreate has run. Same gotcha as UserPreferences /
            // ProfileService.
        }

        /// <summary>Re-read the persisted JWT. Called from the
        /// constructor; can be called again after Logout()/login if
        /// the host wants to reset state.</summary>
        public void ReloadJwtFromDisk()
        {
            string path = ResolvePath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                _jwt = null;
                return;
            }
            try
            {
                string contents = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(contents))
                {
                    _jwt = null;
                    return;
                }
                StoredAuth s = JsonSerializer.Deserialize<StoredAuth>(contents, _json);
                _jwt = s?.Jwt;
            }
            catch (Exception ex)
            {
                try { AppLog.Error(LogTag, "BackendAuth.Reload failed: " + ex.Message); } catch { }
                _jwt = null;
            }
        }

        /// <summary>POST /v1/auth/pair/init — TV side of the
        /// handshake. Returns the user-facing code + opaque poll
        /// token + UTC expiry.</summary>
        public async Task<PairInitResult> PairInitAsync(CancellationToken ct)
        {
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/v1/auth/pair/init");
            // Empty JSON body: some HTTP intermediaries (Google's load
            // balancer in front of Cloud Run, notably) reject bodyless
            // POSTs with 411 Length Required.
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException("pair/init failed " + (int)resp.StatusCode + ": " + Truncate(body, 200));
            }
            return JsonSerializer.Deserialize<PairInitResult>(body, _json);
        }

        /// <summary>GET /v1/auth/pair/poll?token=… — TV polls every
        /// few seconds. Returns Status="pending" until claimed, then
        /// Status="complete" + Jwt.</summary>
        public async Task<PairPollResult> PairPollAsync(string pollToken, CancellationToken ct)
        {
            string url = _baseUrl + "/v1/auth/pair/poll?token=" + Uri.EscapeDataString(pollToken ?? "");
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
            using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Pairing record gone — either expired and was cleaned
                // up, or already-claimed-and-served. Caller should
                // treat this as "re-init from scratch".
                return new PairPollResult { Status = "expired" };
            }
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException("pair/poll failed " + (int)resp.StatusCode + ": " + Truncate(body, 200));
            }
            return JsonSerializer.Deserialize<PairPollResult>(body, _json);
        }

        /// <summary>GET /v1/me — sanity-check the stored JWT against
        /// the backend. True when the JWT works (200), false on 401
        /// (revoked / expired / signing key changed). Network errors
        /// are also treated as false so the host can fall back to the
        /// login screen rather than show a stale state.</summary>
        public async Task<bool> ValidateAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_jwt)) return false;
            try
            {
                using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/v1/me");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _jwt);
                using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                try { AppLog.Info(LogTag, "BackendAuth.Validate: /v1/me returned " + (int)resp.StatusCode); } catch { }
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    // Log the body so dlog explains WHY the backend
                    // rejected the token (signing key mismatch, missing
                    // user doc, etc.) but DO NOT auto-Logout. Product
                    // contract: the local session is sticky and only
                    // ends on an EXPLICIT user "Sign out". A backend
                    // glitch (signing key rotation, transient Firestore
                    // failure) shouldn't kick the user back to the
                    // pairing flow. The auth gate's existing fallback
                    // already trusts the local JWT when validation
                    // returns false.
                    try
                    {
                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        AppLog.Warn(LogTag, "BackendAuth.Validate 401 (keeping local JWT). Body: " + Truncate(body, 200));
                    }
                    catch { }
                    return false;
                }
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                try { AppLog.Error(LogTag, "BackendAuth.Validate failed: " + ex.Message); } catch { }
                // Don't delete the JWT on transient network errors —
                // the user might just be on a flaky connection.
                return false;
            }
        }

        /// <summary>Persist the JWT we received from a successful
        /// pair claim. Subsequent launches read it from disk via the
        /// constructor. Idempotent; safe to call multiple times.</summary>
        public void StoreJwt(string jwt)
        {
            _jwt = jwt;
            string path = ResolvePath();
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(new StoredAuth { Jwt = jwt }, _json));
            }
            catch (Exception ex)
            {
                try { AppLog.Error(LogTag, "BackendAuth.StoreJwt failed: " + ex.Message); } catch { }
            }
        }

        /// <summary>Drop the local JWT. Server-side we don't track
        /// sessions (the JWT is bearer-only with a 1-year exp), so
        /// "logout" is purely a local-state operation. The host should
        /// re-show LoginView after calling this.</summary>
        public void Logout()
        {
            _jwt = null;
            string path = ResolvePath();
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                try { AppLog.Error(LogTag, "BackendAuth.Logout failed: " + ex.Message); } catch { }
            }
        }

        /// <summary>Returns an HttpClient pre-loaded with our JWT as
        /// the default Authorization header — ready for profile-CRUD
        /// calls in Phase 4. No-op when not authenticated.</summary>
        public HttpClient AuthorizedClient()
        {
            if (!string.IsNullOrEmpty(_jwt))
            {
                _http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _jwt);
            }
            return _http;
        }

        /// <summary>Returns the underlying HttpClient WITHOUT mutating
        /// any default headers. Caller is expected to attach
        /// per-request auth via <c>req.Headers.Authorization</c>.
        /// Provided so callers like <see cref="CrashReporter"/> can
        /// reuse the same TLS-validated client + cert callback as the
        /// rest of the auth flow instead of constructing a fresh
        /// HttpClient that may behave differently against Tizen's
        /// chromium-net stack.</summary>
        public HttpClient SharedHttpClient()
        {
            return _http;
        }

        private static string ResolvePath()
        {
            try
            {
                string dataDir = AppData.DataDirectory;
                if (string.IsNullOrEmpty(dataDir)) return null;
                return Path.Combine(dataDir, FileName);
            }
            catch
            {
                return null;
            }
        }

        private static HttpClient CreateDefaultHttpClient()
        {
            HttpClientHandler handler = new HttpClientHandler
            {
            };
            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
        }

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= n) return s ?? "";
            return s.Substring(0, n) + "…";
        }

        // ---- DTOs ----

        public sealed class PairInitResult
        {
            [JsonPropertyName("code")] public string Code { get; set; }
            [JsonPropertyName("pollToken")] public string PollToken { get; set; }
            [JsonPropertyName("expiresAt")] public DateTime ExpiresAt { get; set; }
        }

        public sealed class PairPollResult
        {
            [JsonPropertyName("status")] public string Status { get; set; }
            [JsonPropertyName("jwt")] public string Jwt { get; set; }
        }

        private sealed class StoredAuth
        {
            [JsonPropertyName("jwt")] public string Jwt { get; set; }
        }
    }
}
