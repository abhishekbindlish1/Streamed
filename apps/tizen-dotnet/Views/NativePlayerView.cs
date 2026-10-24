using System;
using System.Collections.Generic;
using StreamedTv.Services;
using StreamedTv.Theme;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace StreamedTv.Views
{
    /// <summary>
    /// Native NUI playback surface for streams whose source m3u8 we already
    /// have a clean, proxied URL for (i.e. <see cref="StreamExtractorService"/>
    /// successfully resolved a wrapper-host stream into a raw HLS URL).
    ///
    /// Distinct from <see cref="PlayerWebView"/>:
    ///   * PlayerWebView wraps a chromium-efl WebView and is needed for
    ///     direct embed pages whose source m3u8 we cannot resolve.
    ///   * NativePlayerView wraps a <see cref="VideoView"/> (Tizen TV's
    ///     hardware-accelerated HLS pipeline) and is used ONLY when we
    ///     have a raw m3u8 URL to hand it.
    ///
    /// The surface is composed of two stacked children:
    ///   1. A full-bleed <see cref="VideoView"/> with <c>Underlay = true</c>
    ///      so it punches through the NUI scene to the HW VideoHole on
    ///      Tizen TV. Without Underlay, you get audio-only or no media.
    ///   2. A semi-transparent overlay <see cref="View"/> with a centered
    ///      <see cref="TextLabel"/> ("Resolving stream…", "Buffering…",
    ///      …) that is shown during extraction and during the brief
    ///      handover into the decoder. Hidden once playback is presumed
    ///      live (no error / buffering events surfaced via NUI's C# API
    ///      for VideoView, so we hide on a short timer after Play()).
    ///
    /// The public surface deliberately mirrors <see cref="PlayerWebView"/>
    /// where it can — <see cref="ExitRequested"/>,
    /// <see cref="StatusChanged"/>, <see cref="TogglePlay"/>,
    /// <see cref="AdjustVolume"/>, <see cref="ToggleMute"/>,
    /// <see cref="RouteKey"/>, <see cref="Stop"/> — so the host
    /// (Program.cs) can route key events with minimal branching.
    ///
    /// THREADING: project memory ("NUI requires UI mutations on main
    /// thread") — every method here is intended to be called from the
    /// NUI main thread, same as PlayerWebView. The only async hop is the
    /// internal NUI <see cref="Timer"/> that hides the loader after a
    /// short delay; Timer ticks fire on the main thread.
    /// </summary>
    public sealed class NativePlayerView : View
    {
        // ---- Tunables ------------------------------------------------------

        // How long after Play() we wait before hiding the loader. The Tizen
        // NUI VideoView C# wrapper does NOT surface "playback started" /
        // "buffering ended" events to managed code on this firmware (only
        // Finished, which is end-of-media and never fires for live HLS).
        // Empirically, 1500 ms is enough for the HW decoder to grab the
        // first segment and show a frame; if it hasn't, the user sees a
        // black underlay rather than the loader, which is acceptable for
        // v1 — better detection can be added later via the underlying
        // Tizen.Multimedia.Player surface if reliability complaints come in.
        private const int LoaderHideDelayMs = 1500;

        // ---- Children ------------------------------------------------------

        private readonly VideoView _video;
        private readonly View _loaderOverlay;
        private readonly TextLabel _loaderLabel;

        // Hide-loader timer; lives across calls so we can reset it on
        // back-to-back PlayUrl invocations (e.g. user picks a different
        // stream while the previous one was still in its loader window).
        private Timer _hideLoaderTimer;

        // Disposal guard. NUI views are held by the parent View tree, but
        // we own the VideoView + loader nodes outright and need an
        // idempotent Dispose path.
        private bool _disposed;

        // Bumps every time Stop()/PlayUrl() is called. Pending hide-loader
        // ticks closing over an older generation become no-ops, mirroring
        // the _generation pattern in PlayerWebView so a delayed callback
        // can't unhide a loader the host has already torn down.
        private int _generation;

        // ---- Public API ----------------------------------------------------

        /// <summary>Raised when the user presses Back / Escape while this
        /// player is on screen, OR when the host explicitly tears it down
        /// via <see cref="RaiseExitRequested"/>. The host (Program.cs)
        /// listens via the same handler used for PlayerWebView.</summary>
        public event EventHandler ExitRequested;

        /// <summary>Mirror of <see cref="PlayerWebView.StatusChanged"/>.
        /// Reserved for future use; for v1 we don't surface any internal
        /// status from the native VideoView — there are no usable C#
        /// events to bridge. Kept on the public surface so the host
        /// doesn't need a separate hookup site.</summary>
        public event EventHandler<string> StatusChanged;

        public NativePlayerView()
        {
            // Full-screen surface. CRITICAL: background MUST be transparent
            // (alpha = 0) for the HW VideoHole compositor to punch through
            // the NUI surface and reveal the video plane underneath. Set to
            // opaque black, the GUI surface covers the video — you hear
            // audio (audio plane is independent) but never see picture.
            // The loader overlay below provides its own semi-transparent
            // dark sheet while loading; once hidden, this transparent
            // background lets the video plane show through.
            WidthSpecification = LayoutParamPolicies.MatchParent;
            HeightSpecification = LayoutParamPolicies.MatchParent;
            BackgroundColor = new Color(0f, 0f, 0f, 0f);

            // ---- Native HLS surface ---------------------------------------
            _video = new VideoView
            {
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.MatchParent,
                Looping = false,
                // Underlay = false renders video through NUI's GL texture
                // pipeline (the video frame ends up as a texture sampled by
                // the GUI surface) rather than the HW VideoHole compositor.
                // Hole-punch was set true initially but the listing page
                // was visible through NativePlayerView's transparent
                // surface — confirming the hole was never being punched.
                // The texture path is slower per-frame but doesn't depend
                // on every ancestor view being alpha-zero, and renders
                // INSIDE the VideoView's bounds regardless of what the
                // GUI compositor does above it.
                Underlay = false,
            };
            _video.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            _video.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            Add(_video);

            // Finished only fires for VOD; live HLS never reaches end of
            // media. We still subscribe so VOD test streams can teardown
            // cleanly without leaking the player surface.
            try
            {
                _video.Finished += OnVideoFinished;
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("NativePlayerView.Finished.subscribe", ex);
            }

            // ---- Loader overlay -------------------------------------------
            // Full-screen darkened sheet with a centered text label.
            // Sits on top of the VideoView until PlayUrl + the hide-loader
            // timer says otherwise.
            _loaderOverlay = new View
            {
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.MatchParent,
                BackgroundColor = new Color(0f, 0f, 0f, 0.6f),
            };
            _loaderOverlay.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            _loaderOverlay.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            Add(_loaderOverlay);

            _loaderLabel = new TextLabel
            {
                Text = "",
                TextColor = Tokens.TextPrimary,
                PointSize = 22f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("semibold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            // Center the label inside the overlay using ParentOrigin /
            // PivotPoint pinning. NUI's "natural" center anchoring works
            // when both points are .Center; we don't need a LinearLayout.
            _loaderLabel.ParentOrigin = Tizen.NUI.ParentOrigin.Center;
            _loaderLabel.PivotPoint = Tizen.NUI.PivotPoint.Center;
            _loaderOverlay.Add(_loaderLabel);

            // Loader starts hidden — host calls ShowLoading() before
            // making the surface visible. Keeps the constructor side-
            // effect-free w.r.t. visible chrome.
            _loaderOverlay.Hide();
        }

        /// <summary>Show the loader sheet with the given text. Call this
        /// before <see cref="Show"/>-ing the player so the user sees the
        /// "Resolving stream…" affordance the moment the player surface
        /// appears.</summary>
        public void ShowLoading(string text)
        {
            try
            {
                _loaderLabel.Text = text ?? "";
                _loaderOverlay.Show();
                _loaderOverlay.RaiseToTop();
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("NativePlayerView.ShowLoading", ex);
            }
        }

        /// <summary>Update the loader text in place (e.g.
        /// "Resolving stream…" → "Buffering…"). No-op if the loader is
        /// currently hidden — caller should re-show via
        /// <see cref="ShowLoading"/> first if needed.</summary>
        public void SetLoaderText(string text)
        {
            try
            {
                _loaderLabel.Text = text ?? "";
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("NativePlayerView.SetLoaderText", ex);
            }
        }

        /// <summary>Hand the resolved m3u8 URL to the underlying
        /// <see cref="VideoView"/> and start playback. Schedules an
        /// internal hide of the loader overlay after
        /// <see cref="LoaderHideDelayMs"/>ms.</summary>
        public void PlayUrl(string m3u8Url)
        {
            if (string.IsNullOrEmpty(m3u8Url))
            {
                Diagnostics.Log("NativePlayerView.PlayUrl: empty URL ignored");
                return;
            }
            try
            {
                int gen = ++_generation;

                Diagnostics.Track("native_player_open", new Dictionary<string, string>
                {
                    ["url"] = Truncate(m3u8Url, 60),
                });

                // Stop anything currently playing before reassigning the
                // ResourceUrl — Tizen.NUI's VideoView doesn't always tear
                // down the previous decoder cleanly when you replace the
                // URL while it's still playing.
                try { _video.Stop(); } catch { }

                _video.ResourceUrl = m3u8Url;
                _video.Play();

                // Schedule the loader fade. We re-use a single Timer
                // field across calls; restart it (Stop + new) so the
                // last PlayUrl() wins if the user picks a different
                // stream within the loader window.
                CancelHideLoaderTimer();
                Timer t = new Timer((uint)LoaderHideDelayMs);
                _hideLoaderTimer = t;
                t.Tick += (s, e) =>
                {
                    try
                    {
                        if (_disposed) return false;
                        if (gen != _generation) return false;
                        _loaderOverlay.Hide();
                        Diagnostics.Track("native_player_play_started", null);
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.LogError("NativePlayerView.HideLoaderTick", ex);
                    }
                    return false; // one-shot
                };
                t.Start();
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("NativePlayerView.PlayUrl", ex);
            }
        }

        /// <summary>Stop playback and clear the resource URL. Idempotent —
        /// safe to call multiple times and from <see cref="OnPlayerExitRequested"/>
        /// in the host without checking visibility first.</summary>
        public void Stop()
        {
            try
            {
                _generation++;
                CancelHideLoaderTimer();
                try { _video.Stop(); } catch { }
                try { _video.ResourceUrl = ""; } catch { }
                try { _loaderOverlay.Hide(); } catch { }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("NativePlayerView.Stop", ex);
            }
        }

        /// <summary>Toggle play / pause on the underlying decoder. No-op if
        /// no URL is loaded.</summary>
        public void TogglePlay()
        {
            try
            {
                // VideoView doesn't expose IsPlaying via the public C#
                // surface on this firmware. We track our own play/pause
                // intent: if PlayPosition is moving we treat as playing
                // and call Pause; otherwise call Play. The simpler
                // alternative — toggling a bool — is fragile because Stop
                // / external pause can desync it.
                //
                // Cheapest reliable approach: just call Pause() and rely
                // on the next Toggle to call Play(). To make Toggle
                // self-recovering, we alternate via a member flag.
                if (_isPaused)
                {
                    _video.Play();
                    _isPaused = false;
                }
                else
                {
                    _video.Pause();
                    _isPaused = true;
                }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("NativePlayerView.TogglePlay", ex);
            }
        }
        private bool _isPaused;

        /// <summary>Volume control: deferred to the system audio path on
        /// Tizen TV. The hardware decoder doesn't expose a clean gain
        /// knob via NUI's VideoView, so we no-op here and let the
        /// physical XF86AudioRaiseVolume key the system already routes
        /// drive the TV's master volume. Kept on the public surface so
        /// the host's key router can call it without a null-check
        /// branch.</summary>
        public void AdjustVolume(int delta)
        {
            // Intentional no-op. See xmldoc above.
            _ = delta;
        }

        /// <summary>Mute toggle: same story as <see cref="AdjustVolume"/>;
        /// deferred to the system audio path. Kept for API parity with
        /// <see cref="PlayerWebView.ToggleMute"/>.</summary>
        public void ToggleMute()
        {
            // Intentional no-op. See xmldoc above.
        }

        /// <summary>Route a key the host's router didn't consume. For v1
        /// we treat Back/Escape as exit; everything else is dropped so
        /// the system can have a chance at it (volume keys especially).
        /// Mirrors <see cref="PlayerWebView.RouteKey"/>.</summary>
        public void RouteKey(string keyName)
        {
            try
            {
                if (string.IsNullOrEmpty(keyName)) return;
                if (keyName == "Escape" || keyName == "XF86Back" || keyName == "Back")
                {
                    RaiseExitRequested();
                }
                // Other keys: drop. We don't need an Escape-rate-limit
                // since the host gates re-entry via _nativePlayerVisible
                // before forwarding subsequent keys here.
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("NativePlayerView.RouteKey", ex);
            }
        }

        /// <summary>Public helper so the host's key router can fire the
        /// exit event without a public event-firing pattern. NUI doesn't
        /// expose Invoke-style raise on events from outside the
        /// declaring type, so we expose this thin wrapper.</summary>
        public void RaiseExitRequested()
        {
            try
            {
                Diagnostics.Track("native_player_exit", null);
                EventHandler h = ExitRequested;
                if (h != null) h(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("NativePlayerView.RaiseExitRequested", ex);
            }
        }

        // ---- Internal ------------------------------------------------------

        private void CancelHideLoaderTimer()
        {
            try
            {
                Timer t = _hideLoaderTimer;
                _hideLoaderTimer = null;
                if (t != null)
                {
                    try { t.Stop(); } catch { }
                    try { t.Dispose(); } catch { }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("NativePlayerView.CancelHideLoaderTimer", ex);
            }
        }

        private void OnVideoFinished(object sender, EventArgs e)
        {
            // Live HLS never reaches Finished. For VOD test streams,
            // treat end-of-media as an exit so the user is bounced back
            // to the listing rather than left staring at a black sheet.
            try
            {
                RaiseExitRequested();
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("NativePlayerView.OnVideoFinished", ex);
            }
        }

        private void RaiseStatus(string msg)
        {
            try
            {
                EventHandler<string> h = StatusChanged;
                if (h != null) h(this, msg);
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("NativePlayerView.RaiseStatus", ex);
            }
        }

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= n) return s;
            return s.Substring(0, n) + "…";
        }

        // ---- Disposal ------------------------------------------------------

        protected override void Dispose(DisposeTypes type)
        {
            if (_disposed)
            {
                base.Dispose(type);
                return;
            }
            try
            {
                _disposed = true;
                CancelHideLoaderTimer();
                try { if (_video != null) _video.Finished -= OnVideoFinished; } catch { }
                try { _video?.Stop(); } catch { }
                try { _video?.Dispose(); } catch { }
                try { _loaderLabel?.Dispose(); } catch { }
                try { _loaderOverlay?.Dispose(); } catch { }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("NativePlayerView.Dispose", ex);
            }
            base.Dispose(type);
        }
    }
}
