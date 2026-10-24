using System;
using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using StreamedTv.Models;
using StreamedTv.Theme;

namespace StreamedTv.Views
{
    /// <summary>
    /// A horizontal row: section label on top, then a horizontal LinearLayout of TileView children.
    /// Per the Tizen 9 device runtime constraint, ALL tile placement is via LinearLayout — no
    /// absolute Position assignments on children.
    /// </summary>
    public sealed class RowView : View
    {
        private readonly TextLabel _labelView;
        private readonly View _scrollClip;
        private readonly View _tilesContainer;
        private readonly TextLabel _emptyLabel;
        private readonly List<TileView> _tiles;
        private readonly List<View> _skeletons;
        private readonly int _tileStride;
        private bool _isEmpty;
        private TileView.TileKind _currentKind;
        private Animation _scrollAnimation;

        public string Label { get; }
        public IReadOnlyList<TileView> Tiles { get; private set; }
        public int FocusedIndex { get; private set; }

        public RowView(string label, IList<CatalogMatch> matches, TileView.TileKind kind)
        {
            Label = label ?? "";
            FocusedIndex = -1;
            _tiles = new List<TileView>();
            _skeletons = new List<View>();
            _tileStride = Dimens.TileWidth + Dimens.TileGap;
            _isEmpty = matches == null || matches.Count == 0;
            _currentKind = kind;

            WidthSpecification = LayoutParamPolicies.MatchParent;
            HeightSpecification = Dimens.RowHeight;
            BackgroundColor = new Color(0f, 0f, 0f, 0f);

            // Outer vertical layout: label, then tiles strip.
            Layout = new LinearLayout
            {
                LinearOrientation = LinearLayout.Orientation.Vertical,
                LinearAlignment = LinearLayout.Alignment.Begin,
                CellPadding = new Size2D(0, Dimens.RowLabelMarginBottom),
                Padding = new Extents(
                    (ushort)Dimens.RowLabelMarginLeft,
                    0,
                    0,
                    0),
            };

            _labelView = new TextLabel
            {
                // Label is rendered as-passed (no forced uppercase). Earlier
                // ToUpperInvariant() shouted the labels; the user wants Title
                // Case, so the label callsite controls capitalization now.
                Text = Label ?? "",
                // Bumped from TextTertiary (faded) to TextPrimary (bright white) +
                // bold weight + larger PointSize so the row headers ("LIVE NOW",
                // "COMING UP", "RECENTLY FINISHED") are readable from a TV viewing
                // distance, not just at desktop arm's-length.
                TextColor = Tokens.TextPrimary,
                PointSize = Dimens.FontRowLabel,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            Add(_labelView);

            // Container that holds the horizontally scrolling tile strip. We do NOT clip
            // children here — clipping at the row's local bounds means tiles get sliced
            // at the page horizontal padding (the "border cut early" feel the user
            // reported). Letting the strip overflow lets tiles glide off the actual
            // screen edge, where the window naturally crops them. The row above/below
            // can't be hit because RowView's own height is fixed and the parent
            // LinearLayout stacks rows vertically with no horizontal overlap.
            _scrollClip = new View
            {
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = Dimens.TileHeight,
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
            };
            Add(_scrollClip);

            // Inside the clip, a horizontal LinearLayout of tiles. We animate this container's PositionX
            // for horizontal scroll. (Animating Position on a LinearLayout child is fine; child layout
            // is handled by the LinearLayout itself.)
            _tilesContainer = new View
            {
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.MatchParent,
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.CenterVertical,
                    CellPadding = new Size2D(Dimens.TileGap, 0),
                },
            };
            // ParentOrigin top-left so PositionX shifts content from the left edge of the clip.
            _tilesContainer.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            _tilesContainer.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            _scrollClip.Add(_tilesContainer);

            if (_isEmpty)
            {
                _emptyLabel = new TextLabel
                {
                    Text = "No matches",
                    TextColor = Tokens.TextTertiary,
                    PointSize = Dimens.FontTileTitle,
                    FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                    MultiLine = false,
                    WidthSpecification = LayoutParamPolicies.WrapContent,
                    HeightSpecification = LayoutParamPolicies.WrapContent,
                };
                _tilesContainer.Add(_emptyLabel);
                Tiles = _tiles.AsReadOnly();
                return;
            }

            // Cap visible tiles per row. Building 30+ TileViews synchronously each kicks off an
            // ImageView network load, which on a TV CPU bunches up into a multi-second hang
            // (the symptom we hit when switching to a sport with many matches like Football).
            // 8 tiles at 320px wide + 23px gap give plenty of horizontal scroll room past 1920px.
            const int MaxTilesPerRow = 8;
            int limit = matches.Count < MaxTilesPerRow ? matches.Count : MaxTilesPerRow;
            // Cap POPULAR badges per row. The streamed.pk API returns Popular
            // as a plain bool with no rank; combined with the upstream sort
            // (favourites → popular → date), the first matches in this list
            // are the most popular ones, so we award the badge to the first
            // MaxPopularBadges that are flagged. Beyond that, hide it — keeps
            // the badge meaningful instead of decorating every tile.
            const int MaxPopularBadges = 2;
            int popularBadgeCount = 0;
            for (int i = 0; i < limit; i++)
            {
                CatalogMatch m = matches[i];
                TileView tile = new TileView(m, kind);
                bool allowPopular = m != null && m.Popular && popularBadgeCount < MaxPopularBadges;
                if (allowPopular) popularBadgeCount++;
                tile.SetPopularBadgeAllowed(allowPopular);
                _tilesContainer.Add(tile);
                _tiles.Add(tile);
            }

            Tiles = _tiles.AsReadOnly();
            // Same staggered background preload that SetMatches uses — the
            // constructor path runs at app startup, so without this the first
            // tab's tiles were blank until the user navigated through them.
            PreloadAllPostersInBackground();
        }

