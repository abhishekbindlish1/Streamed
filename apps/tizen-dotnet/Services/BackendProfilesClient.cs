using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using StreamedTv.Models;

namespace StreamedTv.Services
{
    /// <summary>
    /// HTTP client for /v1/profiles/* on the backend. Stateless:
    /// every call fetches a fresh JWT from <see cref="BackendAuthService"/>
    /// and attaches it as a Bearer header. Returns plain DTOs the
    /// caller (ProfileService) maps onto its in-memory model.
    /// </summary>
    public sealed class BackendProfilesClient
    {
        private readonly BackendAuthService _auth;
        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _json;

        public BackendProfilesClient(BackendAuthService auth, HttpClient http = null)
        {
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _http = http ?? CreateDefaultHttpClient();
            _json = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                // Note: DefaultIgnoreCondition is .NET 5+; Tizen .NET
                // targets an older runtime. We work around omitted-vs-
                // present null distinctions on the wire by using
                // anonymous-object patches on the caller side instead
                // of full Profile serialization for partial updates.
            };
        }

        /// <summary>True iff there's a stored JWT — backend-aware
        /// callers can short-circuit the network round-trip when
        /// the user isn't logged in.</summary>
        public bool IsAuthenticated { get { return _auth.IsAuthenticated; } }

        // ---- DTOs ----

        public sealed class ListResponse
        {
            [JsonPropertyName("profiles")] public List<Profile> Profiles { get; set; } = new List<Profile>();
            [JsonPropertyName("activeProfileId")] public string ActiveProfileId { get; set; } = "";
        }

        // ---- Operations ----

        public async Task<ListResponse> ListAsync(CancellationToken ct)
        {
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, _auth.BaseUrl + "/v1/profiles");
            AttachAuth(req);
            using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException("profiles list failed " + (int)resp.StatusCode + ": " + Truncate(body));
            }
            return JsonSerializer.Deserialize<ListResponse>(body, _json) ?? new ListResponse();
        }

        public async Task<Profile> CreateAsync(Profile p, CancellationToken ct)
        {
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, _auth.BaseUrl + "/v1/profiles");
            AttachAuth(req);
            req.Content = SerializeJson(p);
            using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException("profiles create failed " + (int)resp.StatusCode + ": " + Truncate(body));
            }
            return JsonSerializer.Deserialize<Profile>(body, _json);
        }

        /// <summary>PATCH a partial set of fields. Pass an anonymous
        /// object: <c>new { name = "New Name" }</c>. Server ignores
        /// fields it doesn't recognize and rejects id / createdUtcMs.</summary>
        public async Task<Profile> UpdateAsync(string profileId, object patch, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(profileId)) throw new ArgumentException("profileId required");
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Patch, _auth.BaseUrl + "/v1/profiles/" + Uri.EscapeDataString(profileId));
            AttachAuth(req);
            req.Content = SerializeJson(patch);
            using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException("profiles update failed " + (int)resp.StatusCode + ": " + Truncate(body));
            }
            return JsonSerializer.Deserialize<Profile>(body, _json);
        }

        public async Task DeleteAsync(string profileId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(profileId)) throw new ArgumentException("profileId required");
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Delete, _auth.BaseUrl + "/v1/profiles/" + Uri.EscapeDataString(profileId));
            AttachAuth(req);
            using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new HttpRequestException("profiles delete failed " + (int)resp.StatusCode + ": " + Truncate(body));
            }
        }

        public async Task SetActiveAsync(string profileId, CancellationToken ct)
        {
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, _auth.BaseUrl + "/v1/profiles/active");
            AttachAuth(req);
            req.Content = SerializeJson(new { profileId = profileId ?? "" });
            using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new HttpRequestException("profiles set active failed " + (int)resp.StatusCode + ": " + Truncate(body));
            }
        }

        // ---- Helpers ----

        private void AttachAuth(HttpRequestMessage req)
        {
            string jwt = _auth.CurrentJwt;
            if (!string.IsNullOrEmpty(jwt))
            {
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
            }
        }

        private StringContent SerializeJson(object o)
        {
            string body = JsonSerializer.Serialize(o, _json);
            return new StringContent(body, Encoding.UTF8, "application/json");
        }

        private static HttpClient CreateDefaultHttpClient()
        {
            HttpClientHandler handler = new HttpClientHandler
            {
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        }

        private static string Truncate(string s)
        {
            const int n = 200;
            if (string.IsNullOrEmpty(s) || s.Length <= n) return s ?? "";
            return s.Substring(0, n) + "…";
        }
    }
}
