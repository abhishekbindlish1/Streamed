using System;
using StreamedTv.Models;
using StreamedTv.Services;
using StreamedTv.Theme;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace StreamedTv.Views
{
    /// <summary>
    /// Full-screen player surface backed by a <see cref="Tizen.NUI.BaseComponents.WebView"/>.
    /// The streamed.pk catalog returns iframe embed URLs (not direct .m3u8 / .mp4), so the
    /// embed page's own JS player handles playback — this view just hosts it.
    /// </summary>
    /// <remarks>
    /// Tizen 9 NUI conventions used here:
    /// - All layout via <see cref="LinearLayout"/> with WidthSpecification / HeightSpecification
    ///   (absolute child Position values are not honored on Tizen 9 device firmware).
    /// - <see cref="View.CornerRadius"/> uses <see cref="Vector4"/> in API9.
    /// - ParentOrigin / PivotPoint set OUTSIDE the object initializer with fully qualified names.
    ///
    /// Black-screen / no-video debugging (audio plays, pixels don't):
    /// On Samsung Tizen TV firmware the chromium-efl WebView composites hardware-decoded
    /// video via the "video hole" path — the WebView surface punches a transparent hole
    /// and the HW video plane is presented underneath. For that to be visible we need:
    ///   1. <see cref="WebView.VideoHoleEnabled"/> = true (the official API).
    ///   2. Both the wrapping View's <see cref="View.BackgroundColor"/> and the page's
    ///      <see cref="WebView.ContentBackgroundColor"/> to be fully transparent so they
    ///      don't paint over the hole.
    ///   3. Various engine "extra feature" flags (chromium-efl-internal, undocumented per
    ///      API9, names probed defensively — unknowns are silent no-ops).
    ///   4. CSS that promotes the embed's &lt;video&gt; to its own compositor layer
    ///      (translateZ(0)) and stretches it across the viewport so the HW overlay's
    ///      destination rect matches the visible region.
    /// See also: TizenFX/src/Tizen.NUI/src/public/WebView/WebView.cs (line ~921, VideoHoleEnabled;
    /// line ~972, ContentBackgroundColor; line ~1860, CheckVideoPlayingAsynchronously).
    /// </remarks>
    public sealed class PlayerWebView : View
    {
        private const int HintAutoHideMs = 3000;
        private const int HintFadeMs = 200;
        private const int StatusAutoHideMs = 5000;

        // Master switch for our autoplay JS injection.
        //
        // History: many revisions of complex JS (DOM reparenting, ad-overlay
        // hiding, fullscreen forcing, polling) all made the player worse.
        // Setting this to false (don't inject ANY JS) was also wrong because
        // most embeds sit on a "tap to play" overlay forever without a user
        // gesture to dismiss it.
        //
        // Current approach: inject the absolute minimum — one synthetic click
        // on the body + one v.play() call on whatever video element exists.
        // No DOM mutation, no fullscreen request, no styling, no polling.
        // Just enough to get past the gesture gate.
        private const bool ENABLE_AUTOPLAY = true;

        // We poll-inject the autoplay/CSS JS for this many ticks AFTER PageLoadFinished — embeds
        // sometimes append the <video> element late (after their own bootstrap fetch) so a single
        // shot at PageLoadFinished can miss it.
        private const int AutoplayPollIntervalMs = 1000;
        private const int AutoplayPollMaxTicks = 6;          // 0..5s
        private const int VideoPlaybackProbeMs = 1500;       // how often to ask the WebView "is video playing?"
        private const int VideoPlaybackProbeMaxTicks = 12;   // give up probing after ~18s

        private readonly WebView _webView;
        private readonly View _hintBg;
        private readonly TextLabel _hintLabel;
        private readonly View _statusBg;
        private readonly TextLabel _statusLabel;

        private Animation _hintAnimation;
        private Animation _statusAnimation;
        private Timer _hintTimer;
        private Timer _statusTimer;
        private Timer _autoplayPollTimer;
        private Timer _videoProbeTimer;
        private int _autoplayPollTicks;
        private int _videoProbeTicks;
        private string _currentUrl = "";
        private bool _videoConfirmedPlaying;
        // Rapid-load gate state. See LoadStream() for the rationale.
        // 600ms is empirically enough headroom for chromium-efl's
        // video plane manager to finish its tear-down between two
        // back-to-back stream loads. Smaller values (e.g. 200ms)
        // still occasionally crash; larger values (1000ms+) feel
        // sluggish to the user.
        private DateTime _lastLoadAt = DateTime.MinValue;
        private const int MinLoadSpacingMs = 600;
        // Buffer-underrun guard. When the page-side telemetry reports
        // video_waiting / video_stalled, we push this to "now + a few
        // seconds". Two effects: (1) C#'s autoplay tick skips during
        // the window, (2) we set window.__streamSuppressPlay so the
        // in-page setInterval inside ForceAutoplay also bails. Without
        // this, our retry machinery slams .play() on a paused-buffering
        // video and round-trips through the Clappr/JW state machine,
        // re-showing the play-button overlay (the "reload" pattern).
        private DateTime _suppressForcePlayUntil = DateTime.MinValue;
        private const int BufferSuppressMs = 3000;
        // Bumps every time Stop() is called. The video-probe callback closes
        // over the generation it was scheduled in and short-circuits if the
        // host has moved on, so a CheckVideoPlayingAsynchronously result
        // arriving AFTER the player closed can no longer leak "Playing audio
        // (no video) probe N" updates back into the listing's status banner.
        private int _generation;

        /// <summary>Raised when the user presses Back / Escape inside the player.</summary>
        public event EventHandler ExitRequested;

        /// <summary>
        /// Raised whenever PlayerWebView's internal status changes (load started, video found,
        /// autoplay error, etc). The hosting controller (Program.cs) can hook this to mirror
        /// the message into its main status banner — this is the ONLY debug channel since
        /// `sdb shell` / `sdb pull` are blocked on the Samsung TV firmware we ship to.
        /// </summary>
        public event EventHandler<string> StatusChanged;

        public PlayerWebView()
        {
            // The wrapper is full-screen. We keep this transparent (rather than opaque black)
            // so that if the WebView punches a video-hole, an opaque parent doesn't paint
            // over it. The Window/Layer behind us is itself dark, so there's no leak.
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.FillToParent;
            BackgroundColor = new Color(0f, 0f, 0f, 0f);

            // We intentionally do NOT use a LinearLayout here — the WebView and the hint chip
            // overlay each other, so we want raw stacking. Children below get explicit sizing
            // via LayoutParamPolicies.MatchParent / WrapContent, which IS honored on Tizen 9.

            // ---- WebView (fills the whole surface) -----------------------------------
            // Bare-defaults setup for everything EXCEPT transparency. Tizen's
            // hardware video-hole composite requires both the WebView surface
            // and the page background to be transparent; without that the page
            // paints opaque black over the video plane and you get audio with
            // no visible video. (Earlier "audio plays / no video" reports were
            // caused by destructive JS DOM mutations; the transparency
            // properties below are unrelated and genuinely required.)
            _webView = new WebView
            {
                WidthSpecification = LayoutParamPolicies.MatchParent,
                HeightSpecification = LayoutParamPolicies.MatchParent,
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
            };

            // Anchor the WebView to the top-left so its (0,0) coordinate is the top-left
            // of the parent View — without this it can otherwise default to centered origin.
            _webView.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            _webView.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;

            try
            {
                // Page background also transparent — needed for HW video-hole.
                _webView.ContentBackgroundColor = new Color(0f, 0f, 0f, 0f);
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("PlayerWebView.ContentBackgroundColor", ex);
            }

            try
            {
                // Explicit VideoHoleEnabled (default is true on this firmware,
                // making it explicit so we don't depend on a future default flip).
                //
                // Tested setting this to false to chase cross-origin iframe video
                // (embedsports.top et al.) — turned out it BOTH failed to fix
                // the failing stream AND degraded previously-working streams.
                // SW compositor on this firmware can't keep up. Stay on the HW
                // video plane.
                _webView.VideoHoleEnabled = true;
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("PlayerWebView.VideoHoleEnabled", ex);
            }

            try
            {
                if (_webView.Settings != null)
                {
                    // Mixed content + JS so the embed scripts can fetch their stream.
                    _webView.Settings.JavaScriptEnabled = true;
                    _webView.Settings.MixedContentsAllowed = true;
                    _webView.Settings.AutomaticImageLoadingAllowed = true;

                    // The single chromium-efl extra-feature flag that consistently
                    // matters for "audio plays / no video": force the HW video
                    // overlay even when the page hasn't requested fullscreen. On
                    // Tizen TV firmwares without it, Clappr's <video> element
                    // sometimes fails to be promoted to the HW plane and you get
                    // audio-only. Unknown flags are silently ignored by the
                    // engine, so no harm in calling on firmwares that don't
                    // recognise it.
                    TryEnableExtraFeature("force-overlay-fullscreen-video");
                }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("PlayerWebView.Settings", ex);
            }

            try
            {
                // Spoof the User-Agent as desktop Chrome. Some embeds gate on UA —
                // they serve a slimmer / different page (or no stream at all) to
                // anything that looks like a TV WebKit / mobile crawler. Pretending
                // to be desktop Chrome unlocks streams that work in the browser but
                // failed on Tizen.
                //
                // Tizen NUI exposes this on the WebView directly (not Settings.UserAgent
                // which doesn't exist in this API).
                _webView.UserAgent =
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("PlayerWebView.UserAgent", ex);
            }

            try
            {
                // Don't let the WebView absorb keystrokes — they need to reach
                // Program.OnKeyEvent so the new TogglePlay / AdjustVolume bindings
                // can drive Clappr via JS. The earlier intent was to give Clappr's
                // own keyboard handler a chance, but in practice the OK key was
                // being typed as a character into some input on the embed page
                // (showed up as "Hi" / "H" on screen).
                _webView.KeyEventsEnabled = false;
                _webView.MouseEventsEnabled = false;
            }
            catch
            {
            }

            _webView.PageLoadStarted += OnPageLoadStarted;
            _webView.PageLoadFinished += OnPageLoadFinished;
            _webView.PageLoadError += OnPageLoadError;

            // Surface page-side diagnostics. The control JS logs `__STR_CMD__:...`
            // tokens to console after each command so we can confirm the call
            // landed on the <video> element and what state changed.
            try
            {
                _webView.ConsoleMessageReceived += OnPlayerConsoleMessage;
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("PlayerWebView.ConsoleMessage hook", ex);
            }

            Add(_webView);

            // Pre-warm common wrapper-host connections so the FIRST stream
            // open of a session doesn't pay the full DNS + TCP + TLS
            // handshake to streamed.pk wrappers. The page is just a
            // bag of <link rel="preconnect"> hints; chromium-efl
            // initiates the handshakes during HTML parse and the
            // resulting connections sit in chromium's pool for ~60-90s,
            // ready when the user actually opens a stream. Subsequent
            // stream opens benefit from the same warmth automatically
            // (chromium's cache + DNS) — this fix targets the cold
            // first-open the user noticed feels slower than a re-open.
            //
            // Host list is empirical — the wrappers we've seen the
            // streamed.pk catalog return most often. Easy to extend
            // if we observe new hosts in shipped traces.
            TryPrewarmWrapperHosts();

            // ---- "Back to listing" hint chip in the bottom-left corner ---------------
            // Sized for a couch viewing distance — the prior 9pt + 14/8 padding was
            // unreadable on a TV. Bumped to 18pt + ~30/14 padding + brighter text.
            _hintBg = new View
            {
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                BackgroundColor = new Color(0f, 0f, 0f, 0.75f),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.Center,
                    Padding = new Extents(28, 28, 14, 14),
                },
                CornerRadius = new Vector4(10f, 10f, 10f, 10f),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                Margin = new Extents(28, 28, 28, 28),
                Opacity = 1f,
            };
            _hintBg.ParentOrigin = Tizen.NUI.ParentOrigin.BottomLeft;
            _hintBg.PivotPoint = Tizen.NUI.PivotPoint.BottomLeft;
            Add(_hintBg);

            _hintLabel = new TextLabel
            {
                Text = "← BACK · OK play/pause · ▲▼ volume",
                TextColor = Tokens.TextPrimary,
                PointSize = 18f,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("semibold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _hintBg.Add(_hintLabel);

            // ---- Status overlay chip in the TOP-left corner --------------------------
            // Mirrors the back-hint pattern. Same couch-readable sizing.
            _statusBg = new View
            {
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                BackgroundColor = new Color(0f, 0f, 0f, 0.75f),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    LinearAlignment = LinearLayout.Alignment.Center,
                    Padding = new Extents(28, 28, 14, 14),
                },
                CornerRadius = new Vector4(10f, 10f, 10f, 10f),
                CornerRadiusPolicy = VisualTransformPolicyType.Absolute,
                Margin = new Extents(28, 28, 28, 28),
                Opacity = 1f,
            };
            _statusBg.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            _statusBg.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            Add(_statusBg);

            _statusLabel = new TextLabel
            {
                Text = "",
                TextColor = Tokens.TextPrimary,
                PointSize = 18f,
                FontFamily = StreamedTv.Theme.Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("semibold")),
                MultiLine = false,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _statusBg.Add(_statusLabel);
            _statusBg.Hide();

            Hide();
        }

        /// <summary>
        /// Loads the embed URL from the chosen <see cref="StreamOption"/> into the WebView.
        /// </summary>
        /// <remarks>
        /// Bare-bones approach by design. Earlier iterations tried:
        ///   - Injecting autoplay JS that promoted &lt;video&gt; elements (broke embeds whose
        ///     own player JS holds container references).
        ///   - Wrapping the embed URL in an HTML host page with a synthetic-click iframe
        ///     (added a second cross-origin frame boundary that on this firmware made
        ///     things worse, not better).
        ///
        /// User feedback after each iteration: "still doesn't work, was working before
        /// the refactoring." The minimum that ever worked was literally just
        /// <c>_webView.Url = stream.PlaybackUrl</c> with the engine-level VideoHole +
        /// transparent-background flags already configured in the constructor. So that
        /// is what this method does now. If reliability still suffers, the bottleneck
        /// is engine-side (chromium-efl on this Tizen firmware) and the fix needs to
        /// be at the EnableExtraFeature level, not here.
        /// </remarks>
        public void LoadStream(StreamOption stream)
        {
            // Defensive: tear down any previous stream's WebView state, timers,
            // generation, and <video> pipeline before loading a new URL. In
            // the current flow LoadStream is only called after the user has
            // exited the player (which already runs Stop()), so this is
            // usually a no-op. But it cuts off the class of "previous
            // stream's poll timer / probe callback / decoder leaks into new
            // stream" bugs at the source — Stop() is idempotent.
            Stop();

            // Gate against rapid back-to-back loads. chromium-efl's video
            // plane manager has a known bug where if we tear down + recreate
            // a <video> within ~half a second, the native side enters a
            // corrupted state and SIGABRTs the whole process. We've
            // captured this signature multiple times in shipped traces
            // (no managed CRASH banner because the kill is below
            // AppDomain.UnhandledException — the runtime never gets a
            // chance to fire).
            //
            // The gate enforces a minimum spacing between successive
            // LoadStream calls. Calls that arrive too soon get queued up
            // by the simple "wait then proceed" pattern below.
            try
            {
                long sinceLastMs = (DateTime.UtcNow - _lastLoadAt).Ticks / TimeSpan.TicksPerMillisecond;
                if (sinceLastMs < MinLoadSpacingMs)
                {
                    int waitMs = (int)(MinLoadSpacingMs - sinceLastMs);
                    Diagnostics.Log("PlayerWebView.LoadStream gated: waiting " + waitMs + "ms after prior load");
                    System.Threading.Thread.Sleep(waitMs);
                }
                _lastLoadAt = DateTime.UtcNow;
            }
            catch { /* gate is best-effort; never block playback */ }

            if (stream == null || string.IsNullOrWhiteSpace(stream.PlaybackUrl))
            {
                _currentUrl = "";
                return;
            }

            _currentUrl = stream.PlaybackUrl;
            _videoConfirmedPlaying = false;
            // Telemetry: kick off a stream-attempt trace so the
            // shipped log lets us correlate "user tried to play X" →
            // "video element stayed 0x0" → "user backed out". URL is
            // truncated to keep the line short — full URL is in the
            // stream catalog if needed.
            try
            {
                StreamedTv.Services.Diagnostics.Track("stream_attempt",
                    new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["url"] = TruncForTelemetry(_currentUrl, 200),
                        ["src"] = stream.Source ?? "",
                        ["wrapped"] = StreamedTv.Services.StreamResolverService.IsKnownWrapper(_currentUrl) ? "1" : "0",
                    });
            }
            catch { }

            // Async resolver path. We capture the URL we're about to
            // resolve in a local + a generation token; if the user
            // moves to a different stream while resolution is in
            // flight, the resolver result is discarded.
            string requestedUrl = _currentUrl;
            int gen = ++_loadGen;
            _ = LoadResolvedAsync(stream, requestedUrl, gen);

            ShowHint();
            ScheduleHintAutoHide();
            ReportStatus("Loading…");
        }

        // Generation counter — bumped every LoadStream so an in-flight
        // resolver from a prior call can detect "user moved on" and
        // skip the navigation. Prevents a stale resolved URL from
        // racing in over a fresh LoadStream.
        private int _loadGen;

        // Owned by Program.cs and injected via SetResolver. Optional;
        // when null we skip resolution and load the original URL
        // synchronously (preserves the pre-resolver behaviour for
        // local dev / no-auth setups).
        private StreamedTv.Services.StreamResolverService _resolver;

        public void SetResolver(StreamedTv.Services.StreamResolverService resolver)
        {
            _resolver = resolver;
        }

        // Resolves the requested URL (best-effort), then assigns to
        // WebView.Url. ALL of the following must hold for the
        // resolved URL to actually be used:
        //   * Resolver is wired (_resolver != null).
        //   * URL is a known wrapper (resolver short-circuits otherwise).
        //   * Backend returned a different, non-empty URL.
        //   * The user hasn't navigated to a different stream while
        //     we awaited (gen check).
        // On any failure we fall back to the original URL — the
        // resolver is opportunistic, never load-bearing.
        private async System.Threading.Tasks.Task LoadResolvedAsync(StreamOption stream, string requestedUrl, int gen)
        {
            string finalUrl = requestedUrl;
            try
            {
                if (_resolver != null && StreamedTv.Services.StreamResolverService.IsKnownWrapper(requestedUrl))
                {
                    using System.Threading.CancellationTokenSource cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                    StreamedTv.Services.StreamResolverService.ResolveOutcome o =
                        await _resolver.ResolveDetailedAsync(requestedUrl, cts.Token).ConfigureAwait(true);

                    // Bail if the user moved on while we awaited.
                    if (gen != _loadGen) { Diagnostics.Log("Resolver: superseded gen=" + gen); return; }

                    StreamedTv.Services.Diagnostics.Track("stream_resolve",
                        new System.Collections.Generic.Dictionary<string, string>
                        {
                            ["strategy"] = o.Strategy ?? "?",
                            ["target"] = o.Target ?? "",
                            ["err"] = o.Error ?? "",
                            ["changed"] = o.Changed(requestedUrl) ? "1" : "0",
                        });
                    if (o.Changed(requestedUrl))
                    {
                        finalUrl = o.Url;
                        // Update _currentUrl so OnPageLoadFinished's
                        // about:blank-skip check still works against
                        // the URL we actually loaded.
                        _currentUrl = finalUrl;
                        // Spoof Referer to the original wrapper. Most
                        // bypass targets (pooembed.eu, etc.) check
                        // Referer at the page level — without it they
                        // 403 the navigation entirely. The wrapper
                        // host is the value the inner player would
                        // have seen if it was loaded as a child
                        // iframe under the wrapper, so this restores
                        // the same context. AddCustomHeader applies to
                        // ALL subsequent WebView requests, which is
                        // exactly what we want — the bundle JS, the
                        // m3u8 manifest fetch, and every fragment
                        // download all need the same Referer.
                        try
                        {
                            string wrapperOrigin = ExtractOrigin(requestedUrl);
                            if (!string.IsNullOrEmpty(wrapperOrigin))
                            {
                                _webView.AddCustomHeader("Referer", wrapperOrigin + "/");
                                StreamedTv.Services.Diagnostics.Track("stream_referer_set",
                                    new System.Collections.Generic.Dictionary<string, string>
                                    {
                                        ["origin"] = wrapperOrigin,
                                    });
                            }
                        }
                        catch (Exception hex) { Diagnostics.LogError("AddCustomHeader.Referer", hex); }
                    }
                    else
                    {
                        // Resolver did NOT bypass — clear any
                        // previously-set Referer so a stream that
                        // doesn't need spoofing doesn't inherit a
                        // stale Referer from a prior pooembed load.
                        try { _webView.RemoveCustomHeader("Referer"); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("LoadResolvedAsync.resolve", ex);
            }

            try
            {
                if (gen != _loadGen) return;  // superseded between resolve + assign
                _webView.Url = finalUrl;
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("PlayerWebView.LoadStream", ex);
                ReportStatus("Load failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Stops any in-progress load and clears the WebView's source so we don't keep
        /// streaming bandwidth while the player view is hidden.
        /// </summary>
        public void Stop()
        {
            _currentUrl = "";
            _videoConfirmedPlaying = false;
            _suppressForcePlayUntil = DateTime.MinValue;
            // Bump the generation BEFORE canceling timers — any
            // CheckVideoPlayingAsynchronously callback already in flight will
            // see the new generation and skip its ReportStatus call.
            System.Threading.Interlocked.Increment(ref _generation);
            CancelAutoplayPollTimer();
            CancelVideoProbeTimer();
            // Hard-pause every video element + clear its src BEFORE navigating
            // away. about:blank should release the page on its own, but on
            // chromium-efl audio occasionally lingers if the underlying decoder
            // hasn't been told to stop. .pause() + src='' + .load() is the
            // canonical way to drop the audio/video pipeline now.
            try
            {
                _webView.EvaluateJavaScript(
                    "(function(){try{document.querySelectorAll('video').forEach(function(v){try{v.pause();}catch(e){}try{v.removeAttribute('src');v.src='';v.load();}catch(e){}});}catch(e){}})()");
            }
            catch { }
            try
            {
                _webView.StopLoading();
            }
            catch
            {
            }
            try
            {
                // Navigate off the embed so the page tears down its own player and releases
                // any sockets / decoders it's holding. about:blank is a safe target.
                _webView.Url = "about:blank";
            }
            catch
            {
            }
            CancelHintTimer();
            CancelStatusTimer();
        }

        /// <summary>
        /// Routes a remote-control keypress to the player. Back is consumed (and surfaced
        /// via <see cref="ExitRequested"/>); everything else nudges the hint visible briefly
        /// so the user always knows how to leave.
        /// </summary>
        /// <returns><c>true</c> if the key was handled (caller should not propagate).</returns>
        public bool RouteKey(string keyName)
        {
            if (string.IsNullOrEmpty(keyName)) return false;

            if (keyName == "XF86Back" || keyName == "Escape")
            {
                CancelHintTimer();
                CancelStatusTimer();
                EventHandler handler = ExitRequested;
                if (handler != null) handler(this, EventArgs.Empty);
                return true;
            }

            // Any non-Back press: pop both the hint AND the status banner back into view
            // and re-arm their auto-hide timers — gives the user a way to *peek* at status.
            ShowHint();
            ScheduleHintAutoHide();
            ShowStatus();
            ScheduleStatusAutoHide();
            return false;
        }

        // --------------------------------------------------------------------- internals

        private void TryEnableExtraFeature(string flag)
        {
            try
            {
                _webView.Settings.EnableExtraFeature(flag, true);
            }
            catch
            {
                // Unknown feature flags are silently ignored by the engine; some firmwares
                // throw, others no-op. Either way it's non-fatal.
            }
        }

        // Loads a tiny data: URL whose only purpose is to carry
        // <link rel="preconnect"> + <link rel="dns-prefetch"> hints
        // for the streamed.pk wrapper hosts we see most often. The
        // page itself does nothing else. chromium-efl resolves DNS
        // and completes TCP+TLS handshakes during HTML parse; the
        // resulting sockets sit in chromium's connection pool with
        // a default ~60–90s idle keep-alive, so when the user
        // actually navigates _webView.Url to a wrapper page shortly
        // after construction, the connection is reused rather than
        // re-handshaken.
        //
        // Trade-offs:
        //   * No real bandwidth cost (TLS handshake bytes only).
        //   * If the user spends >90s before opening a stream, the
        //     pool times out — re-prewarming periodically would
        //     close that gap but isn't worth the complexity for an
        //     internal experiment.
        //   * Subsequent Stop() navigates to about:blank, which
        //     does NOT close the pool's idle sockets — they stay
        //     warm for the next stream of the session.
        //
        // Host list is empirical; extend if shipped traces reveal
        // commonly-failing prewarms.

        /// <summary>Re-fire the wrapper-host prewarm. Safe to call
        /// from any UI moment when we know the user is about to
        /// pick a stream — typically the moment the stream picker
        /// opens. No-op if a stream is currently playing
        /// (re-navigating the WebView would interrupt playback). The
        /// pool keep-alive of ~60-90s means re-prewarming right
        /// before each pick keeps the next first-open as fast as a
        /// re-open.</summary>
        public void PrewarmWrapperHosts()
        {
            // _currentUrl is set in LoadStream and cleared in Stop.
            // Non-empty here means a stream is loaded — skip so we
            // don't tear it down to re-warm.
            if (!string.IsNullOrEmpty(_currentUrl)) return;
            TryPrewarmWrapperHosts();
        }

        private void TryPrewarmWrapperHosts()
        {
            try
            {
                if (_webView == null) return;
                const string html = @"<!DOCTYPE html><html><head>
<link rel=""preconnect"" href=""https://embedsports.top"" crossorigin>
<link rel=""preconnect"" href=""https://embedme.top"" crossorigin>
<link rel=""preconnect"" href=""https://pooembed.eu"" crossorigin>
<link rel=""preconnect"" href=""https://rfs2.embedsportsx.com"" crossorigin>
<link rel=""dns-prefetch"" href=""https://embedsports.top"">
<link rel=""dns-prefetch"" href=""https://embedme.top"">
<link rel=""dns-prefetch"" href=""https://pooembed.eu"">
<link rel=""dns-prefetch"" href=""https://rfs2.embedsportsx.com"">
</head><body></body></html>";
                string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(html));
                _webView.Url = "data:text/html;base64," + b64;
            }
            catch (Exception ex) { Diagnostics.LogError("PlayerWebView.TryPrewarmWrapperHosts", ex); }
        }

        private void OnPageLoadStarted(object sender, WebViewPageLoadEventArgs e)
        {
            try { Diagnostics.Log("WebView load started: " + (e?.PageUrl ?? "(null)")); } catch { }
            CancelAutoplayPollTimer();
            CancelVideoProbeTimer();
            _videoConfirmedPlaying = false;
            ReportStatus("Loading page…");
            // ROLLED BACK: an earlier iteration installed a
            // property-setter intercept on `window.Hls` here in an
            // attempt to wrap HLS.js construction (for buffer tuning
            // + error subscription). It NEVER fired in shipped
            // traces — the embedsports/JW bundle imports HLS.js
            // internally and never assigns it to window.Hls — so the
            // intercept achieved nothing while measurably breaking
            // page loads (Error 104153 from JW Player) and producing
            // recurring NREs at the video-element recreate boundary.
            // Per the memory note "Player changes minimal/additive":
            // observation only here. The PageLoadFinished
            // InjectTelemetryProbe still attempts a passive wrap of
            // window.Hls — it captures `hls_present` when the
            // wrapper page exposes a global Hls reference but
            // doesn't hook instances created from the bundle's
            // internal scope. Buffer tuning needs a different
            // mechanism (likely WebView-level settings, not JS).
            //
            // Iframe-context spoof for pooembed kept — narrowly
            // gated to that one host so it can't disturb anything
            // else. (Currently a no-op anyway since chromium-efl
            // marks window.top non-configurable; we keep the
            // call site so the next iteration can swap to a
            // different bypass technique here.)
            try
            {
                string url = e?.PageUrl ?? "";
                if (url.IndexOf("pooembed.eu", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _webView.EvaluateJavaScript(@"
(function(){
  if (window.__strTopSpoofEarly) return;
  window.__strTopSpoofEarly = true;
  try {
    var fakeTop = {};
    Object.defineProperty(window, 'top', { get: function(){ return fakeTop; }, configurable: false });
    Object.defineProperty(window, 'parent', { get: function(){ return fakeTop; }, configurable: false });
    console.log('__STR_TRACE__:topspoof_early|host='+location.hostname);
  } catch(e) {
    console.log('__STR_TRACE__:topspoof_early_failed|msg='+(e&&e.message||'?'));
  }
})();");
                }
            }
            catch (Exception ex) { Diagnostics.LogError("OnPageLoadStarted.spoof", ex); }
        }

        private void OnPageLoadFinished(object sender, WebViewPageLoadEventArgs e)
        {
            string url = e?.PageUrl ?? "";
            try { Diagnostics.Log("WebView load finished: " + url); } catch { }
            // Skip the autoplay JS + probe scheduling when this fired in
            // response to our own tear-down navigation. Stop() navigates to
            // about:blank to release the embed; without this guard the page
            // load would re-arm a fresh probe against about:blank that ticks
            // "Playing audio (no video) · probe N" up to 12 in a loop.
            if (string.IsNullOrEmpty(url)
                || url == "about:blank"
                || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(_currentUrl))
            {
                ReportStatus("");
                return;
            }
            if (!ENABLE_AUTOPLAY)
            {
                // Experiment mode: skip our autoplay JS entirely; let the embed page
                // do whatever it does. We still surface status + iframe perms hint.
                ReportStatus("Page loaded · autoplay JS disabled (experiment)");
                ScheduleVideoPlaybackProbe();
                return;
            }
            ReportStatus("Page loaded · finding video…");
            ForceAutoplay();
            ScheduleAutoplayPolling();
            ScheduleVideoPlaybackProbe();
            // Telemetry: separate, additive observer that ONLY reads
            // page state (no DOM mutation) and posts samples back via
            // console.log. Safe to layer on top of ForceAutoplay — see
            // memory note "Player changes minimal/additive".
            InjectTelemetryProbe();
        }

        // ---- Clappr remote-control bindings -----------------------------------
        // First version drove `window.player`. Then HTMLVideoElement directly.
        // Both broke on click-to-play embeds whose <video> element doesn't
        // exist until the user clicks a centered play overlay. So now: each
        // command first looks for a visible play-button-style element across
        // the page AND any same-origin iframe and clicks it; THEN tries to
        // toggle the video element. The first OK press dismisses the overlay
        // and creates the <video>; subsequent OK presses pause/resume it.

        // Shared helper script — defines window.__streamFind that locates the
        // best play-button or video element across page + iframes. Idempotent;
        // installed lazily by each command call.
        private const string FinderInstallJs = @"
(function(){
  if (window.__streamFind) return;
  window.__streamFind = {
    // Walk window.frames recursively; safely skip cross-origin frames.
    docs: function(){
      var out = [document];
      function walk(w){
        for (var i=0;i<w.frames.length;i++){
          try { var d=w.frames[i].document; if(d){ out.push(d); walk(w.frames[i]); } } catch(e){}
        }
      }
      try { walk(window); } catch(e){}
      return out;
    },
    findVideo: function(){
      var ds = this.docs();
      for (var i=0;i<ds.length;i++){
        try { var v = ds[i].querySelector('video'); if (v) return v; } catch(e){}
      }
      return null;
    },
    findPlayButton: function(){
      // Match common ""big play"" overlays across Clappr / JW / Plyr / Video.js
      // / Shaka / generic. Has to be VISIBLE (non-zero rect) — many players
      // render hidden duplicates we don't want to ""click"".
      var sel = [
        '.play-wrapper', '.play-button', '[data-playpause]',
        '.play-pause-button', '.media-control-icon',
        '.jw-display-icon-container', '.jw-icon-play', '.jw-display-controls',
        '.vjs-big-play-button',
        '.plyr__control--overlaid', '.plyr__control[data-plyr=play]',
        '.shaka-play-button',
        '[aria-label*=""play"" i]', '[title*=""play"" i]',
        'button.btn-play', '.btn-play', '.play-btn'
      ].join(',');
      var ds = this.docs();
      for (var i=0;i<ds.length;i++){
        var matches;
        try { matches = ds[i].querySelectorAll(sel); } catch(e){ continue; }
        for (var j=0;j<matches.length;j++){
          try {
            var el = matches[j];
            var r = el.getBoundingClientRect();
            if (r && r.width>2 && r.height>2) return el;
          } catch(e){}
        }
      }
      return null;
    },
    // Find the biggest visible iframe on the OUTER page. Used when the actual
    // player is wrapped in a cross-origin iframe we can't reach into — we
    // navigate the WebView to the iframe's src so we become its content and
    // the play button is then same-origin to us.
    findInnerIframe: function(){
      try {
        var iframes = document.querySelectorAll('iframe');
        var best = null, bestArea = 0;
        for (var i=0;i<iframes.length;i++){
          var f = iframes[i];
          var src = f.src || '';
          if (!src || src === 'about:blank') continue;
          var r = f.getBoundingClientRect();
          var area = (r.width||0) * (r.height||0);
          if (area > bestArea){ bestArea = area; best = f; }
        }
        return best;
      } catch(e){ return null; }
    },
    diagnose: function(){
      var ds = this.docs();
      var nvid = 0, nframe = 0;
      try { nframe = window.frames.length; } catch(e){}
      for (var i=0;i<ds.length;i++){
        try { nvid += ds[i].querySelectorAll('video').length; } catch(e){}
      }
      return 'docs:'+ds.length+' iframes:'+nframe+' videos:'+nvid;
    }
  };
})();
";

        public void TogglePlay()
        {
            EvalSafe(FinderInstallJs + @"
(function(){try{
  var f = window.__streamFind;
  var v = f.findVideo();
  if (v){
    if (v.paused){ v.play(); console.log('__STR_CMD__:play'); }
    else { v.pause(); console.log('__STR_CMD__:pause'); }
    return;
  }
  // No video — try clicking a same-origin play button overlay.
  var b = f.findPlayButton();
  if (b){ b.click(); console.log('__STR_CMD__:overlay-click'); return; }
  // Still nothing — the player is probably wrapped in a cross-origin iframe
  // we can't reach into via JS. Tell the host to dispatch a native touch
  // event at the iframe's center; native input crosses cross-origin
  // boundaries the way JS .click() can't, and the iframe's play button
  // receives it as a real user gesture.
  var f2 = f.findInnerIframe();
  if (f2){
    var r = f2.getBoundingClientRect();
    var cx = Math.round(r.left + r.width/2);
    var cy = Math.round(r.top + r.height/2);
    console.log('__STR_NATIVE_TOUCH__:' + cx + ',' + cy);
    return;
  }
  console.log('__STR_CMD__:no-video ('+f.diagnose()+')');
}catch(e){console.log('__STR_CMD__:err:'+e.message);}})()");
            ReportStatus("⏯");
        }

        public void AdjustVolume(int delta)
        {
            string js = FinderInstallJs +
                "(function(){try{" +
                "var v=window.__streamFind.findVideo();" +
                "if(!v){console.log('__STR_CMD__:no-video');return;}" +
                "var step=(" + delta + ")/100.0;" +
                "var n=Math.max(0,Math.min(1,(v.volume||0.5)+step));" +
                "v.volume=n;" +
                "v.muted=(n===0);" +
                "console.log('__STR_CMD__:vol:'+Math.round(n*100));" +
                "}catch(e){console.log('__STR_CMD__:err:'+e.message);}})()";
            EvalSafe(js);
            ReportStatus(delta > 0 ? "Volume +" : "Volume -");
        }

        public void ToggleMute()
        {
            EvalSafe(FinderInstallJs + @"
(function(){try{
  var v=window.__streamFind.findVideo();
  if(!v){console.log('__STR_CMD__:no-video');return;}
  v.muted=!v.muted;
  console.log('__STR_CMD__:mute:'+v.muted);
}catch(e){console.log('__STR_CMD__:err:'+e.message);}})()");
            ReportStatus("🔇");
        }

        private void EvalSafe(string js)
        {
            try { _webView?.EvaluateJavaScript(js); }
            catch (Exception ex) { Diagnostics.LogError("PlayerWebView.EvalSafe", ex); }
        }

        // React to playback-state telemetry from the in-page probe BEFORE
        // shipping it through Diagnostics.Track. Two events drive behavior:
        //
        //   video_playing → first confirmed frame. Disarm both the C# autoplay
        //     poll (via _videoConfirmedPlaying) AND the inner JS retry
        //     setInterval (via window.__streamConfirmedPlaying). Without this
        //     we'd burn the full ~4s of half-second .play() calls AFTER the
        //     video has already started — exactly the window where a buffer
        //     underrun is most likely to be misread as 'paused' and trigger
        //     a player-state round-trip that re-shows the play overlay.
        //
        //   video_waiting / video_stalled → buffer underrun. Set a short
        //     suppression window so neither the C# tick nor the JS retry
        //     loop calls .play() while the player is genuinely waiting for
        //     bytes. Lets the player's own buffering logic resume on its own.
        //
        // Layered on top of existing telemetry so it stays observable even
        // when the suppress/early-exit fires unexpectedly — search the trace
        // for autoplay_disarm / autoplay_suppressed to debug.
        private void HandlePlaybackTelemetry(string evName)
        {
            if (string.IsNullOrEmpty(evName)) return;
            try
            {
                if (evName == "video_playing")
                {
                    if (!_videoConfirmedPlaying)
                    {
                        _videoConfirmedPlaying = true;
                        Diagnostics.Log("PlayerWebView: video_playing → autoplay loops disarmed");
                        try { StreamedTv.Services.Diagnostics.Track("autoplay_disarm", null); } catch { }
                        EvalSafe("window.__streamConfirmedPlaying = true;");
                    }
                }
                else if (evName == "video_waiting" || evName == "video_stalled")
                {
                    // Only suppress AFTER the video has actually played at
                    // least once. During initial load the embed page's
                    // first video_waiting fires for normal first-frame
                    // buffering — suppressing then blocks our autoplay
                    // retries from clicking Clappr/JW's overlay before
                    // it becomes interactive, and the embed's own JS
                    // sometimes doesn't autonomously recover. Net effect:
                    // the stream sat on a white screen forever (regression
                    // we observed for streamed.pk wrappers). Once we have
                    // confirmed a frame has rendered, mid-playback
                    // underruns SHOULD suppress our retries — that's the
                    // original bug fix.
                    if (!_videoConfirmedPlaying) return;
                    _suppressForcePlayUntil = DateTime.UtcNow.AddMilliseconds(BufferSuppressMs);
                    Diagnostics.Log("PlayerWebView: " + evName + " → suppressing force-play for " + BufferSuppressMs + "ms");
                    try
                    {
                        StreamedTv.Services.Diagnostics.Track("autoplay_suppressed",
                            new System.Collections.Generic.Dictionary<string, string>
                            {
                                ["ev"] = evName,
                                ["ms"] = BufferSuppressMs.ToString(),
                            });
                    }
                    catch { }
                    EvalSafe("window.__streamSuppressPlay = true; setTimeout(function(){window.__streamSuppressPlay=false;}, " + BufferSuppressMs + ");");
                }
            }
            catch (Exception ex)
            {
                try { Diagnostics.LogError("HandlePlaybackTelemetry", ex); } catch { }
            }
        }

        // Console messages from the control JS. We log everything for diagnostics
        // and surface command outcomes (play/pause/vol/mute/no-video) into the
        // top-left status chip so the user sees what actually happened.
        private void OnPlayerConsoleMessage(object sender, WebViewConsoleMessageReceivedEventArgs e)
        {
            try
            {
                string msg = e?.ConsoleMessage?.Text;
                if (string.IsNullOrEmpty(msg)) return;

                // The JS asked us to dispatch a NATIVE touch at given iframe
                // center coordinates. Cross-origin iframe play buttons can't be
                // activated by JS .click() but DO respond to real browser input
                // — including programmatic touches dispatched via the View base
                // class's DispatchTouchEvent. The format is "x,y" in CSS pixels.
                const string touchPrefix = "__STR_NATIVE_TOUCH__:";
                int touchIdx = msg.IndexOf(touchPrefix, StringComparison.Ordinal);
                if (touchIdx >= 0)
                {
                    string coords = msg.Substring(touchIdx + touchPrefix.Length).Trim();
                    string[] parts = coords.Split(',');
                    if (parts.Length == 2 &&
                        int.TryParse(parts[0], out int x) &&
                        int.TryParse(parts[1], out int y))
                    {
                        Diagnostics.Log("Dispatching native touch at " + x + "," + y);
                        DispatchTouchAt(x, y);
                        ReportStatus("Tapping iframe @ " + x + "," + y);
                    }
                    return;
                }

                const string prefix = "__STR_CMD__:";
                int idx = msg.IndexOf(prefix, StringComparison.Ordinal);
                if (idx < 0)
                {
                    // Telemetry channel: same console.log pipe, different
                    // prefix. Format on the wire is
                    //   __STR_TRACE__:event_name|k1=v1|k2=v2
                    // (pipe-delimited so URL values containing '=' or
                    // ':' don't trip the parser.) Routes through the
                    // same Diagnostics.Track / CrashReporter shipping
                    // pipeline as everything else.
                    const string tracePrefix = "__STR_TRACE__:";
                    int tIdx = msg.IndexOf(tracePrefix, StringComparison.Ordinal);
                    if (tIdx >= 0)
                    {
                        string body = msg.Substring(tIdx + tracePrefix.Length).Trim();
                        if (!string.IsNullOrEmpty(body))
                        {
                            string[] segs = body.Split('|');
                            string evName = segs[0];
                            HandlePlaybackTelemetry(evName);
                            var props = new System.Collections.Generic.Dictionary<string, string>(segs.Length);
                            for (int i = 1; i < segs.Length; i++)
                            {
                                int eq = segs[i].IndexOf('=');
                                if (eq <= 0) continue;
                                string k = segs[i].Substring(0, eq);
                                string v = segs[i].Substring(eq + 1);
                                if (!string.IsNullOrEmpty(k)) props[k] = v;
                            }
                            try { StreamedTv.Services.Diagnostics.Track(evName, props); } catch { }
                        }
                    }
                    return;
                }
                string outcome = msg.Substring(idx + prefix.Length).Trim();
                Diagnostics.Log("Player JS reply: " + outcome);
                ReportStatus("Clappr: " + outcome);
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnPlayerConsoleMessage", ex);
            }
        }

        // Cross-origin iframe play-button workaround. Strategy:
        //   1. Grant autoplay permission via the iframe's `allow` attribute.
        //      Cross-origin iframes inherit Permissions-Policy from the
        //      parent's allow attr. With autoplay granted, the inner page's
        //      <code>video.play()</code> won't be rejected for missing user gesture.
        //   2. Append <code>?autoplay=1</code> (or <code>&autoplay=1</code>) to the iframe's
        //      src on reload. Many embed providers respect this URL convention
        //      and skip rendering the click-to-play overlay entirely.
        //   3. Force-reload the iframe so the new permissions + URL take effect.
        //   4. After the reload completes, postMessage('play', '*') into the
        //      iframe — YouTube/Vimeo/many custom embeds listen for play
        //      commands via this channel and will start playback if granted
        //      autoplay permission.
        //
        // Strategy 2 from earlier (synthetic MouseEvent on iframe element) was
        // dropped — events at the parent level don't reach into cross-origin
        // iframe content and were not contributing.
        //
        // <code>x</code>/<code>y</code> args here are kept on the public method signature for
        // potential future use (e.g. true native touch dispatch when Tizen
        // exposes one); current implementation doesn't need them.
        private void DispatchTouchAt(int x, int y)
        {
            try
            {
                EvalSafe(@"
(function(){try{
  var any = false;
  document.querySelectorAll('iframe').forEach(function(f){
    try {
      // 1. Grant autoplay + related permissions to the iframe.
      f.allow = 'autoplay; fullscreen; encrypted-media; picture-in-picture; clipboard-write';
      f.allowFullscreen = true;

      // 2. Append autoplay=1 to the URL if not already present.
      var src = f.src || '';
      if (src && src.indexOf('autoplay=') < 0) {
        var sep = src.indexOf('?') >= 0 ? '&' : '?';
        src = src + sep + 'autoplay=1';
      }

      // 3. Force reload by clearing then re-setting src. Small delay so
      //    the empty src takes effect before the new src loads.
      if (src) {
        f.src = '';
        setTimeout(function(){ f.src = src; }, 30);

        // 4. After the iframe loads, postMessage('play','*') and a few
        //    YouTube/Vimeo-flavoured payloads. Cross-origin iframes
        //    receive postMessage natively; if their JS listens for any
        //    of these, playback starts.
        f.addEventListener('load', function once(){
          f.removeEventListener('load', once);
          setTimeout(function(){
            try { f.contentWindow.postMessage('play','*'); } catch(e){}
            try { f.contentWindow.postMessage({event:'command',func:'playVideo',args:[]},'*'); } catch(e){}  // YouTube
            try { f.contentWindow.postMessage({method:'play'},'*'); } catch(e){}                              // Vimeo / generic
            try { f.contentWindow.postMessage({type:'play'},'*'); } catch(e){}                                // generic
          }, 250);
        });

        any = true;
      }
    } catch(e){}
  });
  console.log('__STR_CMD__:autoplay-grant:' + any);
}catch(e){console.log('__STR_CMD__:err:'+e.message);}})()");
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("DispatchTouchAt", ex);
            }
        }

        /// <summary>
        /// Injects autoplay JS targeted at the embedsports.top embed. Idempotent —
        /// guarded by <c>window.__streamedInjected</c>. Called on PageLoadFinished
        /// then a couple times more as a safety net for the Clappr bundle being
        /// loaded asynchronously.
        /// </summary>
        /// <remarks>
        /// The embed is built with Clappr (a JS player) on top of a P2P engine and a
        /// JW bundle. Clappr renders a big play-button overlay that requires an actual
        /// click — a synthetic <c>body.click()</c> doesn't propagate to that custom
        /// overlay. Solution: target the play-button DOM nodes specifically AND call
        /// the global Clappr instance's <c>.play()</c> method directly.
        ///
        /// Strategy chain (each independent, all attempted on every retry):
        ///   1. Block <c>window.open</c> so aclib's runPop can't navigate away from
        ///      the player.
        ///   2. Hide any iframe whose src matches ad domains/paths (Clappr's overlay
        ///      siblings; periodic /ad.html injection).
        ///   3. Call <c>.play()</c> on every plausible global player ref:
        ///      <c>window.player</c>, <c>jwplayer()</c>, <c>playerInstance</c>, etc.
        ///   4. Synthetically click every visible element matching common play-button
        ///      selectors (Clappr's <c>.play-wrapper</c>, <c>[data-playpause]</c>, etc).
        ///   5. Raw <c>video.play()</c> on any &lt;video&gt; element as a last resort.
        ///
        /// Retries 8 times on a 500 ms interval (4 s total) because Clappr initialises
        /// asynchronously after its bundle scripts finish loading.
        /// </remarks>
        private void ForceAutoplay()
        {
            if (_webView == null) return;

            const string js = @"
(function(){
  if (window.__streamedInjected) return; window.__streamedInjected = true;

  // 0a. Anti-detection: patch the few navigator/window fields that PPV
  //     embeds commonly sniff to filter out ""not a real browser"". Cheap
  //     and additive — harmless if the embed doesn't check, occasionally
  //     load-bearing if it does.
  try {
    Object.defineProperty(navigator, 'webdriver',
      { get: function(){ return false; }, configurable: true });
  } catch(e) {}
  try { if (!window.chrome) window.chrome = { runtime: {} }; } catch(e) {}
  try {
    if (navigator.plugins && navigator.plugins.length === 0) {
      Object.defineProperty(navigator, 'plugins',
        { get: function(){ return [1,2,3,4,5]; }, configurable: true });
    }
  } catch(e) {}
  try {
    if (navigator.permissions && navigator.permissions.query) {
      var __origPerm = navigator.permissions.query.bind(navigator.permissions);
      navigator.permissions.query = function(p){
        if (p && p.name === 'notifications') {
          // Some bot-checks call permissions.query then compare to
          // Notification.permission — mismatched values is a tell.
          return Promise.resolve({ state: (typeof Notification !== 'undefined') ? Notification.permission : 'denied' });
        }
        return __origPerm(p);
      };
    }
  } catch(e) {}

  // 0b. Diagnostic: tag every <video> element (existing + future) with
  //     error/stall/abort listeners that emit __STR_CMD__ tokens to the
  //     console. The host's OnPlayerConsoleMessage already echoes these
  //     into the status banner + Diagnostics.Log, so failures that today
  //     present as ""play overlay returns silently"" become inspectable.
  try {
    function tagVideo(v){
      if (v.__streamErrTagged) return;
      v.__streamErrTagged = true;
      v.addEventListener('error', function(){
        var err = v.error || {};
        console.log('__STR_CMD__:video-err:code=' + (err.code || '?') + ' msg=' + (err.message || '?'));
      });
      v.addEventListener('stalled', function(){ console.log('__STR_CMD__:video-stalled'); });
      v.addEventListener('abort',   function(){ console.log('__STR_CMD__:video-abort'); });
      v.addEventListener('loadedmetadata', function(){
        console.log('__STR_CMD__:video-meta:' + (v.videoWidth || 0) + 'x' + (v.videoHeight || 0));
      });
    }
    document.querySelectorAll('video').forEach(tagVideo);
    if (typeof MutationObserver !== 'undefined') {
      new MutationObserver(function(){
        document.querySelectorAll('video').forEach(tagVideo);
      }).observe(document.documentElement || document.body, { childList: true, subtree: true });
    }
  } catch(e) {}

  // 1. ""Fake-open"" popups instead of blocking outright. The earlier
  //    window.open = ()=>null broke embeds whose stream-unlock flow waits
  //    for the ad popup to ""succeed"" before booting the Clappr stream —
  //    they saw open()==null and bailed back to the play-overlay, which
  //    looked like ""press play, overlay vanishes briefly, then returns"".
  //    Now: hand back a synthetic Window-like object that reports
  //    closed=true so the embed thinks the popup opened (and immediately
  //    closed, which is fine on TV — there's no second window anyway).
  //    Best-effort: try to actually open a tiny offscreen popup first
  //    (some embeds inspect w.location after) and immediately close it.
  try {
    var __origOpen = window.open;
    window.open = function(url, name, features){
      try {
        var w = __origOpen.call(window, url || 'about:blank', name || '_blank',
                                'width=1,height=1,left=-9999,top=-9999');
        if (w) { try { w.close(); } catch(e){} return w; }
      } catch(e) {}
      return {
        closed: true,
        close: function(){},
        focus: function(){},
        blur: function(){},
        location: { href: url || 'about:blank' },
        document: null,
        opener: window,
        postMessage: function(){},
      };
    };
  } catch(e) {}

  // 2. Soft-hide ad iframes AFTER their load handler has had a chance to
  //    run. The earlier ""yank styles every 1s on every iframe matching the
  //    list"" fired BEFORE the iframe's load event, killing the embed's
  //    ""ad loaded → unlock stream"" wiring (Clappr awaits its sibling
  //    iframe's onload). Now: collapse each ad iframe ONCE, ~250ms after
  //    its load fires, with a 2.5s safety-net fallback in case load
  //    never fires. Keeps the iframe in the DOM (some embeds walk
  //    document.querySelectorAll('iframe').length and re-spawn ad slots
  //    if they go missing).
  //
  //    Also: removed 'cloudfront' from the hide list. Many legit stream
  //    CDNs and P2P signaling endpoints sit on CloudFront — that match
  //    was hiding the actual player iframe on embedsports.top etc.
  function softHideAds(){
    try {
      document.querySelectorAll('iframe').forEach(function(f){
        if (f.dataset && f.dataset.__streamHidden === '1') return;
        var s = (f.src || '').toLowerCase();
        if (s.indexOf('/ad.html') >= 0 ||
            s.indexOf('aclib') >= 0 ||
            s.indexOf('zoneid') >= 0 ||
            s.indexOf('adserver') >= 0 ||
            s.indexOf('doubleclick') >= 0 ||
            s.indexOf('googlesyndication') >= 0) {
          if (f.dataset) f.dataset.__streamHidden = '1';
          var collapse = function(){
            try {
              f.style.display = 'none';
              f.style.visibility = 'hidden';
              f.style.width = '0';
              f.style.height = '0';
            } catch(e) {}
          };
          // Wait for the iframe's own load event so any onload handlers run.
          var booted = false;
          var safety = setTimeout(function(){ if (!booted){ booted = true; collapse(); } }, 2500);
          f.addEventListener('load', function(){
            if (booted) return; booted = true;
            clearTimeout(safety);
            setTimeout(collapse, 250);
          }, { once: true });
        }
      });
    } catch(e) {}
  }
  softHideAds(); setInterval(softHideAds, 1000);

  // Try every play strategy at once. None throw on miss; we just rely on
  // *one* of them landing.
  function startPlayback(){
    // Skip the whole pass if the host has flagged a buffer underrun. The
    // C# side flips window.__streamSuppressPlay for ~3s after a
    // 'waiting'/'stalled' event so we don't slam .play() on a video
    // that's already paused waiting for buffer — that round-trips the
    // Clappr/JW state machine and re-shows the play overlay.
    if (window.__streamSuppressPlay) return;
    // 3. Global player instances. Clappr stores its instance in `window.player`
    //    on this embed. JW Player exposes a `jwplayer()` function. Other
    //    embeds use `playerInstance` etc.
    try {
      ['player','clapprPlayer','playerInstance','vid'].forEach(function(name){
        try {
          var p = window[name];
          if (p && typeof p.play === 'function') p.play();
        } catch(e) {}
      });
    } catch(e) {}
    try {
      if (typeof window.jwplayer === 'function') {
        var jw = window.jwplayer();
        if (jw && typeof jw.play === 'function') jw.play();
      }
    } catch(e) {}

    // 4. Click any visible play-button overlay. Clappr's center overlay
    //    matches .play-wrapper / [data-playpause]. JW Player uses
    //    .jw-display-icon-container. Other players have their own classes
    //    so we cast a fairly wide net via aria/title attrs too.
    try {
      var selectors = [
        '.play-wrapper', '.play-button', '[data-playpause]',
        '.play-pause-button', '.media-control-icon',
        '.jw-display-icon-container', '.jw-icon-play',
        '.vjs-big-play-button',
        'button[aria-label*=""play"" i]',
        'button[title*=""play"" i]'
      ];
      selectors.forEach(function(sel){
        document.querySelectorAll(sel).forEach(function(el){
          try {
            var r = el.getBoundingClientRect();
            if (r.width > 0 && r.height > 0) el.click();
          } catch(e) {}
        });
      });
    } catch(e) {}

    // 5. Raw video.play() on every <video> element (Clappr's underlying
    //    HTMLVideoElement, sometimes plural across nested players). ALSO
    //    apply a CSS-only compositor-layer hint (transform: translateZ(0))
    //    so chromium-efl promotes the video to its own GPU layer — that's
    //    what the HW video-hole overlay aligns against on Tizen. Without
    //    the layer promotion, audio plays but the video plane stays
    //    unpinned and invisible.
    try {
      document.querySelectorAll('video').forEach(function(v){
        try { v.muted = true; } catch(e) {}
        try {
          var p = v.play();
          if (p && p.catch) p.catch(function(){});
        } catch(e) {}
        try {
          if (!v.style.transform || v.style.transform.indexOf('translateZ') < 0) {
            v.style.transform = 'translateZ(0)';
            v.style.willChange = 'transform';
          }
        } catch(e) {}
      });
    } catch(e) {}

    // 6. Body click as a final nudge for any remaining gesture-gated overlays.
    try { document.body.click(); } catch(e) {}
  }

  // Clappr / JW load asynchronously after the page is parsed. Retry on a
  // 500 ms cadence for ~4 s so the play call lands AFTER the player is wired
  // but BEFORE the user gives up.
  startPlayback();
  var n = 0;
  var iv = setInterval(function(){
    n++;
    // Bail the whole loop the moment the host confirms first frame.
    // Without this we'd burn another ~4s of half-second .play() calls
    // on a video that's already happily playing — exactly the window
    // where a buffer underrun is most likely to be misread as 'paused'
    // and re-trigger the play overlay.
    if (window.__streamConfirmedPlaying) { clearInterval(iv); return; }
    startPlayback();
    if (n >= 8) clearInterval(iv);
  }, 500);
})();
";
            try
            {
                _webView.EvaluateJavaScript(js);
                Diagnostics.Log("WebView autoplay JS injected (Clappr-targeted)");
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("ForceAutoplay", ex);
                ReportStatus("Autoplay JS failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Re-injects the autoplay JS once per second for a few seconds. Handles embed pages
        /// that create their &lt;video&gt; element after their own bootstrap fetch finishes.
        /// </summary>
        private void ScheduleAutoplayPolling()
        {
            CancelAutoplayPollTimer();
            _autoplayPollTicks = 0;
            try
            {
                _autoplayPollTimer = new Timer(AutoplayPollIntervalMs);
                _autoplayPollTimer.Tick += OnAutoplayPollTick;
                _autoplayPollTimer.Start();
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("ScheduleAutoplayPolling", ex);
            }
        }

        private bool OnAutoplayPollTick(object source, Timer.TickEventArgs e)
        {
            // Wrap the whole tick body — ForceAutoplay() runs JS via
            // EvaluateJavaScript which can throw across the native
            // boundary mid video-element tear-down (Clappr recreates
            // its <video> ~6s in). Without this catch the exception
            // would propagate to the runtime and kill the process.
            try
            {
                _autoplayPollTicks++;
                if (_videoConfirmedPlaying) return false;
                if (_autoplayPollTicks > AutoplayPollMaxTicks) return false;
                if (_webView == null) return false;
                // Skip this tick if we're in a buffer-underrun suppression
                // window. We keep the timer alive so we resume re-injecting
                // once the video recovers (or hits the tick cap).
                if (DateTime.UtcNow < _suppressForcePlayUntil) return true;
                ForceAutoplay();
                return true;
            }
            catch (Exception ex)
            {
                try { Diagnostics.LogError("OnAutoplayPollTick", ex); } catch { }
                return false;  // stop the timer on error rather than re-throw
            }
        }

        /// <summary>
        /// Asks the WebView every <see cref="VideoPlaybackProbeMs"/> whether a video is
        /// currently playing. Updates the status banner accordingly. Stops when video is
        /// confirmed playing or after <see cref="VideoPlaybackProbeMaxTicks"/> ticks.
        /// </summary>
        private void ScheduleVideoPlaybackProbe()
        {
            CancelVideoProbeTimer();
            _videoProbeTicks = 0;
            try
            {
                _videoProbeTimer = new Timer(VideoPlaybackProbeMs);
                _videoProbeTimer.Tick += OnVideoProbeTick;
                _videoProbeTimer.Start();
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("ScheduleVideoPlaybackProbe", ex);
            }
        }

        private bool OnVideoProbeTick(object source, Timer.TickEventArgs e)
        {
            _videoProbeTicks++;
            if (_videoConfirmedPlaying) return false;
            if (_videoProbeTicks > VideoPlaybackProbeMaxTicks)
            {
                // Timed out without confirmation — leave whatever the most recent status was
                // (typically "Buffering video…" from the JS-side check below).
                return false;
            }

            // Snapshot the generation so the async callback can detect that
            // the player has been Stop()'d in the meantime and bail without
            // leaking status updates into the listing.
            int gen = _generation;

            // The official "is something playing?" hook on Tizen NUI WebView. Returns via
            // a callback — capture it as a local lambda so we can emit our diagnostic.
            //
            // The callback runs from the WebView's native side. It can fire AT
            // ANY MOMENT, including mid-page-reload or during video-element
            // tear-down + re-create (which happens in Clappr ~6s into a
            // session). We've seen NullReferenceException with no managed
            // stack at exactly that boundary — chromium-efl's video plane
            // manager surfaces a native fault as a managed NRE through the
            // delegate. We can't fix the native bug, but we can keep it
            // from killing the process: catch + log everything inside the
            // callback body so the AppDomain.UnhandledException path never
            // fires.
            try
            {
                _webView.CheckVideoPlayingAsynchronously(isPlaying =>
                {
                    try
                    {
                        if (gen != _generation)
                        {
                            // Player was closed (or restarted with a new URL) between the
                            // probe firing and the engine answering. Don't echo a stale
                            // status into whatever screen is now active.
                            return;
                        }
                        if (_webView == null) return;  // disposed mid-flight
                        if (isPlaying)
                        {
                            _videoConfirmedPlaying = true;
                            ReportStatus("Playing video");
                        }
                        else
                        {
                            // CheckVideoPlayingAsynchronously=false ONLY means
                            // the WebView's HW video plane isn't currently
                            // rendering frames. It says NOTHING about audio.
                            // The previous "Playing audio (no video)" wording
                            // was an unfounded inference — many other states
                            // (still buffering, audio-only stream, cross-
                            // origin iframe the compositor can't see, page
                            // mid-init) produce the same false. Use neutral
                            // language until we actually have ground-truth
                            // (the JS-side telemetry already captures the
                            // real video element state for offline analysis).
                            ReportStatus("Buffering video…");
                        }
                    }
                    catch (Exception cbex)
                    {
                        try { Diagnostics.LogError("CheckVideoPlayingAsync.callback", cbex); } catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("CheckVideoPlayingAsynchronously", ex);
                return false;
            }
            return true;
        }

        private void OnPageLoadError(object sender, WebViewPageLoadErrorEventArgs e)
        {
            try
            {
                string url = e != null && e.PageLoadError != null ? e.PageLoadError.Url : "(null)";
                string desc = e != null && e.PageLoadError != null ? e.PageLoadError.Description : "(null)";
                Diagnostics.Log("WebView load error: " + url + " : " + desc);
                ReportStatus("Load error: " + desc);
            }
            catch
            {
            }
        }

        // ---- Hint chip (back-to-return) -----------------------------------------------

        private void ShowHint()
        {
            try
            {
                if (_hintAnimation != null) _hintAnimation.Stop();
                _hintBg.Show();
                Animation a = new Animation(HintFadeMs);
                a.AnimateTo(_hintBg, "Opacity", 1f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
                a.Play();
                _hintAnimation = a;
            }
            catch
            {
            }
        }

        private void HideHint()
        {
            try
            {
                if (_hintAnimation != null) _hintAnimation.Stop();
                Animation a = new Animation(HintFadeMs);
                a.AnimateTo(_hintBg, "Opacity", 0f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseIn));
                a.Play();
                _hintAnimation = a;
            }
            catch
            {
            }
        }

        private void ScheduleHintAutoHide()
        {
            CancelHintTimer();
            try
            {
                _hintTimer = new Timer(HintAutoHideMs);
                _hintTimer.Tick += OnHintTimerTick;
                _hintTimer.Start();
            }
            catch
            {
            }
        }

        private bool OnHintTimerTick(object source, Timer.TickEventArgs e)
        {
            HideHint();
            CancelHintTimer();
            return false; // one-shot
        }

        private void CancelHintTimer()
        {
            try
            {
                if (_hintTimer != null)
                {
                    _hintTimer.Stop();
                    _hintTimer.Tick -= OnHintTimerTick;
                    _hintTimer.Dispose();
                    _hintTimer = null;
                }
            }
            catch
            {
            }
        }

        // ---- Status chip (top-left, mirrors StatusChanged event) ----------------------

        /// <summary>
        /// Single entry point for every "the player state changed" message — updates the
        /// on-screen status chip, fires the <see cref="StatusChanged"/> event so the host
        /// can mirror to its own banner, and writes to <see cref="Diagnostics"/>.
        /// </summary>
        private void ReportStatus(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Diagnostics.Log("PlayerWebView status: " + text); } catch { }

            try
            {
                if (_statusLabel != null) _statusLabel.Text = text;
                ShowStatus();
                ScheduleStatusAutoHide();
            }
            catch
            {
            }

            try
            {
                EventHandler<string> handler = StatusChanged;
                if (handler != null) handler(this, text);
            }
            catch
            {
            }
        }

        private void ShowStatus()
        {
            try
            {
                if (_statusAnimation != null) _statusAnimation.Stop();
                if (_statusLabel == null || string.IsNullOrEmpty(_statusLabel.Text)) return;
                _statusBg.Show();
                Animation a = new Animation(HintFadeMs);
                a.AnimateTo(_statusBg, "Opacity", 1f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
                a.Play();
                _statusAnimation = a;
            }
            catch
            {
            }
        }

        private void HideStatus()
        {
            try
            {
                if (_statusAnimation != null) _statusAnimation.Stop();
                Animation a = new Animation(HintFadeMs);
                a.AnimateTo(_statusBg, "Opacity", 0f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseIn));
                a.Play();
                _statusAnimation = a;
            }
            catch
            {
            }
        }

        private void ScheduleStatusAutoHide()
        {
            CancelStatusTimer();
            try
            {
                _statusTimer = new Timer(StatusAutoHideMs);
                _statusTimer.Tick += OnStatusTimerTick;
                _statusTimer.Start();
            }
            catch
            {
            }
        }

        private bool OnStatusTimerTick(object source, Timer.TickEventArgs e)
        {
            HideStatus();
            CancelStatusTimer();
            return false; // one-shot
        }

        private void CancelStatusTimer()
        {
            try
            {
                if (_statusTimer != null)
                {
                    _statusTimer.Stop();
                    _statusTimer.Tick -= OnStatusTimerTick;
                    _statusTimer.Dispose();
                    _statusTimer = null;
                }
            }
            catch
            {
            }
        }

        // ---- Polling timers cleanup ---------------------------------------------------

        private void CancelAutoplayPollTimer()
        {
            try
            {
                if (_autoplayPollTimer != null)
                {
                    _autoplayPollTimer.Stop();
                    _autoplayPollTimer.Tick -= OnAutoplayPollTick;
                    _autoplayPollTimer.Dispose();
                    _autoplayPollTimer = null;
                }
            }
            catch
            {
            }
        }

        private void CancelVideoProbeTimer()
        {
            try
            {
                if (_videoProbeTimer != null)
                {
                    _videoProbeTimer.Stop();
                    _videoProbeTimer.Tick -= OnVideoProbeTick;
                    _videoProbeTimer.Dispose();
                    _videoProbeTimer = null;
                }
            }
            catch
            {
            }
        }

        // Telemetry probe — passive observer only. Hooks <video> events,
        // samples videoWidth/Height at fixed checkpoints, reports
        // iframe presence + cross-origin-ness. No DOM mutation. All
        // events posted via the existing console.log bridge with the
        // __STR_TRACE__ prefix; OnPlayerConsoleMessage routes them
        // into Diagnostics.Track which ships on next launch.
        //
        // Why this is safe to layer on top of ForceAutoplay even
        // though "player changes are fragile": this script never
        // touches the page — no .play(), no .click(), no display
        // tweaks. Only addEventListener + setInterval reads. If it
        // throws inside the embed it's caught and silently dropped.
        private void InjectTelemetryProbe()
        {
            if (_webView == null) return;
            const string js = @"
(function(){
  if (window.__strTelemetryInstalled) return;
  window.__strTelemetryInstalled = true;
  function send(ev, props){
    try{
      var parts = [ev];
      if (props) for (var k in props) {
        if (!Object.prototype.hasOwnProperty.call(props, k)) continue;
        var v = (''+props[k])
          .replace(/[|\n\r\t]/g, '_')
          .slice(0, 200);
        parts.push(k+'='+v);
      }
      console.log('__STR_TRACE__:'+parts.join('|'));
    }catch(e){}
  }

  // Iframe-context spoof. Some embed bundles (pooembed.eu's
  // /js/bundle-jw.js, others) self-abort with window.self !==
  // window.top when loaded as the top frame instead of inside an
  // iframe — anti-hotlink hardening. Our resolver bypass loads
  // them as the top frame so the HW VideoHole compositor can see
  // the <video> element, which TRIGGERS the check. Override the
  // top reference so the comparison passes.
  //
  // We can only do this if the bundle hasn't executed yet. The
  // injection runs on PageLoadFinished and races against the
  // bundle's <script> download. The 655KB bundle on a slow link
  // gives us a window of ~hundreds of ms; usually enough on
  // a Tizen TV. If we lose the race, the spoof installs but the
  // bundle has already exited — observable as no `bundle_lateinit`
  // signal in subsequent telemetry.
  //
  // Targeted: only swap window.top when host is on a known list.
  // Otherwise we'd interfere with same-origin pages that depend on
  // the real window.top (most legitimate sites don't but the
  // principle of least surprise applies).
  try {
    var spoofHosts = ['pooembed.eu'];
    var hostMatches = spoofHosts.some(function(h){
      return location.hostname === h || location.hostname.endsWith('.' + h);
    });
    if (hostMatches) {
      var fakeTop = {};  // any object that is !== window
      Object.defineProperty(window, 'top', {
        get: function(){ return fakeTop; },
        configurable: false
      });
      Object.defineProperty(window, 'parent', {
        get: function(){ return fakeTop; },
        configurable: false
      });
      send('topspoof_applied', { host: location.hostname });
    }
  } catch(e) {
    send('topspoof_failed', { msg: ((e&&e.message)||'?')+'' });
  }

  send('page_loaded', { url: (''+location.href).slice(0,200) });

  // Inventory: what iframes are on the page, what's their src, can we
  // see into them? This is the most important signal for the
  // 'audio-but-no-video' bug — cross-origin iframes are exactly the
  // case where the Tizen HW VideoHole compositor can't see the video
  // element.
  function reportIframes(){
    try{
      var ifrs = document.getElementsByTagName('iframe');
      send('iframe_count', { n: ifrs.length });
      for (var i=0; i<Math.min(ifrs.length, 4); i++){
        var f = ifrs[i];
        var canSee = false;
        try { canSee = !!f.contentDocument; } catch(e) { canSee = false; }
        send('iframe', {
          i: i,
          src: ((f.src||'')+'').slice(0,200),
          allow: ((f.getAttribute('allow')||'')+'').slice(0,100),
          sandbox: ((f.getAttribute('sandbox')||'')+'').slice(0,100),
          same_origin: canSee ? '1' : '0'
        });
      }
    }catch(e){}
  }
  reportIframes();

  // Hook every <video> we can see (parent doc + same-origin iframes).
  // For cross-origin iframes, we can't reach in — the iframe report
  // above is all the signal we'll get for those.
  var hooked = new WeakSet();
  function hookVideo(v, where){
    try{
      if (hooked.has(v)) return;
      hooked.add(v);
      send('video_found', {
        where: where,
        src: ((v.currentSrc||v.src||'')+'').slice(0,200),
        autoplay: v.autoplay ? '1':'0',
        muted: v.muted ? '1':'0',
        readyState: v.readyState
      });
      var evs = ['play','playing','pause','waiting','stalled','loadedmetadata','loadeddata','canplay','ended','emptied'];
      evs.forEach(function(name){
        v.addEventListener(name, function(){
          send('video_'+name, {
            w: v.videoWidth, h: v.videoHeight,
            ct: v.currentTime.toFixed(2), rs: v.readyState,
            paused: v.paused ? '1':'0'
          });
        }, true);
      });
      v.addEventListener('error', function(){
        var err = v.error || {};
        send('video_error', {
          code: (err.code||'?'),
          msg: (((err.message||'')+'').slice(0,120))
        });
      }, true);
    }catch(e){}
  }
  function scanVideos(){
    try{
      Array.prototype.forEach.call(document.querySelectorAll('video'), function(v){ hookVideo(v, 'parent'); });
      Array.prototype.forEach.call(document.querySelectorAll('iframe'), function(f){
        try { var d = f.contentDocument; if (!d) return;
          Array.prototype.forEach.call(d.querySelectorAll('video'), function(v){ hookVideo(v, 'iframe'); });
        } catch(e) {}
      });
    }catch(e){}
  }
  scanVideos();
  // Re-scan on DOM changes — Clappr inserts the <video> element
  // after a beat, well after PageLoadFinished fires.
  try {
    new MutationObserver(scanVideos).observe(document.documentElement, { childList:true, subtree:true });
  } catch(e) {}

  // Periodic state samples at fixed checkpoints. Captures the
  // signature of 'audio plays, video stays 0×0' as data points the
  // server-side reader can read at a glance.
  var checkpoints = [1000, 3000, 5000, 10000, 20000, 45000];
  checkpoints.forEach(function(ms){
    setTimeout(function(){
      try {
        var vids = document.querySelectorAll('video');
        if (!vids.length) { send('sample', { t: ms, has_video: '0' }); return; }
        for (var i=0; i<Math.min(vids.length,3); i++){
          var v = vids[i];
          send('sample', {
            t: ms, i: i,
            w: v.videoWidth, h: v.videoHeight,
            ct: v.currentTime.toFixed(2), rs: v.readyState,
            paused: v.paused ? '1':'0'
          });
        }
      } catch(e){}
    }, ms);
  });

  // ---- HLS.js hook ----------------------------------------------------
  // The library at window.Hls is just a class; the actual instance is
  // created by the embed page and stored privately. To catch its
  // events we (a) wrap the constructor so any FUTURE instances get
  // listeners attached, and (b) probe a few well-known locations
  // (window.hls, Clappr's container) for any EXISTING instance.
  // Both run on a couple of delays since Clappr re-creates its
  // playback engine ~6s into a session (we observed 'video_found'
  // appearing twice in the trace).
  function hookHlsInstance(hls, where){
    try {
      if (!hls || !hls.on || hls.__strHooked) return;
      hls.__strHooked = true;
      var E = (hls.constructor && hls.constructor.Events) || (window.Hls && window.Hls.Events);
      if (!E) return;
      send('hls_hooked', { where: where });
      hls.on(E.ERROR, function(ev, data){
        try {
          send('hls_error', {
            where: where,
            type: (data && data.type) || '?',
            details: (data && data.details) || '?',
            fatal: (data && data.fatal) ? '1':'0',
            url: ((data && (data.url || (data.context && data.context.url) || (data.frag && data.frag.url) || ''))+'').slice(0,200),
            code: (data && data.response && data.response.code) || '',
            reason: ((data && (data.reason || (data.error && data.error.message) || ''))+'').slice(0,120)
          });
        } catch(e){}
      });
      hls.on(E.MANIFEST_LOADED, function(ev, data){
        send('hls_manifest_loaded', { where: where, levels: ((data && data.levels)||[]).length });
      });
      hls.on(E.MANIFEST_PARSED, function(ev, data){
        try {
          var lvls = (data && data.levels) || [];
          var codecs = lvls.map(function(l){ return (l && (l.codecs||l.codec_set||l.attrs && l.attrs.CODECS))||'?'; }).slice(0,3);
          send('hls_manifest_parsed', {
            levels: lvls.length,
            codecs: codecs.join(',').slice(0,150),
            audio_groups: (((data && data.audioTracks) || []).length)
          });
        } catch(e){}
      });
      hls.on(E.MEDIA_ATTACHED, function(){ send('hls_media_attached', { where: where }); });
      hls.on(E.LEVEL_LOADED, function(ev, data){
        send('hls_level_loaded', { where: where, level: (data && data.level), live: (data && data.details && data.details.live) ? '1':'0' });
      });
      // Only emit the FIRST fragment-loaded — confirms 'bytes flowed
      // at least once'. Subsequent frags would be too noisy.
      var firstFragSent = false;
      hls.on(E.FRAG_LOADED, function(ev, data){
        if (firstFragSent) return; firstFragSent = true;
        send('hls_first_frag', {
          where: where,
          url: (((data && data.frag && data.frag.url) || '')+'').slice(0,200),
          duration: ((data && data.frag && data.frag.duration) || 0).toFixed(2)
        });
      });
    } catch(e){}
  }
  function wrapHlsCtor(){
    try {
      if (!window.Hls || window.__strHlsCtorWrapped) return;
      window.__strHlsCtorWrapped = true;
      var Original = window.Hls;
      send('hls_present', { ver: (Original.version||'?')+'' });
      // Observation-only: capture window.Hls existence + version
      // but do NOT replace it. The previous wrap-and-tune-config
      // iteration broke the wrapper page own player loading
      // (Error 104153 from JW Player) without ever catching the
      // bundle internal HLS instance — bundles import HLS.js
      // from local scope, not via window.Hls. Bigger interventions
      // (config tuning, error subscription on instances) need a
      // different approach (e.g. WebView-level settings) — JS
      // wrapping is not reachable for these embeds.
    } catch(e){ send('hls_wrap_failed', { msg: ((e&&e.message)||'?')+'' }); }
  }
  function findExistingHls(){
    try {
      if (window.hls) hookHlsInstance(window.hls, 'window.hls');
      if (window.player) {
        if (window.player.hls) hookHlsInstance(window.player.hls, 'player.hls');
        try {
          var c = window.player.core && window.player.core.activeContainer;
          if (c && c.playback) {
            // Clappr playback engine wraps hls.js as ._hls (older) or .hls (newer)
            if (c.playback._hls) hookHlsInstance(c.playback._hls, 'clappr_hls_under');
            if (c.playback.hls) hookHlsInstance(c.playback.hls, 'clappr_hls');
          }
        } catch(e){}
      }
    } catch(e){}
  }
  // MediaSource codec support — independent of hls.js. Tells us
  // immediately whether the device claims support for the codec
  // string the m3u8 advertises (HEVC / AC3 are common rejection
  // points on older Tizen builds).
  function reportCodecs(){
    try {
      if (!window.MediaSource) { send('mse_unsupported', {}); return; }
      var probe = ['avc1.42E01E,mp4a.40.2','avc1.4D401E,mp4a.40.2','hvc1.1.6.L93.B0','hev1.1.6.L93.B0','vp09.00.10.08'];
      var supported = probe.filter(function(c){ try { return MediaSource.isTypeSupported('video/mp4; codecs=""'+c+'""'); } catch(e){ return false; } });
      send('mse_support', { ok: supported.join('|') });
    } catch(e){}
  }
  reportCodecs();
  wrapHlsCtor();
  findExistingHls();
  setTimeout(function(){ wrapHlsCtor(); findExistingHls(); }, 1500);
  setTimeout(function(){ wrapHlsCtor(); findExistingHls(); }, 5000);
  setTimeout(function(){ findExistingHls(); }, 12000);
})();";
            try { _webView.EvaluateJavaScript(js); }
            catch (Exception ex) { Diagnostics.LogError("InjectTelemetryProbe", ex); }
        }

        private static string TruncForTelemetry(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, max);
        }

        // Returns "scheme://host" for the input URL, or empty on
        // parse failure. Used to derive the Referer we spoof when
        // bypassing a wrapper — pooembed et al. only check the
        // origin portion, not the full path.
        private static string ExtractOrigin(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            try
            {
                Uri u = new Uri(url, UriKind.Absolute);
                return u.Scheme + "://" + u.Host;
            }
            catch
            {
                return "";
            }
        }
    }
}
