using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using StreamedTv.Models;
using StreamedTv.Theme;

namespace StreamedTv.Views
{
    /// <summary>
    /// Promoted-match hero card. Vertical LinearLayout: status pill, title, subtitle, growing
    /// spacer, then a footer row with the Play CTA on the left and a "PRESS OK" hint on the right.
    /// All layout is via LinearLayout — Tizen 9 device runtime does not honor absolute child Position.
    /// </summary>
    public sealed class HeroView : View
    {
        // Netflix-style Play CTA — the focusable element of the hero now.
        // Bigger than the previous compact pill so it reads from across the
        // room and shifts the visual weight from "white border around the
        // hero" to "tap this button to play".
        private const int CtaCornerRadius   = 8;
        private const int CtaHorizontalPad  = 32;
        private const int CtaVerticalPad    = 14;

        private readonly ImageView _poster;
        private readonly View _scrim;
        private readonly View _content;
        private readonly View _pillBg;
        private readonly TextLabel _pillLabel;
        private readonly TextLabel _titleLabel;
        private readonly TextLabel _subLabel;
        private readonly View _ctaBg;
        private readonly TextLabel _ctaLabel;
        private readonly TextLabel _hintLabel;
        private readonly View _shimmer;
        // Dedicated topmost overlay for the focus border. The poster ImageView fills the entire
        // hero and renders on top of the parent View's BorderlineWidth, which is why the white
        // selection border was invisible on tiles/hero with poster art. Always added LAST.
        private readonly View _focusBorder;
        private Animation _crossfadeAnimation;
        private Animation _focusAnimation;
        private Animation _shimmerAnimation;
        private bool _focused;
        private bool _isLoading;
        private TileView.TileKind _kind;

        public CatalogMatch CurrentMatch { get; private set; }

        public event EventHandler Activated;

        public HeroView()
        {
            _kind = TileView.TileKind.Live;

            WidthSpecification = LayoutParamPolicies.MatchParent;
            HeightSpecification = Dimens.HeroHeight;
            BackgroundColor = Tokens.TileDefault;
            ClippingMode = ClippingModeType.ClipChildren;
            CornerRadius = new Vector4(Dimens.HeroCornerRadius, Dimens.HeroCornerRadius, Dimens.HeroCornerRadius, Dimens.HeroCornerRadius);
            CornerRadiusPolicy = VisualTransformPolicyType.Absolute;
            // Underlying View borderline is unused — _focusBorder overlay (added last) draws the
            // visible selection border so it sits above the poster ImageView.
            BorderlineColor = new Color(0f, 0f, 0f, 0f);
            BorderlineWidth = 0f;
            BorderlineOffset = -1f;
            BoxShadow = new Shadow(24f, new Color(0f, 0f, 0f, 0.55f), new Vector2(0f, 8f), new Vector2(0f, 0f));

            // Layer 1: poster art (loaded async when SetMatch supplies a URL).
            _poster = new ImageView
            {
                ResourceUrl = "",
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.FillToParent,
                FittingMode = FittingModeType.ScaleToFill,
                Opacity = 0f,
            };
            Add(_poster);

            // Layer 2: dark left-side scrim so the title/CTA stay readable over imagery.
            _scrim = new View
            {
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.FillToParent,
                BackgroundColor = new Color(0f, 0f, 0f, 0.45f),
                Opacity = 0f,
            };
            Add(_scrim);

            // Layer 3: the content lives in an inner padded vertical column.
            _content = new View
            {
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.MatchParent,
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    LinearAlignment = LinearLayout.Alignment.Top,
                    CellPadding = new Size2D(0, 12),
                    Padding = new Extents(
                        (ushort)Dimens.HeroPadding,
                        (ushort)Dimens.HeroPadding,
                        (ushort)Dimens.HeroPadding,
                        // Bottom is intentionally smaller than the top so the
                        // Play CTA sits close to the hero's bottom edge,
                        // matching Netflix's hero treatment where the action
                        // buttons hug the lower portion of the artwork.
                        16),
                },
            };
            Add(_content);

