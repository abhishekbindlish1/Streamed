using Tizen.NUI;

namespace StreamedTv.Theme
{
    public static class Tokens
    {
        // Backgrounds
        public static readonly Color BgBase   = new Color(0.024f, 0.024f, 0.031f, 1f); // #060608
        public static readonly Color BgTop    = new Color(0.047f, 0.047f, 0.055f, 1f); // #0c0c0e

        // Glass surface (semi-transparent dark, plus inner highlight applied separately)
        public static readonly Color SurfaceGlass     = new Color(20/255f, 20/255f, 22/255f, 0.55f);
        public static readonly Color SurfaceHighlight = new Color(1f, 1f, 1f, 0.06f);
        public static readonly Color Divider          = new Color(1f, 1f, 1f, 0.08f);

        // Tile surfaces (used as solid fallback when no poster loaded).
        // Vibrant enough that tiles read as distinct designed cards even without poster art.
        public static readonly Color TileDefault      = new Color(0.18f, 0.18f, 0.22f, 1f);
        public static readonly Color TileLive         = new Color(0.42f, 0.10f, 0.16f, 1f);   // richer crimson
        public static readonly Color TileScheduled    = new Color(0.12f, 0.24f, 0.42f, 1f);   // deeper electric blue
        public static readonly Color TileRecent       = new Color(0.20f, 0.20f, 0.24f, 0.85f);

        // Always-visible thin tile outline so each card reads as distinct on the dark page.
        // Bumped from 0.18 → 0.32 alpha so unfocused tiles don't melt into the dark BG.
        public static readonly Color TileBorder       = new Color(1f, 1f, 1f, 0.32f);

        // Status accents — saturated, TV-vivid. The duller versions (esp. AccentScheduled
        // at 0.85 alpha) read as washed-out on a TV panel that's already dimming the picture.
        public static readonly Color AccentLive       = new Color(0.965f, 0.10f, 0.20f, 1f);  // brighter Netflix red
        public static readonly Color AccentScheduled  = new Color(0.31f, 0.71f, 1f, 1f);      // full-alpha electric blue
        public static readonly Color AccentRecent     = new Color(1f, 1f, 1f, 0.32f);

        // Text colors
        public static readonly Color TextPrimary      = new Color(1f, 1f, 1f, 1f);
        public static readonly Color TextSecondary    = new Color(1f, 1f, 1f, 0.7f);
        public static readonly Color TextTertiary     = new Color(1f, 1f, 1f, 0.5f);
        public static readonly Color TextOnLight      = new Color(0.039f, 0.039f, 0.039f, 1f);
        public static readonly Color TextDim          = new Color(1f, 1f, 1f, 0.55f);

        // Active chip pill (white)
        public static readonly Color ChipActive       = new Color(1f, 1f, 1f, 1f);

        // Typography
        // Inter is shipped under Resources/Fonts and registered at app startup via FontClient.
        // NUI's TextLabel.FontFamily is a single name; if Inter isn't found at render time the
        // device falls back to its default sans automatically — no app crash.
        public const string FontFamily = "Inter";
    }
}
