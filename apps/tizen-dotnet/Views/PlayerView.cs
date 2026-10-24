using System;
using System.Threading;
using System.Timers;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using StreamedTv.Models;
using StreamedTv.Theme;

namespace StreamedTv.Views
{
    public enum PlayerAction { SwitchStream, Audio, Quality, Info }

    public sealed class PlayerActionEventArgs : System.EventArgs
    {
        public PlayerAction Action { get; }
        public PlayerActionEventArgs(PlayerAction action) { Action = action; }
    }

    public sealed class PlayerView : View
    {
        private const int AutoHideMs = 3000;
        private const int LivePillHeight = 32;
        private const int LivePillTopMargin = 32;
        private const int LivePillSideMargin = 60;
        private const int ScoreCardWidth = 320;
        private const int ScoreCardHeight = 84;
        private const int ScoreCardCornerRadius = 12;
        private const int ScoreCardSideMargin = 60;
        private const int ScoreCardTopMargin = 32;
        private const int ScoreCardPadding = 14;
        private const int TeamBadgeSize = 44;
        private const int TeamGap = 12;
        private const int ScoreFontSize = 18;
        private const int ActionBarHeight = 64;
        private const int ActionBarBottomMargin = 60;
        private const int ActionBarHorizontalPad = 12;
        private const int ActionBarVerticalPad = 8;
        private const int ActionChipCornerRadius = 18;
        private const int ActionChipHorizontalPad = 18;
        private const int ActionChipVerticalPad = 8;
        private const int ActionChipGap = 6;
        private const int ChipDividerHeight = 24;
        private const int ChipDividerWidth = 1;

        private static readonly int ActionCount = Enum.GetValues(typeof(PlayerAction)).Length;

        private readonly SynchronizationContext _syncContext;
        private readonly System.Timers.Timer _autoHideTimer;

        private readonly View _livePill;
        private readonly TextLabel _livePillLabel;

        private readonly View _chromeGroup;

        private readonly View _scoreCard;
        private readonly View _scoreHomeBadge;
        private readonly TextLabel _scoreHomeInitials;
        private readonly TextLabel _scoreHomeValue;
        private readonly View _scoreAwayBadge;
        private readonly TextLabel _scoreAwayInitials;
        private readonly TextLabel _scoreAwayValue;
        private readonly TextLabel _scoreSeparator;

        private readonly View _actionBar;
        private readonly View[] _chipBg;
        private readonly TextLabel[] _chipLabel;

        private Animation _chromeAnimation;
        private CatalogMatch _match;
        private int _streamCount;

        public bool ChromeVisible { get; private set; }
        public PlayerAction FocusedAction { get; private set; }

        public event System.EventHandler<PlayerActionEventArgs> ActionInvoked;

