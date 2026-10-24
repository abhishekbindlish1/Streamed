using System;
using System.Collections.Generic;
using StreamedTv.Theme;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace StreamedTv.Views
{
    /// <summary>
    /// Netflix-style on-screen keyboard for TV.
    ///
    /// Visual layout (matches Netflix's "Name this profile" reference):
    ///   1. Title    (e.g. "Profile name")
    ///   2. Live text display with caret + underline
    ///   3. 5-row QWERTY grid:
    ///      Row 0:  1 2 3 4 5 6 7 8 9 0       (10 keys)
    ///      Row 1:  Q W E R T Y U I O P        (10 keys)
    ///      Row 2:  A S D F G H J K L -        (10 keys)
    ///      Row 3:  ↓ Z X C V B N M '          (9 keys; ↓ = Shift)
    ///      Row 4:  Shift | Space (wide) | ⌫   (3 wider keys)
    ///   4. Cancel + Done pills (two separate buttons below the grid)
    ///
    /// Focus model: 6 logical rows (4 letter rows + action row + button row).
    /// Up/Down between rows uses proportional column mapping based on each
    /// key's center-X — so cursoring up from "Cancel" lands on the middle
    /// of the alphabet, not the leftmost letter.
    ///
    /// All keys are positioned absolutely inside their host so we can record
    /// each key's CenterX for the proportional Up/Down mapping (a single
    /// LinearLayout per row would force all rows to the same width).
    /// </summary>
    public sealed class OnScreenKeyboardView : View
    {
        // ---- Public configuration ----
        public string InitialText { get; set; } = "";
        public string Title { get; set; } = "";
        public int MaxLength { get; set; } = 24;

        public bool IsOpen { get; private set; }

        public event EventHandler<string> Submitted;
        public event EventHandler Cancelled;

        // ---- Layout constants (1920×1080 reference) ----
        private const int KeyW = 60;
        private const int KeyH = 70;
        private const int KeyGap = 6;
        private const int RowGap = 8;
        private const int PanelHorizontalPad = 32;
        private const int PanelVerticalPad = 28;

        // Action row key widths.
        private const int ShiftKeyW = 96;
        private const int SpaceKeyW = 360;
        private const int DelKeyW   = 96;

        // Button row pill widths.
        private const int ButtonPillW = 220;
        private const int ButtonPillH = 64;
        private const int ButtonGap = 24;

        // Typography.
        private const float FontSizeLetter = 22f;
        private const float FontSizeAction = 14f;
        private const float FontSizeButton = 18f;

        // Letter rows (QWERTY layout, matches Netflix reference).
        private static readonly string[] Row0 = { "1","2","3","4","5","6","7","8","9","0" };
        private static readonly string[] Row1 = { "Q","W","E","R","T","Y","U","I","O","P" };
        private static readonly string[] Row2 = { "A","S","D","F","G","H","J","K","L","-" };
        private static readonly string[] Row3 = { "Z","X","C","V","B","N","M",".",",","'" };

        // ---- Key model ----
        private enum KeyKind { Letter, Number, Punct, Space, Shift, Delete, Cancel, Done }

        private sealed class Key
        {
            public string Upper;
            public string Lower;
            public KeyKind Kind;
            public View Tile;
            public TextLabel Label;
            public int CenterX;
        }

        // 6 logical rows for the focus model.
        private readonly List<Key> _row0 = new List<Key>();
        private readonly List<Key> _row1 = new List<Key>();
        private readonly List<Key> _row2 = new List<Key>();
        private readonly List<Key> _row3 = new List<Key>();
        private readonly List<Key> _actionRow = new List<Key>();
        private readonly List<Key> _buttonRow = new List<Key>();
        private readonly List<List<Key>> _allRows;

        // ---- Layout pieces ----
        private readonly View _panel;
        private readonly TextLabel _titleLabel;
        private readonly TextLabel _textDisplay;

        // ---- State ----
        private string _text = "";
        private bool _shifted = true;
        private int _focusRow;       // index into _allRows
        private int _focusCol;
        private Animation _modalOpacityAnim;

        public OnScreenKeyboardView()
        {
            _allRows = new List<List<Key>> { _row0, _row1, _row2, _row3, _actionRow, _buttonRow };

            // Opaque dark backdrop so the editor/wizard underneath doesn't
            // bleed through and wash out the keyboard.
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.FillToParent;
            BackgroundColor = Tokens.BgBase;
            Layout = new LinearLayout
            {
                LinearOrientation = LinearLayout.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            int gridWidth = 10 * KeyW + 9 * KeyGap;   // letter rows are widest by design
            int actionRowW = ShiftKeyW + KeyGap + SpaceKeyW + KeyGap + DelKeyW;
            int buttonRowW = ButtonPillW * 2 + ButtonGap;

            _panel = new View
            {
                BackgroundColor = new Color(0.10f, 0.10f, 0.12f, 1f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.10f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(20f, 20f, 20f, 20f),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                WidthSpecification = gridWidth + PanelHorizontalPad * 2,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Padding = new Extents(PanelHorizontalPad, PanelHorizontalPad, PanelVerticalPad, PanelVerticalPad),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Center,
                    CellPadding = new Size2D(0, RowGap),
                },
            };
            Add(_panel);

            _titleLabel = new TextLabel
            {
                Text = "",
                TextColor = Tokens.TextPrimary,
                PointSize = 28f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Margin = new Extents(0, 0, 0, 12),
            };
            _panel.Add(_titleLabel);

            _textDisplay = new TextLabel
            {
                Text = "",
                TextColor = Tokens.TextPrimary,
                PointSize = 30f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                WidthSpecification = gridWidth,
                HeightSpecification = 60,
            };
            _panel.Add(_textDisplay);

            View underline = new View
            {
                BackgroundColor = new Color(1f, 1f, 1f, 0.20f),
                WidthSpecification = gridWidth,
                HeightSpecification = 2,
                Margin = new Extents(0, 0, 0, RowGap),
            };
            _panel.Add(underline);

            BuildLetterRow(_row0, Row0, KeyKind.Number, gridWidth);
            BuildLetterRow(_row1, Row1, KeyKind.Letter, gridWidth);
            BuildLetterRow(_row2, Row2, KeyKind.Letter, gridWidth, treatHyphenAsPunct: true);
            BuildLetterRow(_row3, Row3, KeyKind.Letter, gridWidth, treatPunctAsPunct: true);
            BuildActionRow(actionRowW);
            BuildButtonRow(buttonRowW);

            Opacity = 0f;
            Hide();
        }

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;

            _text = InitialText ?? "";
            _shifted = string.IsNullOrEmpty(_text);
            _titleLabel.Text = Title ?? "";
            _focusRow = 1;   // start on Q-row
            _focusCol = 0;
            RefreshTextDisplay();
            RefreshKeyLabels();
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
                    Cancelled?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        // ---- Internal: row builders ----------------------------------------

        private void BuildLetterRow(List<Key> targetList, string[] glyphs, KeyKind defaultKind, int rowWidth, bool treatHyphenAsPunct = false, bool treatPunctAsPunct = false)
        {
            // Each letter row is its own host so we can record CenterX per
            // key inside that host's coordinate space.
            View host = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = rowWidth,
                HeightSpecification = KeyH,
            };
            _panel.Add(host);

            int leftPad = (rowWidth - (glyphs.Length * KeyW + (glyphs.Length - 1) * KeyGap)) / 2;
            int x = leftPad;
            for (int i = 0; i < glyphs.Length; i++)
            {
                string g = glyphs[i];
                KeyKind kind = defaultKind;
                if ((treatHyphenAsPunct && g == "-")
                    || (treatPunctAsPunct && (g == "." || g == "," || g == "'")))
                {
                    kind = KeyKind.Punct;
                }

                Key k = new Key
                {
                    Upper = g,
                    Lower = (kind == KeyKind.Letter) ? g.ToLowerInvariant() : g,
                    Kind = kind,
                    CenterX = x + KeyW / 2,
                };
                BuildKeyTile(k, KeyW, k.Upper);
                k.Tile.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
                k.Tile.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
                k.Tile.Position = new Position(x, 0);
                host.Add(k.Tile);
                targetList.Add(k);
                x += KeyW + KeyGap;
            }
        }

        private void BuildActionRow(int rowWidth)
        {
            int gridWidth = 10 * KeyW + 9 * KeyGap;
            View host = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = gridWidth,
                HeightSpecification = KeyH,
            };
            _panel.Add(host);

            int leftPad = (gridWidth - rowWidth) / 2;
            int x = leftPad;

            Key shift = new Key { Upper = "abc", Lower = "ABC", Kind = KeyKind.Shift };
            BuildKeyTile(shift, ShiftKeyW, "ABC");
            shift.CenterX = x + ShiftKeyW / 2;
            shift.Tile.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            shift.Tile.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            shift.Tile.Position = new Position(x, 0);
            host.Add(shift.Tile);
            _actionRow.Add(shift);
            x += ShiftKeyW + KeyGap;

            Key space = new Key { Upper = "space", Lower = "space", Kind = KeyKind.Space };
            BuildKeyTile(space, SpaceKeyW, "space");
            space.CenterX = x + SpaceKeyW / 2;
            space.Tile.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            space.Tile.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            space.Tile.Position = new Position(x, 0);
            host.Add(space.Tile);
            _actionRow.Add(space);
            x += SpaceKeyW + KeyGap;

            Key del = new Key { Upper = "⌫", Lower = "⌫", Kind = KeyKind.Delete };
            BuildKeyTile(del, DelKeyW, "⌫");
            del.CenterX = x + DelKeyW / 2;
            del.Tile.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            del.Tile.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            del.Tile.Position = new Position(x, 0);
            host.Add(del.Tile);
            _actionRow.Add(del);
        }

        private void BuildButtonRow(int rowWidth)
        {
            int gridWidth = 10 * KeyW + 9 * KeyGap;
            // Button row sits below the keyboard with extra breathing room.
            View host = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                WidthSpecification = gridWidth,
                HeightSpecification = ButtonPillH,
                Margin = new Extents(0, 0, 16, 0),
            };
            _panel.Add(host);

            int leftPad = (gridWidth - rowWidth) / 2;
            int x = leftPad;

            Key cancel = new Key { Upper = "Cancel", Lower = "Cancel", Kind = KeyKind.Cancel };
            BuildButtonTile(cancel, ButtonPillW, "Cancel");
            cancel.CenterX = x + ButtonPillW / 2;
            cancel.Tile.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            cancel.Tile.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            cancel.Tile.Position = new Position(x, 0);
            host.Add(cancel.Tile);
            _buttonRow.Add(cancel);
            x += ButtonPillW + ButtonGap;

            Key done = new Key { Upper = "Done", Lower = "Done", Kind = KeyKind.Done };
            BuildButtonTile(done, ButtonPillW, "Done");
            done.CenterX = x + ButtonPillW / 2;
            done.Tile.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            done.Tile.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            done.Tile.Position = new Position(x, 0);
            host.Add(done.Tile);
            _buttonRow.Add(done);
        }

        private static void BuildKeyTile(Key k, int width, string initialLabel)
        {
            k.Tile = new View
            {
                BackgroundColor = new Color(1f, 1f, 1f, 0.08f),
                BorderlineColor = new Color(0f, 0f, 0f, 0f),
                BorderlineWidth = 0f,
                CornerRadius = new Vector4(8f, 8f, 8f, 8f),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                Size = new Size(width, KeyH),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Center,
                },
            };
            k.Label = new TextLabel
            {
                Text = initialLabel,
                TextColor = Tokens.TextPrimary,
                PointSize = (k.Kind == KeyKind.Letter || k.Kind == KeyKind.Number || k.Kind == KeyKind.Punct) ? FontSizeLetter : FontSizeAction,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            k.Tile.Add(k.Label);
        }

        private static void BuildButtonTile(Key k, int width, string initialLabel)
        {
            k.Tile = new View
            {
                BackgroundColor = new Color(1f, 1f, 1f, 0.08f),
                BorderlineColor = new Color(1f, 1f, 1f, 0.18f),
                BorderlineWidth = 1f,
                BorderlineOffset = -1f,
                CornerRadius = new Vector4(ButtonPillH / 2f, ButtonPillH / 2f, ButtonPillH / 2f, ButtonPillH / 2f),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                Size = new Size(width, ButtonPillH),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    LinearAlignment = LinearLayout.Alignment.Center,
                },
            };
            k.Label = new TextLabel
            {
                Text = initialLabel,
                TextColor = Tokens.TextPrimary,
                PointSize = FontSizeButton,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            k.Tile.Add(k.Label);
        }

        // ---- Internal: focus + activation ----------------------------------

        private void MoveFocus(int dr, int dc)
        {
            if (dc != 0)
            {
                List<Key> row = _allRows[_focusRow];
                int next = _focusCol + dc;
                if (next < 0 || next >= row.Count) return;
                _focusCol = next;
                ApplyFocusVisual();
                return;
            }
            if (dr != 0)
            {
                int newRow = _focusRow + dr;
                if (newRow < 0 || newRow >= _allRows.Count) return;
                int currentX = _allRows[_focusRow][_focusCol].CenterX;
                int bestIdx = NearestKeyByCenterX(_allRows[newRow], currentX);
                _focusRow = newRow;
                _focusCol = bestIdx;
                ApplyFocusVisual();
            }
        }

        private static int NearestKeyByCenterX(List<Key> row, int x)
        {
            int bestIdx = 0;
            int bestDist = int.MaxValue;
            for (int i = 0; i < row.Count; i++)
            {
                int d = Math.Abs(row[i].CenterX - x);
                if (d < bestDist) { bestDist = d; bestIdx = i; }
            }
            return bestIdx;
        }

        private void ActivateFocused()
        {
            Key k = _allRows[_focusRow][_focusCol];
            switch (k.Kind)
            {
                case KeyKind.Letter:
                    if (_text.Length >= MaxLength) return;
                    _text += _shifted ? k.Upper : k.Lower;
                    if (_shifted && _text.Length > 0) { _shifted = false; RefreshKeyLabels(); }
                    RefreshTextDisplay();
                    break;
                case KeyKind.Number:
                case KeyKind.Punct:
                    if (_text.Length >= MaxLength) return;
                    _text += k.Upper;
                    RefreshTextDisplay();
                    break;
                case KeyKind.Space:
                    if (_text.Length >= MaxLength) return;
                    _text += " ";
                    RefreshTextDisplay();
                    break;
                case KeyKind.Delete:
                    if (_text.Length == 0) return;
                    _text = _text.Substring(0, _text.Length - 1);
                    RefreshTextDisplay();
                    break;
                case KeyKind.Shift:
                    _shifted = !_shifted;
                    RefreshKeyLabels();
                    break;
                case KeyKind.Cancel:
                    Cancelled?.Invoke(this, EventArgs.Empty);
                    break;
                case KeyKind.Done:
                    Submitted?.Invoke(this, _text);
                    break;
            }
        }

        private void RefreshTextDisplay()
        {
            _textDisplay.Text = _text + "▌";
        }

        private void RefreshKeyLabels()
        {
            foreach (List<Key> row in _allRows)
            {
                foreach (Key k in row)
                {
                    if (k.Label == null) continue;
                    if (k.Kind == KeyKind.Letter)
                    {
                        k.Label.Text = _shifted ? k.Upper : k.Lower;
                    }
                    else if (k.Kind == KeyKind.Shift)
                    {
                        // Shift caption shows what mode pressing it would switch TO.
                        k.Label.Text = _shifted ? "abc" : "ABC";
                    }
                }
            }
        }

        private void ApplyFocusVisual()
        {
            for (int r = 0; r < _allRows.Count; r++)
            {
                List<Key> row = _allRows[r];
                int focusedIdx = (r == _focusRow) ? _focusCol : -1;
                for (int i = 0; i < row.Count; i++)
                {
                    Key k = row[i];
                    if (k.Tile == null) continue;
                    bool focused = i == focusedIdx;
                    bool isButton = (k.Kind == KeyKind.Cancel || k.Kind == KeyKind.Done);
                    if (focused)
                    {
                        k.Tile.BackgroundColor = Color.White;
                        k.Tile.BorderlineColor = Color.White;
                        k.Tile.BorderlineWidth = isButton ? 2f : 0f;
                        if (k.Label != null) k.Label.TextColor = Tokens.TextOnLight;
                    }
                    else
                    {
                        k.Tile.BackgroundColor = new Color(1f, 1f, 1f, 0.08f);
                        k.Tile.BorderlineColor = isButton ? new Color(1f, 1f, 1f, 0.18f) : new Color(0f, 0f, 0f, 0f);
                        k.Tile.BorderlineWidth = isButton ? 1f : 0f;
                        if (k.Label != null) k.Label.TextColor = Tokens.TextPrimary;
                    }
                }
            }
        }
    }
}
