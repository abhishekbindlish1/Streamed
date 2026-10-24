using System;
using System.Collections.Generic;
using StreamedTv.Models;
using StreamedTv.Theme;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace StreamedTv.Views
{
    /// <summary>
    /// Full-screen modal: dim backdrop + centered cinematic glass panel containing
    /// a 6-column grid of card-style sport pills. Mirrors the StreamSwitcherView
    /// design language: heavier shadow, big bold type, header pill + section header,
    /// scale-on-focus card animation.
    ///
    /// Vertical structure inside the panel:
    ///   1. Header band — [FAVORITES] pill + subtle muted line + big "Choose your sports" title
    ///   2. Hairline divider
    ///   3. "ALL SPORTS" section header
    ///   4. 6-column grid of card pills (200×88, vertical layout, optional ★ PINNED badge)
    ///   5. Inline message strip (warnings)
    ///   6. Subtle key-hint footer
    ///
    /// All layout via LinearLayout (no absolute child positions, which Tizen 9
    /// device runtime ignores on LinearLayout children).
    /// </summary>
    public sealed class MoreOverlayView : View
    {
        private const int Columns = 6;
        private const int CellGap = 16;

        // Cinematic panel sizing — matches StreamSwitcherView.
        private const int PanelCornerRadius = 28;
        private const int PanelPaddingX = 56;
        private const int PanelPaddingTop = 44;
        private const int PanelPaddingBottom = 36;

        // Card-style sport pills.
        private const int PillWidth = 200;
        private const int PillHeight = 88;
        private const int PillCornerRadius = 18;

        // Layout pieces
        private readonly View _panel;
        private readonly View _grid;
        private readonly TextLabel _messageLabel;

        // Sport state
        private readonly List<string> _sportIds = new List<string>();
        private readonly List<string> _sportNames = new List<string>();
        private readonly List<View> _pills = new List<View>();
        private readonly List<TextLabel> _pillLabels = new List<TextLabel>();
        private readonly List<View> _pinnedBadges = new List<View>();
        private readonly HashSet<string> _pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Per-card scale animations so rapid Left/Right interrupts don't stack.
        private readonly Dictionary<View, Animation> _pillScaleAnims = new Dictionary<View, Animation>();

        private int _focusedIndex;

        public bool IsOpen { get; private set; }
        public int FocusedIndex { get { return _focusedIndex; } }
        public int PillCount { get { return _sportIds.Count; } }

        public string FocusedSportId
        {
            get
            {
                if (_focusedIndex < 0 || _focusedIndex >= _sportIds.Count) return null;
                return _sportIds[_focusedIndex];
            }
        }

        public event EventHandler<MoreOverlayPinEventArgs> SportPinned;
        public event EventHandler Closed;

        public MoreOverlayView()
        {
            // Full-screen dim backdrop — matches StreamSwitcherView's 0.82 alpha.
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.FillToParent;
            BackgroundColor = new Color(0f, 0f, 0f, 0.82f);

            // Center the panel via a horizontal LinearLayout that centers its single child.
            Layout = new LinearLayout
            {
                LinearOrientation = LinearLayout.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // Cinematic glass panel — wider, taller, more pronounced shadow.
            _panel = new View
            {
                BackgroundColor = new Color(0.045f, 0.045f, 0.06f, 0.98f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.14f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(PanelCornerRadius, PanelCornerRadius, PanelCornerRadius, PanelCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                BoxShadow = new Shadow(60f, new Color(0f, 0f, 0f, 0.7f), new Vector2(0f, 24f), new Vector2(0f, 0f)),
                WidthSpecification = LayoutParamPolicies.WrapContent,
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

            // ---- Header band -------------------------------------------------
            // Pill row: [FAVORITES] · subtle muted line.
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

            View headerPillWrap = new View
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
            TextLabel headerPillLabel = new TextLabel
            {
                Text = "FAVORITES",
                TextColor = Color.White,
                PointSize = 11f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            headerPillWrap.Add(headerPillLabel);
            pillRow.Add(headerPillWrap);

            TextLabel headerSubLine = new TextLabel
            {
                Text = "Pin up to 3 sports",
                TextColor = Tokens.TextSecondary,
                PointSize = 14f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("medium")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            pillRow.Add(headerSubLine);

            TextLabel title = new TextLabel
            {
                Text = "Choose your sports",
                TextColor = Tokens.TextPrimary,
                PointSize = 30f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Margin = new Extents(0, 0, 4, 6),
            };
            _panel.Add(title);

            // Hairline divider so the grid section reads as a separate block.
            View headerDivider = new View
            {
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = 1,
                BackgroundColor = Tokens.Divider,
                Margin = new Extents(0, 0, 8, 12),
            };
            _panel.Add(headerDivider);

            // ---- "ALL SPORTS" section header --------------------------------
            TextLabel sectionHeader = new TextLabel
            {
                Text = "All sports",
                TextColor = Tokens.TextPrimary,
                PointSize = 16f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _panel.Add(sectionHeader);

            // ---- Grid -------------------------------------------------------
            _grid = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    LinearAlignment = LinearLayout.Alignment.Top,
                    CellPadding = new Size2D(0, CellGap),
                },
            };
            _panel.Add(_grid);

            // Inline message strip — shown briefly when the user tries to pin a 4th sport.
            _messageLabel = new TextLabel
            {
                Text = "",
                TextColor = new Color(1f, 0.7f, 0.45f, 1f),
                PointSize = 12f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("semibold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Extents(0, 0, 6, 0),
            };
            _messageLabel.Hide();
            _panel.Add(_messageLabel);

            // Subtle key-hint footer.
            TextLabel hint = new TextLabel
            {
                Text = "◀ ▶ ▲ ▼ navigate · OK toggle pin · BACK to cancel",
                TextColor = Tokens.TextTertiary,
                PointSize = 11f,
                FontFamily = Tokens.FontFamily,
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Margin = new Extents(0, 0, 18, 0),
            };
            _panel.Add(hint);

            // Re-tint pinned-pill borders live when the active sport changes.
            SportPalette.Changed += OnPaletteChanged;

            // Hidden by default.
            Opacity = 0f;
            Hide();
        }

        public void SetSports(IReadOnlyList<Sport> allSports, IReadOnlyList<string> currentFavoriteIds)
        {
            _sportIds.Clear();
            _sportNames.Clear();
            _pills.Clear();
            _pillLabels.Clear();
            _pinnedBadges.Clear();
            _pinned.Clear();
            _pillScaleAnims.Clear();
            ClearGridChildren();

            if (currentFavoriteIds != null)
            {
                foreach (string fid in currentFavoriteIds)
                {
                    if (!string.IsNullOrEmpty(fid)) _pinned.Add(fid);
                }
            }
            if (allSports == null || allSports.Count == 0)
            {
                _focusedIndex = -1;
                return;
            }

            // Rank: pinned (favourited) sports first, then alphabetically by display
            // name. Stable sort via a key tuple. Operates on a copy of the input
            // so we don't mutate the caller's list.
            List<Sport> ranked = new List<Sport>(allSports);
            ranked.Sort((a, b) =>
            {
                string aId = a?.Id ?? "";
                string bId = b?.Id ?? "";
                bool aPin = _pinned.Contains(aId);
                bool bPin = _pinned.Contains(bId);
                if (aPin != bPin) return aPin ? -1 : 1;  // pinned wins
                string aName = !string.IsNullOrEmpty(a?.Name) ? a.Name : aId;
                string bName = !string.IsNullOrEmpty(b?.Name) ? b.Name : bId;
                return string.Compare(aName, bName, StringComparison.OrdinalIgnoreCase);
            });
            allSports = ranked;

            int rows = (allSports.Count + Columns - 1) / Columns;
            for (int r = 0; r < rows; r++)
            {
                View rowView = new View
                {
                    BackgroundColor = new Color(0f, 0f, 0f, 0f),
                    WidthSpecification = LayoutParamPolicies.WrapContent,
                    HeightSpecification = LayoutParamPolicies.WrapContent,
                    Layout = new LinearLayout
                    {
                        LinearOrientation = LinearLayout.Orientation.Horizontal,
                        LinearAlignment = LinearLayout.Alignment.Begin,
                        CellPadding = new Size2D(CellGap, 0),
                    },
                };
                _grid.Add(rowView);

                for (int c = 0; c < Columns; c++)
                {
                    int idx = r * Columns + c;
                    if (idx >= allSports.Count) break;
                    Sport s = allSports[idx];
                    string id = s != null && s.Id != null ? s.Id : "";
                    string name = s != null && !string.IsNullOrEmpty(s.Name) ? s.Name : id;
                    _sportIds.Add(id);
                    _sportNames.Add(name);

                    BuildSportCard(rowView, name);
                }
            }

            _focusedIndex = _sportIds.Count > 0 ? 0 : -1;
            ApplyFocusVisual();
        }

        private void BuildSportCard(View rowView, string name)
        {
            View pill = new View
            {
                WidthSpecification = PillWidth,
                HeightSpecification = PillHeight,
                BackgroundColor = new Color(0.10f, 0.10f, 0.12f, 1f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.20f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(PillCornerRadius, PillCornerRadius, PillCornerRadius, PillCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    LinearAlignment = LinearLayout.Alignment.Top,
                    CellPadding = new Size2D(0, 4),
                    Padding = new Extents(14, 14, 12, 10),
                },
            };

            // Top: sport name (big, bold).
            TextLabel label = new TextLabel
            {
                Text = name,
                TextColor = Tokens.TextPrimary,
                PointSize = 17f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                Ellipsis = true,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            pill.Add(label);

            // Flex spacer pushes the badge to the bottom.
            View spacer = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = 0,
                Weight = 1f,
            };
            pill.Add(spacer);

            // Bottom: "★ PINNED" badge (red on AccentLive bg, 9pt bold) — hidden if not pinned.
            View pinnedBadge = new View
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
            TextLabel pinnedTxt = new TextLabel
            {
                Text = "★ PINNED",
                TextColor = Color.White,
                PointSize = 9f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            pinnedBadge.Add(pinnedTxt);
            pinnedBadge.Hide();
            pill.Add(pinnedBadge);

            rowView.Add(pill);
            _pills.Add(pill);
            _pillLabels.Add(label);
            _pinnedBadges.Add(pinnedBadge);
        }

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;
            _focusedIndex = _sportIds.Count > 0 ? 0 : -1;
            ApplyFocusVisual();
            Show();
            RaiseToTop();
            Animation a = new Animation(200);
            a.AnimateTo(this, "Opacity", 1f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            a.Play();
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            Animation a = new Animation(160);
            a.AnimateTo(this, "Opacity", 0f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseIn));
            a.Finished += (s, e) => Hide();
            a.Play();
            Closed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Refresh which sports are marked as pinned (★) WITHOUT rebuilding the grid.
        /// Preserves the user's current focus position so they can keep pinning more sports.</summary>
        public void RefreshPinned(IReadOnlyList<string> pinnedIds)
        {
            _pinned.Clear();
            if (pinnedIds != null)
            {
                foreach (string id in pinnedIds)
                {
                    if (!string.IsNullOrEmpty(id)) _pinned.Add(id);
                }
            }
            ApplyFocusVisual();
        }

        public void MoveLeft()
        {
            if (_focusedIndex <= 0) return;
            if (_focusedIndex % Columns == 0) return;
            _focusedIndex--;
            ApplyFocusVisual();
        }

        public void MoveRight()
        {
            if (_focusedIndex < 0 || _focusedIndex >= _sportIds.Count - 1) return;
            if ((_focusedIndex + 1) % Columns == 0) return;
            _focusedIndex++;
            ApplyFocusVisual();
        }

        public void MoveUp()
        {
            int next = _focusedIndex - Columns;
            if (next < 0) return;
            _focusedIndex = next;
            ApplyFocusVisual();
        }

        public void MoveDown()
        {
            int next = _focusedIndex + Columns;
            if (next >= _sportIds.Count) return;
            _focusedIndex = next;
            ApplyFocusVisual();
        }

        public void Activate()
        {
            string id = FocusedSportId;
            if (string.IsNullOrEmpty(id)) return;
            SportPinned?.Invoke(this, new MoreOverlayPinEventArgs(id));
        }

        /// <summary>Show a transient message inline (e.g. "Max 3 favorites — unpin one first").
        /// Pass null/empty to clear immediately.</summary>
        public void ShowMessage(string text)
        {
            if (_messageLabel == null) return;
            if (string.IsNullOrEmpty(text))
            {
                _messageLabel.Hide();
                return;
            }
            _messageLabel.Text = text;
            _messageLabel.Show();
        }

        public void ClearMessage()
        {
            if (_messageLabel == null) return;
            _messageLabel.Hide();
        }

        // ------------- Focus visuals -----------------------------------------

        private void ApplyFocusVisual()
        {
            Color accent = SportPalette.Current != null
                ? SportPalette.Current.Primary
                : Tokens.AccentLive;
            Color pinnedBorder = new Color(accent.R, accent.G, accent.B, 0.6f);

            for (int i = 0; i < _pills.Count; i++)
            {
                View pill = _pills[i];
                bool isFocused = i == _focusedIndex;
                bool isPinned = i < _sportIds.Count && _pinned.Contains(_sportIds[i]);

                // Show/hide PINNED badge.
                if (i < _pinnedBadges.Count)
                {
                    if (isPinned) _pinnedBadges[i].Show();
                    else _pinnedBadges[i].Hide();
                }

                if (isFocused)
                {
                    // White 4px focus border + scale 1.06 lift. Crucially: do NOT
                    // swap the background — keep the card silhouette intact.
                    pill.BackgroundColor = isPinned
                        ? new Color(0.07f, 0.07f, 0.09f, 1f)
                        : new Color(0.10f, 0.10f, 0.12f, 1f);
                    pill.BorderlineColor = Color.White;
                    pill.BorderlineWidth = 4f;
                }
                else if (isPinned)
                {
                    // Subtle accent border + slightly darker inner bg.
                    pill.BackgroundColor = new Color(0.07f, 0.07f, 0.09f, 1f);
                    pill.BorderlineColor = pinnedBorder;
                    pill.BorderlineWidth = 1f;
                }
                else
                {
                    pill.BackgroundColor = new Color(0.10f, 0.10f, 0.12f, 1f);
                    pill.BorderlineColor = new Color(1f, 1f, 1f, 0.20f);
                    pill.BorderlineWidth = 1f;
                }
                AnimateScale(pill, isFocused ? 1.06f : 1.0f);
            }
        }

        private void AnimateScale(View v, float target)
        {
            Animation prev;
            if (_pillScaleAnims.TryGetValue(v, out prev) && prev != null) prev.Stop();
            Animation a = new Animation(Dimens.AnimFocusScale);
            a.AnimateTo(v, "Scale",
                new Vector3(target, target, 1f),
                new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOutSquare));
            a.Play();
            _pillScaleAnims[v] = a;
        }

        private void OnPaletteChanged(object sender, SportPalette.Palette palette)
        {
            // Re-paint pinned pill borders so they retint live when the active
            // sport changes.
            ApplyFocusVisual();
        }

        private void ClearGridChildren()
        {
            uint count = _grid.ChildCount;
            for (uint i = 0; i < count; i++)
            {
                _grid.Remove(_grid.GetChildAt(0));
            }
        }
    }

    public sealed class MoreOverlayPinEventArgs : EventArgs
    {
        public string SportId { get; }

        public MoreOverlayPinEventArgs(string sportId)
        {
            SportId = sportId;
        }
    }
}
