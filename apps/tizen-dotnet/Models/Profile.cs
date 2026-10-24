using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace StreamedTv.Models
{
    /// <summary>
    /// User profile metadata. Each profile has its own favorite teams, pinned
    /// sports, and last-viewed sport via the profile-scoped UserPreferences
    /// (see <see cref="StreamedTv.Services.UserPreferences"/>).
    ///
    /// The avatar is rendered as a colored circle with the profile name's
    /// first 1-2 letters — same fallback strategy as TeamHubView's team
    /// avatar. Color is picked from a deterministic palette indexed off the
    /// profile id so it stays stable across renames and across launches.
    /// </summary>
    public sealed class Profile
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        // RGB values in [0, 1]. Persisted (rather than recomputed from Id at
        // load time) so seed-color tweaks in ProfileService don't shuffle
        // every existing profile's color the next time the user launches.
        [JsonPropertyName("avatarR")]
        public float AvatarR { get; set; } = 0.30f;

        [JsonPropertyName("avatarG")]
        public float AvatarG { get; set; } = 0.62f;

        [JsonPropertyName("avatarB")]
        public float AvatarB { get; set; } = 1.00f;

        // Optional bundled avatar image. When non-empty, the selector renders
        // the bundled file (e.g. "avatar_kohli.jpg") instead of the colored
        // initials circle. Falls back to initials when blank or when the
        // file isn't found in shared/res/.
        [JsonPropertyName("avatarImage")]
        public string AvatarImage { get; set; } = "";

        [JsonPropertyName("createdUtcMs")]
        public long CreatedUtcMs { get; set; }

        // Per-profile preferences. Mirror the backend's Profile shape so
        // a single GET /v1/profiles call returns everything the TV needs
        // to seed both the picker AND the For You row. Kept in sync by
        // ProfileService.UpdateFavorites whenever UserPreferences mutates;
        // null means "not loaded" (cache miss + backend offline).
        [JsonPropertyName("favoriteSports")]
        public List<string> FavoriteSports { get; set; }

        [JsonPropertyName("favoriteTeams")]
        public List<string> FavoriteTeams { get; set; }

        [JsonPropertyName("lastSelectedSport")]
        public string LastSelectedSport { get; set; }

        [JsonIgnore]
        public string Initials
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Name)) return "•";
                string n = Name.Trim();
                string[] parts = n.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return "•";
                if (parts.Length == 1)
                {
                    string p = parts[0];
                    return (p.Length >= 2 ? p.Substring(0, 2) : p).ToUpperInvariant();
                }
                return (parts[0][0].ToString() + parts[parts.Length - 1][0]).ToUpperInvariant();
            }
        }
    }
}
