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
    /// Reusable modal overlay for picking a bundled avatar image (or
    /// "Initials only" fallback) for a profile.
    ///
    /// Visual structure:
    ///   1. Full-bleed dim backdrop (black @ 0.62 alpha) so the host page
    ///      reads clearly as background.
    ///   2. Centered glass panel (Tokens.SurfaceGlass, 1px SurfaceHighlight
    ///      border, 20px corner radius, 36px horizontal / 28px vertical pad).
    ///   3. Title TextLabel — "Choose your avatar" (PointSize 18, Inter bold,
    ///      TextSecondary, centered).
    ///   4. 2×3 grid of 200×200 tiles (24px gap) — 5 image tiles sourced
    ///      in order from <see cref="ProfileService.BundledAvatars"/>, plus
    ///      a trailing "Initials only" tile (file name = "").
    ///   5. Hint footer ("OK select · Back cancel", PointSize 11, TextTertiary).
    ///
    /// Each image tile clips a bundled JPG into a 200×200 rounded card
    /// (CornerRadius 20, ClippingMode.ClipChildren). Files are resolved via
    /// <see cref="HeroView.ResolveBundledAssetPath"/> so all bundled-asset
    /// resolution funnels through the same Tizen .tpk-aware lookup as the
    /// profile selector. If a file can't be resolved, the tile shows a "?"
    /// glyph as a broken-asset placeholder.
    ///
    /// The "Initials only" tile renders as a dark gray card with a centered
    /// "Aa" glyph — same dark-card styling as the "Add profile" tile in
    /// <see cref="ProfileSelectorView.BuildAddProfileTile"/> so it reads
    /// instantly as the no-image option.
    ///
    /// Focus model: <see cref="_focusedIndex"/> walks 0..5 over a flat tile
    /// list; D-pad Left/Right moves within the row (clamped, no wrap),
    /// Up/Down moves between the two rows preserving column. Focused tile
    /// gets a 4px white border + 1.06× scale via cached EaseOutSquare
    /// animation (matching ProfileSelectorView's AnimateScale pattern so
    /// rapid D-pad presses don't stack up Animation instances).
    ///
    /// Open/Close is the same fade pattern as ProfileSelectorView and
    /// OnScreenKeyboardView — Opacity = 0 + Hide() in the constructor,
    /// Open() does Show + RaiseToTop + opacity-fade-in on a cached
    /// Animation, Close() reverses it. Selection events do NOT call Close()
    /// — the host owns the lifecycle so it can chain to follow-up flows
    /// (e.g. saving the choice, refreshing the profile selector) before
    /// dismissing the overlay.
    /// </summary>
    public sealed class AvatarPickerView : View
    {
        // ---- Public configuration / contract ----

        /// <summary>Pre-selected avatar file name, applied on each <see cref="Open"/>.
        /// Empty string means the trailing "Initials only" tile is focused.
        /// A non-empty value that doesn't match any bundled file also falls
        /// through to focusing the "Initials only" tile (defensive — host
        /// might have a stale file name from a renamed bundled asset).</summary>
        public string SelectedFileName { get; set; }

        public bool IsOpen { get; private set; }

        /// <summary>Fired when the user activates a tile. Payload is the
        /// chosen file name from <see cref="ProfileService.BundledAvatars"/>,
        /// or "" for the "Initials only" tile. The host is responsible for
        /// closing the overlay (we don't auto-close so the host can chain
        /// other work first).</summary>
        public event EventHandler<string> AvatarPicked;

        /// <summary>Fired when the user dismisses the overlay with Back/Escape.
        /// Same Close()-not-called contract as <see cref="AvatarPicked"/>.</summary>
        public event EventHandler Cancelled;

        // ---- Layout constants (1920x1080 reference) ----
        // Tile size and gap come from the brief — bigger than ProfileSelector's
        // 220×220 because the avatar picker shows fewer items at once and
        // benefits from larger preview thumbnails.
        private const int TileSize = 240;
        private const int TileGap = 28;
        private const int TileCornerRadius = 20;
        private const int Columns = 3;
        private const int Rows = 2;
        // 6 tiles total: 5 image tiles + 1 "Initials only" tile. The
        // initials tile lives at index TileCount - 1 so SelectedFileName == ""
        // can map straight to it without searching.
        private const int TileCount = Columns * Rows;
        private const int InitialsTileIndex = TileCount - 1;

        private const int PanelHorizontalPad = 36;
        private const int PanelVerticalPad = 28;
        private const int PanelCornerRadius = 20;

        // ---- Layout pieces ----
        private readonly View _panel;
        private readonly View _grid;

        // Flat tile list parallel to _tileFileNames — used by the focus visual
        // pass so it can re-paint borders + animate scale without rebuilding
        // the layout tree. Indices match the visual order: row 0 left-to-right,
        // then row 1 left-to-right.
        private readonly List<View> _tiles = new List<View>();

        // File name per tile slot, mirrors _tiles. "" for the initials tile.
        private readonly List<string> _tileFileNames = new List<string>();

        // Per-tile scale animations — same caching strategy as
        // ProfileSelectorView so rapid D-pad spam doesn't stack up animations.
        private readonly Dictionary<View, Animation> _tileScaleAnims = new Dictionary<View, Animation>();

        // Tracks the open/close opacity animation so a re-Open during a fade-out
        // doesn't leave the view stuck mid-fade.
        private Animation _modalOpacityAnim;

        private int _focusedIndex = InitialsTileIndex;

        public AvatarPickerView()
        {
            // Full-bleed dim backdrop so the picker reads as a transient modal
            // over whatever screen invoked it. Same darken value as
            // OnScreenKeyboardView for visual consistency between sibling
            // overlays.
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.FillToParent;
            // Opaque dark backdrop. The earlier translucent (alpha 0.62)
            // version let the editor underneath bleed through and washed
            // out the title + tiles.
            BackgroundColor = Tokens.BgBase;
            Layout = new LinearLayout
            {
                LinearOrientation = LinearLayout.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // ---- Content column ---------------------------------------------
            // No inner card / border — the content sits directly on the
            // opaque dark backdrop so this reads as a dedicated screen
            // rather than a popup overlay.
            _panel = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                BorderlineColor = new Color(0f, 0f, 0f, 0f),
                BorderlineWidth = 0f,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Padding = new Extents(PanelHorizontalPad, PanelHorizontalPad, PanelVerticalPad, PanelVerticalPad),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Center,
                    CellPadding = new Size2D(0, 28),
                },
            };
            Add(_panel);

            // ---- Title ------------------------------------------------------
            TextLabel title = new TextLabel
            {
                Text = "Choose your avatar",
                TextColor = Tokens.TextPrimary,
                PointSize = 36f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _panel.Add(title);

            // ---- Tile grid (vertical stack of two horizontal rows) ---------
            // We build the grid as a vertical LinearLayout containing two
            // horizontal row hosts. NUI doesn't have a real GridLayout in this
            // codebase's pattern — same approach as OnScreenKeyboardView's
            // _gridHost.
            _grid = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    LinearAlignment = LinearLayout.Alignment.Center,
                    CellPadding = new Size2D(0, TileGap),
                },
            };
            _panel.Add(_grid);

            BuildTiles();

            // ---- Hint footer -----------------------------------------------
            TextLabel hint = new TextLabel
            {
                Text = "OK select · Back cancel",
                TextColor = Tokens.TextTertiary,
                PointSize = 14f,
                FontFamily = Tokens.FontFamily,
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                // Top margin matches the brief's 14px gap between grid and hint.
                Margin = new Extents(0, 0, 14, 0),
            };
            _panel.Add(hint);

            // Hidden by default; Open() fades in. Same pattern as the other
            // overlays in this directory (ProfileSelectorView /
            // OnScreenKeyboardView) so the host can construct an idle
            // instance once and Show/Hide it on demand.
            Opacity = 0f;
            Hide();
        }

        // ---- Public API -----------------------------------------------------

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;

            // Resolve initial focus from SelectedFileName. Empty/null/unknown
            // names land on the trailing "Initials only" tile so the user
            // re-enters the picker on the choice that matches their current
            // profile state.
            _focusedIndex = ResolveInitialFocus(SelectedFileName);
            ApplyFocusVisual();

            Show();
            RaiseToTop();
            _modalOpacityAnim?.Stop();
            _modalOpacityAnim = new Animation(Dimens.AnimChromeFade);
            _modalOpacityAnim.AnimateTo(this, "Opacity", 1f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            _modalOpacityAnim.Play();
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            _modalOpacityAnim?.Stop();
            _modalOpacityAnim = new Animation(Dimens.AnimChromeFade);
            _modalOpacityAnim.AnimateTo(this, "Opacity", 0f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseIn));
            _modalOpacityAnim.Finished += (s, e) => Hide();
            _modalOpacityAnim.Play();
        }

        public void HandleKey(string key)
        {
            if (!IsOpen) return;
            switch (key)
            {
                case "Left":  MoveFocus(0, -1); break;
                case "Right": MoveFocus(0, +1); break;
                case "Up":    MoveFocus(-1, 0); break;
                case "Down":  MoveFocus(+1, 0); break;
                case "Return":
                case "Select":
                case "KP_Enter":
                case "OK":
                case "Ok":
                case "ok":
                case "XF86Ok":
                    ActivateFocused();
                    break;
                case "BackSpace":
                case "XF86Back":
                case "Escape":
                    // Host owns Close() — same contract as OnScreenKeyboardView
                    // so the host can run follow-up logic (revert preview,
                    // restore prior focus, etc.) before dismissing.
                    Cancelled?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        // ---- Tile builders --------------------------------------------------

        private void BuildTiles()
        {
            _tiles.Clear();
            _tileFileNames.Clear();
            _tileScaleAnims.Clear();
            ClearChildren(_grid);

            IReadOnlyList<ProfileService.BundledAvatar> bundled = ProfileService.BundledAvatars;

            // Two rows, three columns. Image tiles fill positions 0..4 in
            // bundled-avatar order; position 5 is the "Initials only" tile.
            for (int r = 0; r < Rows; r++)
            {
                View rowHost = new View
                {
                    BackgroundColor = new Color(0f, 0f, 0f, 0f),
                    WidthSpecification = LayoutParamPolicies.WrapContent,
                    HeightSpecification = LayoutParamPolicies.WrapContent,
                    Layout = new LinearLayout
                    {
                        LinearOrientation = LinearLayout.Orientation.Horizontal,
                        LinearAlignment = LinearLayout.Alignment.CenterVertical,
                        CellPadding = new Size2D(TileGap, 0),
                    },
                };
                for (int c = 0; c < Columns; c++)
                {
                    int idx = r * Columns + c;
                    View tile;
                    string fileName;
                    if (idx == InitialsTileIndex)
                    {
                        // Trailing tile — always "Initials only" regardless of
                        // how many bundled avatars the catalog has, so the
                        // user can always opt back into initials.
                        tile = BuildInitialsTile();
                        fileName = "";
                    }
                    else if (idx < bundled.Count)
                    {
                        ProfileService.BundledAvatar a = bundled[idx];
                        tile = BuildImageTile(a.FileName);
                        fileName = a.FileName;
                    }
                    else
                    {
                        // Defensive: if the catalog ever shrinks below 5 we
                        // still render a 200×200 placeholder so the grid
                        // stays a clean 2×3 instead of going ragged. The
                        // placeholder reuses the broken-asset visual.
                        tile = BuildBrokenAssetTile();
                        fileName = "";
                    }
                    rowHost.Add(tile);
                    _tiles.Add(tile);
                    _tileFileNames.Add(fileName);
                }
                _grid.Add(rowHost);
            }
        }

        private View BuildImageTile(string fileName)
        {
            // 200×200 rounded card with the bundled image clipped to fit.
            // ClippingMode + CornerRadius mask the underlying ImageView to the
            // rounded silhouette — same masking trick as
            // ProfileSelectorView's avatar.
            View tile = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.20f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(TileCornerRadius, TileCornerRadius, TileCornerRadius, TileCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                ClippingMode = ClippingModeType.ClipChildren,
                WidthSpecification = TileSize,
                HeightSpecification = TileSize,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Center,
                },
            };

            // Resolve through HeroView so all bundled shared/res/ asset
            // lookups go through the same Tizen .tpk-aware path as the rest
            // of the app. Returns null when the file isn't on disk in any of
            // the known candidate layouts — we fall back to a "?" glyph so
            // the user gets a clearly-broken visual instead of an empty card
            // that looks intentional.
            string path = HeroView.ResolveBundledAssetPath(fileName);
            if (!string.IsNullOrEmpty(path))
            {
                ImageView img = new ImageView
                {
                    ResourceUrl = path,
                    WidthResizePolicy = ResizePolicyType.FillToParent,
                    HeightResizePolicy = ResizePolicyType.FillToParent,
                    FittingMode = FittingModeType.ScaleToFill,
                };
                tile.Add(img);
            }
            else
            {
                // Broken-asset placeholder — visually distinct from the
                // initials tile (which is intentional) so the user can tell
                // it's a missing file rather than a design choice.
                TextLabel missing = new TextLabel
                {
                    Text = "?",
                    TextColor = Color.White,
                    PointSize = 36f,
                    FontFamily = Tokens.FontFamily,
                    FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                    MultiLine = false,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    WidthSpecification = LayoutParamPolicies.WrapContent,
                    HeightSpecification = LayoutParamPolicies.WrapContent,
                };
                tile.Add(missing);
                // Give the empty card a faint surface so it doesn't look like
                // a transparent hole in the panel.
                tile.BackgroundColor = new Color(0.16f, 0.16f, 0.18f, 1f);
            }

            return tile;
        }

        private View BuildInitialsTile()
        {
            // Same dark-card visual as ProfileSelectorView's "Add profile"
            // tile so the no-image option reads consistently across the app.
            View tile = new View
            {
                BackgroundColor = new Color(0.16f, 0.16f, 0.18f, 1f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.20f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(TileCornerRadius, TileCornerRadius, TileCornerRadius, TileCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                ClippingMode = ClippingModeType.ClipChildren,
                WidthSpecification = TileSize,
                HeightSpecification = TileSize,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Center,
                },
            };

            TextLabel glyph = new TextLabel
            {
                Text = "Aa",
                TextColor = Color.White,
                PointSize = 36f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            tile.Add(glyph);
            return tile;
        }

        private View BuildBrokenAssetTile()
        {
            // Defensive placeholder used only when BundledAvatars has fewer
            // than 5 entries — keeps the 2×3 grid shape from collapsing.
            View tile = new View
            {
                BackgroundColor = new Color(0.16f, 0.16f, 0.18f, 1f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.20f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(TileCornerRadius, TileCornerRadius, TileCornerRadius, TileCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                ClippingMode = ClippingModeType.ClipChildren,
                WidthSpecification = TileSize,
                HeightSpecification = TileSize,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Center,
                },
            };
            TextLabel missing = new TextLabel
            {
                Text = "?",
                TextColor = Color.White,
                PointSize = 36f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            tile.Add(missing);
            return tile;
        }

        // ---- Focus / activation --------------------------------------------

        private int ResolveInitialFocus(string fileName)
        {
            // Empty / null → "Initials only" tile.
            if (string.IsNullOrEmpty(fileName)) return InitialsTileIndex;

            // Search the image tiles only (skip the trailing initials slot).
            // Match exact file name; ordinal compare matches the storage
            // semantics of Profile.AvatarImage (raw file name).
            for (int i = 0; i < _tileFileNames.Count - 1; i++)
            {
                if (string.Equals(_tileFileNames[i], fileName, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            // Unknown file (e.g. a renamed bundled asset) — gracefully fall
            // through to the initials tile rather than picking an arbitrary
            // image and surprising the user.
            return InitialsTileIndex;
        }

        private void MoveFocus(int dr, int dc)
        {
            // Convert flat index back to row/col, clamp the move, and reproject.
            // The grid is a fixed 2×3 so we don't need to deal with ragged rows
            // — every move is either valid in-grid or blocked at the edge.
            int row = _focusedIndex / Columns;
            int col = _focusedIndex % Columns;

            int newRow = row + dr;
            int newCol = col + dc;
            if (newRow < 0 || newRow >= Rows) return;
            if (newCol < 0 || newCol >= Columns) return;

            int newIdx = newRow * Columns + newCol;
            if (newIdx < 0 || newIdx >= _tiles.Count) return;
            if (newIdx == _focusedIndex) return;

            _focusedIndex = newIdx;
            ApplyFocusVisual();
        }

        private void ActivateFocused()
        {
            if (_focusedIndex < 0 || _focusedIndex >= _tileFileNames.Count) return;
            string fileName = _tileFileNames[_focusedIndex];
            // fileName is "" for the initials tile — that's the documented
            // payload for "user picked the initials option". Host distinguishes
            // by checking string.IsNullOrEmpty.
            AvatarPicked?.Invoke(this, fileName);
        }

        private void ApplyFocusVisual()
        {
            for (int i = 0; i < _tiles.Count; i++)
            {
                View tile = _tiles[i];
                if (tile == null) continue;
                bool isFocused = i == _focusedIndex;

                if (isFocused)
                {
                    tile.BorderlineColor = Color.White;
                    tile.BorderlineWidth = 4f;
                }
                else
                {
                    tile.BorderlineColor = new Color(1f, 1f, 1f, 0.20f);
                    tile.BorderlineWidth = 1f;
                }
                AnimateScale(tile, isFocused ? 1.06f : 1.0f);
            }
        }

        private void AnimateScale(View v, float target)
        {
            // Cache + Stop() the prior animation so a stream of D-pad presses
            // doesn't pile up overlapping Animation instances on the same
            // tile (which would cause visible jitter as they fight each
            // other). Same pattern as ProfileSelectorView.AnimateScale.
            Animation prev;
            if (_tileScaleAnims.TryGetValue(v, out prev) && prev != null) prev.Stop();
            Animation a = new Animation(Dimens.AnimFocusScale);
            a.AnimateTo(v, "Scale",
                new Vector3(target, target, 1f),
                new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOutSquare));
            a.Play();
            _tileScaleAnims[v] = a;
        }

        // ---- Helpers --------------------------------------------------------

        private static void ClearChildren(View v)
        {
            // Same idiom as ProfileSelectorView.ClearChildren — Remove(GetChildAt(0))
            // in a count-bounded loop because NUI's child collection mutates
            // as we go.
            if (v == null) return;
            uint count = v.ChildCount;
            for (uint i = 0; i < count; i++) v.Remove(v.GetChildAt(0));
        }
    }
}
