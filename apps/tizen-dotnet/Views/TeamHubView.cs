using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using StreamedTv.Services;
using StreamedTv.Theme;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace StreamedTv.Views
{
    /// <summary>
    /// Read-only "team page" modal opened from the StreamSwitcher's per-team chip.
    /// Renders the team's current league standing, recent form, and next fixture.
    ///
    /// Mirrors the cinematic design language used by StreamSwitcherView and
    /// MoreOverlayView:
    ///   - Full-screen 0.82 alpha dim backdrop
    ///   - Dark glass panel with heavy shadow + rounded 28px corners
    ///   - Header band: status pill + muted league line + big bold team title
    ///   - Hairline divider, then two cards (standings + next match) laid out
    ///     side-by-side
    ///   - Section headers in uppercase 16pt bold
    ///   - 11pt key-hint footer
    ///
    /// View is non-focusable: BACK closes it. There is no focus animation on the
    /// inner cards on purpose — this is a read-only surface.
    ///
    /// One-shot data fetch on Open: no background polling. Cancellation token is
    /// cycled per-open so a fast Open/Close/Open sequence cancels the in-flight
    /// request from the previous open.
    ///
    /// Load failure handling:
    ///   - On any backend miss we surface SportsDataService.LastError as a small
    ///     dim 11pt secondary line so QA can tell HTTP 404 vs network timeout vs
    ///     transport error apart.
    ///   - Lookup retries once with the slugified team name (the backend keys
    ///     other resources off slugs, so this catches name-format mismatches).
    ///   - Lookup miss vs data miss have distinct copy.
    /// </summary>
    public sealed class TeamHubView : View
    {
        // Cinematic panel sizing — matches StreamSwitcherView.
        private const int PanelCornerRadius = 28;
        private const int PanelPaddingX     = 56;
        private const int PanelPaddingTop   = 44;
        private const int PanelPaddingBottom = 36;
        private const int PanelWidth        = 1320;

        // Inner cards.
        private const int CardCornerRadius = 18;
        private const int CardPad          = 24;
        private const int CardGap          = 24;
        // Fixed card width so MatchParent TextLabels inside each card resolve to a
        // non-zero width. Weight=1f + WidthSpecification=0 causes NUI to skip
        // re-measuring grandchildren after weight distribution, leaving labels at 0px.
        private const int CardWidth  = (PanelWidth - 2 * PanelPaddingX - CardGap) / 2; // 592
        // Fixed card height so both cards are always the same size regardless of content.
        // Sized to fit the fuller card (next-match: header + opponent + kickoff +
        // competition + venue + padding). Both WrapContent alternatives collapse the shorter
        // card; LinearLayout.Alignment has no Fill member in the Tizen NUI 9 SDK.
        private const int CardHeight = 220;

        private const int FormDotSize = 22;
        private const int FormDotGap  = 8;

        // Tertiary diagnostic line on empty states ("HTTP 404 …").
        private const float DiagFontSize = 11f;

        private readonly SportsDataService _sports;

        // Header band.
        private readonly View      _panel;
        private readonly View      _headerPillWrap;
        private readonly TextLabel _headerPillLabel;
        private readonly TextLabel _headerLeagueLabel;
        // Team logo from the backend (SportsDataService.TeamLogoUrl). Sits to the
        // left of the big team-name title in the header band so the team has a
        // visual identity beyond just the wordmark. Hidden until LoadAsync sets
        // a ResourceUrl — if the backend doesn't have a logo for the team, the
        // image just stays blank and the team-name carries the header alone.
        private readonly ImageView _teamLogoView;
        // Initials shown beneath the avatar — always visible underneath the
        // ImageView, so a missing/404 backend logo still renders something
        // meaningful instead of an empty grey circle.
        private readonly TextLabel _teamLogoFallback;
        private readonly TextLabel _teamNameLabel;

        // Standings card.
        private readonly View      _standingsCard;
        private readonly TextLabel _standingsHeader;
        private readonly TextLabel _statsLabel;
        private readonly TextLabel _formCaption;
        private readonly View      _formDotRow;

        // Next match card.
        private readonly View      _nextMatchCard;
        private readonly TextLabel _nextMatchHeader;
        private readonly TextLabel _opponentLabel;
        private readonly TextLabel _kickoffLabel;
        private readonly TextLabel _competitionLabel;
        private readonly TextLabel _venueLabel;

        private CancellationTokenSource _loadCts;
        private string _currentTeamName;
        private string _currentBadgeId;

        public bool IsOpen { get; private set; }
        public string CurrentTeamName { get { return _currentTeamName; } }

        public event EventHandler Closed;

        public TeamHubView(SportsDataService sports)
        {
            _sports = sports;

            // Full-screen dim backdrop (matches StreamSwitcher).
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.FillToParent;
            BackgroundColor = new Color(0f, 0f, 0f, 0.82f);

            Layout = new LinearLayout
            {
                LinearOrientation = LinearLayout.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // Cinematic glass panel — matches StreamSwitcherView.
            _panel = new View
            {
                BackgroundColor = new Color(0.045f, 0.045f, 0.06f, 0.98f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.14f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(PanelCornerRadius, PanelCornerRadius, PanelCornerRadius, PanelCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                BoxShadow = new Shadow(60f, new Color(0f, 0f, 0f, 0.7f), new Vector2(0f, 24f), new Vector2(0f, 0f)),
                WidthSpecification = PanelWidth,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    LinearAlignment = LinearLayout.Alignment.Top,
                    CellPadding = new Size2D(0, 16),
                    Padding = new Extents(
                        (ushort)PanelPaddingX, (ushort)PanelPaddingX,
                        (ushort)PanelPaddingTop, (ushort)PanelPaddingBottom),
                },
            };
            Add(_panel);

            // ---- Header band ------------------------------------------------
            // Pill row: [TEAM] · league name.
            View pillRow = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.CenterVertical,
                    CellPadding = new Size2D(14, 0),
                },
            };
            _panel.Add(pillRow);

            _headerPillWrap = new View
            {
                BackgroundColor = Tokens.AccentLive,
                CornerRadius = new Vector4(8f, 8f, 8f, 8f),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.Center,
                    Padding = new Extents(14, 14, 6, 6),
                },
            };
            _headerPillLabel = new TextLabel
            {
                Text = "TEAM",
                TextColor = Color.White,
                PointSize = 11f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _headerPillWrap.Add(_headerPillLabel);
            pillRow.Add(_headerPillWrap);

            _headerLeagueLabel = new TextLabel
            {
                Text = "League Standing",
                TextColor = Tokens.TextSecondary,
                PointSize = 14f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("medium")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            pillRow.Add(_headerLeagueLabel);

            // Header team identity row: avatar (backend logo with team-initials
            // fallback) on the left, team name on the right. The avatar is a
            // wrapper that always shows initials underneath; the ImageView
            // sits on top with Opacity=0 and only fades in when ResourceReady
            // fires successfully. So if the backend doesn't have a logo for
            // this team (404), the initials remain visible — never an empty
            // grey square.
            View teamRow = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Margin = new Extents(0, 0, 4, 6),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.CenterVertical,
                    CellPadding = new Size2D(18, 0),
                },
            };

            // Avatar wrapper. NO Layout so its children z-stack (initials at
            // the back, ImageView on top). Circle via CornerRadius 32 on a
            // 64×64 box. ClipChildren so a non-square image is masked to the
            // circle silhouette.
            View avatar = new View
            {
                WidthSpecification = 64,
                HeightSpecification = 64,
                BackgroundColor = new Color(0.18f, 0.18f, 0.22f, 1f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.16f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(32f, 32f, 32f, 32f),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                ClippingMode = ClippingModeType.ClipChildren,
            };
            _teamLogoFallback = new TextLabel
            {
                Text = "",
                TextColor = Tokens.TextPrimary,
                PointSize = 22f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.FillToParent,
            };
            avatar.Add(_teamLogoFallback);
            _teamLogoView = new ImageView
            {
                ResourceUrl = "",
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.FillToParent,
                FittingMode = FittingModeType.ScaleToFill,
                Opacity = 0f,
            };
            // ResourceReady fires whether the load succeeded or failed; check
            // LoadingStatus to distinguish. On success, fade the image in over
            // 200ms covering the initials; on failure, leave Opacity at 0 so
            // the initials stay readable.
            _teamLogoView.ResourceReady += (s, e) =>
            {
                try
                {
                    if (_teamLogoView.LoadingStatus == ImageView.LoadingStatusType.Ready)
                    {
                        Animation a = new Animation(200);
                        a.AnimateTo(_teamLogoView, "Opacity", 1f,
                            new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
                        a.Play();
                    }
                }
                catch { }
            };
            avatar.Add(_teamLogoView);
            teamRow.Add(avatar);

            _teamNameLabel = new TextLabel
            {
                Text = "",
                TextColor = Tokens.TextPrimary,
                PointSize = 30f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                Ellipsis = true,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            teamRow.Add(_teamNameLabel);
            _panel.Add(teamRow);

            // Hairline divider so the cards section reads as a separate block.
            View headerDivider = new View
            {
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = 1,
                BackgroundColor = Tokens.Divider,
                Margin = new Extents(0, 0, 8, 12),
            };
            _panel.Add(headerDivider);

            // ---- Cards row --------------------------------------------------
            // Horizontal row holding the standings + next-match cards. The two
            // cards share equal width (Weight = 1f) so they fill the panel.
            View cardsRow = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.Top,
                    CellPadding = new Size2D(CardGap, 0),
                },
            };
            _panel.Add(cardsRow);

            // ---- Standings card ---------------------------------------------
            _standingsCard = BuildCard();
            cardsRow.Add(_standingsCard);

            _standingsHeader = new TextLabel
            {
                Text = "Standings",
                TextColor = Tokens.TextPrimary,
                PointSize = 16f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Margin = new Extents(0, 0, 0, 10),
            };
            _standingsCard.Add(_standingsHeader);

            // ---- Standings body — direct card children ----------------------
            // TextLabels added directly to the card (same level as _standingsHeader)
            // so the Weight=1 layout resolves their width correctly.
            _statsLabel = new TextLabel
            {
                Text = "—",
                TextColor = Tokens.TextPrimary,
                PointSize = 18f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("semibold")),
                MultiLine = false,
                Ellipsis = true,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _standingsCard.Add(_statsLabel);

            _formCaption = new TextLabel
            {
                Text = "Recent form",
                TextColor = Tokens.TextTertiary,
                PointSize = Dimens.FontTileSub,
                FontFamily = Tokens.FontFamily,
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _standingsCard.Add(_formCaption);

            _formDotRow = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.CenterVertical,
                    CellPadding = new Size2D(FormDotGap, 0),
                },
            };
            _standingsCard.Add(_formDotRow);

            // ---- Next match card --------------------------------------------
            _nextMatchCard = BuildCard();
            cardsRow.Add(_nextMatchCard);

            _nextMatchHeader = new TextLabel
            {
                Text = "Next match",
                TextColor = Tokens.TextPrimary,
                PointSize = 16f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Margin = new Extents(0, 0, 0, 10),
            };
            _nextMatchCard.Add(_nextMatchHeader);

            // ---- Next match body — direct card children ----------------------
            _opponentLabel = new TextLabel
            {
                Text = "",
                TextColor = Tokens.TextPrimary,
                PointSize = Dimens.FontHeroSub,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                Ellipsis = true,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _nextMatchCard.Add(_opponentLabel);

            _kickoffLabel = new TextLabel
            {
                Text = "",
                TextColor = Tokens.TextSecondary,
                PointSize = Dimens.FontTileTitle,
                FontFamily = Tokens.FontFamily,
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _nextMatchCard.Add(_kickoffLabel);

            _competitionLabel = new TextLabel
            {
                Text = "",
                TextColor = Tokens.TextTertiary,
                PointSize = Dimens.FontTileSub,
                FontFamily = Tokens.FontFamily,
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _nextMatchCard.Add(_competitionLabel);

            _venueLabel = new TextLabel
            {
                Text = "",
                TextColor = Tokens.TextTertiary,
                PointSize = Dimens.FontTileSub,
                FontFamily = Tokens.FontFamily,
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _nextMatchCard.Add(_venueLabel);

            // ---- Footer key hint -------------------------------------------
            TextLabel hint = new TextLabel
            {
                Text = "BACK to return",
                TextColor = Tokens.TextTertiary,
                PointSize = 11f,
                FontFamily = Tokens.FontFamily,
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Extents(0, 0, 18, 0),
            };
            _panel.Add(hint);

            Opacity = 1f;
            Hide();
        }

        /// <summary>Construct one of the two glass-style cards inside the panel. Cards have
        /// the same vertical LinearLayout arrangement (header + content/empty rows). The
        /// containing row applies an equal Weight so each card fills half of the panel.</summary>
        private View BuildCard()
        {
            View card = new View
            {
                BackgroundColor = new Color(0.10f, 0.10f, 0.12f, 1f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.20f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(CardCornerRadius, CardCornerRadius, CardCornerRadius, CardCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                WidthSpecification  = CardWidth,
                HeightSpecification = CardHeight,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    LinearAlignment = LinearLayout.Alignment.Top,
                    CellPadding = new Size2D(0, 10),
                    Padding = new Extents(
                        (ushort)CardPad, (ushort)CardPad,
                        (ushort)CardPad, (ushort)CardPad),
                },
            };
            return card;
        }

        /// <summary>Open the modal for the given team name. Cancels any in-flight load from a
        /// previous open, swaps the panel to the loading state, animates in, and kicks off the
        /// async data fetch on a background task.</summary>
        public void OpenForTeam(string teamName, string badgeId = null)
        {
            if (string.IsNullOrWhiteSpace(teamName)) return;
            _currentTeamName = teamName;
            _currentBadgeId = badgeId;

            // Reset state to loading.
            _teamNameLabel.Text = teamName;
            // Reset the avatar: clear any logo from the previous team and
            // populate the initials fallback immediately so the circle never
            // appears empty even before the lookup resolves.
            _teamLogoView.ResourceUrl = "";
            _teamLogoView.Opacity = 0f;
            _teamLogoFallback.Text = ComputeTeamInitials(teamName);
            // If we have a streamed.pk badge id (passed by the caller from the
            // catalog payload), kick off the badge load IMMEDIATELY — no need
            // to wait for the backend team-lookup roundtrip. The initials stay
            // visible underneath until ResourceReady fires successfully.
            if (!string.IsNullOrWhiteSpace(badgeId))
            {
                try
                {
                    string badgeUrl = StreamedTv.Services.StreamedEndpoints.TeamBadge(badgeId).ToString();
                    string cached = StreamedTv.Services.PosterCache.TryGetLocalPath(badgeUrl);
                    _teamLogoView.ResourceUrl = cached ?? badgeUrl;
                    if (cached == null)
                    {
                        _ = StreamedTv.Services.PosterCache.EnsureCachedAsync(badgeUrl);
                    }
                }
                catch { }
            }
            _headerLeagueLabel.Text = "Loading…";
            _headerPillLabel.Text = "TEAM";
            _headerPillWrap.BackgroundColor = Tokens.AccentLive;

            _statsLabel.Text = "Loading…";
            _formCaption.Hide();
            _formDotRow.Hide();
            _opponentLabel.Text = "Loading…";
            _kickoffLabel.Text = "";
            _competitionLabel.Hide();
            _venueLabel.Hide();
            ClearFormDots();

            // Cancel previous load if any.
            CancellationTokenSource old = _loadCts;
            _loadCts = new CancellationTokenSource();
            try { old?.Cancel(); old?.Dispose(); } catch { }

            if (!IsOpen)
            {
                IsOpen = true;
                Opacity = 1f;
                Show();
                RaiseToTop();
            }

            // Kick off fetch on background task. We don't await; the continuation
            // marshals back to the UI thread before mutating any view.
            CancellationToken ct = _loadCts.Token;
            string snapshotName = teamName;
            _ = LoadAsync(snapshotName, ct);
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;

            try { _loadCts?.Cancel(); } catch { }

            Animation a = new Animation(Dimens.AnimFocusScale);
            a.AnimateTo(this, "Opacity", 0f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseIn));
            a.Finished += (s, e) => Hide();
            a.Play();
            Closed?.Invoke(this, EventArgs.Empty);
        }

        private async Task LoadAsync(string teamName, CancellationToken ct)
        {
            try
            {
                if (_sports == null)
                {
                    Post(() => RenderEmptyAll("Backend not configured.", ""));
                    return;
                }

                // Step 1: lookup team -> teamId.
                // First try the raw name as the user gave it. If that misses
                // (null, or non-null but empty TeamId), retry once with the
                // slugified form — the backend keys other resources off slugs,
                // so this catches name-format mismatches like "Man Utd" vs
                // "manchester-united".
                TeamLookup lookup = await _sports.LookupTeamAsync(teamName, ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested) return;

                if (lookup == null || string.IsNullOrEmpty(lookup.TeamId))
                {
                    string slug = SportsDataService.Slugify(teamName);
                    if (!string.IsNullOrEmpty(slug) && !string.Equals(slug, teamName, StringComparison.OrdinalIgnoreCase))
                    {
                        TeamLookup retry = await _sports.LookupTeamAsync(slug, ct).ConfigureAwait(true);
                        if (ct.IsCancellationRequested) return;
                        if (retry != null && !string.IsNullOrEmpty(retry.TeamId))
                        {
                            lookup = retry;
                        }
                    }
                }

                if (lookup == null || string.IsNullOrEmpty(lookup.TeamId))
                {
                    string diag = _sports.LastError ?? "";
                    Post(() => RenderEmptyAll("Team not in our directory", diag));
                    return;
                }

                // Update the header band with the resolved league + canonical name.
                string leagueLabel = string.IsNullOrEmpty(lookup.League)
                    ? "League Standing"
                    : lookup.League;
                string canonical = string.IsNullOrEmpty(lookup.CanonicalName) ? teamName : lookup.CanonicalName;
                // Resolve the logo to show in the header avatar. If the caller
                // already provided a streamed.pk badge id, keep it (it was set
                // immediately in OpenForTeam). Only fall back to the backend
                // logo when there is no badge — that way the stream's own team
                // art takes priority over the cron-generated backend PNG.
                string logoUrl = string.IsNullOrWhiteSpace(_currentBadgeId)
                    ? (_sports.TeamLogoUrl(canonical) ?? "")
                    : null; // badge already loaded in OpenForTeam — don't overwrite
                string initials = ComputeTeamInitials(canonical);
                Post(() =>
                {
                    _teamNameLabel.Text = canonical;
                    _headerLeagueLabel.Text = leagueLabel;
                    _teamLogoFallback.Text = initials;
                    // Only update the logo if we don't already have a streamed.pk
                    // badge loaded (logoUrl == null means badge was set in OpenForTeam).
                    if (logoUrl != null)
                    {
                        _teamLogoView.Opacity = 0f;
                        _teamLogoView.ResourceUrl = logoUrl;
                    }
                });

                // Step 2: fetch standings + next match in parallel. Capture the
                // exception message (if any) so the empty-state can render
                // "Standings unavailable — {message}" in the dim diagnostic line.
                // OperationCanceledException MUST bubble out — that's the modal
                // closing mid-fetch, not a real failure.
                //
                // ConfigureAwait(true) here is LOAD-BEARING: the continuation
                // calls Post() which captures SynchronizationContext.Current.
                // With ConfigureAwait(false) we'd resume on a worker thread,
                // SynchronizationContext.Current would be null, Post() would
                // fall through to running RenderStandings inline on the worker
                // thread, NUI mutation off-main-thread silently wedges the
                // renderer (per project memory) → stats card stuck on
                // "Loading…" forever.
                Task<TeamStanding> standingsTask = _sports.GetTeamStandingsAsync(lookup.TeamId, ct);
                Task<NextMatch> nextTask = _sports.GetTeamNextMatchAsync(lookup.TeamId, ct);

                TeamStanding standings = null;
                string standingsErr = null;
                try
                {
                    standings = await standingsTask.ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    standingsErr = ex.Message;
                }
                if (ct.IsCancellationRequested) return;

                NextMatch next = null;
                string nextErr = null;
                try
                {
                    next = await nextTask.ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    nextErr = ex.Message;
                }
                if (ct.IsCancellationRequested) return;

                // Capture sport service's last error AFTER both calls so we don't
                // miss a non-2xx response that returned null without throwing.
                string standingsLastError = standings == null ? (standingsErr ?? _sports.LastError ?? "") : "";
                string nextLastError = next == null ? (nextErr ?? _sports.LastError ?? "") : "";

                Post(() =>
                {
                    RenderStandings(standings, standingsLastError);
                    RenderNextMatch(next, nextLastError);
                });
            }
            catch (OperationCanceledException)
            {
                // Expected if the modal closed mid-fetch.
            }
            catch (Exception ex)
            {
                try { Diagnostics.LogError("TeamHubView.LoadAsync", ex); } catch { }
                string msg = ex.Message;
                Post(() => RenderEmptyAll("Couldn't load team data", msg));
            }
        }

        /// <summary>Marshal a UI mutation back onto the main thread. NUIApplication runs the
        /// scene on a single thread; touching views from a background continuation can crash
        /// or silently corrupt layout. Posting via the main thread synchronization context is
        /// the simplest cross-platform way to bounce back.</summary>
        /// <summary>Compute up to 2 uppercase initials from a team name. "Manchester
        /// United" → "MU"; "Liverpool" → "LI"; "Mumbai Indians" → "MI". Used as the
        /// avatar fallback when the backend doesn't have a logo PNG for the team.</summary>
        private static string ComputeTeamInitials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string[] parts = name.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "";
            if (parts.Length == 1)
            {
                string p = parts[0];
                return (p.Length >= 2 ? p.Substring(0, 2) : p).ToUpperInvariant();
            }
            return (parts[0][0].ToString() + parts[parts.Length - 1][0]).ToUpperInvariant();
        }

        private static void Post(Action a)
        {
            if (a == null) return;
            SynchronizationContext ctx = SynchronizationContext.Current;
            if (ctx != null)
            {
                ctx.Post(_ => { try { a(); } catch (Exception ex) { try { Diagnostics.LogError("TeamHubView.Post", ex); } catch { } } }, null);
            }
            else
            {
                try { a(); } catch (Exception ex) { try { Diagnostics.LogError("TeamHubView.Post(direct)", ex); } catch { } }
            }
        }

        /// <summary>Render the same empty headline + diagnostic in BOTH cards. Used for
        /// "couldn't even resolve the team" failure modes (lookup missed, backend
        /// not configured, or load threw an unhandled exception).</summary>
        private void RenderEmptyAll(string headline, string diag)
        {
            // Standings card — put message directly in body label.
            _statsLabel.Text = string.IsNullOrEmpty(diag) ? headline : headline + "\n" + diag;
            _formCaption.Hide();
            _formDotRow.Hide();
            ClearFormDots();

            // Next match card.
            _opponentLabel.Text = headline;
            _kickoffLabel.Text = "";
            _competitionLabel.Hide();
            _venueLabel.Hide();
        }

        private void RenderStandings(TeamStanding s, string lastError)
        {
            bool hasData = s != null && (
                s.Position > 0 || s.Played > 0 || s.Wins > 0 || s.Points > 0 ||
                s.Losses > 0 || s.Draws > 0 ||
                (s.RecentForm != null && s.RecentForm.Count > 0) ||
                !string.IsNullOrEmpty(s.League));
            if (!hasData)
            {
                string msg = "Standings unavailable";
                if (!string.IsNullOrEmpty(lastError)) msg += "  ·  " + lastError;
                _statsLabel.Text = msg;
                _formCaption.Hide();
                _formDotRow.Hide();
                ClearFormDots();
                return;
            }
            string posPrefix = s.Position > 0 ? OrdinalLabel(s.Position) + "  ·  " : "";
            _statsLabel.Text = s.Played > 0
                ? posPrefix + s.Wins + "W-" + s.Draws + "D-" + s.Losses + "L  ·  " + s.Points + " pts"
                : s.Position > 0 ? OrdinalLabel(s.Position) : "Season stats unavailable";

            ClearFormDots();
            if (s.RecentForm != null && s.RecentForm.Count > 0)
            {
                _formCaption.Show();
                _formDotRow.Show();
                foreach (string token in s.RecentForm)
                {
                    _formDotRow.Add(BuildFormDot(token));
                }
            }
            else
            {
                _formCaption.Hide();
                _formDotRow.Hide();
            }
        }

        private void RenderNextMatch(NextMatch n, string lastError)
        {
            bool hasContent = n != null
                && ((n.Opponent != null && !string.IsNullOrEmpty(n.Opponent.Name))
                    || !string.IsNullOrEmpty(n.Kickoff)
                    || !string.IsNullOrEmpty(n.Competition));
            if (!hasContent)
            {
                _opponentLabel.Text = "No upcoming fixture scheduled";
                _kickoffLabel.Text = "";
                _competitionLabel.Hide();
                _venueLabel.Hide();
                return;
            }

            string opponentName = n.Opponent != null && !string.IsNullOrEmpty(n.Opponent.Name) ? n.Opponent.Name : "TBD";
            _opponentLabel.Text = "vs " + opponentName;
            _kickoffLabel.Text = string.IsNullOrEmpty(n.Kickoff) ? "Kickoff TBD" : FormatKickoff(n.Kickoff);
            _competitionLabel.Text = string.IsNullOrEmpty(n.Competition) ? "" : n.Competition;
            _venueLabel.Text = string.IsNullOrEmpty(n.Venue) ? "" : "at " + n.Venue;
            // Hide the secondary lines when empty so we don't leave dead space.
            if (string.IsNullOrEmpty(n.Competition)) _competitionLabel.Hide(); else _competitionLabel.Show();
            if (string.IsNullOrEmpty(n.Venue)) _venueLabel.Hide(); else _venueLabel.Show();
        }

        /// <summary>Render a single colored circle representing a W/D/L in the recent-form list.
        /// W → AccentScheduled (the "go" electric blue), D → muted gray, L → AccentLive (the
        /// brand crimson). Circles are 22px so they read clearly from across a TV room.</summary>
        private static View BuildFormDot(string token)
        {
            char c = string.IsNullOrEmpty(token) ? '?' : char.ToUpperInvariant(token[0]);
            Color fill;
            switch (c)
            {
                case 'W':
                    fill = Tokens.AccentScheduled;
                    break;
                case 'L':
                    fill = Tokens.AccentLive;
                    break;
                case 'D':
                    fill = new Color(1f, 1f, 1f, 0.45f);
                    break;
                default:
                    fill = new Color(1f, 1f, 1f, 0.30f);
                    break;
            }
            View dot = new View
            {
                BackgroundColor = fill,
                CornerRadius = new Vector4(FormDotSize / 2f, FormDotSize / 2f, FormDotSize / 2f, FormDotSize / 2f),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                WidthSpecification = FormDotSize,
                HeightSpecification = FormDotSize,
            };
            return dot;
        }

        private void ClearFormDots()
        {
            uint count = _formDotRow.ChildCount;
            for (uint i = 0; i < count; i++)
            {
                _formDotRow.Remove(_formDotRow.GetChildAt(0));
            }
        }

        private static string OrdinalLabel(int position)
        {
            if (position <= 0) return "—";
            int mod100 = position % 100;
            int mod10 = position % 10;
            string suffix;
            if (mod100 >= 11 && mod100 <= 13) suffix = "th";
            else if (mod10 == 1) suffix = "st";
            else if (mod10 == 2) suffix = "nd";
            else if (mod10 == 3) suffix = "rd";
            else suffix = "th";
            return position.ToString(CultureInfo.InvariantCulture) + suffix;
        }

        /// <summary>Backend kickoff comes through as an ISO-8601 string; we want a friendly
        /// "Sat 10 May · 19:30" rendering for the TV. Falls back to the raw string if it can't
        /// be parsed (so we don't lose information when the backend sends a non-standard format).</summary>
        private static string FormatKickoff(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dto))
            {
                DateTime local = dto.ToLocalTime().DateTime;
                return local.ToString("ddd dd MMM · HH:mm", CultureInfo.InvariantCulture);
            }
            return raw;
        }
    }
}