        public PlayerView()
        {
            _syncContext = SynchronizationContext.Current;
            FocusedAction = PlayerAction.SwitchStream;
            ChromeVisible = false;
            _streamCount = 0;

            BackgroundColor = new Color(0f, 0f, 0f, 0f);
            ClippingMode = ClippingModeType.Disabled;

            // ----- Live pill (top-left, always visible) -----
            _livePill = new View
            {
                SizeHeight = LivePillHeight,
                Position = new Position(LivePillSideMargin, LivePillTopMargin),
                WidthResizePolicy = ResizePolicyType.FitToChildren,
            };
            _livePill.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            _livePill.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            Glass.Apply(_livePill, LivePillHeight / 2f);
            Add(_livePill);

            _livePillLabel = new TextLabel
            {
                Text = "● LIVE",
                TextColor = Tokens.TextPrimary,
                PointSize = Dimens.FontPill,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                MultiLine = false,
                Padding = new Extents((ushort)16, (ushort)16, (ushort)8, (ushort)8),
                WidthResizePolicy = ResizePolicyType.FitToChildren,
                HeightResizePolicy = ResizePolicyType.FitToChildren,
            };
            _livePill.Add(_livePillLabel);

            // ----- Chrome group (everything that auto-hides) -----
            _chromeGroup = new View
            {
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.FillToParent,
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                Opacity = 0f,
            };
            _chromeGroup.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            _chromeGroup.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            _chromeGroup.Hide();
            Add(_chromeGroup);

            // ----- Score card (top-right) -----
            _scoreCard = new View
            {
                Size = new Size(ScoreCardWidth, ScoreCardHeight),
                Position = new Position(-ScoreCardSideMargin, ScoreCardTopMargin),
            };
            _scoreCard.ParentOrigin = Tizen.NUI.ParentOrigin.TopRight;
            _scoreCard.PivotPoint = Tizen.NUI.PivotPoint.TopRight;
            Glass.Apply(_scoreCard, ScoreCardCornerRadius);
            _chromeGroup.Add(_scoreCard);

            _scoreHomeBadge = new View
            {
                Size = new Size(TeamBadgeSize, TeamBadgeSize),
                BackgroundColor = Tokens.AccentLive,
                Position = new Position(ScoreCardPadding, 0f),
            };
            _scoreHomeBadge.ParentOrigin = Tizen.NUI.ParentOrigin.CenterLeft;
            _scoreHomeBadge.PivotPoint = Tizen.NUI.PivotPoint.CenterLeft;
            _scoreCard.Add(_scoreHomeBadge);

            _scoreHomeInitials = new TextLabel
            {
                Text = "",
                TextColor = Tokens.TextPrimary,
                PointSize = Dimens.FontPill,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.FillToParent,
            };
            _scoreHomeBadge.Add(_scoreHomeInitials);

            _scoreHomeValue = new TextLabel
            {
                Text = "—",
                TextColor = Tokens.TextPrimary,
                PointSize = ScoreFontSize,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                Position = new Position(ScoreCardPadding + TeamBadgeSize + TeamGap, 0f),
                WidthResizePolicy = ResizePolicyType.FitToChildren,
                HeightResizePolicy = ResizePolicyType.FitToChildren,
            };
            _scoreHomeValue.ParentOrigin = Tizen.NUI.ParentOrigin.CenterLeft;
            _scoreHomeValue.PivotPoint = Tizen.NUI.PivotPoint.CenterLeft;
            _scoreCard.Add(_scoreHomeValue);

            _scoreSeparator = new TextLabel
            {
                Text = "·",
                TextColor = Tokens.TextSecondary,
                PointSize = ScoreFontSize,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                MultiLine = false,
                Position = new Position(0f, 0f),
                WidthResizePolicy = ResizePolicyType.FitToChildren,
                HeightResizePolicy = ResizePolicyType.FitToChildren,
            };
            _scoreSeparator.ParentOrigin = Tizen.NUI.ParentOrigin.Center;
            _scoreSeparator.PivotPoint = Tizen.NUI.PivotPoint.Center;
            _scoreCard.Add(_scoreSeparator);

            _scoreAwayValue = new TextLabel
            {
                Text = "—",
                TextColor = Tokens.TextPrimary,
                PointSize = ScoreFontSize,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                Position = new Position(-(ScoreCardPadding + TeamBadgeSize + TeamGap), 0f),
                WidthResizePolicy = ResizePolicyType.FitToChildren,
                HeightResizePolicy = ResizePolicyType.FitToChildren,
            };
            _scoreAwayValue.ParentOrigin = Tizen.NUI.ParentOrigin.CenterRight;
            _scoreAwayValue.PivotPoint = Tizen.NUI.PivotPoint.CenterRight;
            _scoreCard.Add(_scoreAwayValue);

            _scoreAwayBadge = new View
            {
                Size = new Size(TeamBadgeSize, TeamBadgeSize),
                BackgroundColor = Tokens.AccentScheduled,
                Position = new Position(-ScoreCardPadding, 0f),
            };
            _scoreAwayBadge.ParentOrigin = Tizen.NUI.ParentOrigin.CenterRight;
            _scoreAwayBadge.PivotPoint = Tizen.NUI.PivotPoint.CenterRight;
            _scoreCard.Add(_scoreAwayBadge);

            _scoreAwayInitials = new TextLabel
            {
                Text = "",
                TextColor = Tokens.TextPrimary,
                PointSize = Dimens.FontPill,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.FillToParent,
            };
            _scoreAwayBadge.Add(_scoreAwayInitials);

            // ----- Bottom action bar (4 chips inside a glass pill) -----
            _actionBar = new View
            {
                SizeHeight = ActionBarHeight,
                Position = new Position(0f, -ActionBarBottomMargin),
                WidthResizePolicy = ResizePolicyType.FitToChildren,
            };
            _actionBar.ParentOrigin = Tizen.NUI.ParentOrigin.BottomCenter;
            _actionBar.PivotPoint = Tizen.NUI.PivotPoint.BottomCenter;
            Glass.Apply(_actionBar, ActionBarHeight / 2f);
            _chromeGroup.Add(_actionBar);

            _chipBg = new View[ActionCount];
            _chipLabel = new TextLabel[ActionCount];

            View chipsContainer = new View
            {
                WidthResizePolicy = ResizePolicyType.FitToChildren,
                HeightResizePolicy = ResizePolicyType.FitToChildren,
                Padding = new Extents((ushort)ActionBarHorizontalPad, (ushort)ActionBarHorizontalPad, (ushort)ActionBarVerticalPad, (ushort)ActionBarVerticalPad),
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
            };
            chipsContainer.ParentOrigin = Tizen.NUI.ParentOrigin.Center;
            chipsContainer.PivotPoint = Tizen.NUI.PivotPoint.Center;
            _actionBar.Add(chipsContainer);

            int xCursor = 0;
            for (int i = 0; i < ActionCount; i++)
            {
                if (i > 0)
                {
                    View divider = new View
                    {
                        Size = new Size(ChipDividerWidth, ChipDividerHeight),
                        BackgroundColor = Tokens.Divider,
                        Position = new Position(xCursor + ActionChipGap, 0f),
                    };
                    divider.ParentOrigin = Tizen.NUI.ParentOrigin.CenterLeft;
                    divider.PivotPoint = Tizen.NUI.PivotPoint.CenterLeft;
                    chipsContainer.Add(divider);
                    xCursor += ActionChipGap * 2 + ChipDividerWidth;
                }

                View bg = new View
                {
                    BackgroundColor = new Color(0f, 0f, 0f, 0f),
                    Position = new Position(xCursor, 0f),
                    WidthResizePolicy = ResizePolicyType.FitToChildren,
                    HeightResizePolicy = ResizePolicyType.FitToChildren,
                };
                bg.ParentOrigin = Tizen.NUI.ParentOrigin.CenterLeft;
                bg.PivotPoint = Tizen.NUI.PivotPoint.CenterLeft;
                chipsContainer.Add(bg);

                TextLabel label = new TextLabel
                {
                    Text = ChipText((PlayerAction)i, _streamCount),
                    TextColor = Tokens.TextPrimary,
                    PointSize = Dimens.FontChip,
                    FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                    MultiLine = false,
                    Padding = new Extents((ushort)ActionChipHorizontalPad, (ushort)ActionChipHorizontalPad, (ushort)ActionChipVerticalPad, (ushort)ActionChipVerticalPad),
                    WidthResizePolicy = ResizePolicyType.FitToChildren,
                    HeightResizePolicy = ResizePolicyType.FitToChildren,
                };
                bg.Add(label);

                _chipBg[i] = bg;
                _chipLabel[i] = label;

                int approxChipWidth = ApproxChipWidth(label.Text);
                xCursor += approxChipWidth;
            }

            ApplyChipFocusVisuals();

            // ----- Auto-hide timer -----
            _autoHideTimer = new System.Timers.Timer(AutoHideMs);
            _autoHideTimer.AutoReset = false;
            _autoHideTimer.Elapsed += OnAutoHideElapsed;
        }

