using System;
using System.Collections.Generic;
using StreamedTv.Models;
using StreamedTv.Theme;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace StreamedTv.Views
{
    /// <summary>
    /// Netflix-style profile-switcher popover, anchored under the top-left
    /// profile chip in the app chrome. The dropdown is the lightweight,
    /// inline counterpart to the full-screen <see cref="ProfileSelectorView"/>
    /// "Who's watching?" picker — a user who only wants to swap to a known
    /// sibling profile shouldn't have to leave the page they're browsing.
    ///
    /// Vertical structure (top to bottom, mirrors the Netflix TV reference):
    ///   1. Active profile row — avatar + name + "Switch profiles" subtext.
    ///      Activating this row delegates to the full picker (so the user
    ///      can manage the current profile, see all initials/avatars at
    ///      poster-size, etc.) — it does NOT re-select the active profile.
    ///   2. One row per non-active profile, in their original list order.
    ///      Activating one of these rows performs an immediate switch
    ///      (host calls Close() and swaps the session profile).
    ///   3. Faint divider.
    ///   4. "Manage profiles" row — opens the rename/delete/edit-avatar
    ///      surface (whatever the host wires it up to).
    ///
    /// Layout: pinned at top-left (PositionX=32, PositionY=90), 360px wide,
    /// height wraps to content. Rounded glass panel matching the rest of the
    /// app chrome — same SurfaceGlass + 1px SurfaceHighlight inner stroke
    /// used by <see cref="OnScreenKeyboardView"/>'s panel and the nav pills,
    /// so this overlay reads as part of the same chrome family rather than
    /// as a foreign popup.
    ///
    /// Focus: D-pad Up/Down walks the focusable rows (skipping the divider).
    /// Left/Right are intentionally ignored — there's no horizontal motion
    /// inside the dropdown, and swallowing them prevents the host from
    /// accidentally moving page focus while the popover is open. Focused
    /// row paints a white@0.10 wash and lifts to scale 1.02 with the same
    /// EaseOutSquare curve as the tile focus animation.
    ///
    /// Open/Close: opacity fade matching <see cref="OnScreenKeyboardView"/>
    /// (Dimens.AnimChromeFade), Show/Hide gated on the IsOpen flag, plus a
    /// RaiseToTop on open so the dropdown always wins z-order against the
    /// page underneath.
    ///
    /// Standalone — knows nothing about ProfileService. The host wires
    /// <see cref="SwitchToProfile"/> / <see cref="OpenPicker"/> /
    /// <see cref="OpenManage"/> / <see cref="Closed"/> to its own router
    /// and feeds keys via <see cref="HandleKey"/>.
    /// </summary>
    public sealed class ProfileSwitcherDropdownView : View
    {
        // ---- Layout constants (1920x1080 reference) ----
        // Anchor under the chip in the top-left of the app chrome.
        private const int AnchorX = 32;
        private const int AnchorY = 90;

        private const int PanelWidth = 420;
        private const int PanelCornerRadius = 24;
        private const int PanelVerticalPad = 10;
        private const int RowSideInset = 10;

        // Row sizing — active + other rows are uniform 64; manage row is a
        // touch shorter (56) so it visually de-emphasises against the
        // profile rows above it.
        private const int RowHeightProfile = 88;
        private const int RowHeightManage  = 76;
        private const int RowHorizontalPad = 18;

        private const int AvatarSize = 56;
        private const int AvatarCornerRadius = 12;
        private const int AvatarTextGap = 14;

        private const int DividerThickness = 1;
        private const int DividerSideInset = 14;
        private const int DividerMarginTop = 6;
        private const int DividerMarginBottom = 6;

        private const float FocusedRowScale = 1.02f;

        // ---- Row role tracking ----
        // Roles let HandleKey route Activate to the correct event without
        // string-matching row text or peeking at index ranges.
        private enum RowRole { ActiveProfile, OtherProfile, Manage, SignOut }

        private sealed class RowEntry
        {
            public View Tile;             // the focusable row container
            public RowRole Role;
            public string ProfileId;       // null for manage row
            // Text labels we re-tint on focus (white-on-dark → dark-on-white,
            // matching the nav pill's active chip style).
            public TextLabel NameLabel;
            public TextLabel SubLabel;     // null for non-active rows
            public TextLabel GlyphLabel;   // null for profile rows; the manage row's "✎"
        }

        // ---- Layout pieces ----
        private readonly View _panel;

        // Focusable rows in display order (active, then others, then manage).
        // Excludes the divider — the divider is in the panel's child tree
        // but not in this list, so Up/Down skips over it cleanly.
        private readonly List<RowEntry> _rows = new List<RowEntry>();

        // Per-row scale animations so rapid Up/Down doesn't stack lifts.
        private readonly Dictionary<View, Animation> _rowScaleAnims = new Dictionary<View, Animation>();

        // Cached open/close opacity animation — re-Open during a fade-out
        // would otherwise leave the view stranded mid-fade.
        private Animation _modalOpacityAnim;

        private int _focusedIndex = -1;

        public bool IsOpen { get; private set; }

        public event EventHandler<string> SwitchToProfile;
        public event EventHandler OpenPicker;
        public event EventHandler OpenManage;
        public event EventHandler SignOut;
        public event EventHandler Closed;

        public ProfileSwitcherDropdownView()
        {
            // The dropdown is positioned manually at the top-left — it's a
            // pop-out from the chip, not a centered modal, so we set absolute
            // PositionX/Y on `this` and let the panel inside drive its own
            // wrap-content sizing.
            PositionX = AnchorX;
            PositionY = AnchorY;
            WidthSpecification = PanelWidth;
            HeightSpecification = LayoutParamPolicies.WrapContent;
            BackgroundColor = new Color(0f, 0f, 0f, 0f);

            _panel = new View
            {
                // Opaque dark surface — the earlier translucent SurfaceGlass
                // washed text out against the bright app content underneath.
                BackgroundColor = new Color(0.06f, 0.06f, 0.08f, 1f),
                BorderlineColor = Tokens.SurfaceHighlight,
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(PanelCornerRadius, PanelCornerRadius, PanelCornerRadius, PanelCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                // Clip the focused row's full-width white wash to the panel's
                // rounded corners. Without this, the focus pill bled past
                // the panel edges at the top/bottom corners; with it we can
                // keep rows flush full-width and still get nice rounding.
                ClippingMode = ClippingModeType.ClipChildren,
                WidthSpecification = PanelWidth,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                // Rows own their own horizontal padding so the focus wash
                // can extend full-width inside the panel; we only pad
                // vertically here for breathing room above the first row
                // and below the last.
                Padding = new Extents(0, 0, PanelVerticalPad, PanelVerticalPad),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    LinearAlignment = LinearLayout.Alignment.Top,
                },
            };
            Add(_panel);

            // Hidden by default; Open() fades in.
            Opacity = 0f;
            Hide();
        }

        /// <summary>Rebuild the dropdown contents from the given profile
        /// list. The profile matched by <paramref name="activeId"/> is
        /// rendered as the active row at the top with the "Switch profiles"
        /// subtext; remaining profiles render as direct-switch rows in
        /// their original list order. If <paramref name="activeId"/>
        /// doesn't match any profile, the first profile in the list is
        /// treated as active. If the list is null or empty, only the
        /// "Manage profiles" row is rendered.</summary>
        public void SetProfiles(IReadOnlyList<Profile> all, string activeId)
        {
            _rows.Clear();
            _rowScaleAnims.Clear();
            ClearChildren(_panel);

            int profileCount = all != null ? all.Count : 0;

            if (profileCount > 0)
            {
                // Resolve active profile — explicit id match first, else
                // fall back to the first entry so the dropdown always has
                // a meaningful "active" row when there are profiles.
                int activeIndex = -1;
                if (!string.IsNullOrEmpty(activeId))
                {
                    for (int i = 0; i < profileCount; i++)
                    {
                        Profile p = all[i];
                        if (p == null) continue;
                        if (string.Equals(p.Id, activeId, StringComparison.Ordinal))
                        {
                            activeIndex = i;
                            break;
                        }
                    }
                }
                if (activeIndex < 0) activeIndex = 0;

                Profile active = all[activeIndex];
                if (active != null)
                {
                    BuildActiveProfileRow(active);
                }

                // Other profiles in original list order — skip the active
                // slot and any null entries. Order matches Netflix's
                // dropdown which preserves the user's account list order
                // rather than re-sorting (so the layout doesn't reshuffle
                // every time the active profile changes).
                for (int i = 0; i < profileCount; i++)
                {
                    if (i == activeIndex) continue;
                    Profile p = all[i];
                    if (p == null) continue;
                    BuildOtherProfileRow(p);
                }

                BuildDivider();
            }

            BuildManageRow();
            BuildSignOutRow();

            // Re-snap focus to the top row whenever the contents change so
            // we never leave _focusedIndex pointing at a stale entry.
            _focusedIndex = _rows.Count > 0 ? 0 : -1;
            ApplyFocusVisual();
        }

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;

            // Always re-snap to the active profile row on open — leftover
            // focus from a prior session would be disorienting since the
            // user re-enters the popover with no prior cue.
            _focusedIndex = _rows.Count > 0 ? 0 : -1;
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
                case "Up":
                    MoveFocus(-1);
                    break;
                case "Down":
                    MoveFocus(+1);
                    break;
                // Left/Right intentionally ignored — no horizontal motion
                // in the popover, and swallowing them prevents stray page
                // movement while the dropdown is up.
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
                    // Host owns the actual Close() call so it can update
                    // any chip-state / focus-restore bookkeeping first.
                    Closed?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        // ---- Internal: row builders ---------------------------------------

        private void BuildActiveProfileRow(Profile p)
        {
            View row = MakeRowContainer(RowHeightProfile);
            row.Add(BuildAvatar(p));
            row.Add(MakeHorizontalGap(AvatarTextGap));

            View textStack = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    LinearAlignment = LinearLayout.Alignment.CenterVertical,
                },
            };
            TextLabel nameLbl = MakeNameLabel(p.Name, Tokens.TextPrimary, 22f, "bold");
            textStack.Add(nameLbl);
            TextLabel subLbl = new TextLabel
            {
                Text = "Switch profiles",
                TextColor = Tokens.TextSecondary,
                PointSize = 14f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("medium")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            textStack.Add(subLbl);
            row.Add(textStack);

            _panel.Add(row);
            _rows.Add(new RowEntry { Tile = row, Role = RowRole.ActiveProfile, ProfileId = p.Id, NameLabel = nameLbl, SubLabel = subLbl });
        }

        private void BuildOtherProfileRow(Profile p)
        {
            View row = MakeRowContainer(RowHeightProfile);
            row.Add(BuildAvatar(p));
            row.Add(MakeHorizontalGap(AvatarTextGap));
            TextLabel nameLbl = MakeNameLabel(p.Name, Tokens.TextPrimary, 22f, "bold");
            row.Add(nameLbl);

            _panel.Add(row);
            _rows.Add(new RowEntry { Tile = row, Role = RowRole.OtherProfile, ProfileId = p.Id, NameLabel = nameLbl });
        }

        private void BuildDivider()
        {
            // 1px divider, inset on both sides so it doesn't kiss the
            // panel's rounded edges. Not focusable — Up/Down jumps over
            // it because it isn't in _rows.
            View divider = new View
            {
                BackgroundColor = Tokens.Divider,
                WidthSpecification = PanelWidth - DividerSideInset * 2,
                HeightSpecification = DividerThickness,
                Margin = new Extents(DividerSideInset, DividerSideInset, DividerMarginTop, DividerMarginBottom),
            };
            _panel.Add(divider);
        }

        private void BuildManageRow()
        {
            View row = MakeRowContainer(RowHeightManage);

            // Pencil glyph — pure-text icon so we don't drag in a font
            // dependency or bundled svg for one row.
            TextLabel glyph = new TextLabel
            {
                Text = "✎",
                TextColor = Tokens.TextSecondary,
                PointSize = 18f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("medium")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            row.Add(glyph);
            row.Add(MakeHorizontalGap(AvatarTextGap));
            TextLabel manageLbl = new TextLabel
            {
                Text = "Manage profiles",
                TextColor = Tokens.TextSecondary,
                PointSize = 18f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("medium")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            row.Add(manageLbl);

            _panel.Add(row);
            _rows.Add(new RowEntry { Tile = row, Role = RowRole.Manage, ProfileId = null, NameLabel = manageLbl, GlyphLabel = glyph });
        }

        // Sign-out row sits below Manage profiles. Same visual weight
        // as Manage (RowHeightManage, secondary text colour, plain
        // glyph) — it's an account-level action, not a primary one,
        // and we want to keep the visual de-emphasis so a user
        // navigating Up/Down doesn't accidentally land here.
        private void BuildSignOutRow()
        {
            View row = MakeRowContainer(RowHeightManage);

            TextLabel glyph = new TextLabel
            {
                Text = "→",
                TextColor = Tokens.TextSecondary,
                PointSize = 18f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("medium")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            row.Add(glyph);
            row.Add(MakeHorizontalGap(AvatarTextGap));
            TextLabel signOutLbl = new TextLabel
            {
                Text = "Sign out",
                TextColor = Tokens.TextSecondary,
                PointSize = 18f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("medium")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            row.Add(signOutLbl);

            _panel.Add(row);
            _rows.Add(new RowEntry { Tile = row, Role = RowRole.SignOut, ProfileId = null, NameLabel = signOutLbl, GlyphLabel = glyph });
        }

        private static View MakeRowContainer(int height)
        {
            // Rectangular full-width rows. The focused white wash fills the
            // entire row edge-to-edge; rounding at the panel's top/bottom
            // corners is handled by the panel's ClippingMode.ClipChildren.
            return new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = PanelWidth,
                HeightSpecification = height,
                Padding = new Extents(RowHorizontalPad, RowHorizontalPad, 0, 0),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.CenterVertical,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }

        private static TextLabel MakeNameLabel(string name, Color color, float pointSize, string weight)
        {
            return new TextLabel
            {
                Text = name ?? "",
                TextColor = color,
                PointSize = pointSize,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue(weight)),
                MultiLine = false,
                Ellipsis = true,
                // Bound the label width to what's left of the row after
                // the avatar + gap + side padding so a long name truncates
                // cleanly instead of blowing the row open.
                WidthSpecification = PanelWidth - (RowHorizontalPad * 2) - AvatarSize - AvatarTextGap,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
        }

        private static View MakeHorizontalGap(int width)
        {
            return new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = width,
                HeightSpecification = 1,
            };
        }

        // Mirrors the avatar render used in ProfileSelectorView/ProfileChip:
        // bundled image when AvatarImage resolves to a real file, otherwise
        // a colored rounded square with centered initials. Clipping to the
        // rounded silhouette is handled by the parent's ClippingMode +
        // CornerRadius combo so the image never bleeds past the corner.
        private static View BuildAvatar(Profile p)
        {
            Color avatarBg = new Color(p.AvatarR, p.AvatarG, p.AvatarB, 1f);
            string avatarImagePath = HeroView.ResolveBundledAssetPath(p.AvatarImage);

            // NO Layout on the wrapper: with a LinearLayout, the layout's
            // measure pass squeezes the FillToParent ImageView into the cell
            // sized by the initials TextLabel — leaves the image floating
            // in a small centered rect with the colored bg + initials still
            // visible around the edges. Absolute positioning + FillToParent
            // gives the image the full 40×40 cell.
            View avatar = new View
            {
                WidthSpecification = AvatarSize,
                HeightSpecification = AvatarSize,
                BackgroundColor = avatarBg,
                CornerRadius = new Vector4(AvatarCornerRadius, AvatarCornerRadius, AvatarCornerRadius, AvatarCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                ClippingMode = ClippingModeType.ClipChildren,
            };

            // Initials always present — they sit beneath the image as the
            // loading placeholder + 404 fallback when AvatarImage points
            // at a missing file. ParentOrigin/PivotPoint = Center keeps
            // the glyph centered without needing a LinearLayout.
            TextLabel initials = new TextLabel
            {
                Text = p.Initials,
                TextColor = Color.White,
                PointSize = 14f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                ParentOrigin = Tizen.NUI.ParentOrigin.Center,
                PivotPoint   = Tizen.NUI.PivotPoint.Center,
            };
            avatar.Add(initials);

            if (!string.IsNullOrEmpty(avatarImagePath))
            {
                ImageView img = new ImageView
                {
                    ResourceUrl = avatarImagePath,
                    WidthResizePolicy = ResizePolicyType.FillToParent,
                    HeightResizePolicy = ResizePolicyType.FillToParent,
                    FittingMode = FittingModeType.ScaleToFill,
                    // Pre-resample at load time so the GPU doesn't have to
                    // bilinear-downscale a 384px source into a ~56px cell on
                    // every frame — that downscale was the "pixelated avatar"
                    // bug. 2× the display size gives crisp HiDPI headroom.
                    DesiredWidth = AvatarSize * 2,
                    DesiredHeight = AvatarSize * 2,
                    ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft,
                    PivotPoint   = Tizen.NUI.PivotPoint.TopLeft,
                };
                avatar.Add(img);
            }

            return avatar;
        }

        // ---- Internal: focus + activation ---------------------------------

        private void MoveFocus(int delta)
        {
            if (_rows.Count == 0) return;
            int next = _focusedIndex + delta;
            // Clamp — no wrap. Wrapping a tiny list this size feels jumpy
            // and Netflix's reference doesn't wrap either.
            if (next < 0) next = 0;
            if (next >= _rows.Count) next = _rows.Count - 1;
            if (next == _focusedIndex) return;
            _focusedIndex = next;
            ApplyFocusVisual();
        }

        private void ActivateFocused()
        {
            if (_focusedIndex < 0 || _focusedIndex >= _rows.Count) return;
            RowEntry entry = _rows[_focusedIndex];
            switch (entry.Role)
            {
                case RowRole.ActiveProfile:
                    OpenPicker?.Invoke(this, EventArgs.Empty);
                    break;
                case RowRole.OtherProfile:
                    if (!string.IsNullOrEmpty(entry.ProfileId))
                    {
                        SwitchToProfile?.Invoke(this, entry.ProfileId);
                    }
                    break;
                case RowRole.Manage:
                    OpenManage?.Invoke(this, EventArgs.Empty);
                    break;
                case RowRole.SignOut:
                    SignOut?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        private void ApplyFocusVisual()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                RowEntry entry = _rows[i];
                View tile = entry.Tile;
                if (tile == null) continue;
                bool focused = (i == _focusedIndex);
                if (focused)
                {
                    // Match the nav-pill active chip — solid white pill with
                    // dark text. No scale (the earlier 1.02× pushed the white
                    // wash past the panel's rounded edges).
                    tile.BackgroundColor = Tokens.ChipActive;
                    if (entry.NameLabel != null)  entry.NameLabel.TextColor  = Tokens.TextOnLight;
                    if (entry.SubLabel  != null)  entry.SubLabel.TextColor   = Tokens.TextOnLight;
                    if (entry.GlyphLabel != null) entry.GlyphLabel.TextColor = Tokens.TextOnLight;
                }
                else
                {
                    tile.BackgroundColor = new Color(0f, 0f, 0f, 0f);
                    if (entry.NameLabel != null)  entry.NameLabel.TextColor  = Tokens.TextPrimary;
                    if (entry.SubLabel  != null)  entry.SubLabel.TextColor   = Tokens.TextSecondary;
                    if (entry.GlyphLabel != null) entry.GlyphLabel.TextColor = Tokens.TextSecondary;
                }
            }
        }

        private void AnimateScale(View v, float target)
        {
            Animation prev;
            if (_rowScaleAnims.TryGetValue(v, out prev) && prev != null) prev.Stop();
            Animation a = new Animation(Dimens.AnimFocusScale);
            a.AnimateTo(v, "Scale",
                new Vector3(target, target, 1f),
                new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOutSquare));
            a.Play();
            _rowScaleAnims[v] = a;
        }

        // ---- Helpers ------------------------------------------------------

        private static void ClearChildren(View v)
        {
            if (v == null) return;
            uint count = v.ChildCount;
            for (uint i = 0; i < count; i++) v.Remove(v.GetChildAt(0));
        }
    }
}
