using StreamedTv.Platform;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamedTv.Models;

namespace StreamedTv.Services
{
    /// <summary>
    /// Per-profile preferences (favourite sports, favourite teams,
    /// last-viewed sport).
    ///
    /// Storage is cache-first, mirroring <see cref="ProfileService"/>:
    ///   * Local <c>{data}/profiles/{id}/favorites.json</c> is the
    ///     primary read path.
    ///   * If the local file is missing, the host can call
    ///     <see cref="SeedFromProfile"/> to bootstrap from the
    ///     already-fetched <see cref="Profile"/> (favourites are bundled
    ///     onto the profile doc on the backend).
    ///   * If both are empty, the user genuinely has no favourites — the
    ///     For You row will be empty until they pin some. We do NOT
    ///     auto-pin anything; the previous "default cricket/football/
    ///     basketball" behaviour silently created favourites the user
    ///     never asked for, which then sync'd to Firestore the moment
    ///     they touched any favourite control.
    ///
    /// Mutations (PinFavorite, ToggleFavoriteTeam, SetLastSelectedSport)
    /// write the local cache AND, when a <see cref="ProfileService"/>
    /// reference was passed in, call its <c>UpdateFavorites</c> so the
    /// backend profile doc gets PATCH'd. ProfileService.UpdateFavorites is
    /// itself fire-and-forget, so the UI never blocks on the network.
    /// </summary>
    public sealed class UserPreferences
    {
        private const int CurrentVersion = 1;
        private const int FavoriteSlots = 3;
        private const int FavoriteTeamsLimit = 20;
        private const string FileName = "favorites.json";
        private const string LogTag = "StreamedTv";

        private readonly JsonSerializerOptions _json;
        private readonly List<string> _favoriteSports;
        // Favorite teams stored as Slugify(teamName) — normalized so "Mumbai Indians" /
        // "MUMBAI INDIANS" / "mumbai-indians" all collide. Slug is identical to what
        // the backend uses for team logos so a single normalization rule serves both.
        private readonly HashSet<string> _favoriteTeams;
        private string _lastSelectedSport;

        // Optional. When wired, every mutation also flows to the backend
        // via ProfileService.UpdateFavorites (which is fire-and-forget).
        private readonly ProfileService _profiles;

        public UserPreferences() : this(null) { }

        public UserPreferences(ProfileService profiles)
        {
            _json = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = false
            };

            _favoriteSports = new List<string>();
            _favoriteTeams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _lastSelectedSport = "";
            _activeProfileId = null;
            _profiles = profiles;

            // INTENTIONALLY NOT calling Load() here. UserPreferences is constructed
            // as a field initializer in Program.cs, which runs BEFORE OnCreate and
            // therefore before Tizen.Applications.Application.Current is wired up.
            // ResolvePath() would silently return null and the persisted favorites
            // file would never be read. The host MUST call Reload(profileId) once
            // OnCreate begins.
        }

        // Tracks which profile's preferences are currently loaded, so Save()
        // writes back to the right per-profile file. Set by Reload(profileId).
        private string _activeProfileId;

        /// <summary>Re-read persisted preferences for a specific profile.
        /// Returns true iff a cached file was found and decoded; false on
        /// cache miss (caller can then optionally call
        /// <see cref="SeedFromProfile"/> to bootstrap from the backend
        /// profile doc).</summary>
        public bool Reload(string profileId)
        {
            _activeProfileId = profileId;
            // Reset to truly empty — no auto-defaults. If the cache is
            // missing AND the backend profile has no favourites, the user
            // legitimately has none.
            _favoriteSports.Clear();
            _favoriteTeams.Clear();
            _lastSelectedSport = "";
            return Load();
        }

        /// <summary>Back-compat shim — defaults to the legacy "no profile"
        /// path. New callers should pass the active profile id.</summary>
        public bool Reload()
        {
            return Reload(null);
        }