        public void SetFocusedIndex(int index)
        {
            if (_isEmpty) return;
            if (index < 0 || index >= _tiles.Count) return;

            int previous = FocusedIndex;
            FocusedIndex = index;

            if (previous >= 0 && previous < _tiles.Count && previous != index)
            {
                _tiles[previous].SetFocused(false);
            }

            TileView focusedTile = _tiles[index];
            focusedTile.SetFocused(true);
            focusedTile.StartDwellRing();

            // Look-ahead preload: warm up the NEXT tile's poster so it's
            // already loaded by the time the user scrolls to it. PreloadPoster
            // is idempotent and the deferred URL assignment runs on idle, so
            // this never blocks the current focus animation.
            if (index + 1 < _tiles.Count)
            {
                _tiles[index + 1]?.PreloadPoster();
            }

            // Scroll the tilesContainer so the focused tile is at the anchor position.
            int anchorPx = 350;
            float desiredX = anchorPx - index * _tileStride;
            float clampedX = Math.Min(0f, desiredX);

            // Visible row width is the screen minus the page's horizontal padding (one
            // PageHorizontalPadding on each side) AND the RowView's own left padding for
            // the section label. The earlier code used Dimens.ReferenceWidth (1920) here,
            // which over-stated the visible width by ~240px and caused the clamp to stop
            // scrolling before the rightmost tiles could come into view — what the user
            // observed as "the row just maintains this narrow window".
            int rowWidth = (int)Dimens.ReferenceWidth - 2 * Dimens.PageHorizontalPadding - Dimens.RowLabelMarginLeft;
            int totalContentWidth = (_tiles.Count - 1) * _tileStride + Dimens.TileWidth;
            if (totalContentWidth > rowWidth)
            {
                float minX = -(totalContentWidth - rowWidth);
                if (clampedX < minX) clampedX = minX;
            }
            else
            {
                clampedX = 0f;
            }

            _scrollAnimation?.Stop();
            // Drive the strip from its CURRENT (possibly mid-animation) PositionX so
            // rapid Left/Right presses don't snap back to where the last animation
            // happened to be when we Stop()'d it. AnimateTo on Position with the
            // current value as the implicit start gives a continuous glide.
            _scrollAnimation = new Animation(Dimens.AnimRowScroll);
            _scrollAnimation.DefaultAlphaFunction = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOutSquare);
            // EaseOutSquare = strong front-loaded velocity that decelerates into the
            // final position. Feels like a swipe coming to rest, vs EaseInOutSine which
            // accelerates AND decelerates and so reads as "starts slow, ends slow" —
            // exactly the jerky feel the user complained about.
            _scrollAnimation.AnimateTo(_tilesContainer, "PositionX", clampedX);
            _scrollAnimation.Play();
        }

        public void ClearFocus()
        {
            if (_isEmpty)
            {
                FocusedIndex = -1;
                return;
            }

            if (FocusedIndex >= 0 && FocusedIndex < _tiles.Count)
            {
                _tiles[FocusedIndex].SetFocused(false);
            }
            FocusedIndex = -1;
        }