            _pillBg = new View
            {
                BackgroundColor = Tokens.AccentLive,
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
            _content.Add(_pillBg);

            _pillLabel = new TextLabel
            {
                Text = "● LIVE",
                TextColor = Color.White,
                PointSize = Dimens.FontPill,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _pillBg.Add(_pillLabel);

            _titleLabel = new TextLabel
            {
                Text = "",
                TextColor = Tokens.TextPrimary,
                PointSize = Dimens.FontHero,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                Ellipsis = true,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _content.Add(_titleLabel);

            _subLabel = new TextLabel
            {
                Text = "",
                TextColor = Tokens.TextSecondary,
                PointSize = Dimens.FontHeroSub,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                MultiLine = false,
                Ellipsis = true,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _content.Add(_subLabel);

            // Spacer: take all remaining vertical room so footer row sits at the bottom.
            View spacer = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = 0,
                Weight = 1f,
            };
            _content.Add(spacer);

            // Footer row: [Play CTA] [spacer] [PRESS OK hint]
            View footer = new View
            {
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.CenterVertical,
                    CellPadding = new Size2D(12, 0),
                },
            };
            _content.Add(footer);

            _ctaBg = new View
            {
                BackgroundColor = Color.White,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.Center,
                    Padding = new Extents(
                        (ushort)CtaHorizontalPad,
                        (ushort)CtaHorizontalPad,
                        (ushort)CtaVerticalPad,
                        (ushort)CtaVerticalPad),
                },
                CornerRadius = new Vector4(CtaCornerRadius, CtaCornerRadius, CtaCornerRadius, CtaCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
            };
            footer.Add(_ctaBg);

            _ctaLabel = new TextLabel
            {
                Text = "▶ Play",
                TextColor = Tokens.TextOnLight,
                PointSize = 18f,                // bumped from FontHeroCta(12) — Netflix-scale
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _ctaBg.Add(_ctaLabel);

            View footerSpacer = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = 0,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Weight = 1f,
            };
            footer.Add(footerSpacer);

            _hintLabel = new TextLabel
            {
                // Permanently hidden — the Play CTA is now the focus target,
                // so the redundant "PRESS OK" hint just adds noise. Field
                // kept around because the rest of the file's Hide()/Show()
                // calls reference it and we don't want a compile-cascade
                // edit just to remove a never-shown widget.
                Text = "",
                TextColor = Tokens.TextTertiary,
                PointSize = Dimens.FontHint,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _hintLabel.Hide();
            footer.Add(_hintLabel);

            // Layer 4: shimmering loading placeholder. Added last so it sits on top of poster +
            // content. Hidden by default; SetLoading() shows + pulses it, SetMatch() hides it
            // before crossfading to real content.
            _shimmer = new View
            {
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.FillToParent,
                BackgroundColor = new Color(0.13f, 0.13f, 0.15f, 1f),
                CornerRadius = new Vector4(Dimens.HeroCornerRadius, Dimens.HeroCornerRadius, Dimens.HeroCornerRadius, Dimens.HeroCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                Opacity = 0f,
            };
            _shimmer.Hide();
            Add(_shimmer);

            // Layer 5: focus border overlay — added last so it draws above poster + scrim +
            // content + shimmer. Animated by SetFocused.
            _focusBorder = new View
            {
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.FillToParent,
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                CornerRadius = new Vector4(Dimens.HeroCornerRadius, Dimens.HeroCornerRadius, Dimens.HeroCornerRadius, Dimens.HeroCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                BorderlineColor = new Color(1f, 1f, 1f, 0f),
                BorderlineWidth = 0f,
                BorderlineOffset = -1f,
            };
            Add(_focusBorder);

            ApplyEmptyState();
        }

        public void SetMatch(CatalogMatch match, TileView.TileKind kind)
        {
            if (!_isLoading
                && match != null
                && CurrentMatch != null
                && string.Equals(match.Id, CurrentMatch.Id, StringComparison.Ordinal)
                && _kind == kind)
            {
                return;
            }

            _crossfadeAnimation?.Stop();

            // If we were in the loading shimmer state, stop the pulse and hide the placeholder
            // immediately so the crossfade reveals the real content cleanly.
            if (_isLoading)
            {
                StopShimmer();
                _content.Opacity = 0f;
            }

            int half = Math.Max(1, Dimens.AnimHeroCrossfade / 2);

            Animation fadeOut = new Animation(half);
            fadeOut.AnimateTo(_content, "Opacity", 0f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseInOut));
            fadeOut.Finished += (sender, e) =>
            {
                ApplyMatch(match, kind);

                Animation fadeIn = new Animation(half);
                fadeIn.AnimateTo(_content, "Opacity", 1f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseInOut));
                fadeIn.Play();
                _crossfadeAnimation = fadeIn;
            };
            fadeOut.Play();
            _crossfadeAnimation = fadeOut;
        }

        /// <summary>Show a Netflix-style shimmering placeholder while data is loading. Hides
        /// pill / title / subtitle / CTA / hint and pulses a dark gray rectangle at ~900ms.</summary>
        public void SetLoading()
        {
            if (_isLoading) return;
            _isLoading = true;

            _crossfadeAnimation?.Stop();
            CurrentMatch = null;

            // Hide all real content (poster + scrim + text/footer column).
            _poster.ResourceUrl = "";
            _poster.Opacity = 0f;
            _scrim.Opacity = 0f;
            _content.Opacity = 0f;
            _pillBg.Hide();
            _subLabel.Hide();
            _ctaBg.Hide();
            _hintLabel.Hide();
            BackgroundColor = Tokens.TileDefault;

            StartShimmer();
        }

        private void StartShimmer()
        {
            _shimmerAnimation?.Stop();
            _shimmer.Show();
            _shimmer.Opacity = 0.3f;

            // Pulse opacity 0.3 → 0.7 → 0.3 on a 900ms loop. Looping must be set before Play().
            Animation pulse = new Animation(900);
            pulse.Looping = true;
            pulse.AnimateTo(_shimmer, "Opacity", 0.7f, 0, 450, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseInOut));
            pulse.AnimateTo(_shimmer, "Opacity", 0.3f, 450, 900, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseInOut));
            pulse.Play();
            _shimmerAnimation = pulse;
        }

