using System;
using StreamedTv.Models;
using StreamedTv.Theme;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace StreamedTv.Views
{
    /// <summary>
    /// Always-visible top-left "profile chip" — a 40×40 rounded avatar plus a
    /// small chevron glyph that signals "press OK to switch profiles". Modeled
    /// after the small avatar+caret cluster Netflix shows in the top-left of
    /// its TV home screen: it's a persistent identity marker AND the
    /// affordance for opening the profile switcher dropdown.
    ///
    /// Integration model — IMPORTANT, this differs from ProfileSelectorView:
    ///   • ProfileSelectorView is a full-screen overlay that owns its own
    ///     Open/Close lifecycle, raises itself to the top, fades in/out, and
    ///     consumes keys while open.
    ///   • This chip view does NOT have any of that. It lives PERMANENTLY in
    ///     the main app shell layer (added once at construction, never hidden
    ///     by us). It exposes only Focus(), Blur(), and Activate() — all
    ///     simple visual / event hooks. Program.cs's existing OnKeyEvent is
    ///     the single source of truth for which UI element currently has
    ///     focus. When focus enters the chip, the host calls Focus(); when it
    ///     leaves, Blur(); when OK is pressed while focused, Activate().
    ///   • There is intentionally no HandleKey method here. The host owns key
    ///     routing for the entire shell and decides whether a press belongs
    ///     to the chip, the nav pill, the rows, or an open overlay.
    ///
    /// Visual structure:
    ///   • Outer chip view (this) — wraps content. Transparent BG by default;
    ///     gains a translucent white wash + 2px white border + 1.05× scale on
    ///     focus.
    ///   • Avatar wrapper — 40×40 rounded square. Tinted to the active
    ///     profile's RGB with centered initials. If the profile has a bundled
    ///     avatar image (resolved through HeroView.ResolveBundledAssetPath),
    ///     an ImageView is layered on top so the initials act as the loading
    ///     placeholder + 404 fallback (same trick ProfileSelectorView uses).
    ///   • Chevron "▾" glyph — Inter medium 11pt, TextSecondary. Cues the
    ///     user that activating opens a menu, distinguishing the chip from a
    ///     plain avatar badge.
    ///
    /// Re-paints are cheap: SetActiveProfile tears down only the avatar and
    /// label children of the chip and rebuilds them, leaving the outer chip
    /// container, layout, focus state, and any in-flight scale animation
    /// untouched. Program.cs is expected to call SetActiveProfile both at
    /// construction time AND on every ProfileService.ActiveChanged event.
    /// </summary>
    public sealed class ProfileChipView : View
    {
        // --- Layout constants ------------------------------------------------
        // Mirrored from the spec; kept private so this view stays
        // self-contained and isn't accidentally tweaked elsewhere.
        private const int AvatarSize         = 40;
        private const int AvatarCornerRadius = 8;
        private const int ChipCornerRadius   = 10;

        // --- Children we may rebuild on SetActiveProfile ---------------------
        // Held as fields so SetActiveProfile can clear them by reference (we
        // also have a generic ClearChildren helper but tracking the avatar
        // wrapper individually keeps focus-time inspection cheap if we ever
        // want to recolor it without a full rebuild).
        private View _avatar;
        private TextLabel _chevron;

        // --- Focus state + animation ----------------------------------------
        // Single cached Animation field — Stop+rebind on every Focus/Blur so
        // rapid focus toggles (e.g. left-right ping-pong between the chip and
        // the nav pill) don't pile up animations and lurch the scale.
        private bool _focused;
        private Animation _scaleAnim;

        /// <summary>True while this chip currently owns shell focus. The host
        /// is the source of truth — we just expose this so callers don't have
        /// to track an extra parallel bool.
        ///
        /// Marked <c>new</c> because <see cref="View"/> already exposes a
        /// HasFocus() *method* tied to NUI's built-in keyboard-focus manager,
        /// which we don't use (Program.cs does shell-wide manual key
        /// routing). The two are intentionally independent — this property
        /// reflects our shell's notion of focus, not NUI's.</summary>
        public new bool HasFocus => _focused;

        /// <summary>Raised when the host calls <see cref="Activate"/> (i.e.
        /// when OK was pressed while the chip had focus). Subscribers
        /// typically respond by opening a profile switcher dropdown.</summary>
        public event EventHandler Activated;

        public ProfileChipView()
        {
            // Wrap-content in both axes — the chip is whatever size the
            // 40×40 avatar + 8px gap + chevron glyph naturally measure to,
            // plus the asymmetric padding below.
            WidthSpecification  = LayoutParamPolicies.WrapContent;
            HeightSpecification = LayoutParamPolicies.WrapContent;

            // Asymmetric padding: extra breathing room left/right (6+6) and
            // a touch on top (10) so the chip's focus halo doesn't visually
            // collide with the screen edge or the nav pill below it.
            // Order: Extents(start, end, top, bottom).
            Padding = new Extents(6, 10, 6, 6);

            CornerRadius       = new Vector4(ChipCornerRadius, ChipCornerRadius, ChipCornerRadius, ChipCornerRadius);
            CornerRadiusPolicy = VisualTransformPolicyType.Absolute;

            // Transparent at rest. Focus state paints a faint white wash so
            // the chip reads as "selected" against the dark shell BG.
            BackgroundColor = new Color(0f, 0f, 0f, 0f);

            Layout = new LinearLayout
            {
                LinearOrientation   = LinearLayout.Orientation.Horizontal,
                VerticalAlignment   = VerticalAlignment.Center,
                CellPadding         = new Size2D(8, 0),
            };

            // Render an empty placeholder up front so the chip has a defined
            // footprint even before SetActiveProfile is called — Program.cs's
            // first paint shouldn't show a zero-size hole.
            SetActiveProfile(null);
        }

        /// <summary>Re-paint the chip for a new active profile. Tears down
        /// the previous avatar/label children and rebuilds them. Safe to
        /// call repeatedly — Program.cs is expected to invoke this both at
        /// construction time and on every ProfileService.ActiveChanged
        /// event.
        ///
        /// Passing null renders a neutral placeholder (dark gray BG, "·"
        /// glyph) and SUPPRESSES the chevron — the chevron only makes sense
        /// when there's a real profile to switch away from.</summary>
        public void SetActiveProfile(Profile p)
        {
            // Wipe the prior contents. We rebuild from scratch rather than
            // mutating in place so we don't have to track every prior child
            // type or worry about stale ImageViews lingering when the new
            // profile has no AvatarImage.
            ClearChildren(this);
            _avatar = null;
            _chevron = null;

            if (p == null)
            {
                BuildPlaceholderAvatar();
                // Intentionally no chevron in the placeholder state.
                return;
            }

            BuildProfileAvatar(p);
            BuildChevron();
        }

        /// <summary>Apply the focus visuals + scale-up animation. The host
        /// calls this when shell focus enters the chip.</summary>
        public void Focus()
        {
            _focused = true;

            // White wash + 2px white border. Negative BorderlineOffset pulls
            // the stroke fully inside the chip's CornerRadius silhouette so
            // it reads as a clean inset border rather than an outer glow.
            BackgroundColor  = new Color(1f, 1f, 1f, 0.10f);
            BorderlineColor  = Color.White;
            BorderlineWidth  = 2f;
            BorderlineOffset = -2f;

            AnimateScale(Dimens.FocusScale);
        }

        /// <summary>Drop focus visuals and animate the chip back to its
        /// resting size. Safe to call when not focused (no-op-ish — just
        /// re-clears the BG/border to known defaults).</summary>
        public void Blur()
        {
            _focused = false;

            BackgroundColor  = new Color(0f, 0f, 0f, 0f);
            BorderlineColor  = new Color(0f, 0f, 0f, 0f);
            BorderlineWidth  = 0f;
            BorderlineOffset = 0f;

            AnimateScale(1.0f);
        }

        /// <summary>Raise <see cref="Activated"/>. The host wires this to its
        /// OK key handler when the chip is focused. We deliberately do NOT
        /// change focus state or visuals here — the host decides what
        /// happens next (typically: open a dropdown that itself takes
        /// focus).</summary>
        public void Activate()
        {
            Activated?.Invoke(this, EventArgs.Empty);
        }

        // ------------- Builders ----------------------------------------------

        private void BuildProfileAvatar(Profile p)
        {
            // Solid color tile tinted from the profile's persisted RGB —
            // matches the seed palette ProfileService picks at create time.
            Color avatarBg = new Color(p.AvatarR, p.AvatarG, p.AvatarB, 1f);

            // Avatar wrapper. ClipChildren + CornerRadius mask any layered
            // image to the rounded silhouette so it can't bleed past the
            // corners. BorderlineOffset = -1 inset the stroke so the rounded
            // edges line up with the fill.
            //
            // NO Layout on the wrapper: with a LinearLayout, the layout's
            // measure pass squeezes the FillToParent ImageView into the cell
            // sized by the initials TextLabel — that's the bug where you see
            // the image floating in a small centered rect with the colored bg
            // + initials still visible around the edges. Absolute positioning
            // + FillToParent gives the image the full 40×40 cell.
            View avatar = new View
            {
                WidthSpecification = AvatarSize,
                HeightSpecification = AvatarSize,
                BackgroundColor = avatarBg,
                BorderlineColor = new Color(1f, 1f, 1f, 0.20f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(AvatarCornerRadius, AvatarCornerRadius, AvatarCornerRadius, AvatarCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                ClippingMode = ClippingModeType.ClipChildren,
            };

            // Initials sit underneath any layered image and act as the
            // load-time placeholder + 404 fallback. ParentOrigin/PivotPoint =
            // Center keeps the glyph centered without needing a LinearLayout.
            TextLabel initials = new TextLabel
            {
                Text = p.Initials,
                TextColor = Color.White,
                PointSize = 14f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                WidthSpecification  = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                ParentOrigin = Tizen.NUI.ParentOrigin.Center,
                PivotPoint   = Tizen.NUI.PivotPoint.Center,
            };
            avatar.Add(initials);

            // Layer a bundled avatar image on top of the initials when the
            // profile has one. FillToParent + ScaleToFill so the image
            // covers the whole 40×40 cell; clipping handles the rounded
            // mask. Going through HeroView's resolver keeps all bundled
            // shared/res/ asset lookups on a single code path.
            string avatarImagePath = HeroView.ResolveBundledAssetPath(p.AvatarImage);
            if (!string.IsNullOrEmpty(avatarImagePath))
            {
                ImageView img = new ImageView
                {
                    ResourceUrl = avatarImagePath,
                    WidthResizePolicy = ResizePolicyType.FillToParent,
                    HeightResizePolicy = ResizePolicyType.FillToParent,
                    FittingMode = FittingModeType.ScaleToFill,
                    // Pre-resample at load time so the GPU isn't bilinear-
                    // downscaling a 384px source into a ~40px cell every
                    // frame — that downscale was the "pixelated avatar" bug.
                    // 2× the display size gives crisp HiDPI headroom.
                    DesiredWidth = AvatarSize * 2,
                    DesiredHeight = AvatarSize * 2,
                    ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft,
                    PivotPoint   = Tizen.NUI.PivotPoint.TopLeft,
                };
                avatar.Add(img);
            }

            Add(avatar);
            _avatar = avatar;
        }

        private void BuildPlaceholderAvatar()
        {
            // Neutral dark-gray pill with a single dot glyph — distinct from
            // any colored profile so a brief flash of the placeholder
            // (e.g. before ProfileService finishes loading) doesn't get
            // mistaken for a real profile.
            View avatar = new View
            {
                WidthSpecification = AvatarSize,
                HeightSpecification = AvatarSize,
                BackgroundColor = new Color(0.16f, 0.16f, 0.18f, 1f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.20f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(AvatarCornerRadius, AvatarCornerRadius, AvatarCornerRadius, AvatarCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                Layout = new LinearLayout
                {
                    LinearOrientation   = LinearLayout.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    LinearAlignment     = LinearLayout.Alignment.Center,
                },
            };

            TextLabel dot = new TextLabel
            {
                Text = "·",
                TextColor = Tokens.TextSecondary,
                PointSize = 14f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                WidthSpecification  = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            avatar.Add(dot);

            Add(avatar);
            _avatar = avatar;
        }

        private void BuildChevron()
        {
            // Down-caret glyph — communicates "this opens a menu". Inter
            // medium at 11pt sits visually below the bold initials so the
            // avatar stays the primary read.
            TextLabel chev = new TextLabel
            {
                Text = "▾",
                TextColor = Tokens.TextSecondary,
                PointSize = 11f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("medium")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                WidthSpecification  = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            Add(chev);
            _chevron = chev;
        }

        // ------------- Animation + helpers -----------------------------------

        private void AnimateScale(float target)
        {
            // Stop the prior scale anim before launching a new one — without
            // this, rapid focus toggles can leave two animations fighting
            // for the Scale property and the chip pops to an inconsistent
            // size. Same pattern ProfileSelectorView uses for its tile
            // scales.
            _scaleAnim?.Stop();
            _scaleAnim = new Animation(Dimens.AnimFocusScale);
            _scaleAnim.AnimateTo(this, "Scale",
                new Vector3(target, target, 1f),
                new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOutSquare));
            _scaleAnim.Play();
        }

        // Local copy of ProfileSelectorView's helper — both views need to
        // empty a container without triggering layout churn from index
        // arithmetic. Removing GetChildAt(0) repeatedly is the NUI-idiomatic
        // way to drain children regardless of count.
        private static void ClearChildren(View v)
        {
            if (v == null) return;
            uint count = v.ChildCount;
            for (uint i = 0; i < count; i++) v.Remove(v.GetChildAt(0));
        }
    }
}
