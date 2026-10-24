using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using StreamedTv.Models;
using StreamedTv.Services;
using StreamedTv.Theme;

namespace StreamedTv.Views
{
    /// <summary>
    /// Cinematic 16:9 card. Structure:
    ///   Tile (no Layout — children overlap at z-order)
    ///     ├─ poster ImageView (full size, optional)
    ///     └─ scrim+content View (LinearLayout vertical: pill, spacer, title, sub)
    /// Focus border is animated on the tile itself (BorderlineWidth) — same pattern as HeroView.
    /// Tizen 9 NUI device runtime ignores absolute child Position, so the content overlay relies on
    /// WidthResizePolicy/HeightResizePolicy = FillToParent rather than Position assignments.
    /// </summary>
    public sealed class TileView : View
    {
        public enum TileKind { Live, Scheduled, Recent }

        private readonly ImageView _poster;
        private readonly View _scrim;
        private readonly TextLabel _pillLabel;
        private readonly View _pillBg;
        private readonly TextLabel _titleLabel;
        private readonly TextLabel _subLabel;
        private readonly TextLabel _favoriteChip;
        // Wrapper view that holds the heart label + its rounded backdrop. We
        // toggle THIS view (not just the inner label) so the whole badge —
        // backdrop, border, glyph — appears or disappears together.
        private View _favoriteBadge;
        // POPULAR pill — shown on tiles whose Match.Popular == true. Lives in
        // the scrim's vertical column between the time-pill row and the spacer
        // so it nests with the other chrome rather than floating as an overlay.
        private View _popularBadge;
        // Lazy poster loading — we resolve the poster URL up front but defer the
        // actual ImageView ResourceUrl assignment (and the HTTP fetch it triggers)
        // until the tile becomes "interesting" (focused or explicitly preloaded).
        // This avoids the 24-poster simultaneous request burst on tab switch.
        private readonly string _posterUrl;
        private bool _posterLoaded;
        private Animation _posterFadeAnimation;
        // Dedicated topmost overlay used to draw the focus border. Necessary because the poster
        // ImageView (added as a FillToParent child) renders on top of the parent View's
        // BorderlineWidth — without this overlay the border disappears the moment a tile loads
        // its image. Always added LAST so it sits above poster + scrim + content.
        private readonly View _focusBorder;
        private Animation _focusAnimation;
        private bool _focused;

        private static readonly Color RestingBorderColor = new Color(1f, 1f, 1f, 0.42f);
        private const float RestingBorderWidth = 4f;
        // Keep the focused border well below the corner radius so NUI's renderer
        // can keep both the outer and inner corner arcs smooth. At 10px against a
        // 22px radius the inner arc was getting clipped to a near-rectangle —
        // the "border breaks the corner" feel the user reported on focus.
        // 6px against 22px leaves an inner radius of ~16 which stays visibly round.
        private const float FocusedBorderWidth = 6f;

        public CatalogMatch Match { get; private set; }
        public TileKind Kind { get; private set; }

        public event EventHandler Activated;

        public TileView(CatalogMatch match, TileKind kind)
        {
            Match = match;
            Kind = kind;

            WidthSpecification = Dimens.TileWidth;
            HeightSpecification = Dimens.TileHeight;
            BackgroundColor = ColorForKind(kind);
            Opacity = kind == TileKind.Recent ? 0.7f : 1f;
            ClippingMode = ClippingModeType.ClipChildren;

            CornerRadius = new Vector4(Dimens.TileCornerRadius, Dimens.TileCornerRadius, Dimens.TileCornerRadius, Dimens.TileCornerRadius);
            CornerRadiusPolicy = VisualTransformPolicyType.Absolute;
            // The base View also gets a thin border, but it's hidden the moment a poster ImageView
            // covers it. The real always-visible border is _focusBorder (added last).
            BorderlineColor = new Color(0f, 0f, 0f, 0f);
            BorderlineWidth = 0f;
            BorderlineOffset = -1f;

            // Layer 1: poster image. ResourceUrl is intentionally left empty here —
            // EnsurePosterLoaded() (called from SetFocused or PreloadPoster) is what
            // actually kicks the HTTP load, so the 24 tiles in a freshly-switched tab
            // don't all fire image requests at the same time.
            string posterUrl = ResolvePosterUrl(match);
            _posterUrl = posterUrl ?? "";
            _poster = new ImageView
            {
                ResourceUrl = "",
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.FillToParent,
                FittingMode = FittingModeType.ScaleToFill,
                Opacity = 0f,
            };
            Add(_poster);

            // Layer 2: dark scrim+content overlay so text stays readable on top of the poster.
            // The scrim color is decided by whether a poster URL EXISTS (not whether it's been
            // loaded yet) — we want the dark veil ready for when the poster fades in.
            _scrim = new View
            {
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.FillToParent,
                BackgroundColor = string.IsNullOrEmpty(_posterUrl)
                    ? new Color(0f, 0f, 0f, 0f)
                    : new Color(0f, 0f, 0f, 0.45f),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    LinearAlignment = LinearLayout.Alignment.Top,
                    CellPadding = new Size2D(0, 4),
                    Padding = new Extents(
                        (ushort)Dimens.TilePadding,
                        (ushort)Dimens.TilePadding,
                        (ushort)Dimens.TilePadding,
                        (ushort)Dimens.TilePadding),
                },
            };
            Add(_scrim);

