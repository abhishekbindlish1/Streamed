using System;
using System.Collections.Generic;
using StreamedTv.Models;
using StreamedTv.Services;
using StreamedTv.Theme;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace StreamedTv.Views
{
    /// <summary>
    /// Live-TV channel grid — opens on the Red remote button. Pulls
    /// the iptv-org sports catalog from the backend, renders a 4×N
    /// scrollable grid of channel tiles. Press OK on a tile to play.
    ///
    /// Kept fully isolated from the existing home-screen surfaces:
    /// own modal opacity, own focus model, own key router. The host
    /// just calls Open()/Close() and routes XF86Red there. PlayerWebView
    /// + everything else stay untouched.
    ///
    /// Pure-test surface — slated for removal once the iptv-org
    /// experiment wraps up. NOT styled to match the rest of the app
    /// beyond using the same color tokens.
    /// </summary>
    public sealed class IptvChannelsOverlayView : View
    {
        // --- Layout (1920×1080 reference) ---
        private const int HorizontalMargin = 80;
        private const int TopMargin = 96;
        private const int TileWidth = 360;
        private const int TileHeight = 110;
        private const int TileSpacing = 18;
        private const int Columns = 4;

        private readonly View _panel;
        private readonly TextLabel _title;
        private readonly TextLabel _subtitle;
        private readonly View _gridClip;
        private readonly View _grid;
        private readonly TextLabel _statusBanner;

        private readonly List<View> _tiles = new List<View>();
        private readonly List<IptvChannel> _channels = new List<IptvChannel>();
        private int _focusIdx = -1;
        // Owns the vertical-scroll animation so rapid Up/Down doesn't
        // stack lifts. Same pattern as RowView's horizontal scroller.
        private Animation _scrollAnim;
        // Cached row-stride (tile height + spacing) used for scroll math.
        // Set on construction; constants make this trivially derivable.
        private const int RowStride = TileHeight + TileSpacing;

        public bool IsOpen { get; private set; }

        /// <summary>Raised when the user activates a channel tile.
        /// Host calls Close(), then invokes the player with the
        /// channel's play URL.</summary>
        public event EventHandler<IptvChannel> ChannelActivated;

        /// <summary>Raised when the user presses Back / Escape.
        /// Host calls Close().</summary>
        public event EventHandler Closed;

        public IptvChannelsOverlayView()
        {
            // Full-screen scrim — click anywhere off the grid to
            // back out (KeyEvent only — no real touch on TV).
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.FillToParent;
            BackgroundColor = new Color(0f, 0f, 0f, 0.92f);

            _panel = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.FillToParent,
                Padding = new Extents(
                    HorizontalMargin, HorizontalMargin,
                    TopMargin, 60),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    LinearAlignment = LinearLayout.Alignment.Top,
                    CellPadding = new Size2D(0, 18),
                },
            };
            Add(_panel);

            _title = new TextLabel
            {
                Text = "Live TV",
                TextColor = Tokens.TextPrimary,
                PointSize = 36f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _panel.Add(_title);

            _subtitle = new TextLabel
            {
                Text = "Sports channels — iptv-org catalog",
                TextColor = Tokens.TextSecondary,
                PointSize = 14f,
                FontFamily = Tokens.FontFamily,
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _panel.Add(_subtitle);

            // Spacer
            _panel.Add(new View
            {
                WidthSpecification = 1,
                HeightSpecification = 12,
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
            });

            // Clipping window for the scrollable grid. Clips children
            // so when we translate the inner grid up/down for
            // scrolling, tiles don't visibly slide over the title /
            // subtitle / status banner. FillToParent lets the grid
            // take whatever vertical room is left after the title +
            // status. ClippingMode.ClipChildren handles the actual
            // mask.
            _gridClip = new View
            {
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightResizePolicy = ResizePolicyType.FillToParent,
                ClippingMode = ClippingModeType.ClipChildren,
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
            };
            _panel.Add(_gridClip);

            _grid = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                // Anchor at top-left so PositionY translations move
                // the grid as a unit. Without explicit pivot the
                // grid would scale around its center and the math
                // would be off.
                ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft,
                PivotPoint = Tizen.NUI.PivotPoint.TopLeft,
                Layout = new GridLayout
                {
                    Columns = Columns,
                    ColumnSpacing = TileSpacing,
                    RowSpacing = TileSpacing,
                },
            };
            _gridClip.Add(_grid);

            _statusBanner = new TextLabel
            {
                Text = "Loading channels…",
                TextColor = new Color(1f, 0.85f, 0.3f, 1f),
                PointSize = 14f,
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _panel.Add(_statusBanner);

            Opacity = 0f;
            Hide();
        }

        public void SetStatus(string text)
        {
            try
            {
                _statusBanner.Text = text ?? "";
                _statusBanner.Show();
            }
            catch { }
        }

        public void HideStatus()
        {
            try { _statusBanner.Hide(); } catch { }
        }

        /// <summary>Replace the tile grid with the given channel list.
        /// Safe to call when the overlay is open or closed; rebuilds
        /// the grid in place. Resets focus to the first tile.</summary>
        public void SetChannels(IReadOnlyList<IptvChannel> channels)
        {
            _channels.Clear();
            _tiles.Clear();
            ClearChildren(_grid);

            if (channels == null || channels.Count == 0)
            {
                _focusIdx = -1;
                SetStatus("No live channels available right now.");
                return;
            }

            HideStatus();

            for (int i = 0; i < channels.Count; i++)
            {
                IptvChannel ch = channels[i];
                if (ch == null) continue;
                _channels.Add(ch);
                View tile = BuildTile(ch);
                _tiles.Add(tile);
                _grid.Add(tile);
            }

            _focusIdx = _tiles.Count > 0 ? 0 : -1;
            ApplyFocusVisual();
        }

        private View BuildTile(IptvChannel ch)
        {
            View tile = new View
            {
                WidthSpecification = TileWidth,
                HeightSpecification = TileHeight,
                BackgroundColor = new Color(1f, 1f, 1f, 0.06f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.18f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(12, 12, 12, 12),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                Padding = new Extents(16, 16, 12, 12),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    LinearAlignment = LinearLayout.Alignment.Top,
                },
            };
            TextLabel name = new TextLabel
            {
                Text = ch.Name ?? "(unnamed)",
                TextColor = Tokens.TextPrimary,
                PointSize = 16f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                Ellipsis = true,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            tile.Add(name);

            string sub = ch.Country ?? "";
            if (ch.Streams != null && ch.Streams.Count > 0 && !string.IsNullOrEmpty(ch.Streams[0].Quality))
            {
                if (!string.IsNullOrEmpty(sub)) sub += " · ";
                sub += ch.Streams[0].Quality;
            }
            TextLabel meta = new TextLabel
            {
                Text = sub,
                TextColor = Tokens.TextSecondary,
                PointSize = 12f,
                FontFamily = Tokens.FontFamily,
                MultiLine = false,
                Ellipsis = true,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            tile.Add(meta);
            return tile;
        }

        // ---- Lifecycle -----------------------------------------------------

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;
            Show();
            RaiseToTop();
            // Snap focus + scroll back to the top every open.
            _focusIdx = _tiles.Count > 0 ? 0 : -1;
            try { _scrollAnim?.Stop(); } catch { }
            if (_grid != null) _grid.PositionY = 0f;
            ApplyFocusVisual();
            Animation a = new Animation(180);
            a.AnimateTo(this, "Opacity", 1f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            a.Play();
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            Animation a = new Animation(180);
            a.AnimateTo(this, "Opacity", 0f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseIn));
            a.Finished += (s, e) => Hide();
            a.Play();
        }

        // ---- Key routing ---------------------------------------------------
        //
        // Standard 2D grid navigation. Wraps row-by-row at edges.
        public void HandleKey(string key)
        {
            if (!IsOpen || _tiles.Count == 0) return;
            switch (key)
            {
                case "Left": Move(-1); break;
                case "Right": Move(+1); break;
                case "Up": Move(-Columns); break;
                case "Down": Move(+Columns); break;
                case "Return":
                case "Select":
                case "KP_Enter":
                case "OK":
                case "Ok":
                case "ok":
                case "XF86Ok":
                    Activate();
                    break;
                case "BackSpace":
                case "XF86Back":
                case "Escape":
                    Closed?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        private void Move(int delta)
        {
            int next = _focusIdx + delta;
            if (next < 0) next = 0;
            if (next >= _tiles.Count) next = _tiles.Count - 1;
            if (next == _focusIdx) return;
            _focusIdx = next;
            ApplyFocusVisual();
        }

        private void Activate()
        {
            if (_focusIdx < 0 || _focusIdx >= _channels.Count) return;
            IptvChannel ch = _channels[_focusIdx];
            ChannelActivated?.Invoke(this, ch);
        }

        private void ApplyFocusVisual()
        {
            for (int i = 0; i < _tiles.Count; i++)
            {
                View t = _tiles[i];
                bool focused = (i == _focusIdx);
                if (focused)
                {
                    t.BackgroundColor = Color.White;
                    t.BorderlineColor = Color.White;
                    t.BorderlineWidth = 2f;
                    // Re-color labels for contrast on white bg.
                    if (t.ChildCount >= 1 && t.GetChildAt(0) is TextLabel lbl1)
                        lbl1.TextColor = Tokens.TextOnLight;
                    if (t.ChildCount >= 2 && t.GetChildAt(1) is TextLabel lbl2)
                        lbl2.TextColor = new Color(0.3f, 0.3f, 0.3f, 1f);
                }
                else
                {
                    t.BackgroundColor = new Color(1f, 1f, 1f, 0.06f);
                    t.BorderlineColor = new Color(1f, 1f, 1f, 0.18f);
                    t.BorderlineWidth = 1f;
                    if (t.ChildCount >= 1 && t.GetChildAt(0) is TextLabel lbl1)
                        lbl1.TextColor = Tokens.TextPrimary;
                    if (t.ChildCount >= 2 && t.GetChildAt(1) is TextLabel lbl2)
                        lbl2.TextColor = Tokens.TextSecondary;
                }
            }
            ScrollFocusedRowIntoView();
        }

        // Vertically translate the inner grid so the focused tile's
        // row is fully visible inside _gridClip. Mirrors RowView's
        // horizontal scroll-into-view pattern but on the Y axis.
        // Without this, the grid renders all 183 tiles statically
        // and the user can D-pad-Down past the visible area into
        // off-screen tiles they can't see.
        //
        // Scroll math:
        //   focusedRow = _focusIdx / Columns
        //   topVisibleRow = derived from current PositionY
        //   if focusedRow < topVisibleRow → scroll UP so focused row is at top
        //   if focusedRow > bottomVisibleRow → scroll DOWN so focused row is at bottom
        //   else: no change
        //
        // We track the position via the grid's PositionY directly
        // (anchored top-left). Negative Y = grid scrolled up.
        private void ScrollFocusedRowIntoView()
        {
            if (_focusIdx < 0 || _tiles.Count == 0 || _gridClip == null || _grid == null) return;
            try
            {
                int focusedRow = _focusIdx / Columns;
                float clipH = _gridClip.SizeHeight;
                if (clipH <= 0f) return;  // clip not laid out yet — bail
                int rowsVisible = Math.Max(1, (int)(clipH / RowStride));
                // Total rows of content; cap scroll so we don't
                // pull the grid past its bottom edge.
                int totalRows = (_tiles.Count + Columns - 1) / Columns;
                int maxTopRow = Math.Max(0, totalRows - rowsVisible);

                int currentTopRow = (int)Math.Round(-_grid.PositionY / RowStride);
                int newTopRow = currentTopRow;
                if (focusedRow < currentTopRow)
                {
                    newTopRow = focusedRow;
                }
                else if (focusedRow >= currentTopRow + rowsVisible)
                {
                    newTopRow = focusedRow - rowsVisible + 1;
                }
                if (newTopRow < 0) newTopRow = 0;
                if (newTopRow > maxTopRow) newTopRow = maxTopRow;
                if (newTopRow == currentTopRow) return;

                float targetY = -newTopRow * RowStride;
                try { _scrollAnim?.Stop(); } catch { }
                _scrollAnim = new Animation(180);
                _scrollAnim.AnimateTo(_grid, "PositionY", targetY,
                    new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
                _scrollAnim.Play();
            }
            catch { /* scroll math is best-effort; never break focus */ }
        }

        private static void ClearChildren(View parent)
        {
            if (parent == null) return;
            // Iterate from the back so removal indices stay valid.
            uint count = parent.ChildCount;
            for (int i = (int)count - 1; i >= 0; i--)
            {
                View child = parent.GetChildAt((uint)i);
                if (child != null) parent.Remove(child);
            }
        }
    }
}
