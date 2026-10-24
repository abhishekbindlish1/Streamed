using System;
using System.Collections.Generic;
using StreamedTv.Models;
using StreamedTv.Theme;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace StreamedTv.Views
{
    public sealed class StreamSelectedEventArgs : EventArgs
    {
        public StreamOption Stream { get; }
        public StreamSelectedEventArgs(StreamOption stream) { Stream = stream; }
    }

    public sealed class TeamFavoriteToggleEventArgs : EventArgs
    {
        public string TeamName { get; }
        public TeamFavoriteToggleEventArgs(string teamName) { TeamName = teamName; }
    }

    public sealed class TeamHubRequestedEventArgs : EventArgs
    {
        public string TeamName { get; }
        // streamed.pk badge id (from `match.teams.home.badge` /
        // `match.teams.away.badge` in the catalog payload). Lets the host
        // immediately load the badge from /api/images/badge/{id}.webp without
        // waiting for the backend's team-lookup roundtrip.
        public string BadgeId { get; }
        public TeamHubRequestedEventArgs(string teamName, string badgeId = null)
        {
            TeamName = teamName;
            BadgeId = badgeId;
        }
    }

    /// <summary>
    /// Pre-play stream picker. Cinematic full-screen modal that shares the
    /// home-page design language: rounded cards, vibrant accents, big bold
    /// typography, scale-on-focus.
    ///
    /// Vertical structure inside the panel:
    ///   1. Match meta header — [● LIVE] [competition] + match title (huge)
    ///   2. "PICK A STREAM" section header
    ///   3. Horizontal row of stream cards (220×150 each, vertical layout)
    ///   4. "TEAMS" section header
    ///   5. Horizontal row of team chips (heart toggle + view-team for each side)
    ///   6. Subtle keyboard hint footer
    ///
    /// Navigation:
    ///   ◀ ▶ moves within the active row
    ///   ▲ ▼ moves between streams row and teams row
    ///   OK plays the focused stream / toggles favorite / opens team hub
    ///   BACK closes
    /// </summary>
    public sealed class StreamSwitcherView : View
    {
        // Visual sizing — scaled to feel like a designed modal, not a popover.
        private const int PanelCornerRadius = 28;
        private const int PanelPaddingX = 56;
        private const int PanelPaddingTop = 44;
        private const int PanelPaddingBottom = 36;

        // Stream cards. Vertical layout inside each card so source/quality/lang
        // stack like a poster — same shape language as the home-page tiles.
        private const int StreamCardWidth = 240;
        private const int StreamCardHeight = 168;
        private const int StreamCardCornerRadius = 20;

        private const int ChipCornerRadius = 22;
        // Bumped to match the app-wide chip sizing (NavPill chips use 38/16 +
        // FontChip=18). The previous 22/14 + 14pt felt cramped next to the
        // 22pt team-name header above and the much larger nav-pill chrome.
        private const int ChipHorizontalPad = 32;
        private const int ChipVerticalPad   = 18;
        private const int ChipGap = 16;

        private const int StreamRowIndex = 0;
        private const int FavoritesRowIndex = 1;

        // Layout pieces
        private readonly View _panel;
        private readonly View _metaPillWrap;
        private readonly TextLabel _metaPillLabel;
        private readonly TextLabel _metaCompetition;
        private readonly TextLabel _matchTitle;
        private readonly View _chipRow;
        private readonly TextLabel _emptyLabel;
        private readonly TextLabel _favHeader;
        private readonly TextLabel _streamsHeader;
        private readonly View _favRow;

        // Stream state
        private readonly List<StreamOption> _streams = new List<StreamOption>();
        private readonly List<View> _chips = new List<View>();
        private readonly List<TextLabel> _chipSourceLabels = new List<TextLabel>();
        private readonly List<View> _chipQualityPills = new List<View>();
        private readonly List<TextLabel> _chipLangLabels = new List<TextLabel>();
        private readonly List<View> _chipNowBadges = new List<View>();

        // Team state — heart chip + view chip pair per team. Index 0/1 = team A
        // (heart, view), 2/3 = team B (heart, view).
        private readonly List<string> _favTeamNames = new List<string>();
        // teamName → streamed.pk badge id; populated in SetMatch so the
        // TeamHubRequested event handler can pass the id to TeamHubView.
        private Dictionary<string, string> _favTeamBadgesByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<FavChipKind> _favChipKinds = new List<FavChipKind>();
        private readonly List<View> _favChips = new List<View>();
        private readonly List<TextLabel> _favChipLabels = new List<TextLabel>();

        private enum FavChipKind { ToggleFavorite, ViewTeam }

        // Per-card scale animations so rapid Left/Right interrupts don't stack.
        private readonly Dictionary<View, Animation> _chipScaleAnims = new Dictionary<View, Animation>();

        private int _focusedIndex = -1;
        private int _activeRow = StreamRowIndex;
        private int _streamFocusedIndex = -1;
        private int _favFocusedIndex = 0;

        public bool IsOpen { get; private set; }
        public int FocusedIndex { get { return _focusedIndex; } }
        public int StreamCount { get { return _streams.Count; } }
        public StreamOption CurrentStream { get; private set; }

        public event EventHandler<StreamSelectedEventArgs> StreamSelected;
        public event EventHandler<TeamFavoriteToggleEventArgs> TeamFavoriteToggled;
        public event EventHandler<TeamHubRequestedEventArgs> TeamHubRequested;
        public event EventHandler Closed;

        public StreamSwitcherView()
        {
            // Full-screen dim backdrop.
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.FillToParent;
            BackgroundColor = new Color(0f, 0f, 0f, 0.82f);

            Layout = new LinearLayout
            {
                LinearOrientation = LinearLayout.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // Glass panel — fixed % of the screen so a match with many streams
            // doesn't blow up the modal beyond what the user can see. Earlier
            // WrapContent + a long stream row sized the panel wider than the
            // window. Cap to 80% of window width and 78% of window height; the
            // streams row inside is independently clipped (see _chipRow construction).
            Size2D windowSize = Window.Instance.WindowSize;
            int panelW = (int)(windowSize.Width * 0.80f);
            int panelH = (int)(windowSize.Height * 0.78f);
            _panel = new View
            {
                BackgroundColor = new Color(0.045f, 0.045f, 0.06f, 0.98f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.14f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(PanelCornerRadius, PanelCornerRadius, PanelCornerRadius, PanelCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                BoxShadow = new Shadow(60f, new Color(0f, 0f, 0f, 0.7f), new Vector2(0f, 24f), new Vector2(0f, 0f)),
                WidthSpecification = panelW,
                HeightSpecification = panelH,
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

            // ---- Match meta header ------------------------------------------
            // Pill row: [● LIVE] · Competition.
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

            _metaPillWrap = new View
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
            _metaPillLabel = new TextLabel
            {
                Text = "● LIVE",
                TextColor = Color.White,
                PointSize = 11f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _metaPillWrap.Add(_metaPillLabel);
            pillRow.Add(_metaPillWrap);

            _metaCompetition = new TextLabel
            {
                Text = "",
                TextColor = Tokens.TextSecondary,
                PointSize = 14f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("medium")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            pillRow.Add(_metaCompetition);

            _matchTitle = new TextLabel
            {
                Text = "Pick a stream",
                TextColor = Tokens.TextPrimary,
                PointSize = 30f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Margin = new Extents(0, 0, 4, 6),
            };
            _panel.Add(_matchTitle);

            // Hairline divider so the streams section reads as a separate block.
            View headerDivider = new View
            {
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = 1,
                BackgroundColor = Tokens.Divider,
                Margin = new Extents(0, 0, 8, 12),
            };
            _panel.Add(headerDivider);

            // ---- Streams section --------------------------------------------
            _streamsHeader = new TextLabel
            {
                Text = "Pick a stream",
                TextColor = Tokens.TextPrimary,
                PointSize = 16f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _panel.Add(_streamsHeader);

            // Chip row: full panel-width. NO ClippingMode — clipping rectangularly
            // cuts the borders of the focused card when its 1.06 scale lifts it
            // past the row bounds (same trap the home-page tile rows had). The
            // visible-stream cap below sizes the row so it fits inside the panel
            // naturally, no clipping needed.
            _chipRow = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.CenterVertical,
                    CellPadding = new Size2D(ChipGap, 0),
                },
            };
            _panel.Add(_chipRow);

            _emptyLabel = new TextLabel
            {
                Text = "No streams available for this match.",
                TextColor = Tokens.TextSecondary,
                PointSize = 14f,
                FontFamily = Tokens.FontFamily,
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _emptyLabel.Hide();
            _panel.Add(_emptyLabel);

            // ---- Teams section ----------------------------------------------
            _favHeader = new TextLabel
            {
                Text = "Teams",
                TextColor = Tokens.TextPrimary,
                PointSize = 16f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Margin = new Extents(0, 0, 14, 0),
            };
            _favHeader.Hide();
            _panel.Add(_favHeader);

            _favRow = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.Begin,
                    // Wider than the inner per-team gap (10px) so the two team
                    // groups read as visually separate blocks, not a flat row.
                    CellPadding = new Size2D(48, 0),
                },
            };
            _favRow.Hide();
            _panel.Add(_favRow);

            // Subtle key-hint footer.
            TextLabel hint = new TextLabel
            {
                Text = "◀ ▶ choose · ▲ ▼ jump rows · OK select · BACK to cancel",
                TextColor = Tokens.TextTertiary,
                PointSize = 11f,
                FontFamily = Tokens.FontFamily,
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Margin = new Extents(0, 0, 18, 0),
            };
            _panel.Add(hint);

            Opacity = 0f;
            Hide();
        }

        /// <summary>Show a transient placeholder message inside the chip area
        /// (e.g. "Loading streams…") so the host can call Open() synchronously
        /// before the async stream fetch completes. Without this, OK on a tile
        /// fired the fetch but the picker stayed invisible until the HTTP
        /// response landed — and any Right keystroke during that window would
        /// race into the picker the moment Open() ran, advancing the focus
        /// from 0 to 1 unexpectedly. With the loading state the picker is up
        /// instantly; navigation keys safely no-op until streams arrive.</summary>
        public void ShowLoadingMessage(string text)
        {
            if (_emptyLabel == null) return;
            ClearChildren(_chipRow);
            _streams.Clear();
            _chips.Clear();
            _chipSourceLabels.Clear();
            _chipQualityPills.Clear();
            _chipLangLabels.Clear();
            _chipNowBadges.Clear();
            _focusedIndex = -1;
            _streamFocusedIndex = -1;
            _activeRow = StreamRowIndex;
            _emptyLabel.Text = text ?? "Loading streams…";
            _emptyLabel.Show();
        }

        public void SetStreams(IReadOnlyList<StreamOption> streams, StreamOption currentStream)
        {
            // Snap the modal to full opacity. The fix for the "Loading streams…
            // stuck until you press an arrow" bug — when the network fetch
            // returned faster than the 200ms Open() fade-in, NUI sometimes
            // failed to render newly-added chip cards added during the
            // animation. Stopping the animation + setting Opacity = 1
            // unconditionally guarantees the next frame paints the new chips.
            _modalOpacityAnim?.Stop();
            Opacity = 1f;

            _streams.Clear();
            _chips.Clear();
            _chipSourceLabels.Clear();
            _chipQualityPills.Clear();
            _chipLangLabels.Clear();
            _chipNowBadges.Clear();
            ClearChildren(_chipRow);
            CurrentStream = currentStream;

            if (streams == null || streams.Count == 0)
            {
                _emptyLabel.Show();
                _focusedIndex = -1;
                return;
            }
            _emptyLabel.Hide();

            // Cap visible cards. Math: panel inner width = panelW − 2×PanelPaddingX.
            // For an 80%-of-1920 panel that's 1536 − 112 = 1424 px. Each card +
            // gap costs (StreamCardWidth+ChipGap)=256 px. 5 cards take 5×240 +
            // 4×16 = 1264 px → 160 px of headroom for the focused card's 1.06
            // scale lift. 6 cards would push past the panel and force the
            // rectangle-clipping fallback that just bit us on the rightmost
            // tile's border, so 5 is the safe ceiling.
            const int MaxStreamsShown = 5;
            int visible = Math.Min(streams.Count, MaxStreamsShown);

            int currentIndex = 0;
            for (int i = 0; i < visible; i++)
            {
                StreamOption s = streams[i];
                _streams.Add(s);
                bool isCurrent = currentStream != null && SameStream(s, currentStream);
                if (isCurrent) currentIndex = i;

                BuildStreamCard(s, isCurrent);
            }

            _focusedIndex = currentStream != null ? currentIndex : 0;
            _streamFocusedIndex = _focusedIndex;
            _activeRow = StreamRowIndex;
            ApplyFocusVisual();
        }

        /// <summary>Update the match-meta header (live pill + competition + title).
        /// Also rebuilds the favourite-team chips (2 per team: heart + view-team).</summary>
        public void SetMatch(CatalogMatch match)
        {
            _favTeamNames.Clear();
            _favChipKinds.Clear();
            _favChips.Clear();
            _favChipLabels.Clear();
            ClearChildren(_favRow);
            _favFocusedIndex = 0;

            // -- meta header --
            if (match != null)
            {
                bool live = match.IsLive;
                _metaPillLabel.Text = live ? "● LIVE" : StatusPillText(match);
                _metaPillWrap.BackgroundColor = live
                    ? Tokens.AccentLive
                    : (IsRecent(match) ? new Color(0.42f, 0.42f, 0.46f, 1f) : Tokens.AccentScheduled);

                _metaCompetition.Text = !string.IsNullOrWhiteSpace(match.Competition)
                    ? match.Competition
                    : (match.Sport ?? "");

                _matchTitle.Text = !string.IsNullOrWhiteSpace(match.Title)
                    ? match.Title
                    : "Pick a stream";
            }
            else
            {
                _metaPillLabel.Text = "STREAM";
                _metaPillWrap.BackgroundColor = Tokens.AccentScheduled;
                _metaCompetition.Text = "";
                _matchTitle.Text = "Pick a stream";
            }

            // -- team chips --
            // Pair team names with their streamed.pk badge IDs so the
            // TeamHubRequested event can carry the badge id to the host. The
            // badge id is what /api/images/badge/{id}.webp expects — passing
            // it lets the stats page render the team's actual badge instead
            // of just the initials fallback.
            List<string> names = new List<string>();
            List<string> badges = new List<string>();
            string home = match?.Teams?.Home?.Name;
            string away = match?.Teams?.Away?.Name;
            string homeBadge = match?.Teams?.Home?.Badge;
            string awayBadge = match?.Teams?.Away?.Badge;
            if (!string.IsNullOrWhiteSpace(home))
            {
                names.Add(home);
                badges.Add(homeBadge ?? "");
            }
            if (!string.IsNullOrWhiteSpace(away))
            {
                names.Add(away);
                badges.Add(awayBadge ?? "");
            }
            // Stash for the chip-activate handler below.
            _favTeamBadgesByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Count; i++)
            {
                _favTeamBadgesByName[names[i]] = badges[i];
            }

            if (names.Count == 0)
            {
                _favHeader.Hide();
                _favRow.Hide();
                return;
            }

            _favHeader.Show();
            _favRow.Show();
            foreach (string n in names)
            {
                bool fav = StreamedTv.Services.FavoritesRegistry.Current != null
                    && StreamedTv.Services.FavoritesRegistry.Current.IsFavoriteTeam(n);

                // Per-team grouped block: a vertical stack with a big team-name
                // header on top and a horizontal row of [Favourite] [Stats]
                // action chips beneath. Replaces the previous flat 4-chip layout
                // which made it ambiguous which chip belonged to which team.
                View teamGroup = new View
                {
                    BackgroundColor = new Color(0f, 0f, 0f, 0f),
                    WidthSpecification = LayoutParamPolicies.WrapContent,
                    HeightSpecification = LayoutParamPolicies.WrapContent,
                    Layout = new LinearLayout
                    {
                        LinearOrientation = LinearLayout.Orientation.Vertical,
                        LinearAlignment = LinearLayout.Alignment.Begin,
                        CellPadding = new Size2D(0, 12),
                    },
                };

                TextLabel teamHeader = new TextLabel
                {
                    Text = n,
                    TextColor = Tokens.TextPrimary,
                    PointSize = 22f,    // bigger than the previous in-chip team text
                    FontFamily = Tokens.FontFamily,
                    FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                    MultiLine = false,
                    WidthSpecification = LayoutParamPolicies.WrapContent,
                    HeightSpecification = LayoutParamPolicies.WrapContent,
                    Margin = new Extents(4, 0, 0, 2),
                };
                teamGroup.Add(teamHeader);

                View actionsRow = new View
                {
                    BackgroundColor = new Color(0f, 0f, 0f, 0f),
                    WidthSpecification = LayoutParamPolicies.WrapContent,
                    HeightSpecification = LayoutParamPolicies.WrapContent,
                    Layout = new LinearLayout
                    {
                        LinearOrientation = LinearLayout.Orientation.Horizontal,
                        LinearAlignment = LinearLayout.Alignment.CenterVertical,
                        CellPadding = new Size2D(10, 0),  // tight gap between a team's two chips
                    },
                };
                teamGroup.Add(actionsRow);

                // Heart toggle. Index 0 (and 2 for the second team) in the flat
                // _favChips list — the existing MoveLeft/Right/Activate logic
                // treats them as a single linear list across both team groups.
                View favChip = BuildFavChip(n, FavChipKind.ToggleFavorite, fav);
                actionsRow.Add(favChip);
                _favTeamNames.Add(n);
                _favChipKinds.Add(FavChipKind.ToggleFavorite);
                _favChips.Add(favChip);
                _favChipLabels.Add((TextLabel)favChip.GetChildAt(0));

                View viewChip = BuildFavChip(n, FavChipKind.ViewTeam, false);
                actionsRow.Add(viewChip);
                _favTeamNames.Add(n);
                _favChipKinds.Add(FavChipKind.ViewTeam);
                _favChips.Add(viewChip);
                _favChipLabels.Add((TextLabel)viewChip.GetChildAt(0));

                _favRow.Add(teamGroup);
            }
            ApplyFocusVisual();
        }

        // ------------- Stream card builder -----------------------------------

        private void BuildStreamCard(StreamOption s, bool isCurrent)
        {
            View card = new View
            {
                WidthSpecification = StreamCardWidth,
                HeightSpecification = StreamCardHeight,
                BackgroundColor = isCurrent ? Tokens.TileLive : new Color(0.10f, 0.10f, 0.12f, 1f),
                BorderlineColor = isCurrent ? Tokens.AccentLive : new Color(1f, 1f, 1f, 0.20f),
                BorderlineWidth = isCurrent ? 2f : 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(StreamCardCornerRadius, StreamCardCornerRadius, StreamCardCornerRadius, StreamCardCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    LinearAlignment = LinearLayout.Alignment.Top,
                    CellPadding = new Size2D(0, 6),
                    Padding = new Extents(18, 18, 16, 16),
                },
            };

            // "▶ NOW PLAYING" badge — only on the currently active stream. Top of card.
            View nowBadge = new View
            {
                BackgroundColor = Tokens.AccentLive,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                CornerRadius = new Vector4(6f, 6f, 6f, 6f),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.Center,
                    Padding = new Extents(8, 8, 3, 3),
                },
            };
            TextLabel nowTxt = new TextLabel
            {
                Text = "▶ NOW",
                TextColor = Color.White,
                PointSize = 9f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            nowBadge.Add(nowTxt);
            if (!isCurrent) nowBadge.Hide();
            card.Add(nowBadge);

            // Source name — the headliner of the card.
            TextLabel src = new TextLabel
            {
                Text = string.IsNullOrEmpty(s.Source) ? "stream" : CapitalizeFirst(s.Source),
                TextColor = Tokens.TextPrimary,
                PointSize = 19f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            card.Add(src);

            // Flex spacer pushes the quality+lang row to the bottom.
            View spacer = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = 0,
                Weight = 1f,
            };
            card.Add(spacer);

            // Quality + language row at the bottom.
            View metaRow = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.CenterVertical,
                    CellPadding = new Size2D(8, 0),
                },
            };
            card.Add(metaRow);

            View qualityPill = new View
            {
                BackgroundColor = s.Hd
                    ? new Color(0.20f, 0.78f, 0.35f, 1f)   // green for HD
                    : new Color(0.55f, 0.55f, 0.60f, 1f),  // gray for SD
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                CornerRadius = new Vector4(6f, 6f, 6f, 6f),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.Center,
                    Padding = new Extents(8, 8, 3, 3),
                },
            };
            TextLabel qualityTxt = new TextLabel
            {
                Text = s.Hd ? "HD" : "SD",
                TextColor = Color.White,
                PointSize = 9f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            qualityPill.Add(qualityTxt);
            metaRow.Add(qualityPill);

            TextLabel langLabel = new TextLabel
            {
                Text = string.IsNullOrEmpty(s.Language) ? "" : CapitalizeFirst(s.Language),
                TextColor = Tokens.TextSecondary,
                PointSize = 11f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("medium")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            metaRow.Add(langLabel);

            _chipRow.Add(card);
            _chips.Add(card);
            _chipSourceLabels.Add(src);
            _chipQualityPills.Add(qualityPill);
            _chipLangLabels.Add(langLabel);
            _chipNowBadges.Add(nowBadge);
        }

        // ------------- Team chip builder -------------------------------------

        private View BuildFavChip(string teamName, FavChipKind kind, bool isFavorited)
        {
            bool isHeart = kind == FavChipKind.ToggleFavorite;
            View chip = new View
            {
                BackgroundColor = isHeart
                    ? new Color(0.10f, 0.10f, 0.12f, 1f)
                    : new Color(0.07f, 0.10f, 0.18f, 1f),
                BorderlineColor = isHeart
                    ? new Color(1f, 0.42f, 0.42f, isFavorited ? 0.85f : 0.30f)
                    : new Color(Tokens.AccentScheduled.R, Tokens.AccentScheduled.G, Tokens.AccentScheduled.B, 0.55f),
                BorderlineWidth = 3f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(ChipCornerRadius, ChipCornerRadius, ChipCornerRadius, ChipCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.Center,
                    Padding = new Extents(
                        (ushort)ChipHorizontalPad, (ushort)ChipHorizontalPad,
                        (ushort)ChipVerticalPad, (ushort)ChipVerticalPad),
                },
            };
            // Team name now lives in the group header above, so the chip itself
            // is purely a verb: "Favourite team" / "Favourited team" / "Stats".
            string text = isHeart
                ? (isFavorited ? "♥  Favourited" : "♡  Favourite team")
                : "Stats  ›";
            TextLabel label = new TextLabel
            {
                Text = text,
                TextColor = Tokens.TextPrimary,
                PointSize = Dimens.FontChip,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("semibold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            chip.Add(label);
            return chip;
        }

        // ------------- Lifecycle / focus -------------------------------------

        // Tracks the open/close opacity animation so SetStreams (which can be
        // called by the host mid-animation when the stream fetch completes
        // fast) can stop it and snap to full opacity. Without this, NUI was
        // sometimes failing to render newly-added chip cards until the
        // animation finished — "Loading streams…" stayed visible until any
        // input event woke the renderer up.
        private Animation _modalOpacityAnim;

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;
            if (_streams.Count > 0 && _focusedIndex < 0) _focusedIndex = 0;
            ApplyFocusVisual();
            Show();
            RaiseToTop();
            _modalOpacityAnim?.Stop();
            _modalOpacityAnim = new Animation(200);
            _modalOpacityAnim.AnimateTo(this, "Opacity", 1f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            _modalOpacityAnim.Play();
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            _modalOpacityAnim?.Stop();
            _modalOpacityAnim = new Animation(160);
            _modalOpacityAnim.AnimateTo(this, "Opacity", 0f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseIn));
            _modalOpacityAnim.Finished += (s, e) => Hide();
            _modalOpacityAnim.Play();
            Closed?.Invoke(this, EventArgs.Empty);
        }

        public void MoveLeft()
        {
            if (_activeRow == StreamRowIndex)
            {
                if (_focusedIndex <= 0) return;
                _focusedIndex--;
                _streamFocusedIndex = _focusedIndex;
            }
            else
            {
                if (_favFocusedIndex <= 0) return;
                _favFocusedIndex--;
            }
            ApplyFocusVisual();
        }

        public void MoveRight()
        {
            if (_activeRow == StreamRowIndex)
            {
                if (_focusedIndex < 0 || _focusedIndex >= _streams.Count - 1) return;
                _focusedIndex++;
                _streamFocusedIndex = _focusedIndex;
            }
            else
            {
                if (_favFocusedIndex >= _favChips.Count - 1) return;
                _favFocusedIndex++;
            }
            ApplyFocusVisual();
        }

        public void MoveUp()
        {
            if (_activeRow == FavoritesRowIndex && _streams.Count > 0)
            {
                _activeRow = StreamRowIndex;
                if (_streamFocusedIndex < 0 && _streams.Count > 0) _streamFocusedIndex = 0;
                _focusedIndex = _streamFocusedIndex;
                ApplyFocusVisual();
            }
        }

        public void MoveDown()
        {
            if (_activeRow == StreamRowIndex && _favChips.Count > 0)
            {
                _activeRow = FavoritesRowIndex;
                if (_favFocusedIndex < 0 || _favFocusedIndex >= _favChips.Count) _favFocusedIndex = 0;
                ApplyFocusVisual();
            }
        }

        public void Activate()
        {
            if (_activeRow == FavoritesRowIndex)
            {
                if (_favFocusedIndex < 0 || _favFocusedIndex >= _favTeamNames.Count) return;
                string teamName = _favTeamNames[_favFocusedIndex];
                FavChipKind kind = _favChipKinds[_favFocusedIndex];

                if (kind == FavChipKind.ViewTeam)
                {
                    EventHandler<TeamHubRequestedEventArgs> hh = TeamHubRequested;
                    string badgeId;
                    _favTeamBadgesByName.TryGetValue(teamName, out badgeId);
                    if (hh != null) hh(this, new TeamHubRequestedEventArgs(teamName, badgeId));
                    return;
                }

                EventHandler<TeamFavoriteToggleEventArgs> h = TeamFavoriteToggled;
                if (h != null) h(this, new TeamFavoriteToggleEventArgs(teamName));
                // Re-paint the heart chip immediately — host updates the underlying
                // preference; we just flip the glyph and brighten the border.
                TextLabel lbl = _favFocusedIndex < _favChipLabels.Count ? _favChipLabels[_favFocusedIndex] : null;
                if (lbl != null)
                {
                    bool nowFav = StreamedTv.Services.FavoritesRegistry.Current != null
                        && StreamedTv.Services.FavoritesRegistry.Current.IsFavoriteTeam(teamName);
                    lbl.Text = nowFav ? "♥  Favourited" : "♡  Favourite team";
                    if (_favFocusedIndex < _favChips.Count)
                    {
                        _favChips[_favFocusedIndex].BorderlineColor =
                            new Color(1f, 0.42f, 0.42f, nowFav ? 0.85f : 0.30f);
                    }
                }
                return;
            }

            if (_focusedIndex < 0 || _focusedIndex >= _streams.Count) return;
            StreamSelected?.Invoke(this, new StreamSelectedEventArgs(_streams[_focusedIndex]));
        }

        // ------------- Focus visuals -----------------------------------------

        private void ApplyFocusVisual()
        {
            // Streams row.
            for (int i = 0; i < _chips.Count; i++)
            {
                View chip = _chips[i];
                bool isFocused = _activeRow == StreamRowIndex && i == _focusedIndex;
                bool isCurrent = CurrentStream != null && i < _streams.Count && SameStream(_streams[i], CurrentStream);

                if (isFocused)
                {
                    chip.BackgroundColor = isCurrent
                        ? new Color(Tokens.TileLive.R + 0.05f, Tokens.TileLive.G + 0.02f, Tokens.TileLive.B + 0.02f, 1f)
                        : new Color(0.18f, 0.18f, 0.22f, 1f);
                    chip.BorderlineColor = Color.White;
                    chip.BorderlineWidth = 4f;
                }
                else if (isCurrent)
                {
                    chip.BackgroundColor = Tokens.TileLive;
                    chip.BorderlineColor = Tokens.AccentLive;
                    chip.BorderlineWidth = 2f;
                }
                else
                {
                    chip.BackgroundColor = new Color(0.10f, 0.10f, 0.12f, 1f);
                    chip.BorderlineColor = new Color(1f, 1f, 1f, 0.20f);
                    chip.BorderlineWidth = 1f;
                }
                AnimateScale(chip, isFocused ? 1.06f : 1.0f);
            }

            // Teams row.
            for (int i = 0; i < _favChips.Count; i++)
            {
                View chip = _favChips[i];
                bool isFocused = _activeRow == FavoritesRowIndex && i == _favFocusedIndex;
                bool isHeart = i < _favChipKinds.Count && _favChipKinds[i] == FavChipKind.ToggleFavorite;

                if (isFocused)
                {
                    chip.BackgroundColor = isHeart
                        ? new Color(0.18f, 0.10f, 0.12f, 1f)
                        : new Color(0.10f, 0.16f, 0.28f, 1f);
                    chip.BorderlineColor = Color.White;
                    chip.BorderlineWidth = 4f;
                }
                else
                {
                    chip.BackgroundColor = isHeart
                        ? new Color(0.10f, 0.10f, 0.12f, 1f)
                        : new Color(0.07f, 0.10f, 0.18f, 1f);
                    if (isHeart)
                    {
                        // Re-derive heart border alpha from current fav state.
                        string n = i < _favTeamNames.Count ? _favTeamNames[i] : null;
                        bool fav = !string.IsNullOrEmpty(n)
                            && StreamedTv.Services.FavoritesRegistry.Current != null
                            && StreamedTv.Services.FavoritesRegistry.Current.IsFavoriteTeam(n);
                        chip.BorderlineColor = new Color(1f, 0.42f, 0.42f, fav ? 0.85f : 0.30f);
                    }
                    else
                    {
                        chip.BorderlineColor = new Color(Tokens.AccentScheduled.R, Tokens.AccentScheduled.G, Tokens.AccentScheduled.B, 0.55f);
                    }
                    chip.BorderlineWidth = 3f;
                }
                AnimateScale(chip, isFocused ? 1.07f : 1.0f);
            }
        }

        private void AnimateScale(View v, float target)
        {
            Animation prev;
            if (_chipScaleAnims.TryGetValue(v, out prev) && prev != null) prev.Stop();
            Animation a = new Animation(Dimens.AnimFocusScale);
            a.AnimateTo(v, "Scale",
                new Vector3(target, target, 1f),
                new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOutSquare));
            a.Play();
            _chipScaleAnims[v] = a;
        }

        // ------------- Helpers -----------------------------------------------

        private static string StatusPillText(CatalogMatch m)
        {
            if (m == null) return "STREAM";
            // Past finish?
            if (m.Date > 0)
            {
                DateTimeOffset start = DateTimeOffset.FromUnixTimeMilliseconds(m.Date);
                if (start < DateTimeOffset.Now.AddHours(-3)) return "FINAL";
                if (start < DateTimeOffset.Now) return "● LIVE";
            }
            return "SCHEDULED";
        }

        private static bool IsRecent(CatalogMatch m)
        {
            if (m == null || m.Date <= 0) return false;
            DateTimeOffset start = DateTimeOffset.FromUnixTimeMilliseconds(m.Date);
            return start < DateTimeOffset.Now.AddHours(-3);
        }

        private static string CapitalizeFirst(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpperInvariant(s[0]) + (s.Length > 1 ? s.Substring(1) : "");
        }

        private static bool SameStream(StreamOption a, StreamOption b)
        {
            if (a == null || b == null) return false;
            if (!string.IsNullOrEmpty(a.Id) && !string.IsNullOrEmpty(b.Id)) return string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(a.EmbedUrl) && !string.IsNullOrEmpty(b.EmbedUrl)) return string.Equals(a.EmbedUrl, b.EmbedUrl, StringComparison.OrdinalIgnoreCase);
            return a.Source == b.Source && a.StreamNo == b.StreamNo;
        }

        private static void ClearChildren(View v)
        {
            if (v == null) return;
            uint count = v.ChildCount;
            for (uint i = 0; i < count; i++) v.Remove(v.GetChildAt(0));
        }
    }
}