        /// <summary>Bootstrap favourites from a backend-fetched
        /// <see cref="Profile"/> when the local cache is empty. Persists
        /// the seeded values back to the cache so subsequent launches
        /// don't re-bootstrap. No-op if profile is null or its favourite
        /// fields are all empty/null. Does NOT round-trip back to the
        /// backend (the data already came from there).</summary>
        public void SeedFromProfile(Profile profile)
        {
            if (profile == null) return;
            bool dirty = false;

            if (profile.FavoriteSports != null && profile.FavoriteSports.Count > 0 && _favoriteSports.Count == 0)
            {
                foreach (string s in profile.FavoriteSports)
                {
                    if (!string.IsNullOrWhiteSpace(s)) _favoriteSports.Add(s);
                }
                dirty = true;
            }
            if (profile.FavoriteTeams != null && profile.FavoriteTeams.Count > 0 && _favoriteTeams.Count == 0)
            {
                foreach (string t in profile.FavoriteTeams)
                {
                    if (!string.IsNullOrWhiteSpace(t)) _favoriteTeams.Add(t);
                }
                dirty = true;
            }
            if (string.IsNullOrEmpty(_lastSelectedSport) && !string.IsNullOrWhiteSpace(profile.LastSelectedSport))
            {
                _lastSelectedSport = profile.LastSelectedSport;
                dirty = true;
            }

            if (dirty) PersistLocal();
        }

        public IReadOnlyList<string> FavoriteSports
        {
            get { return _favoriteSports; }
        }

        public string LastSelectedSport
        {
            get { return _lastSelectedSport; }
        }

        public void SetLastSelectedSport(string sportId)
        {
            if (string.IsNullOrWhiteSpace(sportId))
            {
                return;
            }

            _lastSelectedSport = sportId;
            Save();
        }

        public void PinFavorite(string sportId)
        {
            if (string.IsNullOrWhiteSpace(sportId))
            {
                return;
            }

            int existingIndex = -1;
            for (int i = 0; i < _favoriteSports.Count; i++)
            {
                if (string.Equals(_favoriteSports[i], sportId, StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = i;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                _favoriteSports.RemoveAt(existingIndex);
                _favoriteSports.Add(sportId);
            }
            else
            {
                _favoriteSports.Add(sportId);
                while (_favoriteSports.Count > FavoriteSlots)
                {
                    _favoriteSports.RemoveAt(0);
                }
            }

            Save();
        }

        /// <summary>Add to favorites only if there's an open slot (less than FavoriteSlots pinned).
        /// Returns true on success, false if at the cap (caller should show "remove one first").
        /// If the sport is already pinned, returns true (no-op, considered success).</summary>
        public bool TryPinFavorite(string sportId)
        {
            if (string.IsNullOrWhiteSpace(sportId)) return false;
            if (IsFavorite(sportId)) return true;
            int filled = 0;
            foreach (string s in _favoriteSports)
            {
                if (!string.IsNullOrEmpty(s)) filled++;
            }
            if (filled >= FavoriteSlots) return false;
            _favoriteSports.Add(sportId);
            while (_favoriteSports.Count > FavoriteSlots)
            {
                _favoriteSports.RemoveAt(0);
            }
            Save();
            return true;
        }

        /// <summary>Remove a sport from the favorites list if present.</summary>
        public void UnpinFavorite(string sportId)
        {
            if (string.IsNullOrWhiteSpace(sportId)) return;
            for (int i = _favoriteSports.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_favoriteSports[i], sportId, StringComparison.OrdinalIgnoreCase))
                {
                    _favoriteSports.RemoveAt(i);
                }
            }
            Save();
        }

