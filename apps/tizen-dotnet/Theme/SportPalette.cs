using System;
using Tizen.NUI;

namespace StreamedTv.Theme
{
    /// <summary>
    /// Per-sport color palette extracted to mirror the bundled hero banner art:
    ///   Football  → deep navy stadium with cool blue / cyan stadium lights
    ///   Cricket   → deep teal pitch with emerald + warm amber stand lights
    ///   Basketball → indigo arena with electric-orange basketball + cool blue rim lights
    ///
    /// The palette drives everything in the app that wants to feel "themed by the
    /// current sport": the background gradient under the chrome, the hero focus
    /// wash, the nav-pill active chip, the MoreOverlay focus highlight, etc.
    /// Subscribe to <see cref="Changed"/> to retint when the user pins a different
    /// sport.
    /// </summary>
    public static class SportPalette
    {
        public sealed class Palette
        {
            // Headline accent — used for "active sport" affordances and the hero
            // focus wash. Saturated, TV-bright.
            public Color Primary { get; set; }
            // Secondary accent — used for paired highlights (e.g. a pinned chip's
            // outline next to a focused chip's fill).
            public Color Secondary { get; set; }
            // Background gradient. Painted as a vertical wash behind everything;
            // top stays close to BgBase so the hero blends in, bottom carries the
            // sport tint.
            public Color BgGradientTop { get; set; }
            public Color BgGradientBottom { get; set; }
            public string SportId { get; set; }
        }

        public static event EventHandler<Palette> Changed;

        public static Palette Current { get; private set; } = ForSport(null);

        public static void SetCurrent(string sportId)
        {
            Palette next = ForSport(sportId);
            // Always re-emit so subscribers can re-tint on a no-op switch
            // (e.g. swapping back to a sport you were on); cheap and prevents
            // stale state if a view was added after the last change.
            Current = next;
            Changed?.Invoke(null, next);
        }

        public static Palette ForSport(string sportId)
        {
            string id = (sportId ?? "").Trim().ToLowerInvariant();
            switch (id)
            {
                case "football":
                case "soccer":
                    // Sampled from hero_football.jpg — the warm crimson stadium
                    // lighting + red shirt = the dominant chroma signature.
                    return new Palette
                    {
                        SportId = id,
                        Primary           = new Color(0.870f, 0.585f, 0.408f, 1f),  // warm crimson glow
                        Secondary         = new Color(0.55f, 0.18f, 0.18f, 1f),     // deeper red mid
                        BgGradientTop     = new Color(0.024f, 0.024f, 0.030f, 1f),
                        BgGradientBottom  = new Color(0.174f, 0.117f, 0.082f, 1f),  // 22% scale of signature → dark crimson wash
                    };
                case "cricket":
                    // Sampled from hero_cricket.jpg — golden sunset stadium light
                    // + green field. Signature picks up the warm gold hue.
                    return new Palette
                    {
                        SportId = id,
                        Primary           = new Color(0.876f, 0.709f, 0.331f, 1f),  // sunset gold
                        Secondary         = new Color(0.20f, 0.55f, 0.30f, 1f),     // pitch green
                        BgGradientTop     = new Color(0.024f, 0.028f, 0.030f, 1f),
                        BgGradientBottom  = new Color(0.175f, 0.142f, 0.066f, 1f),  // 22% scale → warm amber wash
                    };
                case "basketball":
                    // Sampled from hero_basketball.jpg — wood floor + warm court
                    // lights dominate; purple stands are the secondary highlight.
                    return new Palette
                    {
                        SportId = id,
                        Primary           = new Color(0.867f, 0.402f, 0.199f, 1f),  // basketball orange
                        Secondary         = new Color(0.42f, 0.20f, 0.62f, 1f),     // arena purple
                        BgGradientTop     = new Color(0.024f, 0.024f, 0.030f, 1f),
                        BgGradientBottom  = new Color(0.173f, 0.080f, 0.040f, 1f),  // 22% scale → deep ember wash
                    };
                default:
                    return new Palette
                    {
                        SportId = id,
                        Primary           = Tokens.AccentLive,
                        Secondary         = Tokens.AccentScheduled,
                        BgGradientTop     = Tokens.BgBase,
                        BgGradientBottom  = new Color(0.05f, 0.05f, 0.08f, 1f),
                    };
            }
        }
    }
}
