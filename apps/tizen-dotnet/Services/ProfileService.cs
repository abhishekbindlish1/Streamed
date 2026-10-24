using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using StreamedTv.Models;

namespace StreamedTv.Services
{
    /// <summary>
    /// Owns the list of profiles and which one is currently active.
    ///
    /// Storage is cache-first:
    ///   * Local <c>{data}/profiles.json</c> is the primary read path.
    ///     Reload() loads it at startup so the picker renders instantly
    ///     and the app keeps working offline.
    ///   * Backend Firestore (<c>users/{uid}/profiles/{pid}</c>) is the
    ///     fallback when the cache is empty AND the user is signed in
    ///     (LoadFromBackendAsync). It's also the destination of every
    ///     mutation — fire-and-forget so the UI never waits on the
    ///     network.
    ///   * If both cache and backend are empty (e.g. first launch on a
    ///     brand-new account) the list legitimately stays empty. The
    ///     host is expected to detect <see cref="HasProfiles"/> == false
    ///     and route the user through the AddWizard before opening the
    ///     picker.
    ///
    /// Lifecycle:
    ///   1. Constructor sets up JSON options. <em>No</em> default profile is
    ///      created — the empty state is a real, valid state now.
    ///   2. Host calls <see cref="Reload"/> from OnCreate to hydrate the
    ///      cache (Tizen.Applications.Application.Current isn't ready at
    ///      field-init time).
    ///   3. After the JWT is validated AND the cache is empty, the host
    ///      calls <see cref="LoadFromBackendAsync"/> to bootstrap from
    ///      Firestore.
    ///   4. UI flows call <see cref="SetActive"/>, <see cref="AddProfileFull"/>,
    ///      <see cref="UpdateProfile"/>, <see cref="DeleteProfile"/> — each
    ///      mutates the list locally, persists to the cache, and (when the
    ///      backend client is wired) fires off the matching backend
    ///      request.
    ///
    /// Subscribe to <see cref="ActiveChanged"/> to re-hydrate per-profile
    /// state (UserPreferences + reload of catalog rows) when the user picks
    /// a different profile from the selector.
    /// </summary>
    public sealed class ProfileService
    {
        private const string FileName = "profiles.json";
        private const string LogTag = "StreamedTv";
        private const int MaxProfiles = 5;

        private readonly JsonSerializerOptions _json;
        private readonly List<Profile> _profiles = new List<Profile>();
        private string _activeId;

        // Optional. When wired (auth flow has produced a JWT), mutations
        // are mirrored to the backend. When null (e.g. local dev without
        // auth, or before auth completes) ProfileService still works
        // exactly as before — file-only.
        private readonly BackendProfilesClient _backend;

        public IReadOnlyList<Profile> Profiles { get { return _profiles; } }

        public Profile Active
        {
            get
            {
                if (string.IsNullOrEmpty(_activeId)) return null;
                foreach (Profile p in _profiles)
                {
                    if (string.Equals(p.Id, _activeId, StringComparison.Ordinal)) return p;
                }
                return _profiles.Count > 0 ? _profiles[0] : null;
            }
        }

        public string ActiveId { get { return _activeId; } }

        /// <summary>True iff at least one profile is loaded. False on a
        /// fresh install before <see cref="Reload"/> populates the cache,
        /// or after a successful auth handshake when neither the cache
        /// nor the backend has any profiles yet — host should route to
        /// the AddWizard in that case.</summary>
        public bool HasProfiles { get { return _profiles.Count > 0; } }

        public event EventHandler<Profile> ActiveChanged;
        public event EventHandler ProfilesChanged;

        public ProfileService() : this(null) { }

        /// <summary>Construct with backend sync. Pass the shared
        /// <see cref="BackendAuthService"/> so mutations can attach the
        /// current JWT. Pass null (or use the parameterless ctor) for
        /// local-only mode.</summary>
        public ProfileService(BackendAuthService auth)
        {
            _json = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = false
            };
            if (auth != null)
            {
                _backend = new BackendProfilesClient(auth);
            }
            // No fallback profile here. The empty state is a real state
            // — see HasProfiles. Reload() may populate from the local
            // cache; LoadFromBackendAsync() may populate from Firestore;
            // if both miss, the host opens the AddWizard.
        }

