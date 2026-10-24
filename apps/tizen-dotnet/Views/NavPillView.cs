using System;
using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using StreamedTv.Theme;

namespace StreamedTv.Views
{
    /// <summary>
    /// Centered glass pill containing horizontal chips: ★ For You · Fav1 · Fav2 · Fav3 · ＋ More
    /// Top-level horizontal LinearLayout drives chip placement. Each chip is itself a small
    /// horizontal LinearLayout so its label sits at chip-center via Padding.
    /// </summary>
    public sealed class NavPillView : View
    {
        public enum NavSelection { ForYou, Favorite0, Favorite1, Favorite2, More, LiveTV }

        private const int TotalChips = 6;
        // Bumped a touch for more visual presence. ChipHeight stays the
        // primary chip size; PillHeight is the wrapper glass capsule height
        // and includes top/bottom padding around the chips.
        private const int ChipHeight = 52;
        private const int PillHeight = 76;

        private readonly View _forYouChip;
        private readonly View _fav0Chip;
        private readonly View _fav1Chip;
        private readonly View _fav2Chip;
        private readonly View _moreChip;
        private readonly View _liveChip;
        private readonly TextLabel _forYouLabel;
        private readonly TextLabel _fav0Label;
        private readonly TextLabel _fav1Label;
        private readonly TextLabel _fav2Label;
        private readonly TextLabel _moreLabel;
        private readonly TextLabel _liveLabel;

        private readonly List<string> _favoriteSports;
        private bool _hasFocus;

        public NavSelection FocusedItem { get; private set; }
        public NavSelection ActiveItem { get; private set; }

        public IReadOnlyList<string> FavoriteSports
        {
            get { return _favoriteSports.AsReadOnly(); }
        }

        public int ChipCount
        {
            get { return TotalChips; }
        }

        public event EventHandler ActivateRequested;

        public NavPillView()
        {
            _favoriteSports = new List<string> { "", "", "" };
            FocusedItem = NavSelection.ForYou;
            ActiveItem = NavSelection.ForYou;
            _hasFocus = false;

            HeightSpecification = PillHeight;
            WidthSpecification = LayoutParamPolicies.WrapContent;

            // Glass surface: rounded fill + soft shadow.
            Glass.Apply(this, PillHeight / 2f);

            Layout = new LinearLayout
            {
                LinearOrientation = LinearLayout.Orientation.Horizontal,
                LinearAlignment = LinearLayout.Alignment.CenterVertical,
                CellPadding = new Size2D(Dimens.ChipGap, 0),
                Padding = new Extents(
                    (ushort)Dimens.NavPillHorizontalPad,
                    (ushort)Dimens.NavPillHorizontalPad,
                    (ushort)Dimens.NavPillVerticalPad,
                    (ushort)Dimens.NavPillVerticalPad),
            };

            _forYouChip = BuildChip("★ For You", out _forYouLabel);
            Add(_forYouChip);

            Add(BuildDivider());

            _fav0Chip = BuildChip("", out _fav0Label);
            Add(_fav0Chip);

            _fav1Chip = BuildChip("", out _fav1Label);
            Add(_fav1Chip);

            _fav2Chip = BuildChip("", out _fav2Label);
            Add(_fav2Chip);

            Add(BuildDivider());

            _moreChip = BuildChip("＋ More", out _moreLabel);
            Add(_moreChip);

            // Live TV (iptv-org). Last chip — discoverable by paging
            // right past the rest. Slated for removal once the
            // iptv-org experiment wraps up; new field + enum value +
            // switch arms only.
            Add(BuildDivider());
            _liveChip = BuildChip("● Live", out _liveLabel);
            Add(_liveChip);

            ApplyChipStyle(NavSelection.ForYou, true, false);
            ApplyChipStyle(NavSelection.Favorite0, false, false);
            ApplyChipStyle(NavSelection.Favorite1, false, false);
            ApplyChipStyle(NavSelection.Favorite2, false, false);
            ApplyChipStyle(NavSelection.More, false, false);
            ApplyChipStyle(NavSelection.LiveTV, false, false);
        }

