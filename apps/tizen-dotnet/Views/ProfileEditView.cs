using System;
using System.Collections.Generic;
using StreamedTv.Models;
using StreamedTv.Theme;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace StreamedTv.Views
{
    /// <summary>Snapshot of the form state emitted by <see cref="ProfileEditView.Saved"/>.
    /// Carries the original profile id plus the user's edited name and avatar
    /// file name (empty string = colored initials fallback). The host applies
    /// these via ProfileService.UpdateProfile.</summary>
    public sealed class EditedProfile
    {
        public string ProfileId { get; }
        public string Name { get; }
        public string AvatarFileName { get; }

        public EditedProfile(string profileId, string name, string avatarFileName)
        {
            ProfileId = profileId ?? "";
            Name = name ?? "";
            AvatarFileName = avatarFileName ?? "";
        }
    }

    /// <summary>
    /// Netflix-style "Edit profile" full-screen overlay. Modeled after the
    /// Netflix reference image — a vertical property list on the left
    /// (Name + Icon rows + Done/Delete pills) and a large live preview pane
    /// on the right (avatar + name).
    ///
    /// Visual structure (1920×1080 reference):
    ///   - Outer dim modal (BgBase @ 0.95) using a horizontal LinearLayout.
    ///   - Left column (~600 wide):
    ///       1. "Edit profile" title (28pt bold)
    ///       2. "Select what you want to change." subtitle (14pt, secondary)
    ///       3. Property list — pill rows for Name + Icon (56px tall each)
    ///       4. Done + Delete Profile pill buttons (44px tall each)
    ///   - Right column (~520 wide):
    ///       1. 320×320 avatar preview (rounded square, 28px corner radius)
    ///       2. Name preview label (22pt bold, centered)
    ///
    /// Host orchestration model — IMPORTANT:
    ///   This view does NOT directly construct or invoke
    ///   <see cref="OnScreenKeyboardView"/> or <see cref="AvatarPickerView"/>.
    ///   Those sub-overlays are owned by the host (Program.cs). The editor
    ///   raises *Request* events when a property row is activated; the host
    ///   opens the relevant sub-overlay, and on completion the host pushes
    ///   the result back via <see cref="SetEditedName"/> /
    ///   <see cref="SetEditedAvatar"/>. This keeps the editor file independent
    ///   of the keyboard / picker implementations and lets the host coordinate
    ///   their layering above the editor in the modal stack.
    ///
    /// Saved/Delete events deliver a final <see cref="EditedProfile"/>
    /// snapshot or the bare profile id; the host applies via
    /// ProfileService.UpdateProfile / .DeleteProfile and then calls
    /// <see cref="Close"/> on this view.
    ///
    /// Focus model: a flat 4-cell list — [NameRow, IconRow, DoneButton,
    /// DeleteButton]. D-pad Up/Down navigates with clamped (no wrap) movement;
    /// Left/Right are ignored. Each focused cell flips its background to white
    /// (Done) or AccentLive (Delete confirm) — pills don't strictly need a
    /// scale animation since the BG color flip is the primary affordance.
    /// </summary>
    public sealed class ProfileEditView : View
    {
        // ---- Layout constants (1920x1080 reference) ----
        private const int LeftColumnWidth = 600;
        private const int RightColumnWidth = 520;
        private const int ColumnGap = 120;

        private const int PropertyRowHeight = 72;
        private const int PropertyRowCornerRadius = 36;   // height/2 → full pill
        private const int PropertyRowHorizontalPad = 24;

        private const int ActionRowGap = 14;
        private const int ActionPillHeight = 56;
        private const int ActionPillCornerRadius = 28;    // height/2 → full pill
        private const int ActionPillHorizontalPad = 22;

        private const int PreviewAvatarSize = 320;
        private const int PreviewAvatarCornerRadius = 28;
        private const int PreviewNameGap = 18;

        // Focus index map — order matters: Up/Down step through this list.
        private const int FocusName = 0;
        private const int FocusIcon = 1;
        private const int FocusDone = 2;
        private const int FocusDelete = 3;
        private const int FocusCellCount = 4;

        // ---- Public API ----
        public bool IsOpen { get; private set; }

        // Editor raises these on Activate of the corresponding property row.
        // The host opens the relevant sub-overlay, then calls SetEditedName /
        // SetEditedAvatar with the result. Payload of each is the *current*
        // edited value so the host can pre-populate the keyboard / pre-select
        // the avatar tile.
        public event EventHandler<string> RequestEditName;
        public event EventHandler<string> RequestPickAvatar;

        // Editor raises these on Done / Delete-confirmed press.
        public event EventHandler<EditedProfile> Saved;
        public event EventHandler<string> DeleteRequested;
        public event EventHandler Closed;

        // ---- Layout pieces ----
        private readonly View _leftColumn;
        private readonly View _rightColumn;
        private readonly View _nameRow;
        private readonly View _iconRow;
        private readonly View _doneButton;
        private readonly TextLabel _doneLabel;
        private readonly View _deleteButton;
        private readonly TextLabel _deleteLabel;

        // Preview pane — the children of _previewAvatar are torn down and
        // rebuilt every time the edited state changes (initials underneath,
        // optional ImageView on top — same layering trick as ProfileSelectorView).
        private readonly View _previewAvatar;
        private readonly TextLabel _previewName;

        // Per-row scale animations — cached so rapid Up/Down spam doesn't pile
        // up overlapping Animation instances on the same view.
        private readonly Dictionary<View, Animation> _rowScaleAnims = new Dictionary<View, Animation>();

        // Tracks the open/close opacity animation so a re-Open during a fade-out
        // doesn't leave the view stuck mid-fade. Same pattern as
        // ProfileSelectorView / OnScreenKeyboardView / AvatarPickerView.
        private Animation _modalOpacityAnim;

        // ---- Edited form state ----
        // The original profile id is preserved across edits; name + avatar
        // file name are the live form copy. Avatar color is cached at
        // SetProfile time so the colored fallback in the preview stays
        // consistent even if the user changes the name (initials change but
        // the BG color stays as the original profile's color).
        private string _profileId = "";
        private string _editedName = "";
        private string _editedAvatarFileName = "";
        private float _avatarR = 0.30f;
        private float _avatarG = 0.62f;
        private float _avatarB = 1.00f;

        // ---- Focus / state ----
        private int _focusedIndex = FocusName;

        // Two-press delete confirmation state machine. First Activate on the
        // Delete pill flips _deleteArmed → true and re-paints the pill in the
        // confirm style ("Press OK again to delete"). Second Activate (only if
        // still focused) raises DeleteRequested. Moving focus away from the
        // Delete pill resets _deleteArmed → false.
        private bool _deleteArmed;

        public ProfileEditView()
        {
            // Full-screen overlay — slightly more opaque than the picker
            // because we sit ABOVE the picker in the layer stack and want to
            // visually quiet the rows of profile tiles underneath.
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.FillToParent;
            BackgroundColor = new Color(Tokens.BgBase.R, Tokens.BgBase.G, Tokens.BgBase.B, 0.95f);

            // Two columns side-by-side, centered both ways, with a ~120px gap.
            // CellPadding's X gives the inter-column gap; LinearLayout takes
            // care of vertical centering of each column independently.
            Layout = new LinearLayout
            {
                LinearOrientation = LinearLayout.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                LinearAlignment = LinearLayout.Alignment.Center,
                CellPadding = new Size2D(ColumnGap, 0),
            };

            // ---- Left column ------------------------------------------------
            _leftColumn = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LeftColumnWidth,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    LinearAlignment = LinearLayout.Alignment.Begin,
                    CellPadding = new Size2D(0, 16),
                },
            };
            Add(_leftColumn);

            TextLabel title = new TextLabel
            {
                Text = "Edit profile",
                TextColor = Tokens.TextPrimary,
                PointSize = 28f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Begin,
                WidthSpecification = LeftColumnWidth,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _leftColumn.Add(title);

            TextLabel subtitle = new TextLabel
            {
                Text = "Select what you want to change.",
                TextColor = Tokens.TextSecondary,
                PointSize = 14f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("medium")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Begin,
                WidthSpecification = LeftColumnWidth,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                // 28px gap before the property list so the subtitle reads as
                // a discrete header rather than as part of the list.
                Margin = new Extents(0, 0, 0, 28),
            };
            _leftColumn.Add(subtitle);

            // ---- Property list ---------------------------------------------
            // Vertical stack of pill rows. CellPadding's Y gives the 6px gap
            // between rows; each row is a fixed 56px tall.
            View propertyList = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LeftColumnWidth,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    LinearAlignment = LinearLayout.Alignment.Begin,
                    CellPadding = new Size2D(0, 6),
                },
            };
            _leftColumn.Add(propertyList);

            // Name row — constant "Edit Profile Name" caption. The current
            // edited name is shown live in the right-pane preview, not in the
            // row itself.
            _nameRow = BuildPropertyRow("Edit Profile Name");
            propertyList.Add(_nameRow);

            // Icon row — constant "Edit Avatar" caption.
            _iconRow = BuildPropertyRow("Edit Avatar");
            propertyList.Add(_iconRow);

            // ---- Action row -------------------------------------------------
            // 32px gap below the property list, then a horizontal row of two
            // pill buttons (Done + Delete Profile). The buttons sit at the
            // start of the row — Done first, Delete to its right — so the
            // user's natural reading flow lands on Done first.
            View actionRow = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = LeftColumnWidth,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Margin = new Extents(0, 0, 32, 0),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.Begin,
                    CellPadding = new Size2D(ActionRowGap, 0),
                },
            };
            _leftColumn.Add(actionRow);

            _doneButton = BuildActionPill("Done", out _doneLabel);
            actionRow.Add(_doneButton);

            _deleteButton = BuildActionPill("Delete Profile", out _deleteLabel);
            actionRow.Add(_deleteButton);

            // ---- Right column (preview pane) -------------------------------
            _rightColumn = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = RightColumnWidth,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Center,
                    CellPadding = new Size2D(0, PreviewNameGap),
                },
            };
            Add(_rightColumn);

            // Preview avatar — fixed 320×320 rounded square that we re-fill
            // from current edited state on every change. NO Layout on the
            // wrapper so the FillToParent ImageView covers the full cell
            // instead of being squeezed by a LinearLayout's measure pass into
            // the initials TextLabel's slot. Initials center via
            // ParentOrigin/PivotPoint inside RebuildPreviewAvatar.
            _previewAvatar = new View
            {
                WidthSpecification = PreviewAvatarSize,
                HeightSpecification = PreviewAvatarSize,
                BackgroundColor = new Color(_avatarR, _avatarG, _avatarB, 1f),
                CornerRadius = new Vector4(
                    PreviewAvatarCornerRadius, PreviewAvatarCornerRadius,
                    PreviewAvatarCornerRadius, PreviewAvatarCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                ClippingMode = ClippingModeType.ClipChildren,
            };
            _rightColumn.Add(_previewAvatar);

            _previewName = new TextLabel
            {
                Text = "",
                TextColor = Tokens.TextPrimary,
                PointSize = 22f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                Ellipsis = true,
                WidthSpecification = RightColumnWidth,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _rightColumn.Add(_previewName);

            // Hidden by default; Open() fades in. Same pattern as
            // ProfileSelectorView / OnScreenKeyboardView / AvatarPickerView.
            Opacity = 0f;
            Hide();

            ApplyFocusVisual();
            RebuildPreviewAvatar();
        }

        // ---- Public API: form hydration / sub-overlay results ---------------

        /// <summary>Hydrate the form copy + preview from a profile. Safe to
        /// re-call (e.g. when the host re-opens the editor on a different
        /// profile). The avatar color is cached so subsequent name edits
        /// keep the same colored fallback BG.</summary>
        public void SetProfile(Profile p)
        {
            if (p == null)
            {
                _profileId = "";
                _editedName = "";
                _editedAvatarFileName = "";
                _avatarR = 0.30f;
                _avatarG = 0.62f;
                _avatarB = 1.00f;
            }
            else
            {
                _profileId = p.Id ?? "";
                _editedName = p.Name ?? "";
                _editedAvatarFileName = p.AvatarImage ?? "";
                _avatarR = p.AvatarR;
                _avatarG = p.AvatarG;
                _avatarB = p.AvatarB;
            }

            // Reset the delete-confirm state — re-hydrating the form clears
            // any in-flight destructive action so a fresh open can't trip
            // the second-press path on stale state.
            _deleteArmed = false;
            _focusedIndex = FocusName;

            RebuildPreviewAvatar();
            RefreshPreviewName();
            ApplyFocusVisual();
        }

        /// <summary>Push a name result from the on-screen keyboard back into
        /// the form. Updates the edited name and the live preview (initials
        /// re-derive from the new name).</summary>
        public void SetEditedName(string name)
        {
            _editedName = name ?? "";
            // Re-derive initials in the preview from the new name.
            RebuildPreviewAvatar();
            RefreshPreviewName();
        }

        /// <summary>Push an avatar choice from the avatar picker back into the
        /// form. Empty string means the user picked "Initials only" — the
        /// preview falls back to the colored initials card.</summary>
        public void SetEditedAvatar(string fileName)
        {
            _editedAvatarFileName = fileName ?? "";
            RebuildPreviewAvatar();
        }

        // ---- Open / Close ---------------------------------------------------

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;

            // Snap focus back to the Name row on every open so the user
            // re-enters at a predictable cell. Reset delete-confirm state
            // for the same reason — we never want a re-Open to land on an
            // armed Delete pill from a previous session.
            _focusedIndex = FocusName;
            _deleteArmed = false;
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

        // ---- Key routing ----------------------------------------------------

        public void HandleKey(string key)
        {
            if (!IsOpen) return;
            switch (key)
            {
                case "Up":    MoveUp(); break;
                case "Down":  MoveDown(); break;
                case "Left":  MoveLeft(); break;
                case "Right": MoveRight(); break;
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
                    // Host owns Close() — same contract as the other overlays
                    // so the host can run follow-up logic before dismiss.
                    Closed?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        // ---- Internal: focus + activation -----------------------------------

        // 3-row layout: Name (row 0) → Icon (row 1) → [Done, Delete] (row 2).
        // Up/Down navigate between rows; Left/Right toggle within the action
        // row only. Up from Done OR Delete returns to Icon; Down from Icon
        // lands on Done (leftmost in the action row).
        private void MoveUp()
        {
            switch (_focusedIndex)
            {
                case FocusIcon:   SetFocus(FocusName); break;
                case FocusDone:   SetFocus(FocusIcon); break;
                case FocusDelete: SetFocus(FocusIcon); break;
            }
        }

        private void MoveDown()
        {
            switch (_focusedIndex)
            {
                case FocusName: SetFocus(FocusIcon); break;
                case FocusIcon: SetFocus(FocusDone); break;
            }
        }

        private void MoveLeft()
        {
            // Only meaningful inside the action row.
            if (_focusedIndex == FocusDelete) SetFocus(FocusDone);
        }

        private void MoveRight()
        {
            // Only meaningful inside the action row.
            if (_focusedIndex == FocusDone) SetFocus(FocusDelete);
        }

        private void SetFocus(int next)
        {
            if (next < 0 || next >= FocusCellCount) return;
            if (next == _focusedIndex) return;

            // If we're moving OFF the Delete pill (whether to Done via Left
            // or to Icon via Up), clear the armed state and restore the
            // default pill visuals. Documented escape hatch for the
            // two-press delete confirm.
            if (_focusedIndex == FocusDelete && next != FocusDelete && _deleteArmed)
            {
                _deleteArmed = false;
                _deleteLabel.Text = "Delete Profile";
            }

            _focusedIndex = next;
            ApplyFocusVisual();
        }

        private void ActivateFocused()
        {
            switch (_focusedIndex)
            {
                case FocusName:
                    // Host opens its OnScreenKeyboardView pre-populated with
                    // the current edited name; on Submit it calls
                    // SetEditedName(...) to push the result back here.
                    RequestEditName?.Invoke(this, _editedName ?? "");
                    break;
                case FocusIcon:
                    // Host opens its AvatarPickerView pre-selected on the
                    // current edited avatar; on Pick it calls
                    // SetEditedAvatar(...) to push the result back here.
                    RequestPickAvatar?.Invoke(this, _editedAvatarFileName ?? "");
                    break;
                case FocusDone:
                    Saved?.Invoke(this, new EditedProfile(_profileId, _editedName, _editedAvatarFileName));
                    break;
                case FocusDelete:
                    // Two-press delete state machine:
                    //   - First press: arm the confirm state, swap label +
                    //     force the AccentLive pill visual regardless of focus
                    //     so the user sees the warning. Stay focused.
                    //   - Second press (still focused, _deleteArmed == true):
                    //     fire DeleteRequested. Don't reset state — the host
                    //     will Close() this view in response.
                    if (!_deleteArmed)
                    {
                        _deleteArmed = true;
                        _deleteLabel.Text = "Press OK again to delete";
                        ApplyFocusVisual();
                    }
                    else
                    {
                        DeleteRequested?.Invoke(this, _profileId ?? "");
                    }
                    break;
            }
        }

        // ---- Internal: row + pill builders ----------------------------------

        // Builds a 56px-tall pill row with a small leading icon glyph + a
        // label. The label reference is returned via `out` so SetEditedName
        // can update it in place (Name row); the Icon row passes the
        // returned label to a local var and then ignores it. The row's
        // BG/border are managed by ApplyFocusVisual.
        // Builds a property-list row. Constant label, no icon glyph (per
        // user feedback the bullets read as decoration noise). Pill shape
        // (CornerRadius = height/2). Focused state is applied in
        // ApplyFocusVisual via background color flip on the row's BG.
        private View BuildPropertyRow(string labelText)
        {
            View row = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.10f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(
                    PropertyRowCornerRadius, PropertyRowCornerRadius,
                    PropertyRowCornerRadius, PropertyRowCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                WidthSpecification = LeftColumnWidth,
                HeightSpecification = PropertyRowHeight,
                Padding = new Extents(PropertyRowHorizontalPad, PropertyRowHorizontalPad, 0, 0),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.CenterVertical,
                },
            };

            TextLabel label = new TextLabel
            {
                Text = labelText ?? "",
                TextColor = Tokens.TextPrimary,
                PointSize = 24f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Begin,
                VerticalAlignment = VerticalAlignment.Center,
                Ellipsis = true,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            row.Add(label);

            return row;
        }

        // Builds an action pill (Done / Delete Profile). Width is wrap-content
        // so each pill sizes to its label + 14px horizontal padding. The
        // returned label reference lets ActivateFocused/MoveFocus mutate the
        // text (Delete pill's confirm-state swap).
        private View BuildActionPill(string text, out TextLabel label)
        {
            View pill = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                BorderlineColor = new Color(0f, 0f, 0f, 0f),
                BorderlineWidth = 0f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(
                    ActionPillCornerRadius, ActionPillCornerRadius,
                    ActionPillCornerRadius, ActionPillCornerRadius),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = ActionPillHeight,
                Padding = new Extents(ActionPillHorizontalPad, ActionPillHorizontalPad, 0, 0),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Center,
                },
            };

            label = new TextLabel
            {
                Text = text ?? "",
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
            pill.Add(label);

            return pill;
        }

        // ---- Internal: focus visuals ----------------------------------------

        // Re-paint every focusable cell from the current _focusedIndex +
        // _deleteArmed state. Cheap to call repeatedly because nothing
        // allocates here aside from the cached scale animations. Pills don't
        // get a strong scale on focus (1.04 is barely perceptible) — the
        // BG color flip is the load-bearing affordance.
        private void ApplyFocusVisual()
        {
            // Property rows — focused = solid white BG, unfocused = transparent
            // with a faint white outline.
            PaintPropertyRow(_nameRow, _focusedIndex == FocusName);
            PaintPropertyRow(_iconRow, _focusedIndex == FocusIcon);

            // Done pill — focused = white BG + dark text, unfocused = white@0.18
            // BG + white text. Subtle but matches the Netflix reference's
            // bright "Done" affordance.
            PaintDonePill(_focusedIndex == FocusDone);

            // Delete pill — three states:
            //   armed (regardless of focus)  → AccentLive BG, white text
            //   focused (not armed)          → AccentLive BG, white text
            //   unfocused (not armed)        → transparent BG + white text + faint border
            PaintDeletePill(_focusedIndex == FocusDelete, _deleteArmed);

            // The 1.04× scale lift is purely a polish cue; pills can stay flat
            // without it but rows benefit from the slight pop on focus.
            AnimateScale(_nameRow,    _focusedIndex == FocusName ? 1.04f : 1.0f);
            AnimateScale(_iconRow,    _focusedIndex == FocusIcon ? 1.04f : 1.0f);
            AnimateScale(_doneButton, _focusedIndex == FocusDone ? 1.04f : 1.0f);
            AnimateScale(_deleteButton, _focusedIndex == FocusDelete ? 1.04f : 1.0f);
        }

        private void PaintPropertyRow(View row, bool focused)
        {
            if (row == null) return;
            if (focused)
            {
                row.BackgroundColor = Color.White;
                row.BorderlineColor = new Color(0f, 0f, 0f, 0f);
                row.BorderlineWidth = 0f;
                // Re-tint the row's children so text stays readable on the
                // bright BG. We walk the immediate children (icon glyph + label)
                // and force them to the dark "on-light" text token.
                for (uint i = 0; i < row.ChildCount; i++)
                {
                    TextLabel tl = row.GetChildAt(i) as TextLabel;
                    if (tl != null) tl.TextColor = Tokens.TextOnLight;
                }
            }
            else
            {
                row.BackgroundColor = new Color(0f, 0f, 0f, 0f);
                row.BorderlineColor = new Color(1f, 1f, 1f, 0.10f);
                row.BorderlineWidth = 1f;
                for (uint i = 0; i < row.ChildCount; i++)
                {
                    TextLabel tl = row.GetChildAt(i) as TextLabel;
                    if (tl == null) continue;
                    // First child is the icon glyph — rendered in TextSecondary;
                    // the rest of the labels are TextPrimary.
                    tl.TextColor = (i == 0) ? Tokens.TextSecondary : Tokens.TextPrimary;
                }
            }
        }

        private void PaintDonePill(bool focused)
        {
            if (focused)
            {
                _doneButton.BackgroundColor = Color.White;
                _doneLabel.TextColor = Tokens.TextOnLight;
            }
            else
            {
                _doneButton.BackgroundColor = new Color(1f, 1f, 1f, 0.18f);
                _doneLabel.TextColor = Tokens.TextPrimary;
            }
        }

        private void PaintDeletePill(bool focused, bool armed)
        {
            if (armed || focused)
            {
                // Both armed and plain-focused share the same alarming visual
                // — AccentLive BG + white text — because once focus lands on
                // Delete we want the user to feel the destructive weight of
                // the action even before they confirm.
                _deleteButton.BackgroundColor = Tokens.AccentLive;
                _deleteButton.BorderlineColor = new Color(0f, 0f, 0f, 0f);
                _deleteButton.BorderlineWidth = 0f;
                _deleteLabel.TextColor = Color.White;
            }
            else
            {
                // Resting state — transparent BG with a white text label and
                // a thin 1px white@0.20 border. Reads as "available action,
                // not selected" without competing with Done for attention.
                _deleteButton.BackgroundColor = new Color(0f, 0f, 0f, 0f);
                _deleteButton.BorderlineColor = new Color(1f, 1f, 1f, 0.20f);
                _deleteButton.BorderlineWidth = 1f;
                _deleteLabel.TextColor = Tokens.TextPrimary;
            }
        }

        private void AnimateScale(View v, float target)
        {
            if (v == null) return;
            Animation prev;
            if (_rowScaleAnims.TryGetValue(v, out prev) && prev != null) prev.Stop();
            Animation a = new Animation(Dimens.AnimFocusScale);
            a.AnimateTo(v, "Scale",
                new Vector3(target, target, 1f),
                new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOutSquare));
            a.Play();
            _rowScaleAnims[v] = a;
        }

        // ---- Internal: form refresh helpers ---------------------------------

        // Update the Name row's caption to the current edited name, falling
        // back to a hint when the user hasn't entered anything yet so the row
        // doesn't render visually empty.
        private void RefreshPreviewName()
        {
            if (_previewName == null) return;
            _previewName.Text = _editedName ?? "";
        }

        // Tear down + rebuild the preview avatar's children to reflect the
        // current edited state. Always lays down the colored initials view
        // first; layers an ImageView ON TOP only when the edited avatar
        // resolves to a bundled file on disk. This mirrors
        // ProfileSelectorView.BuildProfileTile's avatar-with-fallback pattern.
        private void RebuildPreviewAvatar()
        {
            if (_previewAvatar == null) return;

            // Re-apply the BG color from the cached avatar palette so the
            // initials fallback (and the masked margins around any image)
            // stay consistent with the original profile's color identity.
            _previewAvatar.BackgroundColor = new Color(_avatarR, _avatarG, _avatarB, 1f);

            ClearChildren(_previewAvatar);

            // Initials layer — always present; serves as both the fallback
            // for the no-image case AND the loading placeholder when an
            // ImageView is being decoded.
            TextLabel initials = new TextLabel
            {
                Text = DeriveInitials(_editedName),
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
            _previewAvatar.Add(initials);

            // Optional image layer — only added when the file resolves on
            // disk. Resolved through HeroView.ResolveBundledAssetPath so all
            // bundled-asset lookups go through the same Tizen .tpk-aware path.
            string path = HeroView.ResolveBundledAssetPath(_editedAvatarFileName);
            if (!string.IsNullOrEmpty(path))
            {
                ImageView img = new ImageView
                {
                    ResourceUrl = path,
                    WidthResizePolicy = ResizePolicyType.FillToParent,
                    HeightResizePolicy = ResizePolicyType.FillToParent,
                    FittingMode = FittingModeType.ScaleToFill,
                    ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft,
                    PivotPoint = Tizen.NUI.PivotPoint.TopLeft,
                };
                _previewAvatar.Add(img);
            }
        }

        // Re-implements Profile.Initials' logic against the *edited* name
        // (Profile.Initials would only reflect the persisted name, which is
        // stale until the user presses Done). Empty/whitespace → "•" so the
        // preview never renders as an empty card.
        private static string DeriveInitials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "•";
            string n = name.Trim();
            string[] parts = n.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "•";
            if (parts.Length == 1)
            {
                string p = parts[0];
                return (p.Length >= 2 ? p.Substring(0, 2) : p).ToUpperInvariant();
            }
            return (parts[0][0].ToString() + parts[parts.Length - 1][0]).ToUpperInvariant();
        }

        // Same idiom as ProfileSelectorView/AvatarPickerView — Remove the
        // first child in a count-bounded loop because NUI's child collection
        // mutates as we iterate.
        private static void ClearChildren(View v)
        {
            if (v == null) return;
            uint count = v.ChildCount;
            for (uint i = 0; i < count; i++) v.Remove(v.GetChildAt(0));
        }
    }
}
