using System;
using System.Threading;

namespace StreamedTv.Services
{
    public enum FocusZone
    {
        Nav = 0,
        Hero = 1,
        LiveRow = 2,
        ComingUpRow = 3,
        RecentRow = 4
    }

    public sealed class FocusChangedEventArgs : EventArgs
    {
        public FocusZone Zone { get; }
        public int Index { get; }

        public FocusChangedEventArgs(FocusZone zone, int index)
        {
            Zone = zone;
            Index = index;
        }
    }

    public sealed class TilePromotedEventArgs : EventArgs
    {
        public FocusZone Zone { get; }
        public int Index { get; }

        public TilePromotedEventArgs(FocusZone zone, int index)
        {
            Zone = zone;
            Index = index;
        }
    }

    public sealed class FocusManager
    {
        private const int DwellMilliseconds = 2500;
        private const int ZoneCount = 5;

        private readonly object _lock;
        private readonly int[] _zoneCounts;
        private readonly int[] _zoneIndices;
        private readonly SynchronizationContext _syncContext;

        // Tizen.NUI.Timer (NOT System.Threading.Timer) — see
        // StartDwellTimerLocked for why threading matters here.
        private Tizen.NUI.Timer _dwellTimer;
        private long _generation;

        public FocusZone CurrentZone { get; private set; }
        public int CurrentIndex { get; private set; }

        public event EventHandler<FocusChangedEventArgs> FocusChanged;
        public event EventHandler<TilePromotedEventArgs> TilePromoted;

        public FocusManager()
        {
            _lock = new object();
            _zoneCounts = new int[ZoneCount];
            _zoneIndices = new int[ZoneCount];
            _syncContext = SynchronizationContext.Current;

            CurrentZone = FocusZone.LiveRow;
            CurrentIndex = 0;
        }

        public void SetZoneCounts(int navCount, int liveCount, int comingUpCount, int recentCount)
        {
            lock (_lock)
            {
                _zoneCounts[(int)FocusZone.Nav] = Math.Max(0, navCount);
                _zoneCounts[(int)FocusZone.Hero] = 1;
                _zoneCounts[(int)FocusZone.LiveRow] = Math.Max(0, liveCount);
                _zoneCounts[(int)FocusZone.ComingUpRow] = Math.Max(0, comingUpCount);
                _zoneCounts[(int)FocusZone.RecentRow] = Math.Max(0, recentCount);

                for (int i = 0; i < ZoneCount; i++)
                {
                    int count = _zoneCounts[i];
                    if (count <= 0)
                    {
                        _zoneIndices[i] = 0;
                    }
                    else if (_zoneIndices[i] >= count)
                    {
                        _zoneIndices[i] = count - 1;
                    }
                }

                int currentZoneIdx = (int)CurrentZone;
                int currentCount = _zoneCounts[currentZoneIdx];
                if (currentCount <= 0)
                {
                    CurrentIndex = 0;
                }
                else if (CurrentIndex >= currentCount)
                {
                    CurrentIndex = currentCount - 1;
                    _zoneIndices[currentZoneIdx] = CurrentIndex;
                }
            }
        }

        public bool MoveUp()
        {
            lock (_lock)
            {
                // Walk upward past any zones that are currently empty so
                // navigation never strands the user on a hidden row. Hero
                // is always considered traversable (count is artificially 1
                // in SetZoneCounts) so we always pass through it.
                int newZoneIdx = (int)CurrentZone - 1;
                while (newZoneIdx >= 0 && _zoneCounts[newZoneIdx] <= 0)
                {
                    newZoneIdx--;
                }
                if (newZoneIdx < 0) return false;

                ChangeZone((FocusZone)newZoneIdx);
                return true;
            }
        }

        public bool MoveDown()
        {
            lock (_lock)
            {
                // Mirror of MoveUp — skip over zero-count zones (hidden rows).
                int newZoneIdx = (int)CurrentZone + 1;
                while (newZoneIdx < ZoneCount && _zoneCounts[newZoneIdx] <= 0)
                {
                    newZoneIdx++;
                }
                if (newZoneIdx >= ZoneCount) return false;

                ChangeZone((FocusZone)newZoneIdx);
                return true;
            }
        }

        public bool MoveLeft()
        {
            lock (_lock)
            {
                int zoneIdx = (int)CurrentZone;
                int count = _zoneCounts[zoneIdx];
                if (count <= 0 || CurrentIndex <= 0)
                {
                    return false;
                }

                CurrentIndex--;
                _zoneIndices[zoneIdx] = CurrentIndex;
                OnFocusMoved(CurrentZone, CurrentIndex, fireImmediatePromotion: false);
                return true;
            }
        }

        public bool MoveRight()
        {
            lock (_lock)
            {
                int zoneIdx = (int)CurrentZone;
                int count = _zoneCounts[zoneIdx];
                if (count <= 0 || CurrentIndex >= count - 1)
                {
                    return false;
                }

                CurrentIndex++;
                _zoneIndices[zoneIdx] = CurrentIndex;
                OnFocusMoved(CurrentZone, CurrentIndex, fireImmediatePromotion: false);
                return true;
            }
        }