        private void StopShimmer()
        {
            _isLoading = false;
            _shimmerAnimation?.Stop();
            _shimmerAnimation = null;
            _shimmer.Opacity = 0f;
            _shimmer.Hide();
        }

        public void SetFocused(bool focused)
        {
            if (_focused == focused) return;
            _focused = focused;

            _focusAnimation?.Stop();
            _focusAnimation = new Animation(Dimens.AnimFocusScale);
            // Focus indication shifted from "white border around the hero"
            // to "lift the Play button". The hero card itself just gets a
            // tiny 1.015 scale for subtle context — the Play CTA is what
            // actually reads as the focused element, mirroring Netflix's
            // hero treatment where the Play / More Info buttons are the
            // focus targets, not the artwork.
            if (focused)
            {
                _focusBorder.BorderlineColor = new Color(0f, 0f, 0f, 0f);
                _focusAnimation.AnimateTo(_focusBorder, "BorderlineWidth", 0f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
                _focusAnimation.AnimateTo(this, "Scale", new Vector3(1.015f, 1.015f, 1f), new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
                _focusAnimation.AnimateTo(_ctaBg, "Scale", new Vector3(1.10f, 1.10f, 1f), new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOutSquare));
            }
            else
            {
                _focusAnimation.AnimateTo(_focusBorder, "BorderlineWidth", 0f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
                _focusAnimation.AnimateTo(this, "Scale", new Vector3(1f, 1f, 1f), new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
                _focusAnimation.AnimateTo(_ctaBg, "Scale", new Vector3(1f, 1f, 1f), new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOutSquare));
            }
            _focusAnimation.Play();

            // Hero focus wash REMOVED — the bundled hero banner art already
            // carries the sport's color, so an additional colored overlay was
            // double-coloring the card and made the chrome feel painted-on.
            // The white focus border + scale lift are the only "selected" cues.
        }

        public void RaiseActivated()
        {
            Activated?.Invoke(this, EventArgs.Empty);
        }

        private void ApplyMatch(CatalogMatch match, TileView.TileKind kind)
        {
            CurrentMatch = match;
            _kind = kind;

            if (match == null)
            {
                ApplyEmptyState();
                return;
            }

            BackgroundColor = ColorForKind(kind);

            ApplyPoster(match);

            _pillBg.Show();
            _pillBg.BackgroundColor = AccentForKind(kind);
            _pillLabel.Text = PillTextForKind(kind, match);
            _pillLabel.TextColor = kind == TileView.TileKind.Recent ? Tokens.TextSecondary : Color.White;

            _titleLabel.Text = string.IsNullOrWhiteSpace(match.Title) ? "Match" : match.Title;

            _subLabel.Show();
            _subLabel.Text = SubTextForMatch(match);

            _ctaBg.Show();
            // Intentionally NOT showing _hintLabel — Play CTA is the focus
            // target now, "PRESS OK" hint is permanently retired.
        }

        private void ApplyEmptyState()
        {
            CurrentMatch = null;
            BackgroundColor = Tokens.TileDefault;
            _titleLabel.Text = "No matches";
            _pillBg.Hide();
            _subLabel.Hide();
            _ctaBg.Hide();
            _hintLabel.Hide();
            _poster.ResourceUrl = "";
            _poster.Opacity = 0f;
            _scrim.Opacity = 0f;
        }

        // Cache of the last URL we asked for. The actual ImageView ResourceUrl
        // may have been set to a local cached path derived from this URL —
        // we still compare against the SOURCE url so re-applies for the same
        // logical poster short-circuit cleanly.
        private string _lastPosterUrl;
        // Generation counter so a slow async load that comes back AFTER the
        // hero has moved on to a different match doesn't clobber the new
        // poster with the stale one.
        private int _posterLoadGeneration;

        private async void ApplyPoster(CatalogMatch match)
        {
            string url = ResolvePosterUrl(match);
            if (string.IsNullOrEmpty(url))
            {
                if (_lastPosterUrl != "")
                {
                    _poster.ResourceUrl = "";
                    _lastPosterUrl = "";
                }
                _poster.Opacity = 0f;
                _scrim.Opacity = 0f;
                return;
            }
            if (string.Equals(url, _lastPosterUrl, StringComparison.Ordinal))
            {
                _poster.Opacity = 1f;
                _scrim.Opacity = 1f;
                return;
            }
            _lastPosterUrl = url;
            int gen = ++_posterLoadGeneration;

            // Resolve to a local cached file BEFORE assigning ResourceUrl.
            // EnsureCachedAsync runs the network fetch off the UI thread —
            // assigning a remote URL directly hangs input on the first load.
            try
            {
                string local = StreamedTv.Services.PosterCache.TryGetLocalPath(url);
                if (local == null)
                {
                    local = await StreamedTv.Services.PosterCache.EnsureCachedAsync(url).ConfigureAwait(true);
                }
                if (gen != _posterLoadGeneration) return;  // hero moved on
                if (string.IsNullOrEmpty(local)) local = url;
                _poster.ResourceUrl = local;
                _poster.Opacity = 1f;
                _scrim.Opacity = 1f;
            }
            catch
            {
                // Hero could have been disposed mid-load; safe to swallow.
            }
        }

        // Tab-level override for the hero banner. When set (non-empty), every
        // ResolvePosterUrl call returns the bundled file at this name —
        // overriding the sport-bundled banner, the backend poster, and the
        // streamed.pk badge composites. Used by Program.cs to force the For You
        // collage banner whenever the For You tab is active. Static so it
        // applies to every HeroView call site equally; we only ever construct
        // one HeroView so the singleton storage is fine.
        private static string _forcedBundledHero;

        /// <summary>Force the hero to render a specific bundled asset
        /// (e.g. "hero_foryou.jpg") regardless of the focused match's sport.
        /// Pass null/empty to clear the override and resume per-match resolution.
        /// Caller is responsible for calling ApplyMatch / SetMatch again
        /// afterwards if the hero needs to immediately re-paint.</summary>
        public static void ForceBundledHero(string fileName)
        {
            _forcedBundledHero = string.IsNullOrWhiteSpace(fileName) ? null : fileName;
        }

        private static string ResolvePosterUrl(CatalogMatch match)
        {
            if (match == null) return null;

            // 0. Tab-level override — when the host calls ForceBundledHero
            //    (e.g. on the For You tab), that banner wins so the user sees
            //    a consistent collage banner across multi-sport aggregations.
            if (!string.IsNullOrEmpty(_forcedBundledHero))
            {
                string forced = ResolveBundledAssetPath(_forcedBundledHero);
                if (!string.IsNullOrEmpty(forced)) return forced;
            }

            // 1. Sport-specific bundled banner — cinematic hero art shipped
            //    with the app; always present for the active sports so there
            //    is never a blank card, and it's consistent per-sport tab.
            string bundled = ResolveBundledHeroForSport(match.Sport);
            if (!string.IsNullOrEmpty(bundled)) return bundled;

            // 2. Backend-generated poster (cron-produced cinematic art for the
            //    specific match).
            string backendPoster = StreamedTv.Services.BackendAssets.ResolvePosterUrl(match.Id);
            if (!string.IsNullOrEmpty(backendPoster)) return backendPoster;

            // 3. Match's own poster URL from streamed.pk.
            if (!string.IsNullOrWhiteSpace(match.Poster))
            {
                try { return StreamedTv.Services.StreamedEndpoints.ProxiedImage(match.Poster).ToString(); } catch { }
            }

            // 4. Backend-generated home team logo as final stand-in.
            string homeName = match.Teams?.Home?.Name;
            string backendLogo = StreamedTv.Services.BackendAssets.ResolveLogoUrl(homeName);
            if (!string.IsNullOrEmpty(backendLogo)) return backendLogo;

            // 5. streamed.pk composite badge (home + away team badges).
            string home = match.Teams?.Home?.Badge;
            string away = match.Teams?.Away?.Badge;
            if (!string.IsNullOrWhiteSpace(home) && !string.IsNullOrWhiteSpace(away))
                return StreamedTv.Services.StreamedEndpoints.MatchPoster(home, away).ToString();
            if (!string.IsNullOrWhiteSpace(home))
                return StreamedTv.Services.StreamedEndpoints.TeamBadge(home).ToString();

            return null;
        }

        // Map a sport name (as it comes from streamed.pk: "football", "cricket",
        // "basketball", etc.) to a bundled hero banner under shared/res/. Returns
        // an absolute file path, or null if no banner is shipped for that sport
        // OR the file doesn't exist on disk.
        private static string ResolveBundledHeroForSport(string sport)
        {
            if (string.IsNullOrWhiteSpace(sport)) return null;
            string fileName;
            switch (sport.Trim().ToLowerInvariant())
            {
                case "football":
                case "soccer":     fileName = "hero_football.jpg";   break;
                case "cricket":    fileName = "hero_cricket.jpg";    break;
                case "basketball": fileName = "hero_basketball.jpg"; break;
                default:           return null;
            }
            return ResolveBundledAssetPath(fileName);
        }

        /// <summary>Locate a bundled asset on disk by file name. Tries the
        /// standard Tizen .tpk layouts (resource dir, ../shared/res, AppContext
        /// base). Returns null if the file isn't found in any candidate.</summary>
        internal static string ResolveBundledAssetPath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            try
            {
                string resourceDir = Tizen.Applications.Application.Current?.DirectoryInfo?.Resource;
                if (string.IsNullOrEmpty(resourceDir)) return null;
                string[] candidates =
                {
                    System.IO.Path.Combine(resourceDir, fileName),
                    System.IO.Path.Combine(resourceDir, "..", "shared", "res", fileName),
                    System.IO.Path.Combine(System.AppContext.BaseDirectory, "shared", "res", fileName),
                    System.IO.Path.Combine(System.AppContext.BaseDirectory, fileName),
                };
                foreach (string p in candidates)
                {
                    try { if (System.IO.File.Exists(p)) return p; } catch { }
                }
            }
            catch
            {
            }
            return null;
        }

        private static Color ColorForKind(TileView.TileKind kind)
        {
            switch (kind)
            {
                case TileView.TileKind.Live:      return Tokens.TileLive;
                case TileView.TileKind.Scheduled: return Tokens.TileScheduled;
                case TileView.TileKind.Recent:    return Tokens.TileRecent;
                default:                          return Tokens.TileDefault;
            }
        }

        private static Color AccentForKind(TileView.TileKind kind)
        {
            switch (kind)
            {
                case TileView.TileKind.Live:      return Tokens.AccentLive;
                case TileView.TileKind.Scheduled: return Tokens.AccentScheduled;
                case TileView.TileKind.Recent:    return Tokens.AccentRecent;
                default:                          return Tokens.AccentRecent;
            }
        }

        private static string PillTextForKind(TileView.TileKind kind, CatalogMatch match)
        {
            switch (kind)
            {
                case TileView.TileKind.Live:      return "● LIVE";
                case TileView.TileKind.Scheduled: return ScheduledLabelForMatch(match);
                case TileView.TileKind.Recent:    return "FINAL";
                default:                          return "";
            }
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
            // The catalog mapper sets Competition = Category which is the sport
            // name (e.g. "football"). When that's the case there's nothing
            // meaningful to add on top of the focused tile's context, so
            // suppress it entirely. Real competition strings ("Premier League"
            // etc.) still surface. Same rule as TileView.SubTextForMatch.
            if (!string.IsNullOrWhiteSpace(match.Sport)
                && string.Equals(competition.Trim(), match.Sport.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }
            return competition;
        }
    }
}
