using Tizen.NUI;

namespace StreamedTv.Theme
{
    /// <summary>Pixel-based dimensions and font sizes for a 1920x1080 reference TV. Scale callers against the actual window if needed.</summary>
    public static class Dimens
    {
        // Reference design width
        public const float ReferenceWidth = 1920f;

        // Layout
        public const int PageHorizontalPadding = 115;   // 6%
        public const int RowHorizontalPadding  = 60;    // 3%
        public const int NavPillTop            = 32;
        public const int NavPillVerticalPad    = 10;
        public const int NavPillHorizontalPad  = 14;
        public const int ChipVerticalPad       = 16;
        public const int ChipHorizontalPad     = 38;
        public const int ChipGap               = 22;    // bumped: nav pill chips scale to 1.16 on focus, this gap keeps them from overlapping siblings
        public const int ChipDividerHeight     = 36;
        public const int ChipDividerWidth      = 1;

        public const int HeroHeight            = 380;
        public const int HeroPadding           = 36;
        public const int HeroCornerRadius      = 14;

        public const int RowHeight             = 340;   // bumped to fit the bigger tiles + label/margin
        public const int RowLabelMarginBottom  = 18;
        public const int RowLabelMarginLeft    = 8;
        public const int TileWidth             = 460;   // bumped from 380 — feels meaty on a TV at couch distance
        public const int TileHeight            = 258;   // 16:9 (460 / 1.778 ≈ 258)
        public const int TileGap               = 28;    // wider breathing room between bigger tiles
        public const int TileCornerRadius      = 22;    // noticeably rounder, more "card-like" silhouette
        public const int TilePadding           = 16;

        public const int PillCornerRadius      = 4;
        public const int PillVerticalPad       = 4;
        public const int PillHorizontalPad     = 12;

        // Font sizes (PointSize in NUI)
        public const float FontHero            = 36f;   // hero title
        public const float FontTileTitle       = 17f;   // bumped with the larger tile (460×258)
        public const float FontTileSub         = 12f;   // bumped with the larger tile
        public const float FontPill            = 10f;
        public const float FontChip            = 18f;   // nav-pill chip text — readable from a couch
        public const float FontRowLabel        = 26f;   // bumped: 'LIVE NOW' / 'COMING UP' / 'RECENTLY FINISHED'
        public const float FontHeroSub         = 13f;
        public const float FontHeroCta         = 12f;
        public const float FontHint            = 9f;

        // Animation durations (ms)
        // Tuned together so row-to-row switching feels like one smooth motion:
        // page pan, tile focus scale, and row horizontal scroll all run for
        // overlapping windows with the same easing curve (EaseInOutSine in the
        // call sites).
        public const int AnimFocusScale        = 180;   // snap the tile lift in fast so it doesn't race the scroll
        public const int AnimDwellRing         = 2500;
        public const int AnimHeroCrossfade     = 300;
        // Horizontal row scroll is the thing the user moves through most. Decelerating
        // ease (EaseOutCubic at the call site) + ~360ms gives a "swipe" feel — the
        // strip glides to rest instead of cutting off abruptly.
        public const int AnimRowScroll         = 360;
        public const int AnimRowSwitch         = 320;   // _root.PositionY pan when focus moves vertically
        public const int AnimChromeFade        = 200;
        public const int AnimOverlayOpen       = 250;

        public const float FocusScale          = 1.05f;
    }
}