        public void SetFocus(FocusZone zone, int index)
        {
            lock (_lock)
            {
                int zoneIdx = (int)zone;
                if (zoneIdx < 0 || zoneIdx >= ZoneCount)
                {
                    return;
                }

                int count = _zoneCounts[zoneIdx];
                int clampedIndex;
                if (count <= 0)
                {
                    clampedIndex = 0;
                }
                else if (index < 0)
                {
                    clampedIndex = 0;
                }
                else if (index >= count)
                {
                    clampedIndex = count - 1;
                }
                else
                {
                    clampedIndex = index;
                }

                if (zone == CurrentZone && clampedIndex == CurrentIndex)
                {
                    return;
                }

                CurrentZone = zone;
                CurrentIndex = clampedIndex;
                _zoneIndices[zoneIdx] = clampedIndex;
                OnFocusMoved(zone, clampedIndex, fireImmediatePromotion: true);
            }
        }

        public void CancelDwell()
        {
            lock (_lock)
            {
                CancelDwellTimerLocked();
            }
        }

        private void ChangeZone(FocusZone newZone)
        {
            int newZoneIdx = (int)newZone;
            int restoredIndex = _zoneIndices[newZoneIdx];
            int count = _zoneCounts[newZoneIdx];
            if (count <= 0)
            {
                restoredIndex = 0;
            }
            else if (restoredIndex >= count)
            {
                restoredIndex = count - 1;
                _zoneIndices[newZoneIdx] = restoredIndex;
            }

            CurrentZone = newZone;
            CurrentIndex = restoredIndex;
            OnFocusMoved(newZone, restoredIndex, fireImmediatePromotion: false);
        }

        private void OnFocusMoved(FocusZone zone, int index, bool fireImmediatePromotion)
        {
            CancelDwellTimerLocked();
            long generation = ++_generation;

            RaiseFocusChanged(zone, index);

            bool isTileZone = zone == FocusZone.LiveRow || zone == FocusZone.ComingUpRow || zone == FocusZone.RecentRow;
            int count = _zoneCounts[(int)zone];
            if (!isTileZone || count <= 0)
            {
                return;
            }

            if (fireImmediatePromotion)
            {
                RaiseTilePromoted(zone, index);
                return;
            }

            StartDwellTimerLocked(generation, zone, index);
        }

        private void StartDwellTimerLocked(long generation, FocusZone zone, int index)
        {
            // IMPORTANT: must be Tizen.NUI.Timer, NOT System.Threading.Timer.
            // The dwell callback ultimately raises TilePromoted, whose
            // subscribers (HeroView.SetMatch, etc.) construct NUI
            // Animations. DALi's animation/view APIs are single-
            // threaded — calling them from a thread-pool worker is the
            // same wedge we hit on the profile/auth path: native
            // assertion fires inside animation-wrap.cpp, surfacing
            // as `System.ApplicationException FATAL: Exception e is null`
            // up through Animation..ctor. NUI Timer ticks on the main
            // loop, so the handler chain (and any view mutations they
            // do) all run on the right thread.
            //
            // Why we keep the explicit ?.Stop()+Dispose() before
            // re-creating: NUI Timer instances hold native handles;
            // letting the GC collect them lazily can leave stale
            // ticks in flight that race with new dwell starts.
            try { _dwellTimer?.Stop(); } catch { }
            try { _dwellTimer?.Dispose(); } catch { }
            _dwellTimer = new Tizen.NUI.Timer((uint)DwellMilliseconds);
            _dwellTimer.Tick += (s, e) =>
            {
                try { OnDwellElapsed(generation, zone, index); }
                catch (Exception ex) { Diagnostics.LogError("FocusManager.DwellTimer.Tick", ex); }
                return false; // one-shot
            };
            _dwellTimer.Start();
        }

        private void CancelDwellTimerLocked()
        {
            if (_dwellTimer != null)
            {
                try { _dwellTimer.Stop(); } catch { }
                try { _dwellTimer.Dispose(); } catch { }
                _dwellTimer = null;
            }
        }

        private void OnDwellElapsed(long generation, FocusZone zone, int index)
        {
            lock (_lock)
            {
                if (generation != _generation)
                {
                    return;
                }

                if (zone != CurrentZone || index != CurrentIndex)
                {
                    return;
                }

                int count = _zoneCounts[(int)zone];
                if (count <= 0 || index >= count)
                {
                    return;
                }

                CancelDwellTimerLocked();
            }

            RaiseTilePromoted(zone, index);
        }

        private void RaiseFocusChanged(FocusZone zone, int index)
        {
            EventHandler<FocusChangedEventArgs> handler = FocusChanged;
            if (handler == null)
            {
                return;
            }

            FocusChangedEventArgs args = new FocusChangedEventArgs(zone, index);
            Dispatch(() => handler(this, args));
        }

        private void RaiseTilePromoted(FocusZone zone, int index)
        {
            EventHandler<TilePromotedEventArgs> handler = TilePromoted;
            if (handler == null)
            {
                return;
            }

            TilePromotedEventArgs args = new TilePromotedEventArgs(zone, index);
            Dispatch(() => handler(this, args));
        }

        private void Dispatch(Action action)
        {
            if (action == null)
            {
                return;
            }

            if (_syncContext != null)
            {
                _syncContext.Post(_ => action(), null);
            }
            else
            {
                action();
            }
        }
    }
}