            _pillBg = new View
            {
                BackgroundColor = AccentForKind(kind),
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.Center,
                    Padding = new Extents(
                        (ushort)Dimens.PillHorizontalPad,
                        (ushort)Dimens.PillHorizontalPad,
                        (ushort)Dimens.PillVerticalPad,
                        (ushort)Dimens.PillVerticalPad),
                },
                CornerRadius = new Vector4(Dimens.PillCornerRadius, Dimens.PillCornerRadius, Dimens.PillCornerRadius, Dimens.PillCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
            };
            // Topbar row inside the scrim: just the kind/time pill on the left.
            // The favorite heart is NO LONGER in this row — it's now a dedicated
            // overlay anchored to the tile's top-right corner (see _favoriteChip
            // construction below). The earlier "pill + spacer + tiny heart" layout
            // made the heart visually attach to the time pill, hard to spot.
            View topRow = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.CenterVertical,
                    CellPadding = new Size2D(0, 0),
                },
            };
            topRow.Add(_pillBg);
            // POPULAR pill — also lives in topRow so it sits SIDE BY SIDE with
            // the LIVE/SCHEDULED time pill instead of stacking under it (the
            // earlier vertical-stacked layout looked clunky). topRow uses a
            // horizontal LinearLayout with CenterVertical alignment, so the two
            // chips sit on the same baseline. Add a small left margin so they
            // don't touch.
            _popularBadge = new View
            {
                BackgroundColor = new Color(0.95f, 0.55f, 0.10f, 1f),
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                CornerRadius = new Vector4(6f, 6f, 6f, 6f),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                Margin = new Extents(8, 0, 0, 0),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.Center,
                    Padding = new Extents(8, 8, 3, 3),
                },
            };
            TextLabel popularLabel = new TextLabel
            {
                Text = "★ POPULAR",
                TextColor = Color.White,
                PointSize = Dimens.FontPill,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _popularBadge.Add(popularLabel);
            topRow.Add(_popularBadge);
            // Default to hidden. Caller (RowView) explicitly opts each tile
            // in via SetPopularBadgeAllowed — we cap the per-row count so a
            // sport with many popular flags (the streamed.pk API marks lots of
            // matches as popular = true) doesn't end up with every tile
            // wearing the badge.
            _popularBadge.Hide();
            _scrim.Add(topRow);

            _pillLabel = new TextLabel
            {
                Text = PillTextForKind(kind, match),
                TextColor = kind == TileKind.Recent ? Tokens.TextSecondary : Color.White,
                PointSize = Dimens.FontPill,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _pillBg.Add(_pillLabel);

            View spacer = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = 0,
                Weight = 1f,
            };
            _scrim.Add(spacer);

            _titleLabel = new TextLabel
            {
                Text = match?.Title ?? "Match",
                TextColor = Tokens.TextPrimary,
                PointSize = Dimens.FontTileTitle,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                MultiLine = false,
                Ellipsis = true,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _scrim.Add(_titleLabel);

            _subLabel = new TextLabel
            {
                Text = SubTextForMatch(match),
                TextColor = Tokens.TextSecondary,
                PointSize = Dimens.FontTileSub,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                MultiLine = false,
                Ellipsis = true,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _scrim.Add(_subLabel);

            // Layer 3: focus border overlay. Added LAST so it draws on top of poster + scrim.
            // A transparent View with a CornerRadius + BorderlineWidth — when the tile gets
            // focus we animate this overlay's border up to FocusedBorderWidth.
            _focusBorder = new View
            {
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.FillToParent,
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                CornerRadius = new Vector4(Dimens.TileCornerRadius, Dimens.TileCornerRadius, Dimens.TileCornerRadius, Dimens.TileCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                BorderlineColor = RestingBorderColor,
                BorderlineWidth = RestingBorderWidth,
                BorderlineOffset = -1f,
            };
            Add(_focusBorder);

            // Favorite badge — pinned to the tile's top-right corner OUTSIDE the
            // _scrim's LinearLayout so it sits above the time pill and the poster.
            // Uses TopLeft anchoring with explicit pixel Position (rather than
            // TopRight pivot) — the TopRight-pivot approach was clipping ~80% of
            // the badge on-device because Tizen 9's clipping shader interacts
            // unpredictably with non-TopLeft pivots when the parent has a
            // CornerRadius + ClippingMode. TopLeft + a math'd-out X coordinate
            // is the deterministic version of the same intent.
            const int FavBadgeWidth = 60;
            const int FavBadgeHeight = 48;
            const int FavBadgeInset = 14;
            View favBadgeBg = new View
            {
                BackgroundColor = new Color(0.10f, 0.10f, 0.12f, 0.82f),
                BorderlineColor = new Color(1f, 0.30f, 0.36f, 0.95f),  // coral red rim
                BorderlineWidth = 2f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(24f, 24f, 24f, 24f),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                WidthSpecification = FavBadgeWidth,
                HeightSpecification = FavBadgeHeight,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.Center,
                },
            };
            favBadgeBg.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            favBadgeBg.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            favBadgeBg.Position = new Position(
                Dimens.TileWidth - FavBadgeWidth - FavBadgeInset,
                FavBadgeInset);
            _favoriteChip = new TextLabel
            {
                Text = "♥",
                TextColor = new Color(1f, 0.36f, 0.42f, 1f),  // saturated coral red
                PointSize = 24f,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            favBadgeBg.Add(_favoriteChip);
            _favoriteBadge = favBadgeBg;
            _favoriteBadge.Hide();
            Add(_favoriteBadge);

            RefreshFavoriteIndicator();
        }

        /// <summary>Show or hide the heart chip based on the latest UserPreferences state.
        /// Called from the constructor and again whenever the host (Program.cs) toggles
        /// a favorite — that way every visible tile updates without rebuilding the row.</summary>
        /// <summary>Show or hide the POPULAR badge on this tile. The streamed.pk
        /// API returns `popular` as a plain bool with no rank/score, so a sport
        /// can have most matches flagged popular — making the badge meaningless
        /// when shown on every tile. The host (RowView) caps how many tiles in a
        /// row are allowed to wear the badge; this method is the per-tile opt-in
        /// that goes with that cap. Caller is responsible for honouring
        /// `Match.Popular` — passing `true` here on a non-popular match would
        /// still show the badge, which is intentional in case future logic wants
        /// a different "highlight this" rule.</summary>
        public void SetPopularBadgeAllowed(bool allowed)
        {
            if (_popularBadge == null) return;
            if (allowed) _popularBadge.Show();
            else _popularBadge.Hide();
        }

        public void RefreshFavoriteIndicator()
        {
            try
            {
                if (_favoriteBadge == null) return;
                bool fav = StreamedTv.Services.FavoritesRegistry.IsAnyTeamFavorited(Match);
                if (fav) _favoriteBadge.Show();
                else _favoriteBadge.Hide();
            }
            catch
            {
            }
        }

        public void SetFocused(bool focused)
        {
            if (_focused == focused) return;
            _focused = focused;

            if (focused)
            {
                // First time this tile gets focused, kick the poster fetch — defers the
                // initial network burst from constructor time to "user actually cared".
                EnsurePosterLoaded();
            }

            _focusAnimation?.Stop();
            _focusAnimation = new Animation(Dimens.AnimFocusScale);
            // Slightly more aggressive lift now that the border is thinner — keeps
            // the focused tile feeling distinct without the heavy stroke that was
            // breaking the rounded corners.
            // EaseOutSquare = front-loaded snap that settles, matching the row-scroll
            // glide. The previous EaseInOutSine made the lift feel sluggish next to a
            // row that was already gliding into place.
            float targetScale = focused ? 1.07f : 1.0f;
            AlphaFunction ease = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOutSquare);
            _focusAnimation.AnimateTo(this, "Scale", new Vector3(targetScale, targetScale, 1f), ease);

            if (focused)
            {
                _focusBorder.BorderlineColor = Color.White;
                _focusAnimation.AnimateTo(_focusBorder, "BorderlineWidth", FocusedBorderWidth, ease);
                // Note: NOT calling RaiseToTop — it reorders LinearLayout children on Tizen 9 and
                // makes the row visibly re-lay out on every focus change.
            }
            else
            {
                _focusBorder.BorderlineColor = RestingBorderColor;
                _focusAnimation.AnimateTo(_focusBorder, "BorderlineWidth", RestingBorderWidth, ease);
            }
            _focusAnimation.Play();
        }

        public void StartDwellRing()
        {
            // Visual cue: gradually deepen the white border during dwell. Once focus dwells fully,
            // the FocusManager fires TilePromoted and the hero crossfades.
        }

        public void StopDwellRing()
        {
        }

        public void RaiseActivated()
        {
            Activated?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Public hook so the host (Program.cs) can warm the first 2-3 tiles
        /// in each row without focusing them — keeps the row visually populated
        /// before the user starts navigating.</summary>
        public void PreloadPoster()
        {
            EnsurePosterLoaded();
        }

        /// <summary>Idempotent: kicks the ImageView's HTTP fetch and fades the poster
        /// in once the source URL is bound. Safe to call repeatedly — only the first
        /// call does anything.</summary>
        private void EnsurePosterLoaded()
        {
            if (_posterLoaded) return;
            if (string.IsNullOrEmpty(_posterUrl)) return;
            _posterLoaded = true;

            // Defer the ResourceUrl assignment to the NEXT event-loop iteration.
            // NUI's ImageView setter on Tizen does enough synchronous work
            // (texture upload, image-visual rebuild) that doing it inside an
            // input handler or a tight preload loop visibly stalls arrow-key
            // navigation. Posting via the synchronization context puts the
            // assignment after any pending UI events, so input gets serviced
            // first and the load happens on an idle tick.
            System.Threading.SynchronizationContext ctx = System.Threading.SynchronizationContext.Current;
            if (ctx != null)
            {
                ctx.Post(_ => DoApplyPosterUrl(), null);
            }
            else
            {
                DoApplyPosterUrl();
            }
        }

        private async void DoApplyPosterUrl()
        {
            try
            {
                string url = _posterUrl;
                if (string.IsNullOrEmpty(url)) return;

                // Resolve to a local file path BEFORE assigning ResourceUrl.
                // - Cache hit: TryGetLocalPath returns instantly, no await.
                // - Cache miss: EnsureCachedAsync downloads on a background
                //   thread (await releases the UI thread). Local-file URLs
                //   pass through unchanged.
                // The win is that NUI's ImageView.ResourceUrl setter, when
                // given a remote URL, does enough sync work (URL parsing,
                // visual rebuild, queueing the http request) to noticeably
                // hang input on the first load. Assigning a local file path
                // skips all of that — the only sync work is decode + texture
                // upload, both fast for our 100 KB cached JPEGs.
                string local = StreamedTv.Services.PosterCache.TryGetLocalPath(url);
                if (local == null)
                {
                    local = await StreamedTv.Services.PosterCache.EnsureCachedAsync(url).ConfigureAwait(true);
                }
                if (string.IsNullOrEmpty(local)) local = url;  // download failed → fall back

                _poster.ResourceUrl = local;

                _posterFadeAnimation?.Stop();
                _posterFadeAnimation = new Animation(Dimens.AnimChromeFade);
                AlphaFunction ease = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut);
                _posterFadeAnimation.AnimateTo(_poster, "Opacity", 1f, ease);
                _posterFadeAnimation.Play();
            }
            catch
            {
                // Tile may have been disposed (sport switch) before the
                // download completed. NUI view setters throw in that case —
                // safe to swallow, the tile is no longer on screen.
            }
        }

        private static string ResolvePosterUrl(CatalogMatch match)
        {
            if (match == null) return null;
            // 1. Backend-generated poster wins if the cron has produced one for this match.
            string backendPoster = StreamedTv.Services.BackendAssets.ResolvePosterUrl(match.Id);
            if (!string.IsNullOrEmpty(backendPoster)) return backendPoster;
            // 2. Direct poster URL on the match.
            if (!string.IsNullOrWhiteSpace(match.Poster))
            {
                Uri uri = TryProxiedImage(match.Poster);
                if (uri != null) return uri.ToString();
            }
            // 3. Backend-generated team logo for the home team if available — used to fill
            //    the tile while the poster is still queued for generation.
            string homeName = match.Teams?.Home?.Name;
            string backendLogo = StreamedTv.Services.BackendAssets.ResolveLogoUrl(homeName);
            if (!string.IsNullOrEmpty(backendLogo)) return backendLogo;
            // 4. Composite home/away badges from streamed.pk.
            string home = match.Teams?.Home?.Badge;
            string away = match.Teams?.Away?.Badge;
            if (!string.IsNullOrWhiteSpace(home) && !string.IsNullOrWhiteSpace(away))
            {
                return StreamedEndpoints.MatchPoster(home, away).ToString();
            }
            // 5. Single home badge fallback.
            if (!string.IsNullOrWhiteSpace(home))
            {
                return StreamedEndpoints.TeamBadge(home).ToString();
            }
            return null;
        }

        private static Uri TryProxiedImage(string posterRef)
        {
            try
            {
                return StreamedEndpoints.ProxiedImage(posterRef);
            }
            catch
            {
                return null;
            }
        }

        private static Color ColorForKind(TileKind kind)
        {
            switch (kind)
            {
                case TileKind.Live:      return Tokens.TileLive;
                case TileKind.Scheduled: return Tokens.TileScheduled;
                case TileKind.Recent:    return Tokens.TileRecent;
                default:                 return Tokens.TileDefault;
            }
        }

        private static Color AccentForKind(TileKind kind)
        {
            switch (kind)
            {
                case TileKind.Live:      return Tokens.AccentLive;
                case TileKind.Scheduled: return Tokens.AccentScheduled;
                case TileKind.Recent:    return Tokens.AccentRecent;
                default:                 return Tokens.AccentRecent;
            }
        }

        private static string PillTextForKind(TileKind kind, CatalogMatch match)
        {
            // Live snapshot lookup runs for both Live AND Recent — RapidAPI's
            // /cricket-livescores keeps finished matches around for several hours with
            // their final scores, so a "Recently finished" tile can show "DC 180/4 vs
            // KKR 175 — DC won" instead of just "FINAL".
            StreamedTv.Services.LiveCricketMatch live;
            bool haveLive = StreamedTv.Services.LiveScoreRegistry.TryGetForMatch(match, out live);

            switch (kind)
            {
                case TileKind.Live:
                    if (haveLive)
                    {
                        string s = LiveScoreLabel(live);
                        if (!string.IsNullOrEmpty(s)) return "● " + s;
                    }
                    return "● LIVE";
                case TileKind.Scheduled:
                    return ScheduledLabelForMatch(match);
                case TileKind.Recent:
                    if (haveLive)
                    {
                        string s = LiveScoreLabel(live);
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                    return "FINAL";
                default:
                    return "";
            }
        }

        // Compose a compact score string from the live snapshot. Both teams' scores when
        // available, else whichever side has data. Examples:
        //   "MI 180/4 vs CSK 120/5"
        //   "IND 245/6 (38 ov)"
        private static string LiveScoreLabel(StreamedTv.Services.LiveCricketMatch live)
        {
            if (live == null) return "";
            string a = TeamScoreShort(live.Team1);
            string b = TeamScoreShort(live.Team2);
            if (!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)) return a + " vs " + b;
            if (!string.IsNullOrEmpty(a)) return a;
            if (!string.IsNullOrEmpty(b)) return b;
            return "LIVE";
        }

        private static string TeamScoreShort(StreamedTv.Services.LiveTeam t)
        {
            if (t == null || string.IsNullOrEmpty(t.Score)) return "";
            string label = !string.IsNullOrEmpty(t.ShortName) ? t.ShortName : t.Name;
            if (string.IsNullOrEmpty(label)) return t.Score;
            return label + " " + t.Score;
        }

        private static string ScheduledLabelForMatch(CatalogMatch match)
        {
            if (match == null || match.Date <= 0) return "SOON";
            DateTimeOffset start = DateTimeOffset.FromUnixTimeMilliseconds(match.Date);
            DateTimeOffset now = DateTimeOffset.Now;
            if (start.Date == now.Date) return start.ToLocalTime().ToString("h:mm tt").ToUpperInvariant();
            if (start.Date == now.Date.AddDays(1)) return "TOMORROW";
            return start.ToLocalTime().ToString("ddd").ToUpperInvariant();
        }

        private static string SubTextForMatch(CatalogMatch match)
        {
            if (match == null) return "";
            string competition = match.Competition;
            if (string.IsNullOrWhiteSpace(competition)) return "";
            // The streamed.pk catalog mapper synthesises Competition = Category,
            // and Category IS the sport name — so a "Football" match comes back
            // with Competition == "football". That makes the sub-label show
            // "Football" on every tile inside the Football tab, which is the
            // redundant-sport noise we want to suppress. When Competition
            // matches Sport (case-insensitively), skip it entirely. Real
            // competition names like "Premier League" still pass through.
            if (!string.IsNullOrWhiteSpace(match.Sport)
                && string.Equals(competition.Trim(), match.Sport.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }
            return competition;
        }
    }
}