        public void SetMatch(CatalogMatch match)
        {
            _match = match;
            _livePillLabel.Text = LivePillTextFor(match);
            ApplyScoreCard(match);
        }

        public void SetStreamCount(int count)
        {
            _streamCount = count < 0 ? 0 : count;
            int idx = (int)PlayerAction.SwitchStream;
            if (_chipLabel != null && idx >= 0 && idx < _chipLabel.Length && _chipLabel[idx] != null)
            {
                _chipLabel[idx].Text = ChipText(PlayerAction.SwitchStream, _streamCount);
            }
        }

        public void ShowChrome()
        {
            if (ChromeVisible) return;
            ChromeVisible = true;

            _chromeGroup.Show();
            _chromeAnimation?.Stop();
            _chromeAnimation = new Animation(Dimens.AnimChromeFade);
            _chromeAnimation.AnimateTo(_chromeGroup, "Opacity", 1f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            _chromeAnimation.Play();

            ResetAutoHideTimer();
        }

        public void HideChrome()
        {
            if (!ChromeVisible) return;
            ChromeVisible = false;

            _autoHideTimer.Stop();

            _chromeAnimation?.Stop();
            _chromeAnimation = new Animation(Dimens.AnimChromeFade);
            _chromeAnimation.AnimateTo(_chromeGroup, "Opacity", 0f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseIn));
            _chromeAnimation.Play();
        }

