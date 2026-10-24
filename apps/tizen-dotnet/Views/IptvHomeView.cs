using System;
using System.Collections.Generic;
using StreamedTv.Models;
using StreamedTv.Theme;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace StreamedTv.Views
{
    /// <summary>
    /// Inline Live-TV surface — replaces the hero + sport rows on the home
    /// screen when the "● Live" nav-pill chip is the active selection.
    /// Renders the iptv-org sports catalog as letter-bucketed rows (A, B,
    /// C, …, # for non-alpha) so navigation matches the rest of the app
    /// (D-pad Up/Down across rows, Left/Right within a row, OK plays).
    ///
    /// Why a self-contained view rather than a stack of stock RowViews:
    /// RowView is tightly coupled to CatalogMatch + TileView (poster
    /// loading, popular badges, kind enum). IPTV channels have no
    /// posters, no popular flag, and a different play handoff. Building
    /// a parallel view kept the existing focus model untouched and
    /// avoided a generic-content refactor of RowView for what is still
    /// labeled an internal experiment.
    ///
    /// Why a self-managed focus model rather than extending FocusManager:
    /// FocusManager has a fixed 5-zone enum (Nav / Hero / 3 sport rows).
    /// Adding N variable letter-rows would force a much larger refactor.
    /// This view owns its own (rowIdx, colIdx) state and only talks to
    /// the host via two events: ChannelActivated on OK, FocusEscapeUp
    /// when D-pad-Up walks off the top so the host can hand focus back
    /// to the nav pill. Symmetric to how IptvChannelsOverlayView already
    /// works — same pattern, just inline instead of modal.
    ///
    /// Slated for retirement only when the iptv-org experiment ends.
    /// </summary>
    public sealed class IptvHomeView : View
    {
        // --- Layout (1920×1080 reference) ---
        // Tile size matches the regular sport rows so the visual rhythm
        // of the home screen reads as "same kind of row, different
        // content".
        private const int TileWidth = 460;
        private const int TileHeight = 168;            // shorter than poster tiles — text-only content
        private const int TileGap = 28;
        private const int RowGap = 22;
        private const int RowLabelMarginBottom = 8;
        // Total height of a letter-row including label + tile strip + gap.
        // Used by scroll-into-view math.
        private const int RowStride = 28 /*label height*/ + RowLabelMarginBottom + TileHeight + RowGap;

        // Anchor for horizontal scroll within a row — focused tile slides
        // toward this X position so it sits a comfortable distance from
        // the left edge (matches RowView's anchorPx = 350).
        private const int HorizontalAnchorPx = 350;

        // ---- Hierarchy ----
        // _scrollClip ─ ClippingMode.ClipChildren so vertical translation
        //   doesn't bleed into the nav pill above us.
        //   └─ _rowsContainer (vertical LinearLayout)
        //        └─ N × _LetterRow (label + horizontal scroll-clip)
        private readonly View _scrollClip;
        private readonly View _rowsContainer;
        private readonly TextLabel _statusBanner;

        private readonly List<_LetterRow> _rows = new List<_LetterRow>();
        // Flat (row, col) → channel lookup for OK activation.
        private readonly List<List<IptvChannel>> _channelsByRow = new List<List<IptvChannel>>();

        // Focus state. -1 = no focus (e.g. before first SetChannels, or
        // after host calls ClearFocus).
        private int _focusRow = -1;
        private int _focusCol = -1;

        private Animation _vScrollAnim;

        // Vertical viewport when active. _root is itself a scrolling
        // LinearLayout but we deliberately give the surface a fixed
        // height so the internal scroll-into-view (vScrollAnim against
        // _rowsContainer.PositionY) operates inside a known clip rather
        // than dragging _root past the topbar. 880 ≈ 1080 ref height
        // minus the topbar + small breathing room.
        private const int ActiveViewportHeight = 880;

        public bool IsActive { get; private set; }

        /// <summary>Raised when the user OKs a tile. Host plays the
        /// channel via PlayerWebView.</summary>
        public event EventHandler<IptvChannel> ChannelActivated;

        /// <summary>Raised when the user presses Up on the top row,
        /// signaling that focus should walk off this view back into the
        /// nav pill above. Symmetric: the host re-focuses the Live chip
        /// and we ClearFocus().</summary>
        public event EventHandler FocusEscapeUp;

        public IptvHomeView()
        {
            // Fill the home surface slot vacated by hero + 3 sport rows
            // when active. Resting state is collapsed (height 0) so we
            // don't push the layout when not in use — host calls
            // SetActive(true) which expands us to MatchParent.
            WidthSpecification = LayoutParamPolicies.MatchParent;
            HeightSpecification = 0;
            BackgroundColor = new Color(0f, 0f, 0f, 0f);

            // Root vertical layout: [optional status banner] → scroll clip.
            Layout = new LinearLayout
            {
                LinearOrientation = LinearLayout.Orientation.Vertical,
                LinearAlignment = LinearLayout.Alignment.Top,
                CellPadding = new Size2D(0, 12),
                Padding = new Extents(0, 0, 8, 0),
            };

            _statusBanner = new TextLabel
            {
                Text = "Loading channels…",
                TextColor = new Color(1f, 0.85f, 0.3f, 1f),
                PointSize = 14f,
                FontFamily = Tokens.FontFamily,
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            Add(_statusBanner);

            // Clip wraps the entire row stack so vertical scroll doesn't
            // visibly bleed past the surface bounds. Same trick as the
            // grid overlay used for its tile area.
            _scrollClip = new View
            {
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightResizePolicy = ResizePolicyType.FillToParent,
                ClippingMode = ClippingModeType.ClipChildren,
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
            };
            Add(_scrollClip);

            _rowsContainer = new View
            {
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                // Top-left anchor so PositionY translations move the
                // stack as one unit (same anchoring rationale as the
                // overlay grid).
                ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft,
                PivotPoint = Tizen.NUI.PivotPoint.TopLeft,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    LinearAlignment = LinearLayout.Alignment.Top,
                    CellPadding = new Size2D(0, RowGap),
                },
            };
            _scrollClip.Add(_rowsContainer);
        }

        // ---- Host API ------------------------------------------------------

        /// <summary>Toggle the surface in/out. true = expand to fill the
        /// home slot + take focus; false = collapse to height 0 + drop
        /// focus visuals so the regular hero+rows reflow in.</summary>
        public void SetActive(bool active)
        {
            if (IsActive == active) return;
            IsActive = active;
            if (active)
            {
                HeightSpecification = ActiveViewportHeight;
                Show();
                // Snap to top on every activation so the user always
                // starts from row 0. Cheap reset; same approach as the
                // overlay's Open() handler.
                try { _vScrollAnim?.Stop(); } catch { }
                if (_rowsContainer != null) _rowsContainer.PositionY = 0f;
                _focusRow = _rows.Count > 0 ? 0 : -1;
                _focusCol = _focusRow >= 0 ? 0 : -1;
                ApplyFocusVisual();
            }
            else
            {
                ClearFocus();
                HeightSpecification = 0;
                Hide();
            }
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

        /// <summary>Replace the row stack with letter buckets built from
        /// the given channel list. Safe to call before/after activation;
        /// rebuilds in place. Resets focus to (0,0).</summary>
        public void SetChannels(IReadOnlyList<IptvChannel> channels)
        {
            ClearChildren(_rowsContainer);
            _rows.Clear();
            _channelsByRow.Clear();
            _focusRow = -1;
            _focusCol = -1;

            if (channels == null || channels.Count == 0)
            {
                SetStatus("No live channels available right now.");
                return;
            }
            HideStatus();

            // Bucket by uppercase first letter; non-alpha → "#".
            // SortedDictionary keeps the row order alphabetical with
            // "#" placed last (manually re-inserted after iteration so
            // it sorts after Z).
            var buckets = new SortedDictionary<string, List<IptvChannel>>(StringComparer.Ordinal);
            List<IptvChannel> hashBucket = null;
            foreach (IptvChannel ch in channels)
            {
                if (ch == null) continue;
                string key = BucketKeyFor(ch.Name);
                if (key == "#")
                {
                    if (hashBucket == null) hashBucket = new List<IptvChannel>();
                    hashBucket.Add(ch);
                    continue;
                }
                if (!buckets.TryGetValue(key, out List<IptvChannel> list))
                {
                    list = new List<IptvChannel>();
                    buckets[key] = list;
                }
                list.Add(ch);
            }

            foreach (var kv in buckets)
            {
                AddRow(kv.Key, kv.Value);
            }
            if (hashBucket != null && hashBucket.Count > 0)
            {
                AddRow("#", hashBucket);
            }

            if (_rows.Count > 0)
            {
                _focusRow = 0;
                _focusCol = 0;
                if (IsActive) ApplyFocusVisual();
            }

            // Kick a staggered logo preload for the rows that fit in
            // the initial viewport. Without this the user sees blank
            // logo boxes until they navigate into a row — bad UX even
            // though tiles are functional. ApplyFocusVisual already
            // covers the focused row; this seeds the OTHER visible
            // rows so logos appear top-to-bottom rather than only on
            // demand.
            for (int r = 0; r < _rows.Count && r < VisibleRowsForPreload; r++)
            {
                _rows[r].PreloadVisible(VisibleTilesForPreload);
            }
        }

        public void ClearFocus()
        {
            // Hide focus visuals on every row (each tile reverts to its
            // un-focused style; the row's scroll snaps back to col 0
            // via SetFocusedTile's reset logic). DO NOT wipe _focusRow
            // / _focusCol — when the host re-routes focus into us via
            // EnterIptvHomeFocus + RefreshFocusVisual we resume on the
            // last row the user was on (column reset to 0 there too,
            // by the same row-reset logic).
            //
            // Earlier this method DID set _focusRow=-1 / _focusCol=-1,
            // which broke the Up-escape→Down-re-enter cycle: re-entry
            // hit SetActive(true) early-return (IsActive was still true
            // because the surface stayed visible), focus state was -1,
            // every key path bailed at the "if (_focusRow < 0) return"
            // guard, and the user was stuck with no selector. Preserving
            // state here is the fix.
            for (int i = 0; i < _rows.Count; i++)
            {
                _rows[i].SetFocusedTile(-1);
            }
        }

        /// <summary>Re-apply the focus visual based on the surface's
        /// current (_focusRow, _focusCol). Used when focus returns to
        /// the surface from the nav pill — SetActive(true) is a no-op
        /// in that case (IsActive was never flipped) so the visual
        /// won't refresh on its own. If state was wiped or never set,
        /// snaps to (0,0).</summary>
        public void RefreshFocusVisual()
        {
            if (_rows.Count == 0) return;
            if (_focusRow < 0 || _focusRow >= _rows.Count)
            {
                _focusRow = 0;
                _focusCol = _rows[0].TileCount > 0 ? 0 : -1;
            }
            else if (_focusCol < 0 && _rows[_focusRow].TileCount > 0)
            {
                _focusCol = 0;
            }
            ApplyFocusVisual();
        }

        /// <summary>Route a remote-control key. Mirrors the IptvOverlay
        /// + RowView key contract — Up/Down across rows, Left/Right
        /// within a row, OK activates, Back/Escape escapes upward
        /// (host pops focus back to nav pill).</summary>
        public void HandleKey(string key)
        {
            if (!IsActive || _rows.Count == 0) return;
            switch (key)
            {
                case "Left":     MoveCol(-1); break;
                case "Right":    MoveCol(+1); break;
                case "Up":       MoveRow(-1); break;
                case "Down":     MoveRow(+1); break;
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
                    // Same as walking off the top — host returns focus
                    // to the nav pill. Live chip stays selected so the
                    // surface stays up; user can press Back twice or
                    // pick a different chip to leave.
                    FocusEscapeUp?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        // ---- Internals ----------------------------------------------------

        private void MoveCol(int delta)
        {
            if (_focusRow < 0 || _focusRow >= _rows.Count) return;
            _LetterRow row = _rows[_focusRow];
            int next = _focusCol + delta;
            if (next < 0) next = 0;
            if (next >= row.TileCount) next = row.TileCount - 1;
            if (next == _focusCol) return;
            _focusCol = next;
            ApplyFocusVisual();
        }

        private void MoveRow(int delta)
        {
            if (_focusRow < 0) return;
            int next = _focusRow + delta;
            if (next < 0)
            {
                // Walked off the top — host pops focus to nav pill.
                FocusEscapeUp?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (next >= _rows.Count) next = _rows.Count - 1;
            if (next == _focusRow) return;
            _focusRow = next;
            // Always land on the first tile of the new row. The user
            // explicitly asked for this — inheriting the previous row's
            // column was disorienting (you'd land mid-row in some
            // arbitrary tile depending on where you were before). The
            // SetFocusedTile(-1) call on the OLD row (inside
            // ApplyFocusVisual) animates that row's scroll back to the
            // start so it visually resets too — a clean per-row state.
            _focusCol = _rows[_focusRow].TileCount > 0 ? 0 : -1;
            ApplyFocusVisual();
        }

        private void Activate()
        {
            if (_focusRow < 0 || _focusRow >= _channelsByRow.Count) return;
            List<IptvChannel> rowChans = _channelsByRow[_focusRow];
            if (rowChans == null || _focusCol < 0 || _focusCol >= rowChans.Count) return;
            ChannelActivated?.Invoke(this, rowChans[_focusCol]);
        }

        private void ApplyFocusVisual()
        {
            for (int r = 0; r < _rows.Count; r++)
            {
                _rows[r].SetFocusedTile(r == _focusRow ? _focusCol : -1);
            }
            // Lazy-load logos for the focused row — both the focused
            // tile + a look-ahead, plus a row-wide preload so the
            // whole row's logos are warming up while the user scrolls
            // horizontally within it. EnsureLogoLoaded is idempotent
            // so re-calls on already-loaded tiles are no-ops.
            if (_focusRow >= 0 && _focusRow < _rows.Count)
            {
                _LetterRow row = _rows[_focusRow];
                if (_focusCol >= 0) row.EnsureLogoLoaded(_focusCol);
                if (_focusCol + 1 < row.TileCount) row.EnsureLogoLoaded(_focusCol + 1);
                row.PreloadVisible(VisibleTilesForPreload);
            }
            ScrollFocusedRowIntoView();
        }

        // Tunables for the lazy-load pacing. Match RowView's defaults
        // — same Tizen-CPU constraints (each ResourceUrl assignment
        // kicks an HTTP fetch + texture upload; bunching too many
        // synchronously stalls input).
        private const int VisibleRowsForPreload = 4;
        private const int VisibleTilesForPreload = 5;

        // Vertical translate so the focused row sits inside the visible
        // area of _scrollClip. Identical pattern to the overlay's
        // ScrollFocusedRowIntoView, just on a per-row stride here vs
        // a per-grid-row stride.
        private void ScrollFocusedRowIntoView()
        {
            if (_focusRow < 0 || _rows.Count == 0 || _scrollClip == null || _rowsContainer == null) return;
            try
            {
                float clipH = _scrollClip.SizeHeight;
                if (clipH <= 0f) return;
                int rowsVisible = Math.Max(1, (int)(clipH / RowStride));
                int totalRows = _rows.Count;
                int maxTopRow = Math.Max(0, totalRows - rowsVisible);

                int currentTopRow = (int)Math.Round(-_rowsContainer.PositionY / RowStride);
                int newTopRow = currentTopRow;
                if (_focusRow < currentTopRow) newTopRow = _focusRow;
                else if (_focusRow >= currentTopRow + rowsVisible) newTopRow = _focusRow - rowsVisible + 1;
                if (newTopRow < 0) newTopRow = 0;
                if (newTopRow > maxTopRow) newTopRow = maxTopRow;
                if (newTopRow == currentTopRow) return;

                float targetY = -newTopRow * RowStride;
                try { _vScrollAnim?.Stop(); } catch { }
                _vScrollAnim = new Animation(180);
                _vScrollAnim.AnimateTo(_rowsContainer, "PositionY", targetY,
                    new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
                _vScrollAnim.Play();
            }
            catch { /* scroll math is best-effort — never break focus */ }
        }

        private void AddRow(string label, List<IptvChannel> channels)
        {
            _LetterRow row = new _LetterRow(label, channels, TileWidth, TileHeight, TileGap, RowLabelMarginBottom);
            _rows.Add(row);
            _channelsByRow.Add(channels);
            _rowsContainer.Add(row);
        }

        // First letter of name, uppercase; "#" if name is missing or
        // starts with a non-alpha. Numbers and symbols all bucket to "#"
        // (per the design — keeps the alphabetic flow clean instead of
        // intermixing 0–9 in their own three rows).
        private static string BucketKeyFor(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "#";
            char c = char.ToUpperInvariant(name.Trim()[0]);
            if (c < 'A' || c > 'Z') return "#";
            return c.ToString();
        }

        private static void ClearChildren(View parent)
        {
            if (parent == null) return;
            uint count = parent.ChildCount;
            for (int i = (int)count - 1; i >= 0; i--)
            {
                View child = parent.GetChildAt((uint)i);
                if (child != null) parent.Remove(child);
            }
        }

        // ---- Letter row ---------------------------------------------------
        // Internal type — owns the per-row label, horizontal scroll, and
        // tile focus visuals. Public surface is just SetFocusedTile +
        // TileCount + a constructor.
        private sealed class _LetterRow : View
        {
            private readonly View _scrollClip;
            private readonly View _tilesContainer;
            private readonly List<View> _tiles = new List<View>();
            private readonly List<TextLabel> _names = new List<TextLabel>();
            private readonly List<TextLabel> _metas = new List<TextLabel>();
            // Parallel arrays per tile for lazy logo loading. _logoViews
            // entries may be null when the channel has no logo URL —
            // the index alignment with _tiles[] still matters because
            // EnsureLogoLoaded is keyed by tile index.
            private readonly List<ImageView> _logoViews = new List<ImageView>();
            private readonly List<string> _logoUrls = new List<string>();
            private readonly List<bool> _logoLoaded = new List<bool>();
            private readonly int _tileStride;
            private readonly int _tileWidth;
            private readonly int _tileHeight;
            private int _focusedCol = -1;
            private Animation _hScrollAnim;

            public int TileCount { get { return _tiles.Count; } }

            public _LetterRow(string label, List<IptvChannel> channels,
                              int tileWidth, int tileHeight, int tileGap, int labelGap)
            {
                _tileWidth = tileWidth;
                _tileHeight = tileHeight;
                _tileStride = tileWidth + tileGap;

                WidthSpecification = LayoutParamPolicies.MatchParent;
                HeightSpecification = LayoutParamPolicies.WrapContent;
                BackgroundColor = new Color(0f, 0f, 0f, 0f);
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    LinearAlignment = LinearLayout.Alignment.Begin,
                    CellPadding = new Size2D(0, labelGap),
                    Padding = new Extents((ushort)Dimens.RowLabelMarginLeft, 0, 0, 0),
                };

                TextLabel labelView = new TextLabel
                {
                    Text = label,
                    TextColor = Tokens.TextPrimary,
                    PointSize = Dimens.FontRowLabel,
                    FontFamily = Tokens.FontFamily,
                    FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                    MultiLine = false,
                    WidthSpecification = LayoutParamPolicies.WrapContent,
                    HeightSpecification = LayoutParamPolicies.WrapContent,
                };
                Add(labelView);

                // Don't clip horizontally — same rationale as RowView:
                // letting tiles overflow past the row's local bounds
                // means the window edge naturally crops them, instead
                // of slicing at the page padding column.
                _scrollClip = new View
                {
                    WidthSpecification = LayoutParamPolicies.MatchParent,
                    HeightSpecification = tileHeight,
                    BackgroundColor = new Color(0f, 0f, 0f, 0f),
                };
                Add(_scrollClip);

                _tilesContainer = new View
                {
                    WidthSpecification = LayoutParamPolicies.WrapContent,
                    HeightSpecification = LayoutParamPolicies.MatchParent,
                    BackgroundColor = new Color(0f, 0f, 0f, 0f),
                    Layout = new LinearLayout
                    {
                        LinearOrientation = LinearLayout.Orientation.Horizontal,
                        LinearAlignment = LinearLayout.Alignment.CenterVertical,
                        CellPadding = new Size2D(tileGap, 0),
                    },
                };
                _tilesContainer.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
                _tilesContainer.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
                _scrollClip.Add(_tilesContainer);

                if (channels != null)
                {
                    for (int i = 0; i < channels.Count; i++)
                    {
                        IptvChannel ch = channels[i];
                        if (ch == null) continue;
                        View tile = BuildTile(ch);
                        _tilesContainer.Add(tile);
                        _tiles.Add(tile);
                    }
                }
            }

            public void SetFocusedTile(int index)
            {
                int prev = _focusedCol;
                if (index < -1) index = -1;
                if (index >= _tiles.Count) index = _tiles.Count - 1;
                _focusedCol = index;

                for (int i = 0; i < _tiles.Count; i++)
                {
                    StyleTile(i, i == _focusedCol);
                }

                if (_focusedCol >= 0)
                {
                    HorizontalScrollIntoView(_focusedCol);
                }
                else if (prev >= 0)
                {
                    // Lost focus — snap scroll back to the start so
                    // when the user looks at this row again it shows
                    // its leftmost tile (per user request: every row
                    // resets to col 0 when not focused). Earlier we
                    // left scroll position alone "to resume where the
                    // user was" — that turned out to feel disorienting
                    // because rows would mid-scroll independently of
                    // each other.
                    SnapToStart();
                }
            }

            // Animate the tile strip back to PositionX = 0. Same easing
            // as the focus-into-view scroll so leaving + returning to a
            // row reads as one motion language.
            private void SnapToStart()
            {
                try { _hScrollAnim?.Stop(); } catch { }
                if (_tilesContainer.PositionX == 0f) return;
                _hScrollAnim = new Animation(Dimens.AnimRowScroll);
                _hScrollAnim.DefaultAlphaFunction = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOutSquare);
                _hScrollAnim.AnimateTo(_tilesContainer, "PositionX", 0f);
                _hScrollAnim.Play();
            }

            // ---- Lazy logo loading -----------------------------------
            //
            // Tile logos use the same pattern TileView uses for sport
            // posters: ResourceUrl is assigned LATE via the
            // SynchronizationContext so the HTTP fetch is scheduled
            // off the input handler. Without this, building 30+ tiles
            // per row × 15+ rows = hundreds of synchronous HTTP fetches
            // at SetChannels time, which on a Tizen TV CPU stalls the
            // arrow-key handler for seconds.

            /// <summary>Kick the logo fetch for tile <c>idx</c> if it
            /// hasn't started yet. Idempotent — safe to call from
            /// focus paths AND batch preload paths.</summary>
            public void EnsureLogoLoaded(int idx)
            {
                if (idx < 0 || idx >= _logoUrls.Count) return;
                if (_logoLoaded[idx]) return;
                if (string.IsNullOrEmpty(_logoUrls[idx])) return;
                if (_logoViews[idx] == null) return;
                _logoLoaded[idx] = true;

                var ctx = System.Threading.SynchronizationContext.Current;
                if (ctx != null) ctx.Post(_ => ApplyLogo(idx), null);
                else ApplyLogo(idx);
            }

            private void ApplyLogo(int idx)
            {
                try
                {
                    if (idx < 0 || idx >= _logoViews.Count) return;
                    ImageView v = _logoViews[idx];
                    if (v == null) return;
                    string u = _logoUrls[idx];
                    if (string.IsNullOrEmpty(u)) return;
                    v.ResourceUrl = u;
                }
                catch (Exception ex) { StreamedTv.Services.Diagnostics.LogError("IptvHome.ApplyLogo", ex); }
            }

            /// <summary>Preload the first <c>max</c> tiles' logos with
            /// a small stagger between fetches so the network /
            /// decoder pipeline isn't stampeded. Already-loaded
            /// tiles are skipped without delay (so re-calls cost
            /// nothing once the row is fully warmed up).</summary>
            public async void PreloadVisible(int max)
            {
                try
                {
                    int budget = Math.Min(max, _logoUrls.Count);
                    for (int i = 0; i < budget; i++)
                    {
                        if (i < _logoLoaded.Count && _logoLoaded[i]) continue;
                        EnsureLogoLoaded(i);
                        await System.Threading.Tasks.Task.Delay(180);
                    }
                }
                catch { }
            }

            private void HorizontalScrollIntoView(int col)
            {
                float desiredX = HorizontalAnchorPx - col * _tileStride;
                float clampedX = Math.Min(0f, desiredX);
                int rowWidth = (int)Dimens.ReferenceWidth - 2 * Dimens.PageHorizontalPadding - Dimens.RowLabelMarginLeft;
                int totalContentWidth = (_tiles.Count - 1) * _tileStride + _tileWidth;
                if (totalContentWidth > rowWidth)
                {
                    float minX = -(totalContentWidth - rowWidth);
                    if (clampedX < minX) clampedX = minX;
                }
                else
                {
                    clampedX = 0f;
                }
                try { _hScrollAnim?.Stop(); } catch { }
                _hScrollAnim = new Animation(Dimens.AnimRowScroll);
                _hScrollAnim.DefaultAlphaFunction = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOutSquare);
                _hScrollAnim.AnimateTo(_tilesContainer, "PositionX", clampedX);
                _hScrollAnim.Play();
            }

            private View BuildTile(IptvChannel ch)
            {
                View tile = new View
                {
                    WidthSpecification = _tileWidth,
                    HeightSpecification = _tileHeight,
                    BackgroundColor = new Color(1f, 1f, 1f, 0.06f),
                    BorderlineColor = new Color(1f, 1f, 1f, 0.18f),
                    BorderlineWidth = 1f,
                    BorderlineOffset = -1f,
                    CornerRadius = new Vector4(Dimens.TileCornerRadius, Dimens.TileCornerRadius, Dimens.TileCornerRadius, Dimens.TileCornerRadius),
                    CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                    Padding = new Extents(16, 20, 14, 14),
                    // Horizontal: [logo box] [text column]
                    Layout = new LinearLayout
                    {
                        LinearOrientation = LinearLayout.Orientation.Horizontal,
                        LinearAlignment = LinearLayout.Alignment.CenterVertical,
                        CellPadding = new Size2D(14, 0),
                    },
                };

                // Left: logo. We always reserve the box (even when no
                // logo) so the text column starts at a consistent X
                // across the row — letter rows look like a clean grid
                // rather than a ragged left edge.
                View logoBox = new View
                {
                    WidthSpecification = LogoBoxWidth,
                    HeightSpecification = LogoBoxHeight,
                    BackgroundColor = new Color(0f, 0f, 0f, 0f),
                    Layout = new LinearLayout
                    {
                        LinearOrientation = LinearLayout.Orientation.Horizontal,
                        LinearAlignment = LinearLayout.Alignment.Center,
                    },
                };
                if (!string.IsNullOrEmpty(ch.Logo))
                {
                    ImageView logo = new ImageView
                    {
                        // ResourceUrl deliberately empty here — the
                        // actual fetch is kicked by EnsureLogoLoaded
                        // (deferred via SynchronizationContext) so we
                        // don't fire all N channels' logo HTTP fetches
                        // at once on SetChannels. Mirrors the lazy
                        // pattern TileView uses for sport posters.
                        ResourceUrl = "",
                        WidthSpecification = LogoBoxWidth,
                        HeightSpecification = LogoBoxHeight,
                        // FitMode.ShrinkToFit preserves aspect ratio AND
                        // centers within the box — important because
                        // logos vary wildly (some 4:1 horizontal, some
                        // square) and a stretched logo looks awful at
                        // TV viewing distance.
                    };
                    try { logo.FittingMode = FittingModeType.ShrinkToFit; } catch { }
                    logoBox.Add(logo);
                    _logoViews.Add(logo);
                }
                else
                {
                    // Track null so the parallel arrays stay aligned
                    // with _tiles[] — EnsureLogoLoaded keys by tile
                    // index, so a missing entry would shift everything.
                    _logoViews.Add(null);
                }
                _logoUrls.Add(ch.Logo ?? "");
                _logoLoaded.Add(false);
                tile.Add(logoBox);

                // Right: name + meta, stacked vertically.
                View textCol = new View
                {
                    WidthSpecification = LayoutParamPolicies.WrapContent,
                    HeightSpecification = LayoutParamPolicies.WrapContent,
                    BackgroundColor = new Color(0f, 0f, 0f, 0f),
                    Layout = new LinearLayout
                    {
                        LinearOrientation = LinearLayout.Orientation.Vertical,
                        LinearAlignment = LinearLayout.Alignment.Top,
                        CellPadding = new Size2D(0, 4),
                    },
                };
                TextLabel name = new TextLabel
                {
                    Text = ch.Name ?? "(unnamed)",
                    TextColor = Tokens.TextPrimary,
                    PointSize = 17f,
                    FontFamily = Tokens.FontFamily,
                    FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                    MultiLine = false,
                    Ellipsis = true,
                    // Cap text width so a long name doesn't push the
                    // tile past _tileWidth (LinearLayout would happily
                    // expand horizontally otherwise).
                    WidthSpecification = _tileWidth - LogoBoxWidth - 50,
                    HeightSpecification = LayoutParamPolicies.WrapContent,
                };
                textCol.Add(name);
                _names.Add(name);

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
                    PointSize = 13f,
                    FontFamily = Tokens.FontFamily,
                    MultiLine = false,
                    Ellipsis = true,
                    WidthSpecification = _tileWidth - LogoBoxWidth - 50,
                    HeightSpecification = LayoutParamPolicies.WrapContent,
                };
                textCol.Add(meta);
                _metas.Add(meta);
                tile.Add(textCol);
                return tile;
            }

            // Logo box dimensions. ~3:2 aspect — wide enough for a
            // horizontal channel mark, tall enough that square marks
            // (rare in iptv-org's catalog but they exist) still read.
            private const int LogoBoxWidth = 110;
            private const int LogoBoxHeight = 70;

            // White-bg highlight on focus; transparent when unfocused.
            // Same scheme as IptvChannelsOverlayView so both render
            // consistently while we're between the two surfaces.
            private void StyleTile(int i, bool focused)
            {
                if (i < 0 || i >= _tiles.Count) return;
                View t = _tiles[i];
                TextLabel name = i < _names.Count ? _names[i] : null;
                TextLabel meta = i < _metas.Count ? _metas[i] : null;
                if (focused)
                {
                    t.BackgroundColor = Color.White;
                    t.BorderlineColor = Color.White;
                    t.BorderlineWidth = 2f;
                    if (name != null) name.TextColor = Tokens.TextOnLight;
                    if (meta != null) meta.TextColor = new Color(0.3f, 0.3f, 0.3f, 1f);
                }
                else
                {
                    t.BackgroundColor = new Color(1f, 1f, 1f, 0.06f);
                    t.BorderlineColor = new Color(1f, 1f, 1f, 0.18f);
                    t.BorderlineWidth = 1f;
                    if (name != null) name.TextColor = Tokens.TextPrimary;
                    if (meta != null) meta.TextColor = Tokens.TextSecondary;
                }
            }
        }
    }
}