        /// <summary>Replace the row's tiles with skeleton placeholders that pulse — the Netflix-style
        /// "tab switched, content arriving" affordance. Tiles list becomes empty so focus skips this row.</summary>
        public void SetLoading(int placeholderCount = 6)
        {
            ClearTilesContainer();
            _tiles.Clear();
            _skeletons.Clear();
            _isEmpty = true;
            FocusedIndex = -1;

            for (int i = 0; i < placeholderCount; i++)
            {
                View skel = new View
                {
                    WidthSpecification = Dimens.TileWidth,
                    HeightSpecification = Dimens.TileHeight,
                    BackgroundColor = new Color(0.13f, 0.13f, 0.15f, 1f),
                    BorderlineColor = Tokens.TileBorder,
                    BorderlineWidth = 1f,
                    BorderlineOffset = -1f,
                    CornerRadius = new Vector4(Dimens.TileCornerRadius, Dimens.TileCornerRadius, Dimens.TileCornerRadius, Dimens.TileCornerRadius),
                    CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                    Opacity = 0.45f,
                };
                _tilesContainer.Add(skel);
                _skeletons.Add(skel);

                // Pulse opacity to fake a shimmer. Stagger the start so the row looks alive.
                Animation pulse = new Animation(900);
                pulse.Looping = true;
                pulse.AnimateTo(skel, "Opacity", 0.75f, 0, 450, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseInOut));
                pulse.AnimateTo(skel, "Opacity", 0.35f, 450, 900, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseInOut));
                pulse.Play();
            }

            Tiles = _tiles.AsReadOnly();
        }

        /// <summary>Replace the row's tiles with real match data. Used when API data arrives after a SetLoading().</summary>
        public void SetMatches(IList<CatalogMatch> matches, TileView.TileKind kind)
        {
            ClearTilesContainer();
            _tiles.Clear();
            _skeletons.Clear();
            _currentKind = kind;
            _isEmpty = matches == null || matches.Count == 0;
            FocusedIndex = -1;

            if (_isEmpty)
            {
                TextLabel emptyLabel = new TextLabel
                {
                    Text = "No matches",
                    TextColor = Tokens.TextTertiary,
                    PointSize = Dimens.FontTileTitle,
                    FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                    MultiLine = false,
                    WidthSpecification = LayoutParamPolicies.WrapContent,
                    HeightSpecification = LayoutParamPolicies.WrapContent,
                };
                _tilesContainer.Add(emptyLabel);
                Tiles = _tiles.AsReadOnly();
                return;
            }

            const int MaxTilesPerRow = 8;
            int limit = matches.Count < MaxTilesPerRow ? matches.Count : MaxTilesPerRow;
            // Same per-row POPULAR cap as the constructor — see comment there.
            const int MaxPopularBadges = 2;
            int popularBadgeCount = 0;
            for (int i = 0; i < limit; i++)
            {
                CatalogMatch m = matches[i];
                TileView tile = new TileView(m, kind);
                bool allowPopular = m != null && m.Popular && popularBadgeCount < MaxPopularBadges;
                if (allowPopular) popularBadgeCount++;
                tile.SetPopularBadgeAllowed(allowPopular);
                _tilesContainer.Add(tile);
                _tiles.Add(tile);
            }

            // Reset scroll position.
            _tilesContainer.PositionX = 0f;

            Tiles = _tiles.AsReadOnly();

            // Kick off a background, staggered preload of every tile's poster.
            // The constructor only resolves the URL but doesn't fire the HTTP
            // load — that originally happened only on focus, which left tiles
            // in the row blank until the user navigated through them. Stagger
            // the loads ~80ms apart so the network/decoder pipeline isn't
            // stampeded by 8 simultaneous fetches (the original bug we fixed
            // by introducing lazy loading in the first place).
            PreloadAllPostersInBackground();
        }

        // Fire-and-forget. Each tile's PreloadPoster() is idempotent, so it's
        // safe if SetFocused later fires the same load — second call is a no-op.
        // Cap the preload to the first VisiblePreloadCount tiles per row (~5
        // fit on screen at 460×258 inside a 1920-wide window). Tiles beyond
        // the visible window load on focus when the user scrolls to them —
        // that keeps the initial burst short, so arrow-key navigation doesn't
        // compete with a stack of pending image decodes. Stagger raised from
        // 80ms → 180ms so even the initial 5 loads space out enough to not
        // chain back-to-back synchronous decodes.
        private const int VisiblePreloadCount = 5;
        private const int PreloadStaggerMs = 180;

        private async void PreloadAllPostersInBackground()
        {
            try
            {
                List<TileView> snapshot = new List<TileView>(_tiles);
                int budget = Math.Min(snapshot.Count, VisiblePreloadCount);
                for (int i = 0; i < budget; i++)
                {
                    TileView tile = snapshot[i];
                    if (tile == null) continue;
                    tile.PreloadPoster();
                    await System.Threading.Tasks.Task.Delay(PreloadStaggerMs);
                }
            }
            catch
            {
                // Background preload is best-effort. SetFocused() will still
                // trigger EnsurePosterLoaded() on demand if anything went wrong.
            }
        }

        private void ClearTilesContainer()
        {
            uint count = _tilesContainer.ChildCount;
            for (uint i = 0; i < count; i++)
            {
                _tilesContainer.Remove(_tilesContainer.GetChildAt(0));
            }
        }
    }
}
