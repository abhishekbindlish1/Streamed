using StreamedTv.Models;

namespace StreamedTv.Services
{
    /// <summary>
    /// Process-wide pointer to the active <see cref="LiveScoreService"/>. Set once at app
    /// startup by Program.cs; consulted synchronously by views (e.g. <c>TileView</c>) when
    /// building pill text. Exists so views don't need to thread a service reference through
    /// every constructor — same pattern as <see cref="BackendAssets"/>.
    ///
    /// Returns false from every helper when no service has been registered, so views can be
    /// built and rendered safely even before the polling service spins up.
    /// </summary>
    public static class LiveScoreRegistry
    {
        private static LiveScoreService _instance;

        public static void Set(LiveScoreService instance) { _instance = instance; }
        public static LiveScoreService Current { get { return _instance; } }

        public static bool TryGetForMatch(CatalogMatch match, out LiveCricketMatch live)
        {
            LiveScoreService svc = _instance;
            if (svc == null) { live = null; return false; }
            return svc.TryGetForMatch(match, out live);
        }
    }
}
