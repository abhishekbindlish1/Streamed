using System;
using System.Collections.Generic;
using StreamedTv.Models;
using StreamedTv.Theme;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace StreamedTv.Views
{
    /// <summary>
    /// Immutable snapshot of the data the user assembled in
    /// <see cref="ProfileAddWizardView"/>. Emitted via
    /// <see cref="ProfileAddWizardView.Created"/> when both wizard steps
    /// have been completed and the user activates the final "Save" pill.
    ///
    /// The host is responsible for translating this draft into a real
    /// profile (e.g. via <c>ProfileService.AddProfileFull</c>); the wizard
    /// itself never persists anything.
    ///
    /// <para><b>AvatarFileName</b>: empty string means the user picked the
    /// "Initials only" option in <c>AvatarPickerView</c>; a non-empty value
    /// matches a bundled avatar file name from
    /// <c>ProfileService.BundledAvatars</c>.</para>
    /// </summary>
    public sealed class NewProfileDraft
    {
        public string Name { get; }
        public string AvatarFileName { get; }   // empty string = initials only

        public NewProfileDraft(string name, string avatarFileName)
        {
            // Defensive normalization — callers in this file already pass
            // trimmed values, but the public ctor is part of the API contract
            // so we treat null as "" to keep the consumer side simple.
            Name = name ?? "";
            AvatarFileName = avatarFileName ?? "";
        }
    }

    /// <summary>
    /// Two-step "Add profile" wizard, Netflix-styled, that orchestrates
    /// the avatar-pick step and the name-entry step into a single
    /// <see cref="NewProfileDraft"/>.
    ///
    /// <para><b>Why this view exists at all</b> — both <c>AvatarPickerView</c>
    /// and <c>OnScreenKeyboardView</c> are standalone, reusable overlays.
    /// Composing them into a guided flow needs a coordinator that knows the
    /// step order, holds the in-flight values across step transitions, and
    /// produces a single "ok, save this" event at the end. That coordinator
    /// is this view. The "splash" UI it renders is intentionally thin — it's
    /// just the visual context (header, step indicator, optional preview)
    /// that sits BEHIND the picker / keyboard so the user sees what flow
    /// they're in.</para>
    ///
    /// <para><b>Orchestration model — host owns the sub-overlays</b>. The
    /// wizard does NOT directly construct or call into <c>AvatarPickerView</c>
    /// or <c>OnScreenKeyboardView</c>. Instead it fires
    /// <see cref="RequestPickAvatar"/> / <see cref="RequestEditName"/> events
    /// pre-populated with the current value, and the host opens its singleton
    /// picker / keyboard, listens for their result, and pushes that result
    /// back via <see cref="SetPickedAvatar"/> / <see cref="SetEnteredName"/>.
    /// This mirrors how <c>ProfileEditView</c> works — keeping the wizard
    /// dependency-free of the sub-overlay APIs lets the host swap them out,
    /// stack them in a custom layer order, or share singleton instances with
    /// other features (e.g. the editor uses the same keyboard).</para>
    ///
    /// <para><b>Step state machine</b>:
    /// <list type="bullet">
    /// <item><description><c>Step1_Avatar</c>: splash header is rendered;
    /// host is expected to be opening the avatar picker on top. Set after
    /// <see cref="Reset"/>; entered via <see cref="Open"/>.</description></item>
    /// <item><description><c>Step2_Name</c>: splash header + 240×240 avatar
    /// preview is rendered; host is expected to be opening the keyboard on
    /// top. Entered via <see cref="SetPickedAvatar"/>.</description></item>
    /// <item><description><c>Step3_Review</c>: full splash with avatar
    /// preview + entered name + focused "Save profile" pill. The user
    /// activates the pill (OK key) to commit. Entered via
    /// <see cref="SetEnteredName"/>.</description></item>
    /// </list>
    /// </para>
    ///
    /// <para><b>Cancel semantics</b>: if the host calls <see cref="Close"/>
    /// before the user activates "Save" on Step 3, <see cref="Cancelled"/>
    /// is raised. After "Save" fires <see cref="Created"/>, a subsequent
    /// <see cref="Close"/> does NOT raise Cancelled — we track the
    /// committed-vs-cancelled distinction with an internal flag so that the
    /// host can call Close() unconditionally as part of teardown without
    /// double-firing events.</para>
    ///
    /// <para><b>Deferred first-step request</b>: <see cref="Open"/> raises
    /// <see cref="RequestPickAvatar"/> AFTER the modal opacity-in animation
    /// finishes. This guarantees the splash background paints first so that
    /// the avatar picker doesn't appear over a transparent void on slow
    /// devices where the picker fade-in could otherwise outrun the wizard's
    /// own fade-in.</para>
    /// </summary>
    public sealed class ProfileAddWizardView : View
    {
        // ---- Step state machine ---------------------------------------------
        private enum Step { Step1_Avatar, Step2_Name, Step3_Review }

        // ---- Public API -----------------------------------------------------

        public bool IsOpen { get; private set; }

        // Wizard raises these on entering each step. Host opens the
        // sub-overlay pre-populated with the payload value, then calls
        // SetPickedAvatar / SetEnteredName with the result.
        public event EventHandler<string> RequestPickAvatar;   // payload: currently picked avatar (initially "")
        public event EventHandler<string> RequestEditName;     // payload: currently entered name (initially "")

        // Wizard raises Created when both steps are done AND the user
        // activates the final "Save" button. Host applies via
        // ProfileService.AddProfileFull (or equivalent).
        public event EventHandler<NewProfileDraft> Created;

        // Raised by Close() when the wizard is closed in an incomplete
        // state (i.e. before the user activated Save on Step 3). The host
        // typically wires this to "tear down both sub-overlays + return
        // focus to whatever screen invoked the wizard."
        public event EventHandler Cancelled;

        // ---- Layout sizing constants (1920×1080 reference) ------------------
        // Avatar preview lives in Steps 2 and 3. 240×240 is bigger than the
        // selector tile (220×220) so the user gets a clear "this is what
        // your profile photo will be" affordance during the wizard. The
        // corner radius (24) is slightly larger than ProfileSelectorView's
        // (20) so the preview reads as deliberately "polished" vs. the
        // selector grid.
        private const int AvatarPreviewSize = 240;
        private const int AvatarPreviewCornerRadius = 24;

        // Save pill — height 48, padding 18 horizontal / 18 vertical (per
        // brief). Pill-shaped corner radius is computed from height/2 so
        // it scales if we ever bump the height.
        private const int SavePillHeight = 64;
        private const int SavePillHorizontalPad = 28;
        private const int SavePillVerticalPad = 18;
        private const int SavePillCornerRadius = SavePillHeight / 2;

        // ---- Layout pieces --------------------------------------------------
        // _column is the centered vertical stack hosting header + body. It's
        // rebuilt from scratch on every step transition (RenderStep tears
        // down the body region and rebuilds it for the new step). The
        // header text labels live as direct children of _column and get
        // re-text'd in place rather than rebuilt.
        private readonly View _column;
        private readonly TextLabel _wordmark;
        private readonly TextLabel _title;
        private readonly TextLabel _stepIndicator;

        // _body holds the per-step content (empty in Step 1, preview in
        // Step 2, preview + name + Save pill in Step 3). Children are
        // cleared and rebuilt on each step transition.
        private readonly View _body;

        // Save pill view — only present on Step 3. Tracked so HandleKey can
        // route OK to its activation, and so ApplyFocusVisual can re-paint
        // its border / background on focus.
        private View _savePill;
        private TextLabel _savePillLabel;

        // Step 3 has only one focusable element (Save). _focusedIndex is
        // kept for symmetry with the rest of the codebase's focus model
        // and to make a future expansion (multi-button review screen)
        // straightforward.
        private int _focusedIndex = 0;

        // ---- Wizard state ---------------------------------------------------
        private Step _step = Step.Step1_Avatar;
        private string _avatar = "";
        private string _name = "";

        // Goes true the moment Created is fired so a subsequent Close() does
        // NOT also fire Cancelled. Reset on Reset() / Open() so the next
        // run of the wizard starts clean.
        private bool _savedFired;

        // Cached open/close opacity animation — same pattern as
        // ProfileSelectorView so a re-Open during a fade-out doesn't leave
        // the view stuck mid-fade.
        private Animation _modalOpacityAnim;

        public ProfileAddWizardView()
        {
            // Full-screen overlay sitting BEHIND the avatar picker /
            // keyboard sub-overlays. The 0.95 alpha BgBase background
            // provides a near-opaque dark canvas — strong enough to cover
            // any underlying screen, but with a touch of translucency so
            // the wizard reads as "this is a flow on top of the app" rather
            // than "this is a fresh page". When the keyboard / picker
            // overlay opens on top with their own backdrops, the layered
            // effect is intentional.
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.FillToParent;
            // Outer = full-screen overlay. Opaque BgBase so the wizard reads
            // as a dedicated screen, not a popup floating on top of the
            // picker. Sub-overlays (avatar picker, keyboard) layer over this
            // and also use opaque backgrounds — visually it looks like a
            // sequence of screens, not stacked overlays.
            BackgroundColor = Tokens.BgBase;

            // Center the column in the screen. CellPadding 24 in the
            // outer linear layout doesn't apply here (we've only got one
            // child) — the inner column owns its own per-section spacing.
            Layout = new LinearLayout
            {
                LinearOrientation = LinearLayout.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            _column = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Center,
                    // 24px between header rows and the body slot below —
                    // matches the brief's "CellPadding 24" requirement and
                    // keeps the splash feeling deliberately spaced.
                    CellPadding = new Size2D(0, 24),
                },
            };
            Add(_column);

            // ---- Branded wordmark line --------------------------------------
            // Matches the wordmark in ProfileSelectorView so the wizard
            // reads as part of the same UI family.
            _wordmark = new TextLabel
            {
                Text = "STREAMED",
                TextColor = Tokens.AccentLive,
                PointSize = 14f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _column.Add(_wordmark);

            // ---- Title ------------------------------------------------------
            _title = new TextLabel
            {
                Text = "Add a profile",
                TextColor = Tokens.TextPrimary,
                PointSize = 36f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _column.Add(_title);

            // ---- Step indicator ---------------------------------------------
            // Re-text'd on every step transition by RenderStep().
            _stepIndicator = new TextLabel
            {
                Text = "",
                TextColor = Tokens.TextSecondary,
                PointSize = 18f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("medium")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _column.Add(_stepIndicator);

            // ---- Body slot --------------------------------------------------
            // Empty container that RenderStep populates with per-step
            // content. Kept as a child of _column rather than a direct
            // child of `this` so the body sits inside the same vertical
            // flow as the header (with the column's CellPadding providing
            // the vertical gap).
            _body = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Center,
                    CellPadding = new Size2D(0, 18),
                },
            };
            _column.Add(_body);

            // Hidden by default; Open() fades in. Same pattern as the
            // sibling overlays so the host can construct an idle instance
            // once and Show/Hide it on demand.
            Opacity = 0f;
            Hide();
        }

        // ---- Public lifecycle API ------------------------------------------

        /// <summary>Wipe wizard state — call before each new add flow so
        /// the user always re-enters at Step 1 with empty values, even if
        /// a previous flow was cancelled mid-stream.
        ///
        /// <para><c>new</c> intentionally hides <c>BaseHandle.Reset()</c>:
        /// the brief locks this method's signature as part of the public
        /// API, and the parent's Reset semantics (handle-level reset) are
        /// not what callers want here.</para></summary>
        public new void Reset()
        {
            _step = Step.Step1_Avatar;
            _avatar = "";
            _name = "";
            _savedFired = false;
            _focusedIndex = 0;
            // Render the Step 1 splash now so that a host that calls
            // Reset() then Open() back-to-back gets a coherent visual
            // state on first paint instead of a blank body.
            RenderStep();
        }

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;

            // Always re-render so a re-Open after Reset() lands on a fresh
            // Step 1 splash (RenderStep is idempotent and cheap).
            RenderStep();

            Show();
            RaiseToTop();
            _modalOpacityAnim?.Stop();
            _modalOpacityAnim = new Animation(Dimens.AnimChromeFade);
            _modalOpacityAnim.AnimateTo(this, "Opacity", 1f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            // Defer the first RequestPickAvatar emission until AFTER the
            // splash background has finished painting in. On slow devices
            // the picker's own fade-in could otherwise outrun the wizard
            // and briefly appear over a transparent void. We hook the
            // animation Finished event so the deferral is tied to actual
            // paint timing rather than a fixed sleep.
            _modalOpacityAnim.Finished += OnOpenAnimationFinished;
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

            // Cancellation contract: if the user never reached the "Save"
            // activation on Step 3, we treat this Close() as a cancel and
            // notify the host. _savedFired guards the post-save Close()
            // call so the host can teardown unconditionally without
            // double-firing.
            if (!_savedFired)
            {
                Cancelled?.Invoke(this, EventArgs.Empty);
            }

            // Wipe state so the next Reset()/Open() cycle starts clean.
            // (Reset is idempotent; calling it here as well as on the next
            // Open is harmless and defends against hosts that re-Open
            // without explicitly calling Reset.)
            _step = Step.Step1_Avatar;
            _avatar = "";
            _name = "";
            _savedFired = false;
            _focusedIndex = 0;
        }

        public void HandleKey(string key)
        {
            if (!IsOpen) return;

            // Steps 1 and 2 have a sub-overlay (avatar picker / keyboard)
            // sitting on top, which the host's key router will reach
            // FIRST — so practically the wizard's HandleKey is mostly
            // dormant on those steps. We still honor Back/Escape here as
            // a defensive fallback so a missed routing in the host
            // doesn't trap the user.
            switch (key)
            {
                case "Left":
                case "Right":
                case "Up":
                case "Down":
                    // Step 3 has exactly one focusable element (Save) so
                    // directional input is intentionally ignored. Steps 1
                    // and 2 don't own focus at all (the sub-overlay does).
                    return;

                case "Return":
                case "Select":
                case "KP_Enter":
                case "OK":
                case "Ok":
                case "ok":
                case "XF86Ok":
                    // Only the Step 3 review pill responds to OK at the
                    // wizard level. Earlier steps' OK is consumed by the
                    // sub-overlay above us.
                    if (_step == Step.Step3_Review)
                    {
                        ActivateSave();
                    }
                    return;

                case "BackSpace":
                case "XF86Back":
                case "Escape":
                    // Treat Back as a cancel-out request — same contract
                    // as the sibling overlays' Cancelled events. The
                    // host listens and decides whether to call Close().
                    Cancelled?.Invoke(this, EventArgs.Empty);
                    return;
            }
        }

        // ---- Sub-overlay result handoff -------------------------------------

        /// <summary>Host calls this after AvatarPickerView's
        /// AvatarPicked event fires. Advances the wizard to Step 2 (name
        /// entry splash) and raises <see cref="RequestEditName"/> so the
        /// host can open its keyboard for the next step.</summary>
        /// <param name="fileName">Bundled avatar file name, or "" for the
        /// "Initials only" choice.</param>
        public void SetPickedAvatar(string fileName)
        {
            _avatar = fileName ?? "";
            _step = Step.Step2_Name;
            RenderStep();
            // Defer the keyboard-open request so the Step 2 splash paints
            // first (same rationale as Open's deferred avatar-picker
            // request — avoids the keyboard appearing over a void on slow
            // devices). One animation tick is plenty.
            DeferRequest(() => RequestEditName?.Invoke(this, _name));
        }

        /// <summary>Host calls this after OnScreenKeyboardView's
        /// Submitted event fires. Trims the input, advances to Step 3 and
        /// re-renders to show the "Save profile" review screen.</summary>
        public void SetEnteredName(string name)
        {
            // Trim to match how ProfileEditView normalizes name input —
            // trailing whitespace from the keyboard's Space key is the
            // common case here. Empty result is allowed; the host can
            // decide whether to gate Save on a non-empty name (we don't
            // enforce it at the wizard level so the API stays simple).
            _name = (name ?? "").Trim();
            _step = Step.Step3_Review;
            // Step 3 has exactly one focusable element — snap focus to it.
            _focusedIndex = 0;
            RenderStep();
        }

        // ---- Step rendering -------------------------------------------------

        // Tear down + rebuild the body region for the current _step. The
        // header (wordmark + title) stays put — only the step indicator
        // text and the body content vary per step.
        private void RenderStep()
        {
            ClearChildren(_body);
            _savePill = null;
            _savePillLabel = null;

            switch (_step)
            {
                case Step.Step1_Avatar:
                    _stepIndicator.Text = "Step 1 of 3 · Choose an avatar";
                    // Step 1 body is intentionally empty — the avatar
                    // picker overlays on top of this splash, so all the
                    // user sees behind it is the header / step indicator.
                    break;

                case Step.Step2_Name:
                    _stepIndicator.Text = "Step 2 of 3 · Name your profile";
                    // 240×240 preview of the picked avatar so the user
                    // has a visual anchor while the keyboard is open on
                    // top. The keyboard's modal backdrop dims this, but
                    // the preview re-emerges if the keyboard is dismissed.
                    _body.Add(BuildAvatarPreview());
                    break;

                case Step.Step3_Review:
                    _stepIndicator.Text = "Step 3 of 3 · Save";
                    _body.Add(BuildAvatarPreview());
                    _body.Add(BuildNameLabel());
                    _body.Add(BuildSavePill());
                    break;
            }

            // Re-apply focus visual so a fresh Step 3 build paints the
            // Save pill in its focused state without waiting for a key
            // event. (Steps 1/2 have nothing focusable so this is a no-op
            // for them.)
            ApplyFocusVisual();
        }

        // ---- Body builders --------------------------------------------------

        // 240×240 rounded preview, mirroring the avatar render technique
        // used in ProfileSelectorView.BuildProfileTile: dark neutral BG,
        // a "?" placeholder centered underneath, then an ImageView layered
        // ON TOP if the avatar file resolves. ClippingMode + CornerRadius
        // mask the image to the rounded silhouette so it matches the
        // placeholder shape exactly.
        private View BuildAvatarPreview()
        {
            // NO Layout on the wrapper: with a LinearLayout, the layout's
            // measure pass squeezes the FillToParent ImageView into the cell
            // sized by the placeholder TextLabel — leaves the image floating
            // in a small centered rect with the colored bg + glyph still
            // visible at the wrapper edges. Absolute positioning + FillToParent
            // gives the image the full 240×240 cell.
            View wrapper = new View
            {
                BackgroundColor = new Color(0.18f, 0.18f, 0.22f, 1f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.20f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(AvatarPreviewCornerRadius, AvatarPreviewCornerRadius, AvatarPreviewCornerRadius, AvatarPreviewCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                ClippingMode = ClippingModeType.ClipChildren,
                WidthSpecification = AvatarPreviewSize,
                HeightSpecification = AvatarPreviewSize,
            };

            // Placeholder "?" glyph — always added underneath. Loading state
            // + permanent fallback when the avatar file can't be resolved.
            TextLabel placeholder = new TextLabel
            {
                Text = "?",
                TextColor = Color.White,
                PointSize = 80f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                ParentOrigin = Tizen.NUI.ParentOrigin.Center,
                PivotPoint = Tizen.NUI.PivotPoint.Center,
            };
            wrapper.Add(placeholder);

            // Layer the bundled image on top if it resolves. DesiredWidth/
            // DesiredHeight pre-resample the 384px source to ~480px (2× the
            // display size) so we don't bilinear-downscale every frame —
            // same pixelation fix as the chip + dropdown avatars.
            if (!string.IsNullOrEmpty(_avatar))
            {
                string path = HeroView.ResolveBundledAssetPath(_avatar);
                if (!string.IsNullOrEmpty(path))
                {
                    ImageView img = new ImageView
                    {
                        ResourceUrl = path,
                        WidthResizePolicy = ResizePolicyType.FillToParent,
                        HeightResizePolicy = ResizePolicyType.FillToParent,
                        FittingMode = FittingModeType.ScaleToFill,
                        DesiredWidth = AvatarPreviewSize * 2,
                        DesiredHeight = AvatarPreviewSize * 2,
                        ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft,
                        PivotPoint = Tizen.NUI.PivotPoint.TopLeft,
                    };
                    wrapper.Add(img);
                }
            }

            return wrapper;
        }

        // Step 3's name TextLabel — shows the entered name centered below
        // the avatar preview. If the name is empty (user submitted blank),
        // we render a placeholder caption so the review screen still has
        // a visible name slot rather than a confusing blank space.
        private View BuildNameLabel()
        {
            TextLabel label = new TextLabel
            {
                Text = string.IsNullOrEmpty(_name) ? "(no name)" : _name,
                TextColor = string.IsNullOrEmpty(_name) ? Tokens.TextTertiary : Tokens.TextPrimary,
                PointSize = 28f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                Ellipsis = true,
                // Constrain width so an unusually long name doesn't push
                // the column off-center. AvatarPreviewSize * 1.5 gives a
                // generous but bounded line.
                WidthSpecification = (int)(AvatarPreviewSize * 1.5f),
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            return label;
        }

        // The "Save profile" pill — focused-only target on Step 3. Pill
        // shape (corner radius = height/2), white-on-dark when unfocused,
        // and inverted to white BG + dark text when focused, matching the
        // focused-tile pattern used in OnScreenKeyboardView's keys.
        private View BuildSavePill()
        {
            _savePill = new View
            {
                BackgroundColor = new Color(1f, 1f, 1f, 0.10f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.35f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(SavePillCornerRadius, SavePillCornerRadius, SavePillCornerRadius, SavePillCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                HeightSpecification = SavePillHeight,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                Padding = new Extents(SavePillHorizontalPad, SavePillHorizontalPad, SavePillVerticalPad, SavePillVerticalPad),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Center,
                },
            };

            _savePillLabel = new TextLabel
            {
                Text = "Save profile",
                TextColor = Tokens.TextPrimary,
                PointSize = 20f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _savePill.Add(_savePillLabel);
            return _savePill;
        }

        // ---- Focus visuals --------------------------------------------------

        // Step 3 has exactly one focusable element (Save). Steps 1/2 have
        // nothing focusable at the wizard level — the sub-overlay above
        // owns focus. So this method only does work when the Save pill is
        // present.
        private void ApplyFocusVisual()
        {
            if (_savePill == null || _savePillLabel == null) return;
            // _focusedIndex == 0 always means "Save is focused" on Step 3
            // (only one focusable, so no other index is meaningful).
            bool focused = _focusedIndex == 0;
            if (focused)
            {
                // White BG + dark text — same focused-key visual as the
                // OnScreenKeyboardView keys, gives a strong "this is the
                // primary action" affordance for couch-distance viewing.
                _savePill.BackgroundColor = Color.White;
                _savePill.BorderlineColor = Color.White;
                _savePill.BorderlineWidth = 2f;
                _savePillLabel.TextColor = Tokens.TextOnLight;
            }
            else
            {
                _savePill.BackgroundColor = new Color(1f, 1f, 1f, 0.10f);
                _savePill.BorderlineColor = new Color(1f, 1f, 1f, 0.35f);
                _savePill.BorderlineWidth = 1f;
                _savePillLabel.TextColor = Tokens.TextPrimary;
            }
        }

        // ---- Activation -----------------------------------------------------

        private void ActivateSave()
        {
            // Mark the saved-fired flag BEFORE raising Created so any
            // synchronous Close() the host triggers from the Created
            // handler skips the Cancelled emission. Otherwise a
            // reasonable host pattern ("on Created, persist + close") would
            // double-fire as both Created + Cancelled.
            _savedFired = true;
            Created?.Invoke(this, new NewProfileDraft(_name, _avatar));
        }

        // ---- Helpers --------------------------------------------------------

        // Hooked to the open-fade Animation's Finished event so the host's
        // first sub-overlay (avatar picker) appears AFTER the splash
        // background has fully painted. The handler unsubscribes itself
        // so it doesn't fire on subsequent close-fade animations.
        private void OnOpenAnimationFinished(object sender, EventArgs e)
        {
            if (_modalOpacityAnim != null)
            {
                _modalOpacityAnim.Finished -= OnOpenAnimationFinished;
            }
            // Only emit the first-step request if we're still on Step 1
            // (the host might have already pushed a SetPickedAvatar synchronously
            // — unusual but possible — in which case we'd be on Step 2 and
            // shouldn't re-fire the avatar request).
            if (IsOpen && _step == Step.Step1_Avatar)
            {
                RequestPickAvatar?.Invoke(this, _avatar);
            }
        }

        // Defer an action by one animation frame (16ms) so the splash
        // re-render has time to paint before the next sub-overlay request.
        // Same rationale as the Open() deferred RequestPickAvatar — keeps
        // the visual layering predictable on slow devices.
        private void DeferRequest(Action action)
        {
            if (action == null) return;
            Animation tick = new Animation(16);
            tick.Finished += (s, e) => action();
            tick.Play();
        }

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