        public void SetFavorites(IReadOnlyList<string> favoriteIds)
        {
            if (favoriteIds == null || favoriteIds.Count == 0) return;

            for (int i = 0; i < _favoriteSports.Count && i < favoriteIds.Count; i++)
            {
                _favoriteSports[i] = favoriteIds[i] ?? "";
            }

            _fav0Label.Text = DisplayNameForSport(_favoriteSports[0]);
            _fav1Label.Text = DisplayNameForSport(_favoriteSports[1]);
            _fav2Label.Text = DisplayNameForSport(_favoriteSports[2]);

            ApplyChipStyle(NavSelection.Favorite0, ActiveItem == NavSelection.Favorite0, _hasFocus && FocusedItem == NavSelection.Favorite0);
            ApplyChipStyle(NavSelection.Favorite1, ActiveItem == NavSelection.Favorite1, _hasFocus && FocusedItem == NavSelection.Favorite1);
            ApplyChipStyle(NavSelection.Favorite2, ActiveItem == NavSelection.Favorite2, _hasFocus && FocusedItem == NavSelection.Favorite2);
        }

        public void SetActive(NavSelection selection)
        {
            if (ActiveItem == selection) return;

            NavSelection previous = ActiveItem;
            ActiveItem = selection;

            ApplyChipStyle(previous, false, _hasFocus && FocusedItem == previous);
            ApplyChipStyle(selection, true, _hasFocus && FocusedItem == selection);
        }

        public void SetFocused(NavSelection selection)
        {
            if (_hasFocus && FocusedItem == selection) return;

            NavSelection previous = FocusedItem;
            bool hadFocus = _hasFocus;
            FocusedItem = selection;
            _hasFocus = true;

            if (hadFocus && previous != selection)
            {
                ApplyChipStyle(previous, ActiveItem == previous, false);
            }
            ApplyChipStyle(selection, ActiveItem == selection, true);

            // Lift the WHOLE pill on first focus entry (transition from
            // unfocused → focused). Subsequent chip-to-chip moves don't
            // re-lift since the pill is already up. Same easing as chip
            // focus so the two motions feel like one continuous lift.
            if (!hadFocus) AnimateBarLift(true);
        }

        public void ClearFocus()
        {
            if (!_hasFocus) return;

            NavSelection previous = FocusedItem;
            _hasFocus = false;

            ApplyChipStyle(previous, ActiveItem == previous, false);
            // Drop the whole pill back to its resting size as focus leaves.
            AnimateBarLift(false);
        }

        // Whole-pill scale lift, played when the pill gains/loses focus.
        // Stashed Animation so rapid focus toggles cleanly interrupt the
        // previous spring instead of stacking competing lifts.
        private Animation _barLiftAnim;
        private void AnimateBarLift(bool focused)
        {
            try
            {
                _barLiftAnim?.Stop();
                _barLiftAnim = new Animation(Dimens.AnimFocusScale);
                float target = focused ? 1.06f : 1.0f;
                _barLiftAnim.AnimateTo(this, "Scale",
                    new Vector3(target, target, 1f),
                    new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOutSquare));
                _barLiftAnim.Play();
            }
            catch { }
        }

        public NavSelection SelectionForIndex(int index)
        {
            switch (index)
            {
                case 0:  return NavSelection.ForYou;
                case 1:  return NavSelection.Favorite0;
                case 2:  return NavSelection.Favorite1;
                case 3:  return NavSelection.Favorite2;
                case 4:  return NavSelection.More;
                case 5:  return NavSelection.LiveTV;
                default: return NavSelection.ForYou;
            }
        }

        public int IndexForSelection(NavSelection selection)
        {
            switch (selection)
            {
                case NavSelection.ForYou:    return 0;
                case NavSelection.Favorite0: return 1;
                case NavSelection.Favorite1: return 2;
                case NavSelection.Favorite2: return 3;
                case NavSelection.More:      return 4;
                case NavSelection.LiveTV:    return 5;
                default:                     return 0;
            }
        }

        public string SportIdFor(NavSelection selection)
        {
            switch (selection)
            {
                case NavSelection.Favorite0: return _favoriteSports[0];
                case NavSelection.Favorite1: return _favoriteSports[1];
                case NavSelection.Favorite2: return _favoriteSports[2];
                default:                     return null;
            }
        }

        public void RaiseActivated()
        {
            ActivateRequested?.Invoke(this, EventArgs.Empty);
        }

        private static View BuildChip(string text, out TextLabel label)
        {
            View chip = new View
            {
                HeightSpecification = ChipHeight,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.Center,
                    Padding = new Extents(
                        (ushort)Dimens.ChipHorizontalPad,
                        (ushort)Dimens.ChipHorizontalPad,
                        (ushort)Dimens.ChipVerticalPad,
                        (ushort)Dimens.ChipVerticalPad),
                },
                CornerRadius = new Vector4(ChipHeight / 2f, ChipHeight / 2f, ChipHeight / 2f, ChipHeight / 2f),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
            };

