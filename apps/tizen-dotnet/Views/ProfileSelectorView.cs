using System;
using System.Collections.Generic;
using StreamedTv.Models;
using StreamedTv.Theme;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace StreamedTv.Views
{
    public sealed class ProfileSelectedEventArgs : EventArgs
    {
        public string ProfileId { get; }
        public ProfileSelectedEventArgs(string profileId)
        {
            ProfileId = profileId;
        }
    }

    /// <summary>
    /// Netflix-style "Who's watching?" full-screen profile picker.
    ///
    /// Vertical structure:
    ///   1. Tiny "STREAMED" wordmark (subtle accent line)
    ///   2. 32pt bold title "Who's watching?"
    ///   3. 14pt muted subtitle "Pick a profile to continue"
    ///   4. ~44px gap
    ///   5. Horizontal row of profile COLUMNS. Each profile column =
    ///      [160×160 avatar] [name] [Manage pill]. The trailing "Add
    ///      profile" column has no Manage pill.
    ///
    /// The Manage pill replaces the previous global "Manage" tile + ManageMode
    /// toggle. Each profile carries its own edit affordance directly so there
    /// is no modal mode to enter — the user just navigates Down from a
    /// profile to its Manage pill and presses OK.
    ///
    /// Focus model is 2D:
    ///   • Top row    = profile main tiles + add tile
    ///   • Bottom row = Manage pills (one per profile, none under add)
    ///   • Down from a profile main → its Manage pill
    ///   • Up   from a Manage pill  → its profile main
    ///   • Left/Right within current row, clamped at boundaries; the bottom
    ///     row skips columns that have no Manage pill (i.e. the add column).
    ///
    /// All container layouts use LinearLayout EXCEPT the avatar wrapper
    /// itself, which uses no Layout so a FillToParent ImageView covers the
    /// entire 160×160 cell. With a LinearLayout on the wrapper, the layout
    /// shrinks the FillToParent child to the initials TextLabel's measured
    /// size, leaving the colored bg visible around the image — that bug was
    /// the "some part has initials, rest has image" symptom.
    /// </summary>
    public sealed class ProfileSelectorView : View
    {
        // Hard cap mirrors ProfileService.MaxProfiles=5. If we already have 5
        // profiles, the host's add flow is unavailable so we suppress the "+"
        // tile.
        private const int MaxProfiles = 5;

        // Tile sizing.
        private const int TileWidth = 220;
        private const int TileHeight = 220;
        private const int TileGap = 36;
        private const int AvatarSize = 160;
        private const int AvatarCornerRadius = 20;
        private const int NameGap = 18;     // gap between avatar and name caption
        private const int ManageGap = 14;   // gap between name and manage pill
        private const int ManageButtonHeight = 56;
        private const int ManageButtonHorizontalPad = 22;
        private const int DoneButtonHeight = 56;
        private const int DoneButtonHorizontalPad = 36;
        private const int DoneButtonTopMargin = 44;

        // Layout pieces
        private readonly View _column;        // centered vertical stack
        private readonly TextLabel _title;
        private readonly TextLabel _subtitle;  // tracked so EnterManageMode can update it
        private readonly View _row;           // horizontal row of profile columns
        private readonly View _doneButton;     // visible only in ManageMode
        private readonly TextLabel _doneLabel; // tracked for focus color flip
        private readonly View _signOutButton;  // visible only in ManageMode, beside Done
        private readonly TextLabel _signOutLabel;
        private readonly View _actionRow;      // horizontal container for Done + Sign out

        // Per-slot tracking. _slotKinds is Profile or Add (no Manage anymore).
        // For Profile slots, _manageButtons[i] is the focusable Manage pill;
        // for Add slots, _manageButtons[i] is null (no manage cell).
        private enum SlotKind { Profile, Add }
        private readonly List<SlotKind> _slotKinds = new List<SlotKind>();
        private readonly List<View> _columns = new List<View>();           // outer column View per slot
        private readonly List<View> _mainTiles = new List<View>();         // avatar+name container — top-row focus target
        private readonly List<View> _avatars = new List<View>();           // avatar wrapper inside the main tile
        private readonly List<TextLabel> _nameLabels = new List<TextLabel>();
        private readonly List<View> _manageButtons = new List<View>();     // manage pill (null for Add slot)
        private readonly List<TextLabel> _manageLabels = new List<TextLabel>();  // manage pill text (null for Add slot)
        private readonly List<string> _slotProfileIds = new List<string>(); // profile id (null for Add slot)

        // Per-cell scale animations so rapid focus interrupts don't stack.
        private readonly Dictionary<View, Animation> _cellScaleAnims = new Dictionary<View, Animation>();

        // Open/close opacity animation cache so a re-Open during a fade-out
        // doesn't leave the view stuck mid-fade.
        private Animation _modalOpacityAnim;

        // 2D focus.
        // Top    = profile main tiles + add tile
        // Bottom = per-profile manage pill row (hidden in ManageMode)
        // Done   = the trailing "Done" pill (visible only in ManageMode)
        private enum FocusRow { Top, Bottom, Done }
        private int _focusCol = -1;
        private FocusRow _focusRow = FocusRow.Top;
        private string _activeProfileId;

        // True when the picker was opened via the dropdown's "Manage profiles"
        // entry (vs the default "Switch profiles" entry / app-launch path).
        // In ManageMode, top-row tile activation opens the editor instead of
        // switching to the profile, the title swaps to "Manage Profiles", and
        // the per-profile pills hide because they're redundant.
        private bool _manageMode;

        public bool IsOpen { get; private set; }

        public event EventHandler<ProfileSelectedEventArgs> ProfileSelected;
        public event EventHandler AddProfileRequested;
        // Raised when the user activates the Manage pill below a profile
        // tile. Payload is the profileId. Host opens its ProfileEditView in
        // response. We don't close ourselves — the editor lives ABOVE this
        // picker in the layer stack and on dismiss the user is dropped back
        // here on the same Manage pill.
        public event EventHandler<string> EditProfileRequested;
        // Raised when the user activates the trailing "Done" pill in
        // ManageMode. Host calls Close() on us (which auto-resets ManageMode).
        public event EventHandler DoneRequested;
        // Raised when the user activates the "Sign out" pill (next to
        // Done) in ManageMode. Host clears auth + caches and re-opens
        // the LoginView.
        public event EventHandler SignOutRequested;
        public event EventHandler Closed;

        public ProfileSelectorView()
        {
            // Full-screen primary surface — opaque BgBase. NOT a translucent
            // modal: this is the first thing the user sees on launch and we
            // want it to read as the app's home screen, not an overlay. An
            // earlier translucent (alpha 0.92) version showed the main app
            // bleeding through faintly which made the picker feel temporary.
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.FillToParent;
            BackgroundColor = Tokens.BgBase;

            // Center the column in the screen.
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
                    CellPadding = new Size2D(0, 8),
                },
            };
            Add(_column);

            // ---- Branded wordmark line ---------------------------------------
            TextLabel wordmark = new TextLabel
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
                Margin = new Extents(0, 0, 0, 4),
            };
            _column.Add(wordmark);

            // ---- Title --------------------------------------------------------
            _title = new TextLabel
            {
                Text = "Who's watching?",
                TextColor = Tokens.TextPrimary,
                PointSize = 44f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Margin = new Extents(0, 0, 0, 2),
            };
            _column.Add(_title);

            // ---- Subtitle -----------------------------------------------------
            _subtitle = new TextLabel
            {
                Text = "Pick a profile to continue",
                TextColor = Tokens.TextSecondary,
                PointSize = 22f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("medium")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Margin = new Extents(0, 0, 0, 44),
            };
            _column.Add(_subtitle);

            // ---- Tile row -----------------------------------------------------
            _row = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    // Top-align columns: focused profile grows taller because
                    // its Manage pill appears below, but all avatars stay
                    // anchored at the same vertical line. Earlier
                    // CenterVertical caused the avatars to drift up/down as
                    // focus moved.
                    LinearAlignment = LinearLayout.Alignment.Top,
                    CellPadding = new Size2D(TileGap, 0),
                },
            };
            _column.Add(_row);

            // ---- Action row (ManageMode only) ------------------------------
            // Hidden by default; EnterManageMode shows it. Two pills side-
            // by-side: Done (close picker) + Sign out (clear auth + show
            // LoginView). Sits below the profile row with a generous top
            // margin so it's clearly an exit affordance, not a row sibling.
            // Horizontal nav between the two pills uses Left/Right while
            // _focusRow == FocusRow.Done; Up returns to the profile row.
            _actionRow = new View
            {
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                Margin = new Extents(0, 0, DoneButtonTopMargin, 0),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    CellPadding = new Size2D(20, 0),
                },
            };

            _doneButton = new View
            {
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = DoneButtonHeight,
                BackgroundColor = new Color(1f, 1f, 1f, 0.06f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.20f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(DoneButtonHeight / 2f, DoneButtonHeight / 2f, DoneButtonHeight / 2f, DoneButtonHeight / 2f),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                Padding = new Extents((ushort)DoneButtonHorizontalPad, (ushort)DoneButtonHorizontalPad, 0, 0),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Center,
                },
            };
            _doneLabel = new TextLabel
            {
                Text = "Done",
                TextColor = Tokens.TextPrimary,
                PointSize = 18f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _doneButton.Add(_doneLabel);
            _actionRow.Add(_doneButton);

            _signOutButton = new View
            {
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = DoneButtonHeight,
                BackgroundColor = new Color(1f, 1f, 1f, 0.06f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.20f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(DoneButtonHeight / 2f, DoneButtonHeight / 2f, DoneButtonHeight / 2f, DoneButtonHeight / 2f),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                Padding = new Extents((ushort)DoneButtonHorizontalPad, (ushort)DoneButtonHorizontalPad, 0, 0),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Center,
                },
            };
            _signOutLabel = new TextLabel
            {
                Text = "Sign out",
                TextColor = Tokens.TextSecondary,
                PointSize = 18f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("medium")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _signOutButton.Add(_signOutLabel);
            _actionRow.Add(_signOutButton);

            _actionRow.Hide();
            _column.Add(_actionRow);

            // Hidden by default; Open() fades in.
            Opacity = 0f;
            Hide();
        }

        /// <summary>Rebuild the tile row from the given profiles. The
        /// <paramref name="activeProfileId"/> is highlighted (name in
        /// AccentLive) so the user can see which profile is currently logged
        /// in, separate from focus. Always appends an "Add profile" column
        /// unless the profile list is at the MaxProfiles=5 cap.</summary>
        public void SetProfiles(IReadOnlyList<Profile> profiles, string activeProfileId)
        {
            _activeProfileId = activeProfileId;

            _slotKinds.Clear();
            _columns.Clear();
            _mainTiles.Clear();
            _avatars.Clear();
            _nameLabels.Clear();
            _manageButtons.Clear();
            _manageLabels.Clear();
            _slotProfileIds.Clear();
            _cellScaleAnims.Clear();
            ClearChildren(_row);

            int profileCount = profiles != null ? profiles.Count : 0;
            int activeIndex = -1;

            if (profileCount > 0)
            {
                for (int i = 0; i < profileCount; i++)
                {
                    Profile p = profiles[i];
                    if (p == null) continue;
                    BuildProfileColumn(p);
                    if (!string.IsNullOrEmpty(_activeProfileId)
                        && string.Equals(p.Id, _activeProfileId, StringComparison.Ordinal))
                    {
                        activeIndex = _columns.Count - 1;
                    }
                }
            }

            if (profileCount < MaxProfiles)
            {
                BuildAddColumn();
            }

            _focusCol = activeIndex >= 0 ? activeIndex : (_columns.Count > 0 ? 0 : -1);
            _focusRow = FocusRow.Top;
            ApplyFocusVisual();
        }

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;

            // Snap focus back to the active profile (or column 0) every time
            // we open — leftover focus from the previous session would be
            // disorienting since the user re-enters this screen with no prior
            // context cue.
            if (_columns.Count > 0 && _focusCol < 0) _focusCol = 0;
            _focusRow = FocusRow.Top;
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
            // Reset ManageMode on dismiss so the next time the picker opens
            // via tile click / app launch / Switch Profiles, it's in the
            // normal switch flow. Re-entering manage mode is up to the host
            // (called explicitly via EnterManageMode after Open).
            if (_manageMode) ExitManageMode();
            _modalOpacityAnim?.Stop();
            _modalOpacityAnim = new Animation(160);
            _modalOpacityAnim.AnimateTo(this, "Opacity", 0f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseIn));
            _modalOpacityAnim.Finished += (s, e) => Hide();
            _modalOpacityAnim.Play();
            Closed?.Invoke(this, EventArgs.Empty);
        }

        // ---- Manage mode ----------------------------------------------------

        /// <summary>Put the picker into edit mode: title swaps to "Manage
        /// Profiles", per-profile pills hide (redundant), and top-row tile
        /// activation routes to EditProfileRequested instead of
        /// ProfileSelected. Idempotent.</summary>
        public void EnterManageMode()
        {
            if (_manageMode) return;
            _manageMode = true;
            if (_title != null) _title.Text = "Manage Profiles";
            if (_subtitle != null) _subtitle.Text = "Pick a profile to edit";
            _actionRow?.Show();
            ApplyFocusVisual();
        }

        /// <summary>Reset out of edit mode. Restores title + subtitle and
        /// re-enables the per-profile pills + the switch-flow Activate.
        /// Idempotent. Called automatically from Close().</summary>
        public void ExitManageMode()
        {
            if (!_manageMode) return;
            _manageMode = false;
            if (_title != null) _title.Text = "Who's watching?";
            if (_subtitle != null) _subtitle.Text = "Pick a profile to continue";
            _actionRow?.Hide();
            // If focus was on the Done/Sign out row (only reachable in
            // ManageMode), bring it back to the top row before re-paint.
            if (_focusRow == FocusRow.Done) _focusRow = FocusRow.Top;
            _actionFocusIdx = 0;
            ApplyFocusVisual();
        }

        // ------------- 2D navigation -----------------------------------------

        // 0 = Done, 1 = Sign out — index into the action row when
        // _focusRow == FocusRow.Done. Reset to 0 every time the user
        // re-enters the row (so they start on the safe button, not on
        // Sign out).
        private int _actionFocusIdx = 0;

        public void MoveLeft()
        {
            if (_focusRow == FocusRow.Done)
            {
                if (_actionFocusIdx > 0)
                {
                    _actionFocusIdx--;
                    ApplyFocusVisual();
                }
                return;
            }
            if (_focusCol <= 0) return;
            // On the bottom row, skip columns without a Manage cell (i.e. Add).
            int next = _focusCol - 1;
            while (next >= 0 && _focusRow == FocusRow.Bottom && _manageButtons[next] == null) next--;
            if (next < 0) return;
            _focusCol = next;
            ApplyFocusVisual();
        }

        public void MoveRight()
        {
            if (_focusRow == FocusRow.Done)
            {
                if (_actionFocusIdx < 1)
                {
                    _actionFocusIdx++;
                    ApplyFocusVisual();
                }
                return;
            }
            int max = _columns.Count - 1;
            if (_focusCol >= max) return;
            int next = _focusCol + 1;
            while (next <= max && _focusRow == FocusRow.Bottom && _manageButtons[next] == null) next++;
            if (next > max) return;
            _focusCol = next;
            ApplyFocusVisual();
        }

        public void MoveUp()
        {
            if (_focusRow == FocusRow.Top) return;
            // Done/SignOut → Top (preserves _focusCol so user lands back
            // where they came from).
            _focusRow = FocusRow.Top;
            ApplyFocusVisual();
        }

        public void MoveDown()
        {
            if (_focusRow != FocusRow.Top) return;
            if (_manageMode)
            {
                // ManageMode: Down jumps to the action row (Done +
                // Sign out). Always land on Done so accidental
                // double-presses can't sign the user out.
                _focusRow = FocusRow.Done;
                _actionFocusIdx = 0;
                ApplyFocusVisual();
                return;
            }
            // Normal mode: move into the bottom row, but only when the
            // focused column has a manage cell. Add tile column has
            // _manageButtons[col] == null.
            if (_focusCol < 0 || _focusCol >= _manageButtons.Count) return;
            if (_manageButtons[_focusCol] == null) return;
            _focusRow = FocusRow.Bottom;
            ApplyFocusVisual();
        }

        public void Activate()
        {
            if (_focusRow == FocusRow.Done)
            {
                if (_actionFocusIdx == 1) SignOutRequested?.Invoke(this, EventArgs.Empty);
                else DoneRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (_focusCol < 0 || _focusCol >= _slotKinds.Count) return;
            SlotKind kind = _slotKinds[_focusCol];
            string id = _focusCol < _slotProfileIds.Count ? _slotProfileIds[_focusCol] : null;

            if (_focusRow == FocusRow.Bottom)
            {
                // Must be a Profile slot (Add has no manage cell). Open the editor.
                if (kind == SlotKind.Profile && !string.IsNullOrEmpty(id))
                {
                    EditProfileRequested?.Invoke(this, id);
                }
                return;
            }

            // Top row: profile or add.
            switch (kind)
            {
                case SlotKind.Profile:
                    if (string.IsNullOrEmpty(id)) return;
                    if (_manageMode)
                    {
                        // Picker was opened via dropdown's "Manage profiles" —
                        // top-tile click goes straight to the editor.
                        EditProfileRequested?.Invoke(this, id);
                    }
                    else
                    {
                        ProfileSelected?.Invoke(this, new ProfileSelectedEventArgs(id));
                    }
                    break;
                case SlotKind.Add:
                    AddProfileRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        // ------------- Builders ----------------------------------------------

        private void BuildProfileColumn(Profile p)
        {
            // Outer column groups main tile + manage pill vertically.
            View col = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = TileWidth,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Top,
                    CellPadding = new Size2D(0, ManageGap),
                },
            };

            View mainTile = BuildProfileMainTile(p);
            col.Add(mainTile);

            View manageBtn = BuildManageButton();
            col.Add(manageBtn);

            _row.Add(col);
            _columns.Add(col);
            _slotKinds.Add(SlotKind.Profile);
            _slotProfileIds.Add(p.Id);
            _mainTiles.Add(mainTile);
            _manageButtons.Add(manageBtn);
        }

        private View BuildProfileMainTile(Profile p)
        {
            Color avatarBg = new Color(p.AvatarR, p.AvatarG, p.AvatarB, 1f);

            View tile = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = TileWidth,
                HeightSpecification = TileHeight,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Top,
                    CellPadding = new Size2D(0, NameGap),
                },
            };

            // Avatar wrapper. NO Layout on the wrapper so the FillToParent
            // ImageView covers the full 160×160 cell instead of being squeezed
            // by a LinearLayout's measure pass into the initials TextLabel's
            // tiny slot. Initials are centered via ParentOrigin/PivotPoint.
            // ClipChildren + CornerRadius mask the image to the rounded
            // silhouette so it can't bleed past the corners.
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

            TextLabel initials = new TextLabel
            {
                Text = p.Initials,
                TextColor = Color.White,
                PointSize = 40f,
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
            avatar.Add(initials);

            string avatarImagePath = ResolveAvatarImagePath(p.AvatarImage);
            if (!string.IsNullOrEmpty(avatarImagePath))
            {
                ImageView img = new ImageView
                {
                    ResourceUrl = avatarImagePath,
                    WidthResizePolicy = ResizePolicyType.FillToParent,
                    HeightResizePolicy = ResizePolicyType.FillToParent,
                    FittingMode = FittingModeType.ScaleToFill,
                    ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft,
                    PivotPoint = Tizen.NUI.PivotPoint.TopLeft,
                };
                avatar.Add(img);
            }
            tile.Add(avatar);

            // Active profile gets the AccentLive tint as a non-focus indicator
            // so the user can see "this is who I was last signed in as" even
            // without moving focus to the tile.
            bool isActive = !string.IsNullOrEmpty(_activeProfileId)
                && string.Equals(p.Id, _activeProfileId, StringComparison.Ordinal);

            TextLabel name = new TextLabel
            {
                Text = p.Name ?? "",
                TextColor = isActive ? Tokens.AccentLive : Tokens.TextPrimary,
                PointSize = 28f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                Ellipsis = true,
                WidthSpecification = TileWidth,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            tile.Add(name);

            _avatars.Add(avatar);
            _nameLabels.Add(name);

            return tile;
        }

        private View BuildManageButton()
        {
            View btn = new View
            {
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = ManageButtonHeight,
                BackgroundColor = new Color(1f, 1f, 1f, 0.06f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.20f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                // Pill: corner radius = half the height.
                CornerRadius = new Vector4(ManageButtonHeight / 2f, ManageButtonHeight / 2f, ManageButtonHeight / 2f, ManageButtonHeight / 2f),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                Padding = new Extents((ushort)ManageButtonHorizontalPad, (ushort)ManageButtonHorizontalPad, 0, 0),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Center,
                },
            };

            TextLabel label = new TextLabel
            {
                Text = "✎  Manage",
                TextColor = Tokens.TextSecondary,
                PointSize = 18f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("medium")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            btn.Add(label);

            // Hidden by default — only revealed when its column is focused
            // (see ApplyFocusVisual). Keeps the picker's resting state clean.
            btn.Hide();

            _manageLabels.Add(label);
            return btn;
        }

        private void BuildAddColumn()
        {
            View col = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = TileWidth,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Top,
                    CellPadding = new Size2D(0, ManageGap),
                },
            };

            View tile = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = TileWidth,
                HeightSpecification = TileHeight,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Top,
                    CellPadding = new Size2D(0, NameGap),
                },
            };

            // Dark gray "+" avatar — visually distinguishable from any colored
            // profile avatar so the affordance is obvious.
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
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Center,
                },
            };

            TextLabel plus = new TextLabel
            {
                Text = "+",
                TextColor = Color.White,
                PointSize = 40f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            avatar.Add(plus);
            tile.Add(avatar);

            TextLabel name = new TextLabel
            {
                Text = "Add profile",
                TextColor = Tokens.TextSecondary,
                PointSize = 28f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                Ellipsis = true,
                WidthSpecification = TileWidth,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            tile.Add(name);

            col.Add(tile);

            _row.Add(col);
            _columns.Add(col);
            _slotKinds.Add(SlotKind.Add);
            _slotProfileIds.Add(null);
            _mainTiles.Add(tile);
            _avatars.Add(avatar);
            _nameLabels.Add(name);
            // No manage button under the Add tile.
            _manageButtons.Add(null);
            _manageLabels.Add(null);
        }

        // ------------- Focus visuals -----------------------------------------

        private void ApplyFocusVisual()
        {
            for (int i = 0; i < _columns.Count; i++)
            {
                bool topFocused = (i == _focusCol && _focusRow == FocusRow.Top);
                bool bottomFocused = (i == _focusCol && _focusRow == FocusRow.Bottom);

                // Avatar focus ring (only on top focus).
                View avatar = i < _avatars.Count ? _avatars[i] : null;
                if (avatar != null)
                {
                    if (topFocused)
                    {
                        avatar.BorderlineColor = Color.White;
                        avatar.BorderlineWidth = 4f;
                    }
                    else
                    {
                        avatar.BorderlineColor = new Color(1f, 1f, 1f, 0.20f);
                        avatar.BorderlineWidth = 1f;
                    }
                }

                // Main tile scale (subtle lift on top focus).
                View mainTile = i < _mainTiles.Count ? _mainTiles[i] : null;
                if (mainTile != null) AnimateScale(mainTile, topFocused ? 1.06f : 1.0f);

                // Manage button visual + visibility. Pill is hidden when its
                // column isn't the focused one — keeps the resting picker
                // clean and surfaces the affordance only on the active tile.
                View btn = i < _manageButtons.Count ? _manageButtons[i] : null;
                TextLabel lbl = i < _manageLabels.Count ? _manageLabels[i] : null;
                if (btn != null)
                {
                    // Show the pill only when its column is focused AND we
                    // aren't in ManageMode (which makes the pill redundant
                    // since top-row tile activation already opens the editor).
                    bool columnIsFocused = (i == _focusCol);
                    bool shouldShow = columnIsFocused && !_manageMode;
                    if (shouldShow) btn.Show(); else btn.Hide();

                    if (bottomFocused)
                    {
                        btn.BackgroundColor = Color.White;
                        btn.BorderlineColor = Color.White;
                        btn.BorderlineWidth = 2f;
                        if (lbl != null) lbl.TextColor = Tokens.TextOnLight;
                    }
                    else
                    {
                        btn.BackgroundColor = new Color(1f, 1f, 1f, 0.06f);
                        btn.BorderlineColor = new Color(1f, 1f, 1f, 0.20f);
                        btn.BorderlineWidth = 1f;
                        if (lbl != null) lbl.TextColor = Tokens.TextSecondary;
                    }
                    AnimateScale(btn, bottomFocused ? 1.05f : 1.0f);
                }
            }

            // Done + Sign out pills — only painted/visible in ManageMode.
            // _actionFocusIdx selects which one is focused (0 = Done,
            // 1 = Sign out). Both pills always exist in the layout; we
            // just flip their per-button focus visual.
            if (_doneButton != null && _doneLabel != null)
            {
                bool doneFocused = (_focusRow == FocusRow.Done && _actionFocusIdx == 0);
                if (doneFocused)
                {
                    _doneButton.BackgroundColor = Color.White;
                    _doneButton.BorderlineColor = Color.White;
                    _doneButton.BorderlineWidth = 2f;
                    _doneLabel.TextColor = Tokens.TextOnLight;
                }
                else
                {
                    _doneButton.BackgroundColor = new Color(1f, 1f, 1f, 0.06f);
                    _doneButton.BorderlineColor = new Color(1f, 1f, 1f, 0.20f);
                    _doneButton.BorderlineWidth = 1f;
                    _doneLabel.TextColor = Tokens.TextPrimary;
                }
                AnimateScale(_doneButton, doneFocused ? 1.05f : 1.0f);
            }
            if (_signOutButton != null && _signOutLabel != null)
            {
                bool signOutFocused = (_focusRow == FocusRow.Done && _actionFocusIdx == 1);
                if (signOutFocused)
                {
                    _signOutButton.BackgroundColor = Color.White;
                    _signOutButton.BorderlineColor = Color.White;
                    _signOutButton.BorderlineWidth = 2f;
                    _signOutLabel.TextColor = Tokens.TextOnLight;
                }
                else
                {
                    _signOutButton.BackgroundColor = new Color(1f, 1f, 1f, 0.06f);
                    _signOutButton.BorderlineColor = new Color(1f, 1f, 1f, 0.20f);
                    _signOutButton.BorderlineWidth = 1f;
                    _signOutLabel.TextColor = Tokens.TextSecondary;
                }
                AnimateScale(_signOutButton, signOutFocused ? 1.05f : 1.0f);
            }
        }

        private void AnimateScale(View v, float target)
        {
            Animation prev;
            if (_cellScaleAnims.TryGetValue(v, out prev) && prev != null) prev.Stop();
            Animation a = new Animation(Dimens.AnimFocusScale);
            a.AnimateTo(v, "Scale",
                new Vector3(target, target, 1f),
                new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOutSquare));
            a.Play();
            _cellScaleAnims[v] = a;
        }

        // ------------- Helpers -----------------------------------------------

        private static void ClearChildren(View v)
        {
            if (v == null) return;
            uint count = v.ChildCount;
            for (uint i = 0; i < count; i++) v.Remove(v.GetChildAt(0));
        }

        // Delegates to HeroView's lookup so all bundled shared/res/ asset
        // resolution goes through one path (which knows the Tizen .tpk
        // candidate layouts). Returns null for blank input or files not
        // present on disk; callers null/empty-check before using.
        private static string ResolveAvatarImagePath(string fileName)
        {
            return HeroView.ResolveBundledAssetPath(fileName);
        }
    }
}
