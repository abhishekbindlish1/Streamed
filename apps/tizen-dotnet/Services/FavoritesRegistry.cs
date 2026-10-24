using System.Linq;
using StreamedTv.Models;

namespace StreamedTv.Services
{
    /// <summary>
    /// Process-wide pointer to the active <see cref="UserPreferences"/> for view-side
    /// favorite-team lookup. Same registry pattern as <see cref="BackendAssets"/> and
    /// <see cref="LiveScoreRegistry"/>: views need synchronous "is this team favorited?"
    /// access without threading a service reference through every constructor.
    ///
    /// All lookups fail silently (return false) until <see cref="Set"/> has been called
    /// at app startup, so views render correctly during initialization.
    /// </summary>
    public static class FavoritesRegistry
    {
        private static UserPreferences _prefs;

        public static void Set(UserPreferences prefs) { _prefs = prefs; }
        public static UserPreferences Current { get { return _prefs; } }

        /// <summary>True if either the home or away team in this match is favorited.</summary>
        public static bool IsAnyTeamFavorited(CatalogMatch match)
        {
            UserPreferences p = _prefs;
            if (p == null || match == null) return false;
            string home = match.Teams?.Home?.Name;
            string away = match.Teams?.Away?.Name;
            return p.IsFavoriteTeam(home) || p.IsFavoriteTeam(away);
        }

        /// <summary>Return the names of all teams featured in this match that are favorited.
        /// Used by the F-key toggle handler to compose user-visible status messages.</summary>
        public static string[] FavoritedTeamsIn(CatalogMatch match)
        {
            UserPreferences p = _prefs;
            if (p == null || match == null) return new string[0];
            return new[] { match.Teams?.Home?.Name, match.Teams?.Away?.Name }
                .Where(n => p.IsFavoriteTeam(n))
                .ToArray();
        }
    }
}