            TextLabel chipLabel = new TextLabel
            {
                Text = text,
                TextColor = Tokens.TextSecondary,
                PointSize = Dimens.FontChip,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("medium")),
                MultiLine = false,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            chip.Add(chipLabel);
            label = chipLabel;

            return chip;
        }

        private static View BuildDivider()
        {
            View divider = new View
            {
                WidthSpecification = Dimens.ChipDividerWidth,
                HeightSpecification = Dimens.ChipDividerHeight,
                BackgroundColor = Tokens.Divider,
            };
            return divider;
        }

        private void ApplyChipStyle(NavSelection selection, bool active, bool focused)
        {
            View chip = ChipFor(selection);
            TextLabel label = LabelFor(selection);
            if (chip == null || label == null) return;

            if (active)
            {
                // Active chip stays neutral white. Earlier the background tracked
                // SportPalette.Current.Primary, but the static per-sport accents
                // (e.g. basketball orange) read as overpainted on the chrome —
                // the bundled hero banner already carries the sport's color.
                chip.BackgroundColor = Tokens.ChipActive;
                label.TextColor = Tokens.TextOnLight;
                label.FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold"));
                // Active chip: subtle font bump so the "currently selected sport"
                // reads as anchored without competing with the focus lift.
                label.PointSize = Dimens.FontChip + 2f;
                chip.BorderlineWidth = 0f;
            }
            else if (focused)
            {
                chip.BackgroundColor = new Color(1f, 1f, 1f, 0.10f);
                label.TextColor = Tokens.TextPrimary;
                label.FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold"));
                // Focused chip: larger font so the cursor's location is unmistakable
                // even from across the room.
                label.PointSize = Dimens.FontChip + 4f;
                chip.BorderlineColor = new Color(1f, 1f, 1f, 0.30f);
                chip.BorderlineWidth = 1f;
                chip.BorderlineOffset = -1f;
            }
            else
            {
                chip.BackgroundColor = new Color(0f, 0f, 0f, 0f);
                label.TextColor = Tokens.TextSecondary;
                label.FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold"));
                label.PointSize = Dimens.FontChip;
                chip.BorderlineWidth = 0f;
            }

            // Scale-up affordance — same easing as the home-page tile focus so the
            // whole app shares one motion language. ONLY the focused chip scales.
            // (Active alone stays at 1.0.) If both flags are true, the focused
            // branch wins, so at most one chip is scaled at a time — fixing the
            // overlap the user saw when the active chip and the newly-focused
            // chip were both at 1.10 and crashed into each other in the row.
            float targetScale = focused ? 1.16f : 1.0f;
            Animation prev;
            if (_chipScaleAnims.TryGetValue(chip, out prev) && prev != null) prev.Stop();
            Animation a = new Animation(Dimens.AnimFocusScale);
            a.AnimateTo(chip, "Scale",
                new Vector3(targetScale, targetScale, 1f),
                new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOutSquare));
            a.Play();
            _chipScaleAnims[chip] = a;
        }

        private readonly Dictionary<View, Animation> _chipScaleAnims = new Dictionary<View, Animation>();

        private View ChipFor(NavSelection selection)
        {
            switch (selection)
            {
                case NavSelection.ForYou:    return _forYouChip;
                case NavSelection.Favorite0: return _fav0Chip;
                case NavSelection.Favorite1: return _fav1Chip;
                case NavSelection.Favorite2: return _fav2Chip;
                case NavSelection.More:      return _moreChip;
                case NavSelection.LiveTV:    return _liveChip;
                default:                     return null;
            }
        }

        private TextLabel LabelFor(NavSelection selection)
        {
            switch (selection)
            {
                case NavSelection.ForYou:    return _forYouLabel;
                case NavSelection.Favorite0: return _fav0Label;
                case NavSelection.Favorite1: return _fav1Label;
                case NavSelection.Favorite2: return _fav2Label;
                case NavSelection.More:      return _moreLabel;
                case NavSelection.LiveTV:    return _liveLabel;
                default:                     return null;
            }
        }

        private static string DisplayNameForSport(string sportId)
        {
            if (string.IsNullOrWhiteSpace(sportId)) return "";
            string trimmed = sportId.Trim();
            if (trimmed.Length == 1) return trimmed.ToUpperInvariant();
            return char.ToUpperInvariant(trimmed[0]) + trimmed.Substring(1).ToLowerInvariant();
        }
    }
}