        public bool IsFavorite(string sportId)
        {
            if (string.IsNullOrWhiteSpace(sportId)) return false;
            foreach (string s in _favoriteSports)
            {
                if (string.Equals(s, sportId, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ---- Favorite teams -----------------------------------------------------

        public IReadOnlyCollection<string> FavoriteTeams { get { return _favoriteTeams; } }

        /// <summary>True if this team name (slugified) has been favorited.</summary>
        public bool IsFavoriteTeam(string teamName)
        {
            string slug = SportsDataService.Slugify(teamName);
            if (string.IsNullOrEmpty(slug)) return false;
            return _favoriteTeams.Contains(slug);
        }

        /// <summary>Toggle the favorite state of a team. Returns the new state
        /// (true = now favorited, false = no longer favorited). Caps total favorites
        /// at <see cref="FavoriteTeamsLimit"/>; oldest entries get evicted FIFO.</summary>
        public bool ToggleFavoriteTeam(string teamName)
        {
            string slug = SportsDataService.Slugify(teamName);
            if (string.IsNullOrEmpty(slug)) return false;
            bool nowFavorited;
            if (_favoriteTeams.Contains(slug))
            {
                _favoriteTeams.Remove(slug);
                nowFavorited = false;
            }
            else
            {
                _favoriteTeams.Add(slug);
                nowFavorited = true;
                // Cap with rough FIFO. HashSet doesn't preserve order, so on overflow we
                // just drop arbitrary entries — acceptable trade-off for the caller's
                // simplicity. If precise ordering matters, swap to a List<>.
                while (_favoriteTeams.Count > FavoriteTeamsLimit)
                {
                    string victim = null;
                    foreach (string s in _favoriteTeams) { victim = s; break; }
                    if (victim != null) _favoriteTeams.Remove(victim);
                }
            }
            Save();
            return nowFavorited;
        }

        // ---- Persistence ---------------------------------------------------------

        // Save is the public mutation hook: persist locally + sync to backend
        // via ProfileService (fire-and-forget). Splitting it lets SeedFromProfile
        // call PersistLocal directly without re-syncing data we just read from
        // the backend.
        private void Save()
        {
            PersistLocal();
            if (_profiles != null && !string.IsNullOrEmpty(_activeProfileId))
            {
                _profiles.UpdateFavorites(
                    _activeProfileId,
                    _favoriteSports,
                    _favoriteTeams,
                    _lastSelectedSport);
            }
        }

        private bool Load()
        {
            string path = ResolvePath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

            try
            {
                string contents = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(contents)) return false;

                PreferencesPayload payload = JsonSerializer.Deserialize<PreferencesPayload>(contents, _json);
                if (payload == null) return false;

                if (payload.FavoriteSports != null)
                {
                    foreach (string s in payload.FavoriteSports)
                    {
                        if (!string.IsNullOrWhiteSpace(s)) _favoriteSports.Add(s);
                    }
                    while (_favoriteSports.Count > FavoriteSlots) _favoriteSports.RemoveAt(0);
                }

                if (payload.FavoriteTeams != null)
                {
                    foreach (string t in payload.FavoriteTeams)
                    {
                        if (!string.IsNullOrWhiteSpace(t)) _favoriteTeams.Add(t);
                    }
                }

                if (!string.IsNullOrWhiteSpace(payload.LastSelectedSport))
                {
                    _lastSelectedSport = payload.LastSelectedSport;
                }
                return true;
            }
            catch
            {
                _favoriteSports.Clear();
                _favoriteTeams.Clear();
                _lastSelectedSport = "";
                return false;
            }
        }

        private void PersistLocal()
        {
            string path = ResolvePath();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                PreferencesPayload payload = new PreferencesPayload
                {
                    FavoriteSports = _favoriteSports.ToList(),
                    FavoriteTeams = _favoriteTeams.ToList(),
                    LastSelectedSport = _lastSelectedSport,
                    Version = CurrentVersion
                };

                string serialized = JsonSerializer.Serialize(payload, _json);
                File.WriteAllText(path, serialized);
            }
            catch (Exception ex)
            {
                try
                {
                    AppLog.Error(LogTag, "Failed to write user preferences: " + ex.Message);
                }
                catch
                {
                    // Logging failures are not fatal.
                }
            }
        }

        private string ResolvePath()
        {
            try
            {
                // Per-profile favorites file: {data}/profiles/{id}/favorites.json
                // Falls back to legacy {data}/favorites.json when no profile id is
                // set, so existing single-user installs keep their preferences
                // across the upgrade.
                if (!string.IsNullOrEmpty(_activeProfileId))
                {
                    string profileDir = ProfileService.DataDirForProfile(_activeProfileId);
                    if (!string.IsNullOrEmpty(profileDir))
                    {
                        return Path.Combine(profileDir, FileName);
                    }
                }
                string dataDir = AppData.DataDirectory;
                if (string.IsNullOrEmpty(dataDir))
                {
                    return null;
                }

                return Path.Combine(dataDir, FileName);
            }
            catch
            {
                return null;
            }
        }

        private sealed class PreferencesPayload
        {
            [JsonPropertyName("favoriteSports")]
            public List<string> FavoriteSports { get; set; }

            [JsonPropertyName("favoriteTeams")]
            public List<string> FavoriteTeams { get; set; }

            [JsonPropertyName("lastSelectedSport")]
            public string LastSelectedSport { get; set; }

            [JsonPropertyName("version")]
            public int Version { get; set; }
        }
    }
}