        /// <summary>Re-read profiles from the on-device cache. Call from
        /// Program.OnCreate after base.OnCreate so the Tizen app context is
        /// available. On a missing/corrupt cache the in-memory list is left
        /// empty — the host should call <see cref="LoadFromBackendAsync"/>
        /// next, then route to the AddWizard if that also returns empty.
        /// Returns true iff at least one profile was loaded.</summary>
        public bool Reload()
        {
            _profiles.Clear();
            _activeId = null;
            string path = ResolvePath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            try
            {
                string contents = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(contents)) return false;
                ProfilesPayload payload = JsonSerializer.Deserialize<ProfilesPayload>(contents, _json);
                if (payload == null || payload.Profiles == null) return false;
                foreach (Profile p in payload.Profiles)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.Id)) continue;
                    _profiles.Add(p);
                }
                if (_profiles.Count == 0) return false;
                _activeId = !string.IsNullOrWhiteSpace(payload.ActiveId)
                    ? payload.ActiveId
                    : _profiles[0].Id;
                // Validate active id still exists in the list (handles a
                // hand-edited file that dropped the active profile).
                if (Active == null) _activeId = _profiles[0].Id;
                return true;
            }
            catch
            {
                _profiles.Clear();
                _activeId = null;
                return false;
            }
        }

        /// <summary>Bootstrap the profile list from Firestore. Intended to
        /// be called by the host AFTER <see cref="Reload"/> returned false
        /// (cache miss). Replaces the in-memory + cached state with the
        /// backend response.
        ///
        /// Returns true on transport success — INCLUDING when the backend
        /// returns zero profiles (that's a legitimate "first launch on
        /// this account" state, not an error). The caller should check
        /// <see cref="HasProfiles"/> separately to decide whether to open
        /// the picker or the AddWizard.
        ///
        /// Returns false only on transport error so the caller can leave
        /// the previous (cached) state alone.</summary>
        public async Task<bool> LoadFromBackendAsync(CancellationToken ct)
        {
            if (_backend == null || !_backend.IsAuthenticated) return false;
            try
            {
                // ConfigureAwait(true) is LOAD-BEARING here. The
                // post-await code below fires ProfilesChanged, whose
                // subscriber in Program.cs touches NUI views
                // (_profileChip.SetActiveProfile, _profileSelector.SetProfiles).
                // NUI is single-threaded — touching views from any
                // other thread silently wedges the renderer + input
                // pipeline (sports view stays up, picker never paints,
                // remote stops responding). Capturing the calling sync
                // context (the NUI main loop) and resuming on it is
                // what keeps the event firing on the right thread.
                BackendProfilesClient.ListResponse resp = await _backend.ListAsync(ct).ConfigureAwait(true);
                if (resp == null) return false;

                _profiles.Clear();
                _activeId = null;
                if (resp.Profiles != null)
                {
                    int idx = 0;
                    foreach (Profile p in resp.Profiles)
                    {
                        if (p == null || string.IsNullOrWhiteSpace(p.Id)) continue;
                        // Backend doesn't track avatar palette colors —
                        // it just stores whatever floats we sent. If they
                        // come back zeroed (older profiles created before
                        // we sent the palette), reassign deterministically
                        // so the UI doesn't render a black tile.
                        if (p.AvatarR == 0f && p.AvatarG == 0f && p.AvatarB == 0f)
                        {
                            AssignAvatarColor(p, idx);
                        }
                        _profiles.Add(p);
                        idx++;
                    }
                }
                if (_profiles.Count > 0)
                {
                    _activeId = !string.IsNullOrWhiteSpace(resp.ActiveProfileId)
                        ? resp.ActiveProfileId
                        : _profiles[0].Id;
                    if (Active == null) _activeId = _profiles[0].Id;
                }

                Save();
                ProfilesChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                try { Tizen.Log.Warn(LogTag, "ProfileService.LoadFromBackendAsync failed: " + ex.Message); } catch { }
                return false;
            }
        }

        /// <summary>Make this profile the active one. Fires ActiveChanged
        /// even when called with the already-active id so subscribers can
        /// force a fresh hydrate path (useful after rename, etc.).</summary>
        public void SetActive(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return;
            bool exists = false;
            foreach (Profile p in _profiles)
            {
                if (string.Equals(p.Id, profileId, StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }
            if (!exists) return;
            _activeId = profileId;
            Save();
            ActiveChanged?.Invoke(this, Active);
            FireAndForgetSetActive(profileId);
        }

        public Profile AddProfile(string name)
        {
            if (_profiles.Count >= MaxProfiles) return null;
            string trimmed = (name ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed)) trimmed = "You";
            Profile p = new Profile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = trimmed,
                CreatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            AssignAvatarColor(p, _profiles.Count);
            // Default new profiles to the first bundled avatar image so users
            // see the image-avatar feature without having to discover a
            // separate "pick avatar" flow. They can switch to initials by
            // setting AvatarImage to "" via SetProfileAvatarImage.
            if (_bundled.Length > 0)
            {
                p.AvatarImage = _bundled[0].FileName;
            }
            _profiles.Add(p);
            Save();
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
            FireAndForgetCreate(p);
            return p;
        }

        public void RenameProfile(string profileId, string newName)
        {
            string trimmed = (newName ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed)) return;
            foreach (Profile p in _profiles)
            {
                if (string.Equals(p.Id, profileId, StringComparison.Ordinal))
                {
                    p.Name = trimmed;
                    Save();
                    ProfilesChanged?.Invoke(this, EventArgs.Empty);
                    FireAndForgetUpdate(profileId, new { name = trimmed });
                    return;
                }
            }
        }

        public void DeleteProfile(string profileId)
        {
            if (_profiles.Count <= 1) return;  // never let the list go empty
            for (int i = 0; i < _profiles.Count; i++)
            {
                if (string.Equals(_profiles[i].Id, profileId, StringComparison.Ordinal))
                {
                    _profiles.RemoveAt(i);
                    if (string.Equals(_activeId, profileId, StringComparison.Ordinal))
                    {
                        _activeId = _profiles[0].Id;
                        ActiveChanged?.Invoke(this, Active);
                        FireAndForgetSetActive(_activeId);
                    }
                    Save();
                    ProfilesChanged?.Invoke(this, EventArgs.Empty);
                    FireAndForgetDelete(profileId);
                    return;
                }
            }
        }

        /// <summary>Profile-scoped data dir, e.g. {data}/profiles/{id}/.
        /// Used by UserPreferences to compute its favorites file path.</summary>
        public static string DataDirForProfile(string profileId)
        {
            try
            {
                string dataDir = Tizen.Applications.Application.Current?.DirectoryInfo?.Data;
                if (string.IsNullOrEmpty(dataDir) || string.IsNullOrEmpty(profileId)) return null;
                string profileDir = Path.Combine(dataDir, "profiles", profileId);
                if (!Directory.Exists(profileDir)) Directory.CreateDirectory(profileDir);
                return profileDir;
            }
            catch
            {
                return null;
            }
        }

        public bool CanAddProfile { get { return _profiles.Count < MaxProfiles; } }

        // ---- Bundled avatar catalog ----------------------------------------
        // Kept tiny so we can ship a few hand-picked images in shared/res/ and
        // grow the list without touching consumers. ProfileSelectorView reads
        // this list to render an avatar-picker chooser; AddProfile picks the
        // first entry by default.

        public sealed class BundledAvatar
        {
            public string FileName { get; }   // e.g. "profile_image_1.jpg"
            public string Label { get; }      // human-readable, e.g. "Profile 1"
            public BundledAvatar(string fileName, string label)
            {
                FileName = fileName;
                Label = label;
            }
        }

        private static readonly BundledAvatar[] _bundled = new[]
        {
            new BundledAvatar("profile_image_1.jpg", "Profile 1"),
            new BundledAvatar("profile_image_2.jpg", "Profile 2"),
            new BundledAvatar("profile_image_3.jpg", "Profile 3"),
            new BundledAvatar("profile_image_4.jpg", "Profile 4"),
            new BundledAvatar("profile_image_5.jpg", "Profile 5"),
        };

        public static IReadOnlyList<BundledAvatar> BundledAvatars { get { return _bundled; } }

        /// <summary>Set the bundled-avatar image for a profile. Pass null/empty
        /// to revert to the colored-initials avatar.</summary>
        public void SetProfileAvatarImage(string profileId, string fileName)
        {
            foreach (Profile p in _profiles)
            {
                if (string.Equals(p.Id, profileId, StringComparison.Ordinal))
                {
                    p.AvatarImage = fileName ?? "";
                    Save();
                    ProfilesChanged?.Invoke(this, EventArgs.Empty);
                    FireAndForgetUpdate(profileId, new { avatarImage = p.AvatarImage });
                    return;
                }
            }
        }

        /// <summary>Atomic add: create a new profile with both name and avatar
        /// set in one shot, persist, and fire ProfilesChanged exactly once.
        /// Returns the new Profile, or null if MaxProfiles is reached. Caller
        /// is responsible for calling SetActive(returned.Id) afterwards if it
        /// wants the new profile to become the active one.</summary>
        public Profile AddProfileFull(string name, string avatarFileName)
        {
            if (_profiles.Count >= MaxProfiles) return null;
            string trimmed = (name ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed)) trimmed = "You";
            Profile p = new Profile
            {
                // Pre-generate the id locally so the backend honors it
                // (BackendProfilesClient.CreateAsync passes it through;
                // backend uses tx.Create so there's no silent overwrite).
                // This means our local cache + the Firestore doc share
                // an id, so future PATCHes / DELETEs by id work without
                // a "renamed by server" reconciliation step.
                Id = Guid.NewGuid().ToString("N"),
                Name = trimmed,
                AvatarImage = avatarFileName ?? "",
                CreatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            AssignAvatarColor(p, _profiles.Count);
            _profiles.Add(p);
            Save();
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
            FireAndForgetCreate(p);
            return p;
        }

        /// <summary>Atomic edit: rename + change avatar in one Save() and one
        /// ProfilesChanged event. Used by the editor's Done path so the UI
        /// doesn't see a half-applied state. No-op if profileId doesn't
        /// resolve, or if newName trims to empty.</summary>
        public void UpdateProfile(string profileId, string newName, string newAvatarFileName)
        {
            string trimmed = (newName ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed)) return;
            foreach (Profile p in _profiles)
            {
                if (string.Equals(p.Id, profileId, StringComparison.Ordinal))
                {
                    p.Name = trimmed;
                    p.AvatarImage = newAvatarFileName ?? "";
                    Save();
                    ProfilesChanged?.Invoke(this, EventArgs.Empty);
                    FireAndForgetUpdate(profileId, new
                    {
                        name = trimmed,
                        avatarImage = p.AvatarImage,
                    });
                    return;
                }
            }
        }

        // ---- Backend sync helpers --------------------------------------------
        // All four are fire-and-forget: the local change has already
        // been applied + persisted by the time we get here. Logging the
        // failure is the most we do — there's no UI surface for "your
        // profile didn't sync to the cloud", and a retry queue would be
        // overkill for a 5-doc collection that re-syncs on next launch
        // via LoadFromBackendAsync.

        private void FireAndForgetCreate(Profile p)
        {
            if (_backend == null || !_backend.IsAuthenticated || p == null) return;
            _ = SafeRun("create", async () =>
            {
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _backend.CreateAsync(p, cts.Token).ConfigureAwait(false);
            });
        }

        private void FireAndForgetUpdate(string profileId, object patch)
        {
            if (_backend == null || !_backend.IsAuthenticated || string.IsNullOrEmpty(profileId)) return;
            _ = SafeRun("update", async () =>
            {
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _backend.UpdateAsync(profileId, patch, cts.Token).ConfigureAwait(false);
            });
        }

        private void FireAndForgetDelete(string profileId)
        {
            if (_backend == null || !_backend.IsAuthenticated || string.IsNullOrEmpty(profileId)) return;
            _ = SafeRun("delete", async () =>
            {
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _backend.DeleteAsync(profileId, cts.Token).ConfigureAwait(false);
            });
        }

        private void FireAndForgetSetActive(string profileId)
        {
            if (_backend == null || !_backend.IsAuthenticated) return;
            _ = SafeRun("set-active", async () =>
            {
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _backend.SetActiveAsync(profileId ?? "", cts.Token).ConfigureAwait(false);
            });
        }

        private static async Task SafeRun(string op, Func<Task> body)
        {
            try { await body().ConfigureAwait(false); }
            catch (Exception ex)
            {
                try { Tizen.Log.Warn(LogTag, "ProfileService backend " + op + " failed: " + ex.Message); } catch { }
            }
        }

        // ---- Local cache I/O --------------------------------------------------

        private void Save()
        {
            string path = ResolvePath();
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                ProfilesPayload payload = new ProfilesPayload
                {
                    ActiveId = _activeId,
                    Profiles = _profiles.ToList(),
                };
                File.WriteAllText(path, JsonSerializer.Serialize(payload, _json));
            }
            catch (Exception ex)
            {
                try { Tizen.Log.Error(LogTag, "ProfileService.Save failed: " + ex.Message); } catch { }
            }
        }

        /// <summary>Wipe the in-memory list, the active id, AND the
        /// local cache file. Called by the host right before completing
        /// a fresh login: the previous cache is almost certainly from a
        /// different account (or from a pre-upgrade auto-fallback "You"
        /// profile) and would otherwise leak into the new session via
        /// the cache-first read path.
        ///
        /// Does NOT touch backend state — the backend's profile list is
        /// the source of truth post-login. Does NOT fire ProfilesChanged
        /// to avoid jolting any in-flight UI; the immediate next call
        /// (LoadFromBackendAsync) will fire it once it has fresh data.</summary>
        public void Clear()
        {
            _profiles.Clear();
            _activeId = null;
            string path = ResolvePath();
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex)
            {
                try { Tizen.Log.Error(LogTag, "ProfileService.Clear failed: " + ex.Message); } catch { }
            }
        }

        /// <summary>Mirror per-profile favourites onto the active
        /// Profile object so they get persisted to the local cache and
        /// synced to the backend in a single call. Called by
        /// UserPreferences after every favourites mutation; takes the
        /// already-mutated lists rather than diffs to keep the bridge
        /// stupidly simple. Pass null lists to leave a field unchanged
        /// (rare — typical caller passes all three every time).</summary>
        public void UpdateFavorites(string profileId, IReadOnlyList<string> favoriteSports, IReadOnlyCollection<string> favoriteTeams, string lastSelectedSport)
        {
            if (string.IsNullOrEmpty(profileId)) return;
            foreach (Profile p in _profiles)
            {
                if (!string.Equals(p.Id, profileId, StringComparison.Ordinal)) continue;
                if (favoriteSports != null) p.FavoriteSports = favoriteSports.ToList();
                if (favoriteTeams != null) p.FavoriteTeams = favoriteTeams.ToList();
                if (lastSelectedSport != null) p.LastSelectedSport = lastSelectedSport;
                Save();
                // Don't fire ProfilesChanged — the visible profile
                // metadata (name/avatar) didn't change. UserPreferences
                // already raises its own event for favourite-list
                // consumers (For You row, etc.).
                FireAndForgetUpdate(profileId, new
                {
                    favoriteSports = p.FavoriteSports ?? new List<string>(),
                    favoriteTeams = p.FavoriteTeams ?? new List<string>(),
                    lastSelectedSport = p.LastSelectedSport ?? "",
                });
                return;
            }
        }

        // Deterministic palette so a profile's color stays stable. Index
        // lookup means the Nth profile gets the Nth color; user can rename
        // freely without the avatar reshuffling.
        private static readonly (float R, float G, float B)[] AvatarPalette = new (float, float, float)[]
        {
            (0.96f, 0.32f, 0.32f),  // coral red
            (0.31f, 0.62f, 1.00f),  // electric blue
            (0.18f, 0.78f, 0.55f),  // emerald
            (0.95f, 0.55f, 0.10f),  // amber
            (0.62f, 0.40f, 0.92f),  // violet
        };

        private static void AssignAvatarColor(Profile p, int idx)
        {
            var c = AvatarPalette[Math.Abs(idx) % AvatarPalette.Length];
            p.AvatarR = c.R;
            p.AvatarG = c.G;
            p.AvatarB = c.B;
        }

        private static string ResolvePath()
        {
            try
            {
                string dataDir = Tizen.Applications.Application.Current?.DirectoryInfo?.Data;
                if (string.IsNullOrEmpty(dataDir)) return null;
                return Path.Combine(dataDir, FileName);
            }
            catch
            {
                return null;
            }
        }

        private sealed class ProfilesPayload
        {
            [JsonPropertyName("activeId")]
            public string ActiveId { get; set; }

            [JsonPropertyName("profiles")]
            public List<Profile> Profiles { get; set; }
        }
    }
}