        public void TouchActivity()
        {
            if (!ChromeVisible)
            {
                ShowChrome();
            }
            else
            {
                ResetAutoHideTimer();
            }
        }

        public void MoveActionLeft()
        {
            int next = (int)FocusedAction - 1;
            if (next < 0) next = 0;
            if (next == (int)FocusedAction) return;
            FocusedAction = (PlayerAction)next;
            ApplyChipFocusVisuals();
        }

        public void MoveActionRight()
        {
            int next = (int)FocusedAction + 1;
            if (next >= ActionCount) next = ActionCount - 1;
            if (next == (int)FocusedAction) return;
            FocusedAction = (PlayerAction)next;
            ApplyChipFocusVisuals();
        }

        public void Activate()
        {
            EventHandler<PlayerActionEventArgs> handler = ActionInvoked;
            if (handler != null)
            {
                handler(this, new PlayerActionEventArgs(FocusedAction));
            }
        }

        private void ResetAutoHideTimer()
        {
            _autoHideTimer.Stop();
            _autoHideTimer.Interval = AutoHideMs;
            _autoHideTimer.Start();
        }

        private void OnAutoHideElapsed(object sender, ElapsedEventArgs e)
        {
            if (_syncContext != null)
            {
                _syncContext.Post(_ => HideChrome(), null);
            }
            else
            {
                HideChrome();
            }
        }

        private void ApplyChipFocusVisuals()
        {
            for (int i = 0; i < ActionCount; i++)
            {
                bool isFocused = i == (int)FocusedAction;
                if (_chipBg[i] != null)
                {
                    _chipBg[i].BackgroundColor = isFocused ? Tokens.ChipActive : new Color(0f, 0f, 0f, 0f);
                }
                if (_chipLabel[i] != null)
                {
                    _chipLabel[i].TextColor = isFocused ? Tokens.TextOnLight : Tokens.TextPrimary;
                }
            }
        }

        private void ApplyScoreCard(CatalogMatch match)
        {
            if (match == null || match.Teams == null)
            {
                _scoreCard.Hide();
                return;
            }

            string homeName = match.Teams.Home != null ? (match.Teams.Home.Name ?? "") : "";
            string awayName = match.Teams.Away != null ? (match.Teams.Away.Name ?? "") : "";

            if (string.IsNullOrWhiteSpace(homeName) && string.IsNullOrWhiteSpace(awayName))
            {
                _scoreCard.Hide();
                return;
            }

            _scoreCard.Show();
            _scoreHomeInitials.Text = InitialsFor(homeName);
            _scoreAwayInitials.Text = InitialsFor(awayName);
            _scoreHomeValue.Text = "—";
            _scoreAwayValue.Text = "—";
        }

        private static string LivePillTextFor(CatalogMatch match)
        {
            if (match == null) return "● LIVE · No match";
            string title = string.IsNullOrWhiteSpace(match.Title) ? "Match" : match.Title;
            if (match.IsLive) return "● LIVE · " + title;
            return title;
        }

        private static string InitialsFor(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            string trimmed = name.Trim();
            int take = trimmed.Length < 3 ? trimmed.Length : 3;
            return trimmed.Substring(0, take).ToUpperInvariant();
        }

        private static string ChipText(PlayerAction action, int streamCount)
        {
            switch (action)
            {
                case PlayerAction.SwitchStream: return "⤢ Switch stream (" + streamCount + ")";
                case PlayerAction.Audio:        return "🔊 Audio";
                case PlayerAction.Quality:      return "⚙ Quality";
                case PlayerAction.Info:         return "ℹ Info";
                default:                        return "";
            }
        }

        private static int ApproxChipWidth(string text)
        {
            if (string.IsNullOrEmpty(text)) return ActionChipHorizontalPad * 2;
            // Rough monospaced estimate at ~9px per character at FontChip; good enough for layout cursor.
            return text.Length * 9 + ActionChipHorizontalPad * 2;
        }
    }
}
