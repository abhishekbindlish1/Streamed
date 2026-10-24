using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StreamedTv.Models;
using StreamedTv.Platforms.Tizen;
using StreamedTv.Services;
using StreamedTv.Theme;
using StreamedTv.Views;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace StreamedTv
{
    internal sealed class Program : NUIApplication
    {
        private readonly StreamedCatalogService _catalog = new StreamedCatalogService();
        // BackendAuthService MUST be declared before ProfileService /
        // UserPreferences so the latter two can take a reference to it
        // (or to ProfileService) in the constructor body. C# runs field
        // initializers in textual order; both are constructed in the
        // Program() ctor below, after _backendAuth is ready.
        private readonly BackendAuthService _backendAuth = new BackendAuthService();
        private readonly ProfileService _profiles;
        private readonly UserPreferences _prefs;
        private readonly StreamedTv.Services.FocusManager _focus = new StreamedTv.Services.FocusManager();
        private readonly IStreamPlayer _player = new TizenStreamPlayer();
        private readonly SportsDataService _sportsData = new SportsDataService();
        // Remote config (streamed.pk base URL + feature flags). Constructed
        // here, but Load + StartBackgroundRefresh are deferred to OnCreate
        // because we need Tizen.Applications.Application.Current.DirectoryInfo.Data
        // for disk persistence and that's null until base.OnCreate runs.
        // Reuses _sportsData.BaseUrl so a dev-time backend-url.txt override
        // is honored consistently for /v1/config too.
        private readonly AppConfigService _appConfig;
        // Pre-resolves wrapper-host stream URLs (embedsports.top et al.) to
        // our self-hosted hls.js wrapper so chromium-efl never loads the
        // crash-prone Cloudflare wrapper page. Gated by useBackendStreamProxy
        // remote flag; default-false in code so a config-fetch failure on
        // first launch leaves behaviour byte-identical to the old path.
        private readonly StreamExtractorService _streamExtractor;
        private LiveScoreService _liveScores;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();

        private readonly List<Sport> _sports = new List<Sport>();
        private readonly List<CatalogMatch> _liveMatches = new List<CatalogMatch>();
        private readonly List<CatalogMatch> _scheduledMatches = new List<CatalogMatch>();
        private readonly List<CatalogMatch> _recentMatches = new List<CatalogMatch>();

        private View _root;
        private View _bgGradient;
        private NavPillView _navPill;
        private HeroView _hero;
        private RowView _liveRow;
        private RowView _scheduledRow;
        private RowView _recentRow;
        private MoreOverlayView _moreOverlay;
        // ---- IPTV (iptv-org) test path ----------------------------------
        // Standalone Live TV overlay opened on the Red remote button.
        // Slated for removal once the iptv-org experiment wraps up.
        private IptvChannelsOverlayView _iptvOverlay;
        private IptvHomeView _iptvHome;
        // True while focus is INSIDE _iptvHome (vs in the nav pill above
        // it while the surface is still visible). Drives the key router.
        // Decoupled from _iptvHome.IsActive so the surface stays on
        // screen as the user walks focus up to nav and back down.
        private bool _iptvHomeFocusOwned;
        private readonly StreamedTv.Services.IptvCatalogService _iptvCatalog = new StreamedTv.Services.IptvCatalogService();
        private StreamSwitcherView _streamSwitcher;
        private TeamHubView _teamHub;
        private ProfileSelectorView _profileSelector;
        private LoginView _loginView;
        // ---- Profile-management surfaces (Netflix-style) -------------------
        // Chip lives in the topbar (always visible). Other surfaces are
        // overlays added to the layer in BuildScene. Keyboard + avatar picker
        // are reusable sub-overlays the editor + add-wizard request via
        // host-routed events (see OpenKeyboardForName / OpenAvatarPicker).
        private ProfileChipView _profileChip;
        private ProfileSwitcherDropdownView _profileDropdown;
        private ProfileEditView _profileEdit;
        private ProfileAddWizardView _profileAddWizard;
        private AvatarPickerView _avatarPicker;
        private OnScreenKeyboardView _keyboard;
        // True when keyboard navigation has stolen focus from the nav pill
        // and parked it on the top-right profile chip. Up from any nav cell
        // sets this true; Down/Left/Back/OK clear it (or open the dropdown).
        private bool _profileChipFocused;
        // One-shot subscriptions held so OpenKeyboardForName / OpenAvatarPicker
        // can swap callbacks without leaking handlers across opens.
        private EventHandler<string> _keyboardSubmittedHandler;
        private EventHandler _keyboardCancelledHandler;
        private EventHandler<string> _avatarPickedHandler;
        private EventHandler _avatarCancelledHandler;
        // Status banner field removed when the on-screen banner above
        // the hero was dropped — see BuildScene + SetStatus.
        private PlayerWebView _playerView;
        private bool _playerVisible;

        // Full-screen "Resolving stream…" overlay shown during the ~12-20s
        // chromedp extract on the proxy path. Sits ABOVE PlayerWebView in
        // z-order so the user sees something happen instead of the listing
        // freezing for 15s. Hidden as soon as PlayerWebView's LoadStream
        // gets called — at that point the WebView is bringing in the
        // wrapper page and any further loading state is its responsibility.
        private View _loadingOverlay;
        private TextLabel _loadingOverlayLabel;
        // True between ShowLoadingOverlay and HideLoadingOverlay. Used by
        // the key router to swallow input + treat Back as "cancel the
        // pending extract" instead of letting key presses leak through to
        // whatever surface is behind the (technically transparent-ish)
        // overlay. Without this the user could pick another stream while
        // the previous extract is still running, which races the two
        // ShowPlayerView invocations against each other.
        private bool _loadingOverlayVisible;
        // Native NUI VideoView player. Used ONLY when StreamExtractorService
        // successfully resolved a wrapper-host stream into a raw m3u8 URL —
        // we hand that URL directly to a hardware HLS pipeline instead of
        // round-tripping through the hls.js wrapper page in PlayerWebView.
        // Direct streams + extraction failures keep the existing
        // PlayerWebView code path byte-for-byte (project memory: PlayerWebView
        // is fragile — additive only).
        private NativePlayerView _nativePlayer;
        private bool _nativePlayerVisible;
        private Animation _nativePlayerCrossfade;
        // Source string of the most recently played stream — set in
        // every site that calls _playerView.LoadStream so OnPlayerExitRequested
        // can decide whether to reopen the StreamSwitcher (sports) or skip
        // it and let the underlying view (IPTV home) take focus naturally.
        // Empty string ⇒ no source recorded ⇒ default to sports behavior.
        private string _lastPlayedSource = "";
        private Animation _playerCrossfade;

        private string _activeSportId = "cricket";

        // Two-presses-to-exit on nav pill. First Back shows a toast; second
        // Back within BackToExitWindowSeconds actually closes the app. Reset
        // any time the user navigates back into content.
        private DateTime _lastBackOnNavAt = DateTime.MinValue;
        private const double BackToExitWindowSeconds = 3.0;

        // Static topbar height (brand wordmark + nav pill, both anchored top).
        // Sized for the larger nav-pill chips (18 pt + 16/38 padding = ~80 px tall)
        // plus 32 px top margin and 24 px bottom breathing room.
        private const int TopbarHeight = 132;

        // Hero card runs nearly corner-to-corner. _root only pads HeroEdgeBreathing
        // px on each side; rows + status banner add a small extra indent via Margin
        // so the section labels don't pinch against the screen edge but the rows
        // still feel "edge to edge" alongside the hero.
        private const int HeroEdgeBreathing = 30;
        private const int RowSideIndent = 12;

        public Program()
        {
            // _backendAuth is field-init'd above this, so it's already
            // constructed by the time we get here. Wiring ProfileService
            // with it lets profile mutations sync to Firestore once the
            // user has a JWT (see ProfileService.LoadFromBackendAsync +
            // the FireAndForget* helpers). UserPreferences gets a
            // reference to ProfileService so favourite mutations sync
            // through to the profile doc on the backend.
            _profiles = new ProfileService(_backendAuth);
            _prefs = new UserPreferences(_profiles);
            // _sportsData is field-init'd above us, so its resolved
            // BaseUrl (which honors backend-url.txt) is available now.
            _appConfig = new AppConfigService(_sportsData.BaseUrl);
            // Pre-resolve wrapper URLs to our backend's hls.js page when the
            // useBackendStreamProxy flag is on. See StreamExtractorService.
            _streamExtractor = new StreamExtractorService(_sportsData.BaseUrl, _appConfig);
        }

        protected override void OnCreate()
        {
            try
            {
                base.OnCreate();
                Diagnostics.Init();
                Diagnostics.Log("OnCreate begin");
                // Wire AppDomain + Task unhandled-exception capture
                // EARLY so any subsequent OnCreate failure becomes a
                // shippable crash record. Idempotent.
                StreamedTv.Services.CrashReporter.Init();
                RegisterFonts();
                // Hydrate persisted JWT from disk NOW — BackendAuthService
                // was constructed as a field initializer (before
                // base.OnCreate), at which point Tizen.Applications.Application.Current
                // is null and the auth.json path can't be resolved.
                // Without this call, every restart looked like a fresh
                // install (the user was prompted to re-pair on every
                // launch even though their JWT was sitting on disk).
                _backendAuth.ReloadJwtFromDisk();
                Diagnostics.Log("OnCreate: backend auth reloaded, IsAuthenticated=" + _backendAuth.IsAuthenticated);
                // Hydrate persisted remote-config from disk NOW so the
                // very first /api/matches/... request below uses the
                // last-known-good streamed.pk origin (and any feature
                // flags) instead of the compile-time default. Disk
                // access requires DirectoryInfo.Data which is null
                // before base.OnCreate — same pattern as _backendAuth.
                _appConfig.LoadFromDiskAndApply();
                Diagnostics.Log("OnCreate: app config loaded from disk, baseUrl=" + (_appConfig.Current != null ? _appConfig.Current.StreamedBaseUrl : "null"));
                // Heartbeat telemetry: guarantees the pending-crashes
                // file has at least ONE entry per app session so the
                // next launch's ShipPendingAsync has something to POST.
                // Without this, a clean session (no errors, no streams
                // opened) produces an empty file and we never exercise
                // the shipping pipeline at all — making it look like
                // crash reporting is broken when actually nothing
                // happened to report.
                StreamedTv.Services.Diagnostics.Track("session_start",
                    new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["authed"] = _backendAuth.IsAuthenticated ? "1" : "0",
                    });
                // Ship any pending crash logs from a previous run to
                // the backend (POST /v1/crash-logs) so we can debug
                // remote TVs without USB sdb. Fire-and-forget, bounded
                // timeout — auth must be present (the endpoint is
                // auth-required so we'd 401 otherwise).
                _ = StreamedTv.Services.CrashReporter.ShipPendingAsync(_backendAuth, _shutdown.Token);
                // Hydrate persisted prefs from disk NOW — UserPreferences was
                // constructed as a field initializer (before base.OnCreate), at
                // which point Tizen.Applications.Application.Current is null and
                // the data dir can't be resolved. Without this call, every restart
                // looked like a fresh install (favorites silently dropped).
                _profiles.Reload();
                // Hydrate persisted prefs from disk NOW — UserPreferences was
                // constructed as a field initializer (before base.OnCreate), at
                // which point Tizen.Applications.Application.Current is null and
                // the data dir can't be resolved. Without this call, every restart
                // looked like a fresh install (favorites silently dropped).
                // Pass the active profile id so the per-profile favorites file
                // gets loaded (each profile has its own file under
                // {data}/profiles/{id}/favorites.json).
                _prefs.Reload(_profiles.ActiveId);
                // Hydrate the poster cache from disk so subsequent runs reuse
                // previously-downloaded posters/badges instead of re-fetching
                // them. Requires the Tizen app context (data dir) so we call
                // it after base.OnCreate.
                StreamedTv.Services.PosterCache.Init();
                // Decide the initial active tab:
                //   * If the user has favourited any teams, For You will have
                //     meaningful content — land there.
                //   * Otherwise fall back to the user's last-viewed sport
                //     (or cricket as the first-launch default).
                // Earlier code always used LastSelectedSport, which meant a
                // fresh install with empty favourites flashed "no matches" on
                // For You for a moment before swapping over to cricket.
                bool hasFavouritedTeams = _prefs.FavoriteTeams != null && _prefs.FavoriteTeams.Count > 0;
                if (hasFavouritedTeams)
                {
                    _activeSportId = ForYouSportId;
                }
                else
                {
                    _activeSportId = string.IsNullOrEmpty(_prefs.LastSelectedSport)
                        ? "cricket"
                        : _prefs.LastSelectedSport;
                }
                // Seed the per-sport palette before BuildScene so HeroView, NavPill,
                // MoreOverlay, and the background gradient view all see the right
                // accent at first paint. Subsequent calls happen in OnNavSelected
                // when the user pins a different sport.
                // For You doesn't carry a sport tint — pass null so the palette
                // reverts to the default (BgBase) chrome.
                StreamedTv.Theme.SportPalette.SetCurrent(
                    _activeSportId == ForYouSportId ? null : _activeSportId);
                // Force the For You collage banner whenever the For You tab is
                // active. Cleared on a sport tab so per-sport bundled banners
                // (hero_football.jpg etc.) take over.
                StreamedTv.Views.HeroView.ForceBundledHero(
                    _activeSportId == ForYouSportId ? "hero_foryou.jpg" : null);
                BuildScene();

                // Pre-paint the nav pill + show the loading skeleton on the rows
                // BEFORE the catalog fetch returns. Without this, the rows render
                // empty ("No matches") for the half-second the network takes —
                // the user sees that as "broken" rather than "loading".
                List<string> initialFavs = new List<string>(_prefs.FavoriteSports);
                while (initialFavs.Count < 3) initialFavs.Add("");
                _navPill.SetFavorites(initialFavs);
                ApplyActiveChipFromActiveSport();
                ShowLoadingState(_activeSportId == ForYouSportId ? "For You" : _activeSportId);

                Window.Instance.KeyEvent += OnKeyEvent;
                _focus.FocusChanged += OnFocusChanged;
                _focus.TilePromoted += OnTilePromoted;
                _ = LoadInitialCatalogAsync();
                _ = PingSportsDataAsync();
                // Start the background remote-config refresh AFTER kicking
                // the catalog load — the disk-cached config we already
                // applied above is good enough for this session's first
                // request; the background loop just keeps it fresh and
                // catches operator changes without a TV restart.
                _appConfig.StartBackgroundRefresh(_shutdown.Token);
                // StartLiveScorePolling(); — disabled; RapidAPI quota exhausted.
                StreamedTv.Services.FavoritesRegistry.Set(_prefs);

                // Pre-open the auth-gate's eventual surface SYNCHRONOUSLY
                // so the home screen never paints uncovered between
                // BuildScene's first frame and the async auth gate
                // opening the picker / login. Without this, restarting
                // a logged-in app showed a brief flash of the sports
                // listing before the picker faded in.
                //   * No JWT → pre-open LoginView (covers home).
                //   * JWT + cached profiles → pre-open the picker.
                //   * JWT but no cached profiles → can't tell yet
                //     whether to open picker or wizard; leave home for
                //     the brief async window. Only happens on a fresh
                //     install or after an explicit Sign out, both of
                //     which are rare.
                // ShowAuthGateAsync below is idempotent against an
                // already-open surface (Open()/RaiseToTop() short-
                // circuit when IsOpen).
                if (!_backendAuth.IsAuthenticated)
                {
                    _loginView.Open();
                    _loginView.RaiseToTop();
                }
                else if (_profiles.HasProfiles)
                {
                    _profileSelector.SetProfiles(_profiles.Profiles, _profiles.ActiveId);
                    _profileSelector.Open();
                    _profileSelector.RaiseToTop();
                }

                // Show the profile selector on every launch ("Who's
                // watching?" — Netflix/Disney+ idiom). The catalog fetch
                // already kicked off above so by the time the user picks a
                // profile, the rows are usually already populated for the
                // initial profile. Picking a different one re-runs the
                // load via OnProfileSelected.
                //
                // Auth gate: if the local JWT is missing or rejected by
                // the backend (/v1/me), we show LoginView FIRST. The
                // login flow's LoggedIn event then transitions to the
                // profile picker via OnLoginCompleted.
                _ = ShowAuthGateAsync();
                Diagnostics.Log("OnCreate end");
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnCreate FAILED", ex);
                ShowCrashScreen("OnCreate", ex);
            }
        }

        private void RegisterFonts()
        {
            try
            {
                string fontDir = System.IO.Path.Combine(
                    Tizen.Applications.Application.Current.DirectoryInfo.Resource,
                    "Fonts");
                if (!System.IO.Directory.Exists(fontDir))
                {
                    Diagnostics.Log("Font dir missing at " + fontDir);
                    return;
                }

                Tizen.NUI.FontClient fc = Tizen.NUI.FontClient.Instance;
                bool addOk = fc.AddCustomFontDirectory(fontDir);
                Diagnostics.Log("AddCustomFontDirectory(" + fontDir + ") -> " + addOk);

                // API9's public FontClient surface doesn't expose a family-enumeration call
                // (GetSystemFonts / GetDescription are interop-only). The next-best signal is
                // GetFontId(path) per file: a non-zero ID means FreeType successfully opened the
                // font face. If a file is on disk but ID == 0, the TTF itself is the problem
                // (variable-axis quirk, corruption, etc.) — not the FontFamily token.
                string[] expected = new[]
                {
                    "Inter-Regular.ttf",
                    "Inter-Medium.ttf",
                    "Inter-SemiBold.ttf",
                    "Inter-Bold.ttf",
                };
                foreach (string name in expected)
                {
                    string p = System.IO.Path.Combine(fontDir, name);
                    bool exists = System.IO.File.Exists(p);
                    long size = exists ? new System.IO.FileInfo(p).Length : -1;
                    uint id = 0;
                    bool scalable = false;
                    string err = null;
                    if (exists)
                    {
                        try { id = fc.GetFontId(p); } catch (Exception fx) { err = fx.GetType().Name + ": " + fx.Message; }
                        try { scalable = fc.IsScalable(p); } catch { }
                    }
                    Diagnostics.Log("Font probe " + name + " exists=" + exists + " size=" + size + " fontId=" + id + " scalable=" + scalable + (err == null ? "" : " err=" + err));
                }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("RegisterFonts", ex);
            }
        }

        private void ShowCrashScreen(string phase, Exception ex)
        {
            try
            {
                Window.Instance.BackgroundColor = new Color(0.1f, 0f, 0f, 1f);
                Layer layer = Window.Instance.GetDefaultLayer();
                TextLabel label = new TextLabel
                {
                    Text = "CRASH IN " + phase + "\n\n" + (ex == null ? "(null)" : ex.GetType().Name + ": " + ex.Message + "\n\n" + ex.StackTrace),
                    TextColor = new Color(1f, 0.6f, 0.6f, 1f),
                    PointSize = 14f,
                    MultiLine = true,
                    HorizontalAlignment = HorizontalAlignment.Begin,
                    VerticalAlignment = VerticalAlignment.Top,
                    WidthResizePolicy = ResizePolicyType.FillToParent,
                    HeightResizePolicy = ResizePolicyType.FillToParent,
                    Padding = new Extents(40, 40, 40, 40),
                };
                layer.Add(label);
            }
            catch
            {
            }
        }

        private void BuildScene()
        {
            Diagnostics.Log("BuildScene begin");
            Window.Instance.BackgroundColor = Tokens.BgBase;
            Layer layer = Window.Instance.GetDefaultLayer();
            Size2D windowSize = Window.Instance.WindowSize;

            // ---- App-wide background gradient -----------------------------------
            // Vertical wash from BgGradientTop (near-black) at the top to
            // BgGradientBottom (sport-tinted dark) at the bottom. Added FIRST so
            // it sits behind topbar + _root. Subscribes to SportPalette.Changed
            // and re-applies the gradient PropertyMap whenever the user pins a
            // different sport — this is what gives the entire app a sport-themed
            // mood that mirrors the bundled hero banner.
            _bgGradient = new View
            {
                BackgroundColor = Tokens.BgBase,
            };
            _bgGradient.WidthResizePolicy = ResizePolicyType.FillToParent;
            _bgGradient.HeightResizePolicy = ResizePolicyType.FillToParent;
            _bgGradient.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            _bgGradient.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            _bgGradient.Position = new Position(0f, 0f);
            ApplyBackgroundGradient(StreamedTv.Theme.SportPalette.Current);
            StreamedTv.Theme.SportPalette.Changed += (s, p) => ApplyBackgroundGradient(p);
            layer.Add(_bgGradient);

            // ---- Static topbar (brand + nav pill) -------------------------------
            // Lives DIRECTLY on the layer so the row-pan logic (which slides _root
            // up when focus enters lower rows) doesn't push the brand and nav off
            // the top of the screen — earlier the brand and nav pill bled into
            // each other / drifted on every Down keypress.
            //
            // Use EXPLICIT pixel sizes (not LayoutParamPolicies.MatchParent). The
            // layer has no Layout, so MatchParent collapses children to 0×0 — that
            // was the regression that made the whole screen go blank. Sibling siblings
            // of the layer must be sized explicitly or via ResizePolicyType.FillToParent.
            View topbar = new View
            {
                // Opaque BgBase so content from _root (hero, rows) panning
                // upward doesn't bleed through behind the brand/nav pill.
                // The topbar gets raised above _root in z-order below
                // (RaiseAbove call) so it physically clips the panning
                // content. Netflix-style: content slides under a solid
                // top bar instead of being visible through it.
                BackgroundColor = Tokens.BgBase,
            };
            topbar.WidthResizePolicy = ResizePolicyType.FillToParent;
            topbar.HeightResizePolicy = ResizePolicyType.Fixed;
            topbar.SizeHeight = TopbarHeight;
            topbar.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            topbar.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            topbar.Position = new Position(0f, 0f);
            layer.Add(topbar);

            // Brand icon — pinned top-right of the static topbar. Replaces the
            // earlier "STREAMED" wordmark.
            //
            // We use TopLeft pivot/origin with an EXPLICIT X computed from the
            // window width, NOT TopRight pivot with a negative X. NUI's
            // negative-Position-against-TopRight-pivot path was unreliable
            // here — kept rendering the brand half-clipped past the right
            // edge regardless of Size or WidthSpecification settings. The
            // explicit window-width math is bulletproof on every device.
            const int BrandLogoSize = 56;
            const int BrandRightInset = 40;
            string brandLogoPath = StreamedTv.Views.HeroView.ResolveBundledAssetPath("brand_logo.jpg");
            ImageView brand = new ImageView
            {
                ResourceUrl = brandLogoPath,
                FittingMode = FittingModeType.ScaleToFill,
                DesiredWidth = BrandLogoSize * 2,
                DesiredHeight = BrandLogoSize * 2,
            };
            brand.Size = new Size(BrandLogoSize, BrandLogoSize);
            brand.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            brand.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            brand.Position = new Position(
                windowSize.Width - BrandLogoSize - BrandRightInset,
                28f);
            topbar.Add(brand);

            // Nav pill — centered horizontally inside the static topbar via a
            // LinearLayout container that explicitly fills the topbar's bounds.
            View navRow = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            navRow.WidthResizePolicy = ResizePolicyType.FillToParent;
            navRow.HeightResizePolicy = ResizePolicyType.FillToParent;
            navRow.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            navRow.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            navRow.Position = new Position(0f, 0f);
            topbar.Add(navRow);

            _navPill = new NavPillView();
            navRow.Add(_navPill);

            // Top-LEFT profile chip — always-visible Netflix-style affordance
            // for switching profiles from inside the app. Lives in the topbar
            // (NOT _root) so it doesn't pan when the user scrolls into lower
            // rows. Anchored top-left with a small inset; pressing Left from
            // the leftmost nav cell focuses it (see OnKeyEvent).
            _profileChip = new ProfileChipView();
            _profileChip.SetActiveProfile(_profiles.Active);
            _profileChip.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            _profileChip.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            _profileChip.Position = new Position(32f, 28f);
            _profileChip.Activated += OnProfileChipActivated;
            topbar.Add(_profileChip);

            // ---- Scrolling root (hero + rows) ----------------------------------
            // Sits BELOW the static topbar. Pans up/down when focus enters lower
            // rows; topbar stays put because it's a sibling, not a child.
            //
            // Width/height set EXPLICITLY (not via LayoutParamPolicies.MatchParent —
            // the layer has no Layout, see comment on `topbar` above).
            //
            // Horizontal padding here is intentionally SMALL (HeroEdgeBreathing = 30).
            // The hero card sits flush against this small margin so it runs nearly
            // corner-to-corner. Other children (status banner, rows) opt back into
            // the original PageHorizontalPadding indent via a per-child Margin —
            // see _liveRow / _scheduledRow / _recentRow / _statusBanner below.
            _root = new View
            {
                // Transparent — the app-wide background gradient (_bgGradient,
                // added to the layer earlier) is what paints behind the hero +
                // rows. A solid BgBase here would cover that gradient.
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    LinearAlignment = LinearLayout.Alignment.Top,
                    CellPadding = new Size2D(0, 18),
                    Padding = new Extents(
                        (ushort)HeroEdgeBreathing,
                        (ushort)HeroEdgeBreathing,
                        12,
                        24),
                },
            };
            _root.WidthResizePolicy = ResizePolicyType.Fixed;
            _root.HeightResizePolicy = ResizePolicyType.Fixed;
            _root.SizeWidth = windowSize.Width;
            _root.SizeHeight = windowSize.Height + 600f;  // extra room so rows below the fold can pan into view
            _root.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            _root.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            _root.Position = new Position(0f, TopbarHeight);
            layer.Add(_root);

            // Push topbar above _root in z-order so the opaque topbar BG
            // physically clips _root's content as it pans upward (hero
            // sliding under the nav pill, etc.). Overlays added later in
            // BuildScene end up above topbar via the natural append order;
            // they also call RaiseToTop in their Open() handlers when shown.
            try { topbar.RaiseAbove(_root); } catch { }

            // Status banner removed — it was a stop-gap for debugging
            // sport-data API issues on a TV where we couldn't see logs.
            // Now that crash-log shipping is wired (POST /v1/crash-logs)
            // we get real telemetry without taking up screen real estate.
            // SetStatus calls below are kept (no-op) so we don't have to
            // sweep every call site; they still flow into Diagnostics
            // logs.

            // Hero card — explicitly NO horizontal margin so it inherits only the
            // small HeroEdgeBreathing pad from _root and runs nearly edge-to-edge.
            _hero = new HeroView();
            _root.Add(_hero);

            // Three rows. Each row carries its own Margin so the row content stays
            // aligned with the original PageHorizontalPadding column even though
            // _root itself is now near-flush with the screen edges.
            _liveRow = new RowView("Live Now", new List<CatalogMatch>(), TileView.TileKind.Live);
            _liveRow.Margin = new Extents(RowSideIndent, RowSideIndent, 0, 0);
            _root.Add(_liveRow);

            _scheduledRow = new RowView("Coming Up", new List<CatalogMatch>(), TileView.TileKind.Scheduled);
            _scheduledRow.Margin = new Extents(RowSideIndent, RowSideIndent, 0, 0);
            _root.Add(_scheduledRow);

            _recentRow = new RowView("Recently Finished", new List<CatalogMatch>(), TileView.TileKind.Recent);
            _recentRow.Margin = new Extents(RowSideIndent, RowSideIndent, 0, 0);
            _root.Add(_recentRow);

            // Inline Live-TV surface — sits in _root next to hero +
            // sport rows but starts collapsed (height 0). When the
            // "● Live" nav chip becomes active we swap visibility:
            // hide hero + sport rows, expand _iptvHome to fill the
            // slot. Keeps the home-screen chrome (nav pill, padding)
            // identical and reuses the same vertical LinearLayout
            // reflow trick as ApplyRowVisibility.
            _iptvHome = new IptvHomeView();
            _iptvHome.Margin = new Extents(RowSideIndent, RowSideIndent, 0, 0);
            _iptvHome.ChannelActivated += OnIptvChannelActivated;
            _iptvHome.FocusEscapeUp += OnIptvHomeFocusEscapeUp;
            _root.Add(_iptvHome);

            // More overlay sits on top of everything else and is hidden by default.
            _moreOverlay = new MoreOverlayView();
            _moreOverlay.SportPinned += OnSportPinned;
            _moreOverlay.Closed += (s, e) => { /* nothing extra */ };
            layer.Add(_moreOverlay);

            // Live TV (iptv-org) channel grid — opens on Red remote
            // button. Standalone overlay; doesn't touch the main
            // home-screen surfaces. See IptvChannelsOverlayView for
            // the full surface contract. When a channel is
            // activated we hand its play URL to PlayerWebView via
            // a synthesized StreamOption (PlayerWebView treats it
            // exactly like any other stream wrapper page).
            _iptvOverlay = new IptvChannelsOverlayView();
            _iptvOverlay.ChannelActivated += OnIptvChannelActivated;
            _iptvOverlay.Closed += (s, e) => CloseIptvOverlay();
            layer.Add(_iptvOverlay);

            // Stream selector — modal overlay shown before launching the WebView player.
            _streamSwitcher = new StreamSwitcherView();
            _streamSwitcher.StreamSelected += OnStreamSelected;
            _streamSwitcher.TeamFavoriteToggled += OnTeamFavoriteToggledFromSwitcher;
            _streamSwitcher.TeamHubRequested += OnTeamHubRequestedFromSwitcher;
            _streamSwitcher.Closed += (s, e) => SetStatus("Stream picker closed");
            layer.Add(_streamSwitcher);

            // Team Hub — read-only modal showing standings + next match for one team.
            // Opened from a "View team" chip inside the stream picker.
            _teamHub = new TeamHubView(_sportsData);
            _teamHub.Closed += (s, e) => SetStatus("Team page closed");
            layer.Add(_teamHub);

            // Profile selector — Netflix-style "Who's watching?" full-screen
            // view. Shown on every launch. Selecting a profile reloads the
            // per-profile UserPreferences and re-runs the catalog fetch so
            // the rows reflect that profile's favourites + last sport.
            _profileSelector = new ProfileSelectorView();
            _profileSelector.ProfileSelected += OnProfileSelected;
            _profileSelector.AddProfileRequested += OnAddProfileRequested;
            _profileSelector.EditProfileRequested += OnSelectorEditProfileRequested;
            _profileSelector.DoneRequested += (s, e) => _profileSelector?.Close();
            _profileSelector.SignOutRequested += OnSignOutRequested;
            _profileSelector.Closed += (s, e) => SetStatus("");
            layer.Add(_profileSelector);

            // Login view — gate before the profile selector. Hidden by
            // default; OnCreate's auth-check shows it if there's no
            // valid JWT, and on LoggedIn we close it + transition to
            // the profile selector.
            _loginView = new LoginView(_backendAuth);
            _loginView.LoggedIn += OnLoginCompleted;
            layer.Add(_loginView);

            // ---- In-app profile management overlays --------------------------
            // Order of layer.Add calls determines z-order: later wins. Dropdown
            // sits low (small popover above main app); editor + wizard sit
            // above the picker; avatar picker + keyboard are top-most sub-
            // overlays opened on demand by the editor + wizard. Player view
            // is added LAST below this block so it sits above all of these.
            _profileDropdown = new ProfileSwitcherDropdownView();
            _profileDropdown.SwitchToProfile += OnDropdownSwitchToProfile;
            _profileDropdown.OpenPicker += OnDropdownOpenPicker;
            _profileDropdown.OpenManage += OnDropdownOpenManage;
            _profileDropdown.SignOut += OnSignOutRequested;
            _profileDropdown.Closed += OnDropdownClosed;
            // Re-anchor top-left under the chip so the popover drops down
            // from the chip's corner.
            _profileDropdown.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            _profileDropdown.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            _profileDropdown.Position = new Position(16f, 90f);
            layer.Add(_profileDropdown);

            _profileEdit = new ProfileEditView();
            _profileEdit.RequestEditName += OnEditRequestEditName;
            _profileEdit.RequestPickAvatar += OnEditRequestPickAvatar;
            _profileEdit.Saved += OnEditSaved;
            _profileEdit.DeleteRequested += OnEditDeleteRequested;
            _profileEdit.Closed += OnEditClosed;
            layer.Add(_profileEdit);

            _profileAddWizard = new ProfileAddWizardView();
            _profileAddWizard.RequestPickAvatar += OnWizardRequestPickAvatar;
            _profileAddWizard.RequestEditName += OnWizardRequestEditName;
            _profileAddWizard.Created += OnWizardCreated;
            _profileAddWizard.Cancelled += OnWizardCancelled;
            layer.Add(_profileAddWizard);

            // Sub-overlays — opened on-demand by editor + wizard via host-
            // routed events. Owned here so both screens share one instance
            // and the host can manage one-shot event subscriptions.
            _avatarPicker = new AvatarPickerView();
            layer.Add(_avatarPicker);

            _keyboard = new OnScreenKeyboardView();
            layer.Add(_keyboard);

            // Keep chip + (when open) picker in sync with profile mutations.
            // BOTH subscribers are wrapped in try/catch so an exception
            // here (e.g. transient NUI access from a misrouted thread)
            // can't propagate up the EventHandler invocation list and
            // wedge whatever was firing the event. We also log so the
            // dlog shows the real failure instead of a silent freeze.
            _profiles.ActiveChanged += (s, p) =>
            {
                try { _profileChip?.SetActiveProfile(p); }
                catch (Exception ex) { Diagnostics.LogError("ActiveChanged subscriber", ex); }
            };
            _profiles.ProfilesChanged += (s, e) =>
            {
                try
                {
                    _profileChip?.SetActiveProfile(_profiles.Active);
                    if (_profileSelector != null && _profileSelector.IsOpen)
                    {
                        _profileSelector.SetProfiles(_profiles.Profiles, _profiles.ActiveId);
                    }
                }
                catch (Exception ex) { Diagnostics.LogError("ProfilesChanged subscriber", ex); }
            };

            // Full-screen WebView player. Added LAST so it sits at the top of the z-order
            // and covers the listing when visible. Hidden until the user activates a tile.
            _playerView = new PlayerWebView
            {
                Opacity = 0f,
            };
            _playerView.ExitRequested += OnPlayerExitRequested;
            _playerView.StatusChanged += (s, msg) => SetStatus(msg);
            // Wire the URL resolver so wrapper-style streams
            // (embedsports.top → cross-origin pooembed.eu iframe)
            // get bypassed in favour of loading the inner player
            // directly. Resolver is opportunistic: any failure
            // falls back to the original URL silently.
            _playerView.SetResolver(new StreamedTv.Services.StreamResolverService(_backendAuth));
            layer.Add(_playerView);
            _playerView.Hide();

            // Native NUI player. Sits on the same layer as PlayerWebView and
            // is added AFTER it so it's higher in z-order — matters for the
            // brief moment both could otherwise be visible during a crossfade.
            // Reuses OnPlayerExitRequested so the exit / picker-reopen flow
            // is identical regardless of which player surface was active.
            _nativePlayer = new NativePlayerView
            {
                Opacity = 0f,
            };
            _nativePlayer.ExitRequested += OnPlayerExitRequested;
            _nativePlayer.StatusChanged += (s, msg) => SetStatus(msg);
            layer.Add(_nativePlayer);
            _nativePlayer.Hide();

            // Loading overlay for the proxy-path extract wait. Full-screen
            // dim sheet with a centered text label. Added LAST so it's at
            // the top of the z-order — must cover both player surfaces +
            // the listing while extraction grinds. Initially hidden.
            //
            // SIZING: layer children must use ResizePolicyType (the legacy
            // explicit-sizing API) — the layer has no LinearLayout / Layout
            // attached, so LayoutParamPolicies.MatchParent collapses to
            // zero. Same pattern as _bgGradient + topbar above.
            _loadingOverlay = new View
            {
                BackgroundColor = new Color(0f, 0f, 0f, 0.78f),
            };
            _loadingOverlay.WidthResizePolicy = ResizePolicyType.FillToParent;
            _loadingOverlay.HeightResizePolicy = ResizePolicyType.FillToParent;
            _loadingOverlay.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            _loadingOverlay.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            _loadingOverlayLabel = new TextLabel
            {
                Text = "",
                TextColor = Color.White,
                PointSize = 28f,
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _loadingOverlayLabel.WidthResizePolicy = ResizePolicyType.FillToParent;
            _loadingOverlayLabel.HeightResizePolicy = ResizePolicyType.FillToParent;
            _loadingOverlayLabel.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            _loadingOverlayLabel.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            _loadingOverlay.Add(_loadingOverlayLabel);
            layer.Add(_loadingOverlay);
            _loadingOverlay.Hide();

            Diagnostics.Log("BuildScene end");
        }

        private void OnSportPinned(object sender, MoreOverlayPinEventArgs e)
        {
            try
            {
                if (_prefs.IsFavorite(e.SportId))
                {
                    // Already pinned → toggle off so user can free a slot.
                    _prefs.UnpinFavorite(e.SportId);
                    _moreOverlay.ShowMessage("");
                }
                else
                {
                    bool ok = _prefs.TryPinFavorite(e.SportId);
                    if (!ok)
                    {
                        _moreOverlay.ShowMessage("Max 3 favorites — remove one first by selecting it");
                        return;
                    }
                    _moreOverlay.ShowMessage("");
                }

                List<string> favorites = new List<string>(_prefs.FavoriteSports);
                while (favorites.Count < 3) favorites.Add("");
                _navPill.SetFavorites(favorites);
                ApplyActiveChipFromActiveSport();
                _moreOverlay.RefreshPinned(_prefs.FavoriteSports);

                int pinnedCount = 0;
                foreach (string id in _prefs.FavoriteSports)
                {
                    if (!string.IsNullOrEmpty(id)) pinnedCount++;
                }
                SetStatus(pinnedCount + " of 3 favorites pinned · press Back when done");
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnSportPinned", ex);
            }
        }

        // SetStatus formerly painted text into an on-screen banner
        // above the hero. The banner was a stop-gap for debugging
        // sport-data API issues on a TV with no log access; now that
        // crash-log shipping is wired (POST /v1/crash-logs) we don't
        // need to take screen real estate for it. Existing call sites
        // still flow through here so the messages land in dlog.
        private void SetStatus(string text)
        {
            try
            {
                if (!string.IsNullOrEmpty(text)) Diagnostics.Log("status: " + text);
            }
            catch
            {
            }
        }

        private async Task LoadInitialCatalogAsync()
        {
            try
            {
                SetStatus("Loading sports from streamed.pk…");
                _sports.Clear();
                _sports.AddRange(await _catalog.GetSportsAsync(_shutdown.Token));

                // Use the user's saved favorites verbatim (defaults: cricket / football / basketball).
                List<string> favorites = new List<string>(_prefs.FavoriteSports);
                while (favorites.Count < 3) favorites.Add("");
                _navPill.SetFavorites(favorites);

                // Highlight the active chip so the visual matches the data we'll load.
                ApplyActiveChipFromActiveSport();

                SetStatus("Got " + _sports.Count + " sports · loading matches for " + _activeSportId + "…");
                await LoadActiveTabAsync();
                string apiState = _catalog.LastNetworkError == null ? "API OK" : "API: " + _catalog.LastNetworkError;
                SetStatus(apiState + " · " + _liveMatches.Count + " live · " + _scheduledMatches.Count + " upcoming · " + _recentMatches.Count + " recent");
            }
            catch (Exception ex)
            {
                SetStatus("FAILED " + ex.GetType().Name + ": " + ex.Message);
                Diagnostics.LogError("LoadInitialCatalogAsync", ex);
            }
        }

        private async Task PingSportsDataAsync()
        {
            try
            {
                HealthResponse h = await _sportsData.GetHealthAsync(_shutdown.Token);
                if (h != null && string.Equals(h.Status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    Diagnostics.Log("Sports backend reachable at " + _sportsData.BaseUrl);
                }
                else
                {
                    Diagnostics.Log("Sports backend unreachable at " + _sportsData.BaseUrl + ": " + (_sportsData.LastError ?? "no response"));
                }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("PingSportsDataAsync", ex);
            }
        }

        // Spin up the LiveScoreService once at app launch. It polls the backend's
        // /v1/live/cricket every 60s and surfaces the latest snapshot via TryGetForMatch
        // — TileView reads it lazily when building the live pill text. Runs for the
        // lifetime of the app; cancel via _shutdown when we exit.
        private void StartLiveScorePolling()
        {
            try
            {
                _liveScores = new LiveScoreService(_sportsData);
                _liveScores.SnapshotUpdated += (_, __) =>
                {
                    Diagnostics.Log("Live cricket snapshot updated: " + _liveScores.MatchCount + " matches");
                };
                _liveScores.Start();
                LiveScoreRegistry.Set(_liveScores);
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("StartLiveScorePolling", ex);
            }
        }

        private void ApplyActiveChipFromActiveSport()
        {
            try
            {
                IReadOnlyList<string> favs = _prefs.FavoriteSports;
                for (int i = 0; i < favs.Count && i < 3; i++)
                {
                    if (string.Equals(favs[i], _activeSportId, StringComparison.OrdinalIgnoreCase))
                    {
                        NavPillView.NavSelection sel =
                            i == 0 ? NavPillView.NavSelection.Favorite0 :
                            i == 1 ? NavPillView.NavSelection.Favorite1 :
                            NavPillView.NavSelection.Favorite2;
                        _navPill.SetActive(sel);
                        return;
                    }
                }
                // If active sport isn't in the favorites, default to For You highlighted.
                _navPill.SetActive(NavPillView.NavSelection.ForYou);
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("ApplyActiveChipFromActiveSport", ex);
            }
        }

        private const string ForYouSportId = "__foryou__";

        private async Task LoadActiveTabAsync()
        {
            _liveMatches.Clear();
            _scheduledMatches.Clear();
            _recentMatches.Clear();

            if (string.Equals(_activeSportId, ForYouSportId, StringComparison.Ordinal))
            {
                // For You partitions inside its own helper (multi-sport fan-out),
                // and then renders progressively in the same way as the per-sport
                // path: live first, yield, scheduled, yield, recent.
                await LoadForYouAsync();
            }
            else
            {
                // (1) Single HTTP roundtrip — same as before. We DO NOT add a second call.
                IReadOnlyList<CatalogMatch> matches = await _catalog.GetMatchesBySportAsync(_activeSportId, Array.Empty<string>(), _shutdown.Token);

                // (2) Partition into the three buckets immediately.
                foreach (CatalogMatch m in matches)
                {
                    if (string.Equals(m.Status, "live", StringComparison.OrdinalIgnoreCase)) _liveMatches.Add(m);
                    else if (string.Equals(m.Status, "recent", StringComparison.OrdinalIgnoreCase)) _recentMatches.Add(m);
                    else _scheduledMatches.Add(m);
                }
            }

            // (3) Render LIVE row first so the user sees a focusable tile immediately.
            RebuildLiveRow();
            UpdateHero();
            RefreshFocusZones();
            SeedInitialFocus();

            // (4) Yield back to the UI thread so the pulse loop can paint the live
            // row + focus ring before we spend more time building the next two rows.
            await Task.Yield();

            // (5) Scheduled and recent rows in two separate steps, each yielding
            // between them so focus paints / scrolls can complete.
            RebuildScheduledRow();
            RefreshFocusZones();
            await Task.Yield();

            RebuildRecentRow();
            RefreshFocusZones();
        }

        // For You tab: show ONLY matches involving the user's favorited teams. The
        // earlier implementation was a noisy aggregate of "popular live across all
        // sports" + "favorite sports' upcoming/recent" — useful for first-launch but
        // overwhelming once a user has actually picked teams to follow.
        //
        // Strategy: pull the live + scheduled + recent for each favorited sport (so we
        // hit the existing per-sport cache rather than fanning out across every sport),
        // then filter to matches where the home or away team is in the favorites set.
        // Empty rows render as "No matches" so the user has unambiguous feedback that
        // their teams aren't playing right now.
        //
        // NOTE: This helper now ONLY partitions into the three lists. Progressive
        // rendering (live first, then yield, then scheduled, then yield, then recent)
        // is owned by LoadActiveTabAsync — same shape as the per-sport path.
        private async Task LoadForYouAsync()
        {
            // Build the haystack: union of all favorited sports' matches. If the user
            // has zero favorited sports we still try to scan the currently-active
            // sport (fallback) so the page isn't completely blank.
            HashSet<string> favSports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in _prefs.FavoriteSports)
            {
                if (!string.IsNullOrWhiteSpace(s)) favSports.Add(s);
            }
            if (favSports.Count == 0 && !string.IsNullOrEmpty(_activeSportId))
            {
                favSports.Add(_activeSportId);
            }

            List<Task<IReadOnlyList<CatalogMatch>>> tasks = new List<Task<IReadOnlyList<CatalogMatch>>>();
            foreach (string sportId in favSports)
            {
                if (string.Equals(sportId, ForYouSportId, StringComparison.Ordinal)) continue;
                tasks.Add(_catalog.GetMatchesBySportAsync(sportId, Array.Empty<string>(), _shutdown.Token));
            }

            try
            {
                IReadOnlyList<CatalogMatch>[] results = await Task.WhenAll(tasks);
                // Partition immediately — same shape as the per-sport branch in
                // LoadActiveTabAsync. Progressive rendering happens in the caller.
                foreach (IReadOnlyList<CatalogMatch> per in results)
                {
                    foreach (CatalogMatch m in per)
                    {
                        // The whole point of For You: only matches with a favorited team.
                        if (!StreamedTv.Services.FavoritesRegistry.IsAnyTeamFavorited(m)) continue;

                        if (string.Equals(m.Status, "live", StringComparison.OrdinalIgnoreCase)) _liveMatches.Add(m);
                        else if (string.Equals(m.Status, "recent", StringComparison.OrdinalIgnoreCase)) _recentMatches.Add(m);
                        else _scheduledMatches.Add(m);
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("LoadForYouAsync", ex);
            }
        }

        private void RebuildRows()
        {
            // Preserved as a thin wrapper for any caller that wants the old "rebuild
            // everything at once" semantics. The progressive path in
            // LoadActiveTabAsync calls the three split methods directly so it can
            // yield to the UI between rows.
            RebuildLiveRow();
            RebuildScheduledRow();
            RebuildRecentRow();
        }

        private void RebuildLiveRow()
        {
            try
            {
                SortMatchesForDisplay(_liveMatches, ascendingDate: true);
                _liveRow.SetMatches(_liveMatches, TileView.TileKind.Live);
                // Hide the row AND collapse its layout slot. NUI's Hide()
                // only flips visibility — the LinearLayout above still
                // reserves the row's height + cell-padding gap. Setting
                // HeightSpecification to 0 makes the layout reflow and
                // pull subsequent rows up.
                ApplyRowVisibility(_liveRow, _liveMatches.Count > 0);
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("RebuildLiveRow", ex);
            }
        }

        private void RebuildScheduledRow()
        {
            try
            {
                SortMatchesForDisplay(_scheduledMatches, ascendingDate: true);
                _scheduledRow.SetMatches(_scheduledMatches, TileView.TileKind.Scheduled);
                ApplyRowVisibility(_scheduledRow, _scheduledMatches.Count > 0);
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("RebuildScheduledRow", ex);
            }
        }

        private void RebuildRecentRow()
        {
            try
            {
                // Recently-finished list reads best newest-first.
                SortMatchesForDisplay(_recentMatches, ascendingDate: false);
                _recentRow.SetMatches(_recentMatches, TileView.TileKind.Recent);
                ApplyRowVisibility(_recentRow, _recentMatches.Count > 0);
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("RebuildRecentRow", ex);
            }
        }

        /// <summary>Show or collapse a RowView based on whether it has content.
        /// Show() alone leaves the layout slot occupied; setting
        /// HeightSpecification = 0 makes the parent LinearLayout actually
        /// reflow and pull subsequent rows up to fill the space.</summary>
        private static void ApplyRowVisibility(RowView row, bool visible)
        {
            if (row == null) return;
            if (visible)
            {
                row.HeightSpecification = StreamedTv.Theme.Dimens.RowHeight;
                row.Show();
            }
            else
            {
                row.HeightSpecification = 0;
                row.Hide();
            }
        }

        // Sort priority (highest wins, applied in this order):
        //   1. Has a favourited team (home OR away)
        //   2. Popular flag
        //   3. Date — ascending for live/scheduled (soonest first), descending for
        //      recent (most-recently-finished first).
        //
        // Uses List<T>.Sort with an index-based tiebreaker so the order is stable —
        // ties fall back to original API order. That matters because the
        // MaxTilesPerRow=8 cap downstream takes the first 8, so favourites + popular
        // get the visible slots first.
        private void SortMatchesForDisplay(List<CatalogMatch> matches, bool ascendingDate)
        {
            if (matches == null || matches.Count < 2) return;

            int n = matches.Count;
            int[] originalIndex = new int[n];
            for (int i = 0; i < n; i++) originalIndex[i] = i;

            CatalogMatch[] arr = matches.ToArray();
            // Sort an index array so we can reach back into originalIndex for the
            // stable tiebreaker (List<T>.Sort is not guaranteed stable on its own).
            int[] order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;

            Array.Sort(order, (a, b) =>
            {
                CatalogMatch ma = arr[a];
                CatalogMatch mb = arr[b];

                int favA = HasFavoritedTeam(ma) ? 1 : 0;
                int favB = HasFavoritedTeam(mb) ? 1 : 0;
                if (favA != favB) return favB - favA; // favourites first

                int popA = ma.Popular ? 1 : 0;
                int popB = mb.Popular ? 1 : 0;
                if (popA != popB) return popB - popA; // popular next

                int dateCmp = ma.Date.CompareTo(mb.Date);
                if (dateCmp != 0) return ascendingDate ? dateCmp : -dateCmp;

                // Stable tiebreaker — preserve original API order.
                return a - b;
            });

            matches.Clear();
            for (int i = 0; i < n; i++) matches.Add(arr[order[i]]);
        }

        private bool HasFavoritedTeam(CatalogMatch m)
        {
            if (m == null || m.Teams == null) return false;
            string home = m.Teams.Home?.Name;
            string away = m.Teams.Away?.Name;
            if (!string.IsNullOrWhiteSpace(home) && _prefs.IsFavoriteTeam(home)) return true;
            if (!string.IsNullOrWhiteSpace(away) && _prefs.IsFavoriteTeam(away)) return true;
            return false;
        }

        /// <summary>Netflix-style instant tab switch: clear current data and show shimmering skeleton
        /// placeholders the moment the user picks a sport. Called BEFORE the API request kicks off.</summary>
        private void ShowLoadingState(string sportName)
        {
            try
            {
                _liveMatches.Clear();
                _scheduledMatches.Clear();
                _recentMatches.Clear();
                // Reveal all three rows during loading — RebuildXxxRow may
                // have hidden + collapsed some on the previous tab, and we
                // always want skeletons visible while a fresh load is in
                // flight (otherwise tab switch looks frozen).
                ApplyRowVisibility(_liveRow, true);
                ApplyRowVisibility(_scheduledRow, true);
                ApplyRowVisibility(_recentRow, true);
                _liveRow.SetLoading();
                _scheduledRow.SetLoading();
                _recentRow.SetLoading();
                _hero.SetLoading();
                RefreshFocusZones();
                if (!string.IsNullOrEmpty(sportName))
                {
                    SetStatus("Loading " + sportName + "…");
                }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("ShowLoadingState", ex);
            }
        }

        private void UpdateHero()
        {
            try
            {
                CatalogMatch first = _liveMatches.Count > 0 ? _liveMatches[0]
                    : _scheduledMatches.Count > 0 ? _scheduledMatches[0]
                    : _recentMatches.Count > 0 ? _recentMatches[0]
                    : null;
                TileView.TileKind kind = _liveMatches.Count > 0 ? TileView.TileKind.Live
                    : _scheduledMatches.Count > 0 ? TileView.TileKind.Scheduled
                    : TileView.TileKind.Recent;
                _hero.SetMatch(first, kind);
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("UpdateHero", ex);
            }
        }

        private void OnKeyEvent(object sender, Window.KeyEventArgs e)
        {
            try
            {
                if (e.Key.State != Key.StateType.Down) return;
                string key = e.Key.KeyPressedName;

                // Loading overlay (proxy-extract wait) intercepts ALL keys.
                // Back/Escape cancels the in-flight extract + dismisses the
                // overlay. Any other key is swallowed so the user can't
                // race two ShowPlayerView calls (e.g. "click another stream
                // while the previous one is still resolving") which would
                // otherwise leave two extracts running and the second
                // overwriting the first's player target.
                if (_loadingOverlayVisible)
                {
                    if (key == "Escape" || key == "XF86Back" || key == "Back")
                    {
                        Diagnostics.Log("Loader: back pressed → cancelling extract");
                        CancelLoaderAndExtract();
                    }
                    return;
                }

                // Native NUI player (used when StreamExtractorService produced
                // a clean m3u8). Routed BEFORE PlayerWebView's branch — both
                // never visible at once, but checking native first keeps the
                // hot path single-branch when the native player is active.
                if (_nativePlayerVisible && _nativePlayer != null)
                {
                    Diagnostics.Log("NativePlayer key: " + key);
                    if (key == "Escape" || key == "XF86Back" || key == "Back")
                    {
                        _nativePlayer.RaiseExitRequested();
                        return;
                    }
                    if (key == "Return" || key == "Select" || key == "KP_Enter"
                        || key == "OK" || key == "Ok" || key == "ok" || key == "XF86Ok")
                    {
                        _nativePlayer.TogglePlay();
                        return;
                    }
                    // Volume keys: explicitly DON'T return — let them fall
                    // through so the system audio path (XF86AudioRaiseVolume
                    // etc.) drives the TV's master volume. The native
                    // VideoView doesn't expose a clean per-stream gain knob.
                    if (key == "Up" || key == "Down" || key == "XF86VolumeUp"
                        || key == "XF86VolumeDown" || key == "XF86AudioRaiseVolume"
                        || key == "XF86AudioLowerVolume" || key == "m" || key == "M"
                        || key == "XF86AudioMute")
                    {
                        return;
                    }
                    return;
                }

                // When the player WebView is up, all keys go to it. Back triggers the exit
                // path back to the listing; anything else is forwarded so the embed JS can
                // react (and so the back-to-listing hint reappears).
                if (_playerVisible && _playerView != null)
                {
                    // Diagnostic: every key the remote sends while the player is open
                    // is logged so we can see what name to match against (Samsung
                    // remotes have used "Return", "Select", "OK", "XF86Ok" and
                    // others over the years).
                    Diagnostics.Log("Player key: " + key);

                    // Drive the underlying <video> element via JS. The C# side
                    // routes; the JS side actually toggles play/volume/mute.
                    if (key == "Return" || key == "Select" || key == "KP_Enter" || key == "OK" || key == "Ok" || key == "ok" || key == "XF86Ok" ||
                        key == "OK" || key == "Ok" || key == "ok" || key == "XF86Ok")
                    {
                        _playerView.TogglePlay();
                    }
                    else if (key == "Up" || key == "XF86VolumeUp" || key == "XF86AudioRaiseVolume")
                    {
                        _playerView.AdjustVolume(10);
                    }
                    else if (key == "Down" || key == "XF86VolumeDown" || key == "XF86AudioLowerVolume")
                    {
                        _playerView.AdjustVolume(-10);
                    }
                    else if (key == "m" || key == "M" || key == "XF86AudioMute")
                    {
                        _playerView.ToggleMute();
                    }
                    else
                    {
                        // Back/Escape and anything else falls through to RouteKey,
                        // which handles ExitRequested + the back-to-listing hint.
                        _playerView.RouteKey(key);
                    }
                    return;
                }

                // When the More overlay is open, all keys go to it.
                if (_moreOverlay != null && _moreOverlay.IsOpen)
                {
                    if (key == "XF86Back" || key == "Escape")
                    {
                        _moreOverlay.Close();
                    }
                    else if (key == "Up") _moreOverlay.MoveUp();
                    else if (key == "Down") _moreOverlay.MoveDown();
                    else if (key == "Left") _moreOverlay.MoveLeft();
                    else if (key == "Right") _moreOverlay.MoveRight();
                    else if (key == "Return" || key == "Select" || key == "KP_Enter" || key == "OK" || key == "Ok" || key == "ok" || key == "XF86Ok") _moreOverlay.Activate();
                    return;
                }

                // Live TV (iptv-org) inline home surface — owns its own
                // 2D focus + key router while focus is INSIDE it. The
                // _iptvHomeFocusOwned flag, not _iptvHome.IsActive,
                // gates key routing because the surface stays visible
                // when focus walks back up to the nav pill (D-pad-Up
                // off the top row → focus returns to nav, surface
                // stays on screen so D-pad-Down re-enters it).
                // Routed BEFORE the modal overlay check so we cleanly
                // take over once focus enters this surface.
                if (_iptvHome != null && _iptvHomeFocusOwned)
                {
                    _iptvHome.HandleKey(key);
                    return;
                }

                // Live TV (iptv-org) overlay — own key router. Same
                // exclusivity rule as MoreOverlay: while open it eats
                // every key. Entry point is the "Live" chip in the
                // nav pill (handled in the focus-zone activation
                // switch above) — no remote-button shortcut since
                // not every Tizen remote has the colored buttons.
                if (_iptvOverlay != null && _iptvOverlay.IsOpen)
                {
                    _iptvOverlay.HandleKey(key);
                    return;
                }

                // Sub-overlays for profile management sit ABOVE the picker.
                // Routing order mirrors visual z-order: keyboard → avatar
                // picker → editor → add-wizard → picker → dropdown.
                if (_keyboard != null && _keyboard.IsOpen)
                {
                    _keyboard.HandleKey(key);
                    return;
                }
                if (_avatarPicker != null && _avatarPicker.IsOpen)
                {
                    _avatarPicker.HandleKey(key);
                    return;
                }
                if (_profileEdit != null && _profileEdit.IsOpen)
                {
                    _profileEdit.HandleKey(key);
                    return;
                }
                if (_profileAddWizard != null && _profileAddWizard.IsOpen)
                {
                    _profileAddWizard.HandleKey(key);
                    return;
                }

                // Login view — top-of-funnel. While shown it consumes
                // ALL input (no profile picker / main app underneath).
                // Back is intentionally ignored here: login is required.
                if (_loginView != null && _loginView.IsOpen)
                {
                    _loginView.HandleKey(key);
                    return;
                }

                // Full-screen profile picker — back dismisses (only when
                // there's already an active profile so the very first launch
                // has somewhere to fall back to).
                if (_profileSelector != null && _profileSelector.IsOpen)
                {
                    if (key == "XF86Back" || key == "Escape")
                    {
                        if (_profiles.Active != null) _profileSelector.Close();
                    }
                    else if (key == "Left") _profileSelector.MoveLeft();
                    else if (key == "Right") _profileSelector.MoveRight();
                    else if (key == "Up") _profileSelector.MoveUp();
                    else if (key == "Down") _profileSelector.MoveDown();
                    else if (key == "Return" || key == "Select" || key == "KP_Enter" || key == "OK" || key == "Ok" || key == "ok" || key == "XF86Ok") _profileSelector.Activate();
                    return;
                }

                // In-app dropdown popover (anchored under the chip).
                if (_profileDropdown != null && _profileDropdown.IsOpen)
                {
                    _profileDropdown.HandleKey(key);
                    return;
                }

                // When the team hub is open, only Back/Escape are meaningful (read-only modal).
                // Checked BEFORE the StreamSwitcher block because the hub may have been opened
                // on top of (and after closing) the stream picker, so it's the topmost modal.
                if (_teamHub != null && _teamHub.IsOpen)
                {
                    if (key == "XF86Back" || key == "Escape")
                    {
                        _teamHub.Close();
                        // Return user to the stream selector so they can pick another stream.
                        _streamSwitcher?.Open();
                    }
                    return;
                }

                // When the stream picker is open, route to it.
                if (_streamSwitcher != null && _streamSwitcher.IsOpen)
                {
                    if (key == "XF86Back" || key == "Escape")
                    {
                        _streamSwitcher.Close();
                    }
                    else if (key == "Up") _streamSwitcher.MoveUp();
                    else if (key == "Down") _streamSwitcher.MoveDown();
                    else if (key == "Left") _streamSwitcher.MoveLeft();
                    else if (key == "Right") _streamSwitcher.MoveRight();
                    else if (key == "Return" || key == "Select" || key == "KP_Enter" || key == "OK" || key == "Ok" || key == "ok" || key == "XF86Ok") _streamSwitcher.Activate();
                    return;
                }

                // In-app profile chip — temporary focus stealer that
                // intercepts keys when the user pressed Left off the leftmost
                // nav cell. Right returns focus to the nav pill; Down moves
                // straight into hero/rows. OK opens the dropdown.
                if (_profileChipFocused)
                {
                    if (key == "XF86Back" || key == "Escape" || key == "Right")
                    {
                        _profileChipFocused = false;
                        _profileChip?.Blur();
                    }
                    else if (key == "Down")
                    {
                        _profileChipFocused = false;
                        _profileChip?.Blur();
                        // FocusManager is still parked at Nav (we never moved
                        // it). Down from here naturally lands on hero/rows.
                        _focus.MoveDown();
                    }
                    else if (key == "Return" || key == "Select" || key == "KP_Enter" || key == "OK" || key == "Ok" || key == "ok" || key == "XF86Ok")
                    {
                        _profileChip?.Activate();
                    }
                    // Up/Left at the corner — no-op
                    return;
                }

                if (key == "XF86Back" || key == "Escape")
                {
                    // Back behavior:
                    //   * On nav pill → first press shows "Press back again
                    //     to exit" status; second press within the window
                    //     actually exits. Prevents accidental app dismiss.
                    //   * Anywhere else (hero / tile rows) → jump focus to
                    //     the nav pill instead. Same affordance as Netflix /
                    //     Apple TV: back walks UP the navigation hierarchy
                    //     before it leaves the app entirely.
                    if (_focus.CurrentZone == StreamedTv.Services.FocusZone.Nav)
                    {
                        DateTime now = DateTime.UtcNow;
                        if ((now - _lastBackOnNavAt).TotalSeconds < BackToExitWindowSeconds)
                        {
                            Exit();
                        }
                        else
                        {
                            _lastBackOnNavAt = now;
                            SetStatus("Press back again to exit");
                        }
                    }
                    else
                    {
                        // Restore focus to whichever nav chip is currently
                        // active so the user lands on the chip that matches
                        // what they were just browsing.
                        int navIndex = _navPill?.IndexForSelection(_navPill.ActiveItem) ?? 0;
                        _focus.SetFocus(StreamedTv.Services.FocusZone.Nav, navIndex);
                        // Reset the double-back timer — the user clearly didn't
                        // mean to exit; they just walked back to nav.
                        _lastBackOnNavAt = DateTime.MinValue;
                    }
                }
                else if (key == "Up")
                {
                    _focus.MoveUp();
                }
                else if (key == "Down")
                {
                    // If the Live surface is up and focus is on the Live chip,
                    // Down re-enters the surface instead of moving to Hero.
                    // (Hero is hidden when Live is active, but the FocusManager
                    // doesn't know about _iptvHome — it would otherwise try to
                    // skip past Hero into the LiveRow zone, which is also
                    // hidden, with confusing behavior.)
                    if (_iptvHome != null && _iptvHome.IsActive
                        && _focus.CurrentZone == StreamedTv.Services.FocusZone.Nav
                        && _navPill != null
                        && _navPill.SelectionForIndex(_focus.CurrentIndex) == NavPillView.NavSelection.LiveTV)
                    {
                        EnterIptvHomeFocus();
                    }
                    else
                    {
                        _focus.MoveDown();
                    }
                }
                else if (key == "Left")
                {
                    // Left from the leftmost nav cell focuses the top-left
                    // profile chip (Netflix-style lateral navigation). Right
                    // from the chip returns focus here.
                    if (_focus.CurrentZone == StreamedTv.Services.FocusZone.Nav && _focus.CurrentIndex == 0)
                    {
                        _profileChipFocused = true;
                        _profileChip?.Focus();
                    }
                    else
                    {
                        _focus.MoveLeft();
                    }
                }
                else if (key == "Right")
                {
                    _focus.MoveRight();
                }
                else if (key == "Return" || key == "Select" || key == "KP_Enter" || key == "OK" || key == "Ok" || key == "ok" || key == "XF86Ok")
                {
                    HandleActivate();
                }
                // Favorite-toggle keys. F is the obvious keyboard shortcut; XF86Yellow
                // is the Samsung remote's yellow color button which most TV remotes
                // expose. Both toggle the favorite state for BOTH teams in the match
                // currently focused — that's the simplest UX for "I'm into this match"
                // without forcing a home/away picker dialog.
                else if (key == "f" || key == "F" || key == "XF86Yellow" || key == "XF86ColorF2Yellow")
                {
                    HandleToggleFavoriteOnFocused();
                }
            }
            catch (Exception ex)
            {
                SetStatus("Key handler error: " + ex.Message);
            }
        }

        private void HandleActivate()
        {
            switch (_focus.CurrentZone)
            {
                case StreamedTv.Services.FocusZone.Nav:
                    NavPillView.NavSelection sel = _navPill.SelectionForIndex(_focus.CurrentIndex);
                    string sportId = _navPill.SportIdFor(sel);
                    if (!string.IsNullOrEmpty(sportId) && !string.Equals(sportId, _activeSportId, StringComparison.OrdinalIgnoreCase))
                    {
                        // Coming back from Live mode → restore the regular
                        // home surface (hero + sport rows) before swapping
                        // sport content. ExitLiveMode is a no-op when the
                        // surface isn't currently up.
                        ExitLiveMode();
                        _activeSportId = sportId;
                        _prefs.SetLastSelectedSport(sportId);
                        // Re-tint the entire app palette (background gradient, hero
                        // wash, nav-pill active chip, more-overlay) to match the
                        // bundled hero art for the chosen sport.
                        StreamedTv.Theme.SportPalette.SetCurrent(sportId);
                        // Clear the For You banner override — sport-specific
                        // bundled banners take over from here.
                        StreamedTv.Views.HeroView.ForceBundledHero(null);
                        _navPill.SetActive(sel);
                        // Always show the skeleton state on a tab switch — it's the user's
                        // confirmation that the keypress was heard. The earlier optimisation
                        // (skip skeleton when cached) made the second visit to a sport look
                        // frozen because the screen sat with the previous tab's tiles until
                        // ReloadAsync's await completed. A brief skeleton flash is cheaper
                        // UX-wise than apparent silence.
                        ShowLoadingState(sportId);
                        _ = ReloadAsync();
                    }
                    else if (sel == NavPillView.NavSelection.ForYou)
                    {
                        if (!string.Equals(_activeSportId, ForYouSportId, StringComparison.Ordinal))
                        {
                            // Same Live-mode exit handshake as the sport
                            // case — re-show hero + rows before swapping
                            // content.
                            ExitLiveMode();
                            _activeSportId = ForYouSportId;
                            // For-You has no single sport — fall back to the default palette.
                            StreamedTv.Theme.SportPalette.SetCurrent(null);
                            // Force the For You collage banner so the hero
                            // shows the multi-sport poster regardless of the
                            // focused tile's sport.
                            StreamedTv.Views.HeroView.ForceBundledHero("hero_foryou.jpg");
                            _navPill.SetActive(NavPillView.NavSelection.ForYou);
                            ShowLoadingState("For You");
                            _ = ReloadAsync();
                        }
                    }
                    else if (sel == NavPillView.NavSelection.More)
                    {
                        if (_moreOverlay != null)
                        {
                            _moreOverlay.SetSports(_sports, _prefs.FavoriteSports);
                            _moreOverlay.Open();
                            _moreOverlay.RaiseToTop();
                        }
                    }
                    else if (sel == NavPillView.NavSelection.LiveTV)
                    {
                        // iptv-org Live TV chip — expand the inline Live
                        // home surface (hero + sport rows hide; letter-
                        // bucketed channel rows take the slot). Same
                        // gating as the legacy overlay path so we don't
                        // pop over login/profile/player screens.
                        if (CanEnterLiveMode()) EnterLiveMode();
                    }
                    break;
                case StreamedTv.Services.FocusZone.Hero:
                    _ = PlayMatchAsync(_hero.CurrentMatch);
                    break;
                case StreamedTv.Services.FocusZone.LiveRow:
                case StreamedTv.Services.FocusZone.ComingUpRow:
                case StreamedTv.Services.FocusZone.RecentRow:
                    CatalogMatch m = ResolveMatch(_focus.CurrentZone, _focus.CurrentIndex);
                    _ = PlayMatchAsync(m);
                    break;
            }
        }

        private CatalogMatch ResolveMatch(StreamedTv.Services.FocusZone zone, int index)
        {
            List<CatalogMatch> list =
                zone == StreamedTv.Services.FocusZone.LiveRow ? _liveMatches :
                zone == StreamedTv.Services.FocusZone.ComingUpRow ? _scheduledMatches :
                zone == StreamedTv.Services.FocusZone.RecentRow ? _recentMatches :
                null;
            if (list == null || index < 0 || index >= list.Count) return null;
            return list[index];
        }

        // Handler for the F / XF86Yellow key. Toggles favorite for both teams of the
        // currently focused tile (or hero card). Also refreshes every visible tile's
        // heart indicator so the change is immediately reflected — without rebuilding
        // the row.
        private void HandleToggleFavoriteOnFocused()
        {
            try
            {
                CatalogMatch m;
                if (_focus.CurrentZone == StreamedTv.Services.FocusZone.Hero)
                {
                    m = _hero?.CurrentMatch;
                }
                else
                {
                    m = ResolveMatch(_focus.CurrentZone, _focus.CurrentIndex);
                }
                if (m == null)
                {
                    SetStatus("Nothing focused to favorite");
                    return;
                }

                string home = m.Teams?.Home?.Name;
                string away = m.Teams?.Away?.Name;
                int added = 0, removed = 0;
                if (!string.IsNullOrWhiteSpace(home))
                {
                    bool nowFav = _prefs.ToggleFavoriteTeam(home);
                    if (nowFav) added++; else removed++;
                }
                if (!string.IsNullOrWhiteSpace(away))
                {
                    bool nowFav = _prefs.ToggleFavoriteTeam(away);
                    if (nowFav) added++; else removed++;
                }

                RefreshAllVisibleFavoriteIndicators();
                SetStatus(added > 0 ? "Favorited " + added + " team(s)" : "Removed " + removed + " favorite(s)");
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("HandleToggleFavoriteOnFocused", ex);
            }
        }

        // Walk every TileView currently rendered and ask it to re-check its favorite
        // status. Cheap enough to do synchronously after a toggle (~40 tiles max).
        private void RefreshAllVisibleFavoriteIndicators()
        {
            try
            {
                foreach (RowView r in new[] { _liveRow, _scheduledRow, _recentRow })
                {
                    if (r == null) continue;
                    foreach (TileView t in r.Tiles)
                    {
                        try { t.RefreshFavoriteIndicator(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("RefreshAllVisibleFavoriteIndicators", ex);
            }
        }

        private void OnFocusChanged(object sender, StreamedTv.Services.FocusChangedEventArgs e)
        {
            try
            {
                _navPill.ClearFocus();
                _hero.SetFocused(false);
                _liveRow.ClearFocus();
                _scheduledRow.ClearFocus();
                _recentRow.ClearFocus();

                switch (e.Zone)
                {
                    case StreamedTv.Services.FocusZone.Nav:
                        _navPill.SetFocused(_navPill.SelectionForIndex(e.Index));
                        break;
                    case StreamedTv.Services.FocusZone.Hero:
                        _hero.SetFocused(true);
                        break;
                    case StreamedTv.Services.FocusZone.LiveRow:
                        _liveRow.SetFocusedIndex(e.Index);
                        break;
                    case StreamedTv.Services.FocusZone.ComingUpRow:
                        _scheduledRow.SetFocusedIndex(e.Index);
                        break;
                    case StreamedTv.Services.FocusZone.RecentRow:
                        _recentRow.SetFocusedIndex(e.Index);
                        break;
                }

                EnsureRowVisible(e.Zone);
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnFocusChanged", ex);
            }
        }

        // Total content height (status + nav + hero + 3 rows + gaps + padding) is ~1358 px
        // on the 1080-px TV viewport now that the Live row uses larger tiles, so the
        // Recent row's tiles fall further below the visible edge. Animate _root.Position.Y
        // upward when focus enters a row that would otherwise be clipped, and snap back
        // to 0 when focus returns to the upper rows.
        //
        // Offsets were picked to bring the targeted row's tiles fully into view on a
        // 1920x1080 reference. If the layout heights in Dimens.cs change again, revisit.
        private Animation _scrollAnimation;
        private float _currentScrollY;

        /// <summary>Repaint the app-wide vertical background gradient with a new
        /// SportPalette. Called once during BuildScene with the seeded palette and
        /// again whenever SportPalette.Changed fires.</summary>
        private void ApplyBackgroundGradient(StreamedTv.Theme.SportPalette.Palette palette)
        {
            if (_bgGradient == null || palette == null) return;
            try
            {
                PropertyMap gradient = new PropertyMap();
                gradient.Add(Visual.Property.Type, new PropertyValue((int)Visual.Type.Gradient));

                PropertyArray stopOffsets = new PropertyArray();
                stopOffsets.Add(new PropertyValue(0.0f));
                stopOffsets.Add(new PropertyValue(1.0f));
                gradient.Add(GradientVisualProperty.StopOffset, new PropertyValue(stopOffsets));

                PropertyArray stopColors = new PropertyArray();
                stopColors.Add(new PropertyValue(new Vector4(palette.BgGradientTop.R, palette.BgGradientTop.G, palette.BgGradientTop.B, 1f)));
                stopColors.Add(new PropertyValue(new Vector4(palette.BgGradientBottom.R, palette.BgGradientBottom.G, palette.BgGradientBottom.B, 1f)));
                gradient.Add(GradientVisualProperty.StopColor, new PropertyValue(stopColors));

                // Vertical wash: top of viewport → bottom of viewport. NUI gradient
                // positions are in [-1, 1] visual space (origin at center).
                gradient.Add(GradientVisualProperty.StartPosition, new PropertyValue(new Vector2(0f, -1f)));
                gradient.Add(GradientVisualProperty.EndPosition, new PropertyValue(new Vector2(0f, 1f)));
                gradient.Add(GradientVisualProperty.SpreadMethod, new PropertyValue((int)GradientVisualSpreadMethodType.Pad));

                _bgGradient.Background = gradient;
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("ApplyBackgroundGradient", ex);
            }
        }

        private void EnsureRowVisible(StreamedTv.Services.FocusZone zone)
        {
            try
            {
                if (_root == null) return;
                // Pan offsets are relative to the static topbar baseline. _root.Position.Y
                // sits at TopbarHeight in its rest state (so the brand + nav pill are
                // never overlapped by hero/rows). When focus moves down to ComingUp /
                // Recent rows, we pull _root upward by adding negative deltas.
                // Offsets re-tuned for RowHeight=340 (was 280): each row is ~60px taller,
                // so the previous 160 / 380 deltas no longer pulled the focused row
                // fully into the visible area.
                float targetY;
                switch (zone)
                {
                    case StreamedTv.Services.FocusZone.RecentRow:
                        // Pull further (was -520) so the recent row's bottom
                        // edge + focus-scaled tile shadow + breathing room
                        // all fit within the visible area below the topbar.
                        // Earlier value left ~50px clipped at the screen
                        // bottom on the focused tile.
                        targetY = TopbarHeight - 620f;
                        break;
                    case StreamedTv.Services.FocusZone.ComingUpRow:
                        targetY = TopbarHeight - 220f;
                        break;
                    default:
                        targetY = TopbarHeight;        // Nav / Hero / LiveRow: rest position (just under topbar)
                        break;
                }
                if (Math.Abs(_currentScrollY - targetY) < 0.5f) return;
                _currentScrollY = targetY;

                if (_scrollAnimation != null) _scrollAnimation.Stop();
                // EaseInOutSine for a continuous-feeling pan (no abrupt start
                // or stop). Slightly longer than the focus-scale animation so
                // the page settles AFTER the focused-tile border has finished
                // animating — avoids the jarring "two motions ending at
                // different times" feel.
                Animation a = new Animation(Dimens.AnimRowSwitch);
                a.AnimateTo(_root, "PositionY", targetY, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseInOutSine));
                a.Play();
                _scrollAnimation = a;
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("EnsureRowVisible", ex);
            }
        }

        private void OnTilePromoted(object sender, StreamedTv.Services.TilePromotedEventArgs e)
        {
            try
            {
                CatalogMatch m = ResolveMatch(e.Zone, e.Index);
                if (m == null) return;
                TileView.TileKind kind =
                    e.Zone == StreamedTv.Services.FocusZone.LiveRow ? TileView.TileKind.Live :
                    e.Zone == StreamedTv.Services.FocusZone.ComingUpRow ? TileView.TileKind.Scheduled :
                    TileView.TileKind.Recent;
                _hero.SetMatch(m, kind);
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnTilePromoted", ex);
            }
        }

        private void RefreshFocusZones()
        {
            try
            {
                int navCount = _navPill != null ? _navPill.ChipCount : 0;
                int liveCount = _liveRow != null ? _liveRow.Tiles.Count : 0;
                int upcomingCount = _scheduledRow != null ? _scheduledRow.Tiles.Count : 0;
                int recentCount = _recentRow != null ? _recentRow.Tiles.Count : 0;
                _focus.SetZoneCounts(navCount, liveCount, upcomingCount, recentCount);
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("RefreshFocusZones", ex);
            }
        }

        private void SeedInitialFocus()
        {
            try
            {
                // Hero is the user-facing "current selection" — focus lands there after every reload
                // so pressing OK plays the promoted match immediately. Down arrow drops into the rows.
                if (_liveMatches.Count > 0 || _scheduledMatches.Count > 0 || _recentMatches.Count > 0)
                {
                    _focus.SetFocus(StreamedTv.Services.FocusZone.Hero, 0);
                }
                else
                {
                    _focus.SetFocus(StreamedTv.Services.FocusZone.Nav, 0);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("SeedInitialFocus", ex);
            }
        }

        private async Task ReloadAsync()
        {
            try
            {
                await LoadActiveTabAsync();
                string apiState = _catalog.LastNetworkError == null ? "API OK" : "API: " + _catalog.LastNetworkError;
                SetStatus(apiState + " · " + _liveMatches.Count + " live · " + _scheduledMatches.Count + " upcoming · " + _recentMatches.Count + " recent");
            }
            catch (Exception ex)
            {
                SetStatus("Reload FAILED " + ex.GetType().Name + ": " + ex.Message);
                Diagnostics.LogError("ReloadAsync", ex);
            }
        }

        // Per-match stream-fetch cancellation. The catalog call can take a few
        // seconds; if the user backs out of the picker mid-fetch we want to
        // cancel it so its completion handler doesn't overwrite the listing's
        // status banner with stale "Streams loaded…" / "WebView playing…" text.
        private CancellationTokenSource _streamFetchCts;

        // Short-TTL cache of resolved stream lists per match. The
        // catalog deliberately bypasses its own 1h cache for streams
        // (signed manifest URLs expire in ~5-15min) so each call is
        // a fresh network round-trip — ~500ms on a warm backend,
        // longer cold. We trade a small TTL for fast picker re-opens
        // in the close-player→reopen-picker→try-different-stream
        // cycle the user actually does. 90s is well under the
        // signed-token expiry, so a cached entry is always playable.
        private string _cachedStreamsMatchId;
        private IReadOnlyList<StreamOption> _cachedStreams;
        private DateTime _cachedStreamsAt;
        private static readonly TimeSpan StreamsCacheTtl = TimeSpan.FromSeconds(90);

        // Tracks the match the player is CURRENTLY showing (or just
        // finished showing). On player exit we re-open the picker
        // for this match so the user can pick a different stream
        // source without going all the way back to the home tile.
        // Only set when the picker was actually used (multi-stream
        // matches); single-stream matches return to home as before.
        private CatalogMatch _currentlyPlayingMatch;
        private bool _currentlyPlayingHadPicker;

        private async Task PlayMatchAsync(CatalogMatch match)
        {
            if (match == null)
            {
                SetStatus("PlayMatchAsync: match is null");
                return;
            }
            // Cancel any previous in-flight stream fetch first.
            try { _streamFetchCts?.Cancel(); } catch { }
            _streamFetchCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            CancellationToken ct = _streamFetchCts.Token;

            // Open the picker SYNCHRONOUSLY with a "Loading streams…" placeholder
            // BEFORE awaiting the network call. Without this, OK on a tile fired
            // the fetch but the picker stayed invisible until the HTTP response
            // landed (~500 ms). Any Right keystroke during that window raced
            // into the picker the moment Open() ran, advancing focus from 0 to
            // 1 — exactly the bug the user reported as "press OK, nothing
            // happens, press Right, picker opens with stream #2 selected".
            // ShowLoadingMessage clears any prior streams + sets focus to -1
            // so navigation keys safely no-op until SetStreams is called below.
            if (_streamSwitcher != null)
            {
                _streamSwitcher.SetMatch(match);
                _streamSwitcher.ShowLoadingMessage("Loading streams…");
                _streamSwitcher.Open();
                _streamSwitcher.RaiseToTop();
            }

            // Re-fire DNS/TCP/TLS handshakes to wrapper hosts so the next
            // ShowPlayerView's first byte arrives faster. Safe no-op when a
            // stream is already loaded; gated internally on _currentUrl.
            try { _playerView?.PrewarmWrapperHosts(); } catch { }

            // Cache hit: the user just exited the player and is re-opening
            // the picker for the same match within the TTL. Skip the network
            // round-trip entirely.
            if (_cachedStreamsMatchId == match.Id
                && _cachedStreams != null
                && _cachedStreams.Count > 0
                && (DateTime.UtcNow - _cachedStreamsAt) < StreamsCacheTtl)
            {
                Diagnostics.Log("PlayMatchAsync: cache hit for " + match.Id);
                IReadOnlyList<StreamOption> cached = _cachedStreams;
                _currentlyPlayingMatch = match;
                _currentlyPlayingHadPicker = (cached.Count > 1);
                if (cached.Count == 1)
                {
                    _streamSwitcher?.Close();
                    ShowPlayerView(cached[0]);
                    SetStatus("WebView playing " + cached[0].Source + " · " + match.Title);
                    return;
                }
                if (_streamSwitcher != null)
                {
                    _streamSwitcher.SetStreams(cached, null);
                    SetStatus(cached.Count + " streams · pick one and press OK · ▼ to follow teams");
                }
                return;
            }

            try
            {
                SetStatus("Resolving streams for " + match.Title + "…");
                IReadOnlyList<StreamOption> streams = await _catalog.GetStreamsAsync(match, ct);
                if (ct.IsCancellationRequested) return;
                if (streams == null || streams.Count == 0)
                {
                    SetStatus("No streams returned for " + match.Title);
                    // Keep the picker open with the empty-state copy so the user
                    // sees that the request DID land, just produced no streams.
                    _streamSwitcher?.SetStreams(streams, null);
                    return;
                }
                // Cache the resolved streams + remember this match so the
                // close-player→reopen-picker path can skip the fetch.
                _cachedStreamsMatchId = match.Id;
                _cachedStreams = streams;
                _cachedStreamsAt = DateTime.UtcNow;
                _currentlyPlayingMatch = match;
                _currentlyPlayingHadPicker = (streams.Count > 1);
                if (streams.Count == 1)
                {
                    // Single stream → skip the picker, go straight to playback.
                    _streamSwitcher?.Close();
                    ShowPlayerView(streams[0]);
                    SetStatus("WebView playing " + streams[0].Source + " · " + match.Title);
                    return;
                }
                // Multiple streams → swap the loading placeholder for the real chips.
                if (_streamSwitcher != null)
                {
                    _streamSwitcher.SetStreams(streams, null);
                    SetStatus(streams.Count + " streams · pick one and press OK · ▼ to follow teams");
                }
            }
            catch (OperationCanceledException)
            {
                // Picker may have been dismissed mid-fetch; that's fine, no message.
            }
            catch (Exception ex)
            {
                SetStatus("Play FAILED " + ex.GetType().Name + ": " + ex.Message);
                Diagnostics.LogError("PlayMatchAsync", ex);
                _streamSwitcher?.ShowLoadingMessage("Couldn't load streams: " + ex.Message);
            }
        }

        private void OnStreamSelected(object sender, StreamSelectedEventArgs e)
        {
            try
            {
                if (e?.Stream == null) return;
                if (_streamSwitcher != null) _streamSwitcher.Close();
                ShowPlayerView(e.Stream);
                SetStatus("WebView playing " + e.Stream.Source);
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnStreamSelected", ex);
            }
        }

        // Triggered when the user presses OK on a favorite-team chip in the stream picker.
        // Toggle the persisted state and refresh every visible heart indicator. The modal
        // re-paints its own chip in its Activate handler.
        private void OnTeamFavoriteToggledFromSwitcher(object sender, TeamFavoriteToggleEventArgs e)
        {
            try
            {
                if (e == null || string.IsNullOrWhiteSpace(e.TeamName)) return;
                bool nowFav = _prefs.ToggleFavoriteTeam(e.TeamName);
                RefreshAllVisibleFavoriteIndicators();
                SetStatus((nowFav ? "★ Favorited " : "Removed favorite ") + e.TeamName);
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnTeamFavoriteToggledFromSwitcher", ex);
            }
        }

        // Triggered when the user presses OK on a "View team" chip in the stream picker.
        // ---- Profile selector handlers --------------------------------------
        private void OnProfileSelected(object sender, ProfileSelectedEventArgs e)
        {
            try
            {
                if (e == null || string.IsNullOrEmpty(e.ProfileId)) return;
                bool changed = !string.Equals(e.ProfileId, _profiles.ActiveId, StringComparison.Ordinal);
                _profiles.SetActive(e.ProfileId);
                _profileSelector?.Close();
                // Re-hydrate per-profile prefs (cache-first; seed from
                // the backend-bundled favourites if the local cache is
                // missing). Then mirror those prefs onto the home UI.
                // We DO this even when changed=false because on first
                // login the cached active profile id can match the
                // backend-stamped active id, but the home UI was built
                // by OnCreate with empty prefs (no fallback defaults
                // anymore) — without this refresh the nav pill stays
                // empty even though the More overlay shows the right
                // favourites.
                bool prefsCacheHit = _prefs.Reload(_profiles.ActiveId);
                if (!prefsCacheHit) _prefs.SeedFromProfile(_profiles.Active);
                FavoritesRegistry.Set(_prefs);
                ApplyPrefsToHomeUi();

                if (!changed)
                {
                    SetStatus("Welcome back, " + (_profiles.Active?.Name ?? "you"));
                    return;
                }
                // Different profile chosen — re-run the catalog flow so
                // the rows reflect the new profile's favourites +
                // last-viewed sport.
                ShowLoadingState(_activeSportId == ForYouSportId ? "For You" : _activeSportId);
                _ = ReloadAsync();
                SetStatus("Switched to " + (_profiles.Active?.Name ?? "profile"));
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnProfileSelected", ex);
            }
        }

        // Mirror the current _prefs values onto the home shell:
        // pick the active sport tab, set the nav-pill favourite chips,
        // align hero + sport palette. Called both at OnCreate (with
        // possibly-empty prefs) AND after every prefs hydrate (login,
        // profile switch, background backend refresh) so the chrome
        // never lags behind the underlying data.
        private void ApplyPrefsToHomeUi()
        {
            try
            {
                bool hasFavouritedTeams = _prefs.FavoriteTeams != null && _prefs.FavoriteTeams.Count > 0;
                _activeSportId = hasFavouritedTeams
                    ? ForYouSportId
                    : (string.IsNullOrEmpty(_prefs.LastSelectedSport) ? "cricket" : _prefs.LastSelectedSport);
                StreamedTv.Theme.SportPalette.SetCurrent(
                    _activeSportId == ForYouSportId ? null : _activeSportId);
                StreamedTv.Views.HeroView.ForceBundledHero(
                    _activeSportId == ForYouSportId ? "hero_foryou.jpg" : null);
                List<string> favs = new List<string>(_prefs.FavoriteSports);
                while (favs.Count < 3) favs.Add("");
                _navPill?.SetFavorites(favs);
                ApplyActiveChipFromActiveSport();
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("ApplyPrefsToHomeUi", ex);
            }
        }

        // Open the add-profile wizard (avatar pick → name → save). Wizard
        // commits via OnWizardCreated, which calls ProfileService.AddProfileFull
        // and auto-selects the new profile.
        private void OnAddProfileRequested(object sender, EventArgs e)
        {
            try
            {
                if (!_profiles.CanAddProfile)
                {
                    SetStatus("Profile limit reached — remove one first");
                    return;
                }
                _profileAddWizard.Reset();
                _profileAddWizard.Open();
                _profileAddWizard.RaiseToTop();
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnAddProfileRequested", ex);
            }
        }

        // ---- In-app chip + dropdown handlers --------------------------------

        private void OnProfileChipActivated(object sender, EventArgs e)
        {
            try
            {
                _profileDropdown.SetProfiles(_profiles.Profiles, _profiles.ActiveId);
                _profileDropdown.Open();
                _profileDropdown.RaiseToTop();
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnProfileChipActivated", ex);
            }
        }

        private void OnDropdownSwitchToProfile(object sender, string profileId)
        {
            try
            {
                _profileDropdown.Close();
                _profileChipFocused = false;
                _profileChip?.Blur();
                OnProfileSelected(this, new ProfileSelectedEventArgs(profileId));
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnDropdownSwitchToProfile", ex);
            }
        }

        private void OnDropdownOpenPicker(object sender, EventArgs e)
        {
            try
            {
                _profileDropdown.Close();
                _profileChipFocused = false;
                _profileChip?.Blur();
                _profileSelector.SetProfiles(_profiles.Profiles, _profiles.ActiveId);
                // Ensure normal switch-flow mode (in case ManageMode was
                // entered earlier and the picker was reopened via Switch).
                _profileSelector.ExitManageMode();
                _profileSelector.Open();
                _profileSelector.RaiseToTop();
                KickOffPickerBackgroundRefresh();
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnDropdownOpenPicker", ex);
            }
        }

        private void OnDropdownOpenManage(object sender, EventArgs e)
        {
            try
            {
                _profileDropdown.Close();
                _profileChipFocused = false;
                _profileChip?.Blur();
                _profileSelector.SetProfiles(_profiles.Profiles, _profiles.ActiveId);
                // ManageMode flips top-row tile activation to "open editor"
                // (vs the default "switch profile") and updates the title.
                _profileSelector.EnterManageMode();
                _profileSelector.Open();
                _profileSelector.RaiseToTop();
                KickOffPickerBackgroundRefresh();
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnDropdownOpenManage", ex);
            }
        }

        private void OnDropdownClosed(object sender, EventArgs e)
        {
            try { _profileDropdown.Close(); }
            catch (Exception ex) { Diagnostics.LogError("OnDropdownClosed", ex); }
        }

        // ---- Picker → editor handler ----------------------------------------

        private void OnSelectorEditProfileRequested(object sender, string profileId)
        {
            try
            {
                Profile p = FindProfile(profileId);
                if (p == null) return;
                _profileEdit.SetProfile(p);
                _profileEdit.Open();
                _profileEdit.RaiseToTop();
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnSelectorEditProfileRequested", ex);
            }
        }

        // ---- Editor handlers -------------------------------------------------

        private void OnEditRequestEditName(object sender, string currentName)
        {
            OpenKeyboardForName(currentName, "Profile name", text =>
            {
                _profileEdit.SetEditedName(text);
            });
        }

        private void OnEditRequestPickAvatar(object sender, string currentAvatar)
        {
            OpenAvatarPicker(currentAvatar, file =>
            {
                _profileEdit.SetEditedAvatar(file);
            });
        }

        private void OnEditSaved(object sender, EditedProfile e)
        {
            try
            {
                if (e == null) return;
                _profiles.UpdateProfile(e.ProfileId, e.Name, e.AvatarFileName);
                _profileEdit.Close();
                if (_profileSelector != null && _profileSelector.IsOpen)
                {
                    _profileSelector.SetProfiles(_profiles.Profiles, _profiles.ActiveId);
                }
                SetStatus("Profile saved");
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnEditSaved", ex);
            }
        }

        private void OnEditDeleteRequested(object sender, string profileId)
        {
            try
            {
                _profiles.DeleteProfile(profileId);
                _profileEdit.Close();
                if (_profileSelector != null && _profileSelector.IsOpen)
                {
                    _profileSelector.SetProfiles(_profiles.Profiles, _profiles.ActiveId);
                }
                SetStatus("Profile deleted");
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnEditDeleteRequested", ex);
            }
        }

        private void OnEditClosed(object sender, EventArgs e)
        {
            try { _profileEdit.Close(); }
            catch (Exception ex) { Diagnostics.LogError("OnEditClosed", ex); }
        }

        // ---- Wizard handlers -------------------------------------------------

        private void OnWizardRequestPickAvatar(object sender, string currentAvatar)
        {
            OpenAvatarPicker(currentAvatar, file =>
            {
                _profileAddWizard.SetPickedAvatar(file);
            });
        }

        private void OnWizardRequestEditName(object sender, string currentName)
        {
            OpenKeyboardForName(currentName, "Profile name", text =>
            {
                _profileAddWizard.SetEnteredName(text);
            });
        }

        private void OnWizardCreated(object sender, NewProfileDraft draft)
        {
            try
            {
                if (draft == null)
                {
                    _profileAddWizard.Close();
                    return;
                }
                Profile created = _profiles.AddProfileFull(draft.Name, draft.AvatarFileName);
                _profileAddWizard.Close();
                if (created == null)
                {
                    SetStatus("Profile limit reached");
                    return;
                }
                // Auto-select new profile so user lands in their own context.
                OnProfileSelected(this, new ProfileSelectedEventArgs(created.Id));
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnWizardCreated", ex);
            }
        }

        private void OnWizardCancelled(object sender, EventArgs e)
        {
            try
            {
                _profileAddWizard.Close();
                // First-launch invariant: the user MUST have at least
                // one profile to use the app (every downstream surface
                // assumes _profiles.Active != null). If they hit Cancel
                // before creating one, immediately re-open the wizard
                // — same Netflix-style "no exit until you've created a
                // profile" treatment.
                if (!_profiles.HasProfiles) OpenAddWizardForFirstProfile();
            }
            catch (Exception ex) { Diagnostics.LogError("OnWizardCancelled", ex); }
        }

        // ---- Sub-overlay orchestration helpers ------------------------------

        // Opens the on-screen keyboard with the given seed text + title. Calls
        // onSubmit(text) when the user presses Done (closes keyboard); closes
        // silently on Cancel. One-shot subscription pattern — handlers are
        // unbound on completion to avoid leaking into the next open.
        private void OpenKeyboardForName(string current, string title, Action<string> onSubmit)
        {
            if (_keyboard == null) return;
            if (_keyboardSubmittedHandler != null) _keyboard.Submitted -= _keyboardSubmittedHandler;
            if (_keyboardCancelledHandler != null) _keyboard.Cancelled -= _keyboardCancelledHandler;

            _keyboardSubmittedHandler = (s, text) =>
            {
                try
                {
                    _keyboard.Submitted -= _keyboardSubmittedHandler;
                    _keyboard.Cancelled -= _keyboardCancelledHandler;
                    _keyboard.Close();
                    onSubmit?.Invoke(text);
                }
                catch (Exception ex) { Diagnostics.LogError("OpenKeyboardForName.submit", ex); }
            };
            _keyboardCancelledHandler = (s, e) =>
            {
                try
                {
                    _keyboard.Submitted -= _keyboardSubmittedHandler;
                    _keyboard.Cancelled -= _keyboardCancelledHandler;
                    _keyboard.Close();
                }
                catch (Exception ex) { Diagnostics.LogError("OpenKeyboardForName.cancel", ex); }
            };

            _keyboard.Submitted += _keyboardSubmittedHandler;
            _keyboard.Cancelled += _keyboardCancelledHandler;
            _keyboard.InitialText = current ?? "";
            _keyboard.Title = title ?? "";
            _keyboard.Open();
            _keyboard.RaiseToTop();
        }

        // Opens the avatar picker with the given seed selection. Calls
        // onPick(fileName) on selection (closes picker); closes silently on
        // Cancel. Same one-shot subscription pattern as OpenKeyboardForName.
        private void OpenAvatarPicker(string current, Action<string> onPick)
        {
            if (_avatarPicker == null) return;
            if (_avatarPickedHandler != null) _avatarPicker.AvatarPicked -= _avatarPickedHandler;
            if (_avatarCancelledHandler != null) _avatarPicker.Cancelled -= _avatarCancelledHandler;

            _avatarPickedHandler = (s, file) =>
            {
                try
                {
                    _avatarPicker.AvatarPicked -= _avatarPickedHandler;
                    _avatarPicker.Cancelled -= _avatarCancelledHandler;
                    _avatarPicker.Close();
                    onPick?.Invoke(file);
                }
                catch (Exception ex) { Diagnostics.LogError("OpenAvatarPicker.pick", ex); }
            };
            _avatarCancelledHandler = (s, e) =>
            {
                try
                {
                    _avatarPicker.AvatarPicked -= _avatarPickedHandler;
                    _avatarPicker.Cancelled -= _avatarCancelledHandler;
                    _avatarPicker.Close();
                }
                catch (Exception ex) { Diagnostics.LogError("OpenAvatarPicker.cancel", ex); }
            };

            _avatarPicker.AvatarPicked += _avatarPickedHandler;
            _avatarPicker.Cancelled += _avatarCancelledHandler;
            _avatarPicker.SelectedFileName = current ?? "";
            _avatarPicker.Open();
            _avatarPicker.RaiseToTop();
        }

        private Profile FindProfile(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return null;
            foreach (Profile p in _profiles.Profiles)
            {
                if (string.Equals(p.Id, profileId, StringComparison.Ordinal)) return p;
            }
            return null;
        }

        // ---- Auth gate (LoginView) -----------------------------------------

        // Decides between LoginView (no/invalid JWT) and the profile
        // picker (valid JWT). Validation is best-effort: if the backend
        // is unreachable we still trust a stored JWT — the user can keep
        // using the app on a flaky network rather than being kicked to
        // login. /v1/me only forces logout on an explicit 401.
        private async System.Threading.Tasks.Task ShowAuthGateAsync()
        {
            try
            {
                Diagnostics.Log("ShowAuthGate: start, IsAuthenticated=" + _backendAuth.IsAuthenticated);
                bool hasValidJwt = false;
                if (_backendAuth.IsAuthenticated)
                {
                    using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    hasValidJwt = await _backendAuth.ValidateAsync(cts.Token).ConfigureAwait(true);
                    Diagnostics.Log("ShowAuthGate: ValidateAsync returned " + hasValidJwt + ", IsAuthenticated=" + _backendAuth.IsAuthenticated);
                    // ValidateAsync returns false on transient errors
                    // too — fall back to "trust the local JWT" so a
                    // network blip doesn't force re-login.
                    if (!hasValidJwt && _backendAuth.IsAuthenticated)
                    {
                        Diagnostics.Log("ShowAuthGate: validate failed but JWT still present, trusting local");
                        hasValidJwt = true;
                    }
                }
                if (hasValidJwt)
                {
                    Diagnostics.Log("ShowAuthGate: hasValidJwt=true → OpenSessionForCurrentUserAsync");
                    await OpenSessionForCurrentUserAsync().ConfigureAwait(true);
                }
                else
                {
                    Diagnostics.Log("ShowAuthGate: hasValidJwt=false → opening LoginView");
                    _loginView.Open();
                    _loginView.RaiseToTop();
                }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("ShowAuthGateAsync", ex);
                // Best-effort fallback: if anything goes wrong with
                // the auth check, default to login so the user has a
                // path forward instead of a stuck splash.
                _loginView.Open();
                _loginView.RaiseToTop();
            }
        }

        // Resolves which surface to show after a successful auth check.
        //
        //   1. Cache hit (Reload populated the list at OnCreate) → open picker.
        //   2. Cache miss → call backend. If backend returns profiles,
        //      cache them + open picker, AND seed UserPreferences from the
        //      active profile's bundled favourites if the local prefs file
        //      is missing too (this is the "signed in on a different TV"
        //      restore path).
        //   3. Backend also empty → open the AddWizard so the user creates
        //      their first profile. OnWizardCreated handles the rest
        //      (auto-selects, lands on home).
        //
        // Step 2/3 are skipped on a cache hit — we don't proactively poll
        // the backend on every launch since the cache is the source of
        // truth in steady state. ProfileService.UpdateFavorites + the
        // FireAndForget mutation hooks keep Firestore in sync as the user
        // edits.
        private async System.Threading.Tasks.Task OpenSessionForCurrentUserAsync()
        {
            try
            {
                Diagnostics.Log("OpenSession: start, HasProfiles=" + _profiles.HasProfiles);
                if (!_profiles.HasProfiles)
                {
                    using CancellationTokenSource loadCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    bool loaded = await _profiles.LoadFromBackendAsync(loadCts.Token).ConfigureAwait(true);
                    Diagnostics.Log("OpenSession: LoadFromBackendAsync returned " + loaded + ", HasProfiles=" + _profiles.HasProfiles);

                    // The active profile may have changed (or just been
                    // populated for the first time) after the backend
                    // load. Re-hydrate prefs against it AND seed from the
                    // backend profile's bundled favourites if the local
                    // prefs file doesn't exist yet.
                    if (_profiles.HasProfiles)
                    {
                        bool prefsCacheHit = _prefs.Reload(_profiles.ActiveId);
                        if (!prefsCacheHit) _prefs.SeedFromProfile(_profiles.Active);
                        FavoritesRegistry.Set(_prefs);
                        ApplyPrefsToHomeUi();
                    }
                }

                if (_profiles.HasProfiles)
                {
                    Diagnostics.Log("OpenSession: opening picker, " + _profiles.Profiles.Count + " profiles");
                    OpenProfilePickerForCurrentSession();
                }
                else
                {
                    Diagnostics.Log("OpenSession: no profiles → opening AddWizard");
                    OpenAddWizardForFirstProfile();
                }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OpenSessionForCurrentUserAsync", ex);
                // Defensive fallback: if anything went wrong, prefer
                // the wizard over a stranded splash so the user has a
                // path forward.
                if (_profiles.HasProfiles) OpenProfilePickerForCurrentSession();
                else OpenAddWizardForFirstProfile();
            }
        }

        private void OpenAddWizardForFirstProfile()
        {
            try
            {
                _profileAddWizard.Reset();
                _profileAddWizard.Open();
                _profileAddWizard.RaiseToTop();
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OpenAddWizardForFirstProfile", ex);
            }
        }

        private void OnLoginCompleted(object sender, string jwt)
        {
            try
            {
                Diagnostics.Log("OnLoginCompleted: closing login, wiping local profile cache");
                // Open the profile selector FIRST (with whatever cached
                // profiles we have — usually none right after a fresh
                // login) so the screen below the closing login view
                // is the picker, not the home screen behind it. Without
                // this swap the home screen flashed for ~100-1000ms
                // while OpenSessionForCurrentUserAsync's backend round-
                // trip ran. The picker can render an empty/loading
                // state safely; OpenSessionForCurrentUserAsync below
                // calls SetProfiles + Open again once profiles have
                // loaded.
                try
                {
                    _profileSelector?.SetProfiles(_profiles.Profiles, _profiles.ActiveId);
                    _profileSelector?.Open();
                    _profileSelector?.RaiseToTop();
                }
                catch (Exception ex) { Diagnostics.LogError("OnLoginCompleted.preopen-picker", ex); }

                _loginView.Close();
                // Just-completed login = different (or first-time) account.
                // The cache-first read might have populated _profiles from
                // a pre-upgrade auto-fallback "You" profile or from a
                // previous user's session. Wipe so OpenSessionForCurrentUserAsync
                // is guaranteed to consult the backend before deciding
                // between picker and AddWizard.
                _profiles.Clear();
                // Also retry crash-log shipping NOW. OnCreate's
                // ShipPending fired with no JWT (we hadn't paired
                // yet); without a retry here, anything captured
                // pre-pair (or earlier sessions on a fresh install)
                // would never ship until the NEXT launch. Particularly
                // important on the first-launch-after-install path —
                // catches the install + pair flow without the user
                // having to close+reopen the app.
                _ = StreamedTv.Services.CrashReporter.ShipPendingAsync(_backendAuth, _shutdown.Token);
                _ = OpenSessionForCurrentUserAsync();
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnLoginCompleted", ex);
            }
        }

        // Explicit "Sign out" button handler — fired from BOTH the
        // in-app dropdown (top-left chip → Sign out) AND the Manage
        // Profiles screen (Sign out pill next to Done). Wipes ALL
        // local session state (JWT, profile cache, prefs cache for
        // the active profile) so the next launch lands on the pairing
        // flow with no leftover data from this account.
        //
        // Order matters: drop the JWT FIRST (so any in-flight backend
        // request that races sees the unauth state), then close every
        // modal so we have a clean canvas, then open LoginView.
        private void OnSignOutRequested(object sender, EventArgs e)
        {
            try
            {
                Diagnostics.Log("OnSignOutRequested: explicit sign-out");
                _backendAuth.Logout();
                _profiles.Clear();
                // Close every profile-management surface that might be
                // currently open. Order doesn't matter — all of these
                // are no-ops when the surface is already closed.
                _profileDropdown?.Close();
                _profileEdit?.Close();
                _profileAddWizard?.Close();
                _avatarPicker?.Close();
                _keyboard?.Close();
                _profileSelector?.Close();
                _profileChipFocused = false;
                _profileChip?.Blur();
                _profileChip?.SetActiveProfile(null);
                _loginView?.Open();
                _loginView?.RaiseToTop();
                SetStatus("Signed out");
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnSignOutRequested", ex);
            }
        }

        private void OpenProfilePickerForCurrentSession()
        {
            try
            {
                _profileSelector.SetProfiles(_profiles.Profiles, _profiles.ActiveId);
                _profileSelector.Open();
                _profileSelector.RaiseToTop();
                KickOffPickerBackgroundRefresh();
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OpenProfilePickerForCurrentSession", ex);
            }
        }

        // Fires a non-blocking GET /v1/profiles immediately after the
        // picker opens. Cached profiles render instantly; if the
        // backend's view differs (profile added/renamed/deleted on
        // another device) the in-memory list gets replaced and the
        // ProfilesChanged subscriber re-binds the picker tiles in
        // place. On error we log and leave the cache alone — the
        // picker keeps working.
        //
        // Also re-seeds UserPreferences for the (possibly new) active
        // profile when the local prefs cache is missing for it; this
        // is what makes a profile created on another device pick up
        // its bundled favourites once the refresh lands.
        //
        // The async machinery yields to the NUI main loop on the
        // first await inside HttpClient, so we DON'T need Task.Run —
        // ConfigureAwait(true) brings us back on the UI thread for the
        // post-refresh UI mutations.
        private async System.Threading.Tasks.Task RefreshProfilesInBackgroundAsync()
        {
            if (!_backendAuth.IsAuthenticated) return;
            try
            {
                Diagnostics.Log("RefreshProfilesInBackground: start");
                bool hadProfilesBefore = _profiles.HasProfiles;
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                bool ok = await _profiles.LoadFromBackendAsync(cts.Token).ConfigureAwait(true);
                Diagnostics.Log("RefreshProfilesInBackground: done ok=" + ok + " hadBefore=" + hadProfilesBefore + " hasNow=" + _profiles.HasProfiles);
                if (!ok) return;

                if (_profiles.HasProfiles)
                {
                    bool prefsCacheHit = _prefs.Reload(_profiles.ActiveId);
                    if (!prefsCacheHit) _prefs.SeedFromProfile(_profiles.Active);
                    FavoritesRegistry.Set(_prefs);
                    ApplyPrefsToHomeUi();
                }
                else if (hadProfilesBefore && _profileSelector != null && _profileSelector.IsOpen)
                {
                    // Cache was stale: picker opened with profiles that
                    // the backend doesn't actually have anymore (e.g.
                    // they were deleted on another device, or the cache
                    // is from a previous account). Don't strand the user
                    // on an empty picker — close it and route to the
                    // AddWizard.
                    Diagnostics.Log("RefreshProfilesInBackground: backend empty, closing stale picker → wizard");
                    _profileSelector.Close();
                    OpenAddWizardForFirstProfile();
                }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("RefreshProfilesInBackgroundAsync", ex);
            }
        }

        private void KickOffPickerBackgroundRefresh()
        {
            // Fire-and-forget — the picker is already showing cached
            // state, so we don't await this. The ProfilesChanged
            // subscriber wired in BuildScene re-binds the picker tiles
            // when the response lands.
            _ = RefreshProfilesInBackgroundAsync();
        }

        // Closes the stream picker (so its dimmer doesn't double up under the hub) and
        // opens the team hub for the chosen team. The hub does its own background fetch.
        private void OnTeamHubRequestedFromSwitcher(object sender, TeamHubRequestedEventArgs e)
        {
            try
            {
                if (e == null || string.IsNullOrWhiteSpace(e.TeamName)) return;
                if (_streamSwitcher != null && _streamSwitcher.IsOpen)
                {
                    _streamSwitcher.Close();
                }
                if (_teamHub != null)
                {
                    _teamHub.OpenForTeam(e.TeamName, e.BadgeId);
                    _teamHub.RaiseToTop();
                    SetStatus("Loading team page for " + e.TeamName + "…");
                }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnTeamHubRequestedFromSwitcher", ex);
            }
        }

        private async void ShowPlayerView(StreamOption chosen)
        {
            try
            {
                if (_playerView == null) return;

                // Decide UP-FRONT whether we're going to attempt extraction so
                // we can surface a "Resolving stream…" status banner over the
                // player area immediately — otherwise the user stares at the
                // listing for ~12-20 s while ResolveOrPassThroughAsync grinds.
                // The predicate here mirrors StreamExtractorService's own gate
                // (feature flag + wrapper-host allowlist).
                bool willTryExtract = false;
                try
                {
                    willTryExtract = _appConfig != null
                        && _appConfig.GetFlag("useBackendStreamProxy", false)
                        && IsKnownWrapperHost(chosen?.EmbedUrl);
                }
                catch (Exception px)
                {
                    Diagnostics.LogError("ShowPlayerView.willTryExtract", px);
                    willTryExtract = false;
                }

                // NativePlayerView path is currently disabled: NUI VideoView
                // would not surface video on this Tizen TV firmware (audio
                // played, no picture, even with Underlay=false). Keeping the
                // class in the codebase so we can revisit, but route ALL
                // extraction-success flows through PlayerWebView with the
                // /v1/iptv/play wrapper page (which IS proven to render —
                // it's the same hls.js wrapper we ship for IPTV channels and
                // for the proxy path in earlier sessions).
                if (willTryExtract)
                {
                    ShowLoadingOverlay("Resolving stream…");
                }

                // Pre-resolve wrapper URLs (embedsports.top etc.) to our
                // hls.js wrapper page when the feature flag is on. Returns
                // `chosen` unchanged on disabled-flag, non-wrapper-host, or
                // any failure path — the original LoadStream contract is
                // preserved. ConfigureAwait(true): PlayerWebView.LoadStream
                // mutates NUI views and MUST run on the main thread (project
                // memory: "NUI requires UI mutations on main thread").
                StreamOption resolved = chosen;
                try
                {
                    string title = _currentlyPlayingMatch?.Title ?? "";
                    // Use _streamFetchCts.Token (already created per stream
                    // pick at PlayMatchAsync) so cancelling the fetch — e.g.
                    // user pressed Back on the loader, or picked another
                    // stream — also aborts the in-flight extractor HTTP call.
                    // Falls back to _shutdown.Token if PlayMatchAsync wasn't
                    // the entry point and didn't initialize the CTS.
                    CancellationToken extractCt = _streamFetchCts?.Token ?? _shutdown.Token;
                    resolved = await _streamExtractor.ResolveOrPassThroughAsync(chosen, title, extractCt).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    // User backed out of the loader. Don't open the player.
                    HideLoadingOverlay();
                    return;
                }
                catch (Exception rx)
                {
                    Diagnostics.LogError("StreamExtractorService", rx);
                    resolved = chosen;
                }
                _lastPlayedSource = resolved?.Source ?? chosen?.Source ?? "";

                // Whether extraction succeeded (resolved.EmbedUrl now points
                // at our hls.js wrapper) or failed (resolved == chosen with
                // the original embed URL), we use the same player path. The
                // wrapper-vs-original distinction is invisible to PlayerWebView
                // — it just loads whatever URL it's handed.
                HideLoadingOverlay();
                _playerView.LoadStream(resolved);
                _playerView.Show();
                _playerView.RaiseToTop();
                _playerVisible = true;
                // Pause any background HTTP polling while the player is on screen.
                // The 60s LiveScoreService poll competes with the WebView for Tizen's
                // shared HTTP stack and was a likely contributor to the "audio loaded,
                // video plane unstable" reliability bug.
                try { _liveScores?.Pause(); } catch { }

                // Smooth crossfade from the listing to the WebView surface.
                _playerCrossfade?.Stop();
                Animation a = new Animation(Dimens.AnimChromeFade);
                a.AnimateTo(_playerView, "Opacity", 1f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
                a.Play();
                _playerCrossfade = a;
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("ShowPlayerView", ex);
            }
        }

        // Mirrors StreamExtractorService.WrapperHosts. Kept in sync by hand
        // because duplicating five strings is cheaper than threading a
        // pre-flight check through the service interface.
        private static bool IsKnownWrapperHost(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            try
            {
                Uri u = new Uri(url, UriKind.Absolute);
                string h = u.Host?.ToLowerInvariant() ?? "";
                return h == "embedsports.top" || h == "www.embedsports.top"
                    || h == "pooembed.eu"     || h == "www.pooembed.eu";
            }
            catch
            {
                return false;
            }
        }

        // Show / hide / update the proxy-extract loading overlay. Safe to
        // call from anywhere on the main thread; no-ops if BuildScene
        // hasn't run yet (defensive against races during startup).
        private void ShowLoadingOverlay(string text)
        {
            try
            {
                if (_loadingOverlay == null) return;
                if (_loadingOverlayLabel != null) _loadingOverlayLabel.Text = text ?? "";
                _loadingOverlay.RaiseToTop();
                _loadingOverlay.Show();
                _loadingOverlayVisible = true;
            }
            catch (Exception ex) { Diagnostics.LogError("ShowLoadingOverlay", ex); }
        }

        private void HideLoadingOverlay()
        {
            try
            {
                _loadingOverlayVisible = false;
                if (_loadingOverlay == null) return;
                _loadingOverlay.Hide();
            }
            catch (Exception ex) { Diagnostics.LogError("HideLoadingOverlay", ex); }
        }

        // Cancel any in-flight extract + hide the loader. Called from the
        // Back/Escape key path while the loader is visible. Safe to call
        // multiple times — _streamFetchCts.Cancel is idempotent and the
        // hide path is null-checked.
        private void CancelLoaderAndExtract()
        {
            try { _streamFetchCts?.Cancel(); } catch { }
            HideLoadingOverlay();
        }

        private void OnPlayerExitRequested(object sender, EventArgs e)
        {
            try
            {
                // Either player can fire this — PlayerWebView via its
                // Back/Escape route, NativePlayerView via the same. We
                // tear down whichever was active; the post-teardown work
                // (status banner, picker reopen, _liveScores resume) runs
                // exactly once regardless of which surface was visible.
                bool webWasActive = _playerVisible && _playerView != null;
                bool nativeWasActive = _nativePlayerVisible && _nativePlayer != null;
                // Always tear down the loading overlay if we're exiting the
                // player flow at all — it's visible during the proxy extract
                // wait and would otherwise stay covering the listing if the
                // user backed out before the extract completed.
                HideLoadingOverlay();
                if (!webWasActive && !nativeWasActive)
                {
                    // Defensive: nothing to do, but keep the visible flags
                    // clean so subsequent key presses route correctly.
                    _playerVisible = false;
                    _nativePlayerVisible = false;
                    return;
                }

                // Mark not-visible IMMEDIATELY so any concurrent key press routes to the listing,
                // and stop the player(s) BEFORE the crossfade so the decoder pipeline / sockets
                // release while the fade-out is still running. This is the fix for the freeze
                // we hit when switching sport right after closing the player.
                _playerVisible = false;
                _nativePlayerVisible = false;
                // Kill any still-in-flight stream fetch so its completion can't
                // overwrite the listing's status banner with stale text.
                try { _streamFetchCts?.Cancel(); } catch { }

                if (webWasActive)
                {
                    try
                    {
                        _playerView.Stop();
                    }
                    catch (Exception sx)
                    {
                        Diagnostics.LogError("OnPlayerExitRequested.Stop", sx);
                    }
                }
                if (nativeWasActive)
                {
                    try
                    {
                        _nativePlayer.Stop();
                    }
                    catch (Exception sx)
                    {
                        Diagnostics.LogError("OnPlayerExitRequested.NativeStop", sx);
                    }
                }
                // Resume background polling now that the WebView is releasing its sockets.
                try { _liveScores?.Resume(); } catch { }

                if (webWasActive)
                {
                    _playerCrossfade?.Stop();
                    Animation a = new Animation(Dimens.AnimChromeFade);
                    a.AnimateTo(_playerView, "Opacity", 0f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseIn));
                    a.Finished += (s2, e2) =>
                    {
                        try
                        {
                            _playerView.Hide();
                        }
                        catch (Exception inner)
                        {
                            Diagnostics.LogError("OnPlayerExitRequested.fadeFinished", inner);
                        }
                    };
                    a.Play();
                    _playerCrossfade = a;
                }
                if (nativeWasActive)
                {
                    // Mirror the WebView fade — same animation shape, no
                    // socket-drain ordering needed since the native
                    // pipeline has no chromium under it.
                    _nativePlayerCrossfade?.Stop();
                    Animation na = new Animation(Dimens.AnimChromeFade);
                    na.AnimateTo(_nativePlayer, "Opacity", 0f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseIn));
                    na.Finished += (s2, e2) =>
                    {
                        try
                        {
                            _nativePlayer.Hide();
                        }
                        catch (Exception inner)
                        {
                            Diagnostics.LogError("OnPlayerExitRequested.nativeFadeFinished", inner);
                        }
                    };
                    na.Play();
                    _nativePlayerCrossfade = na;
                }
                // Sports streams have multiple sources (admin/echo/delta/...) so
                // exiting routes the user back to the StreamSwitcher to pick
                // another. IPTV channels have one stream — there's nothing to
                // pick — so we skip the picker and let the underlying IPTV home
                // (already in the layer beneath the now-fading player) take focus
                // naturally. Source string convention: IPTV streams use
                // "iptv-org:<country>"; everything else is treated as sports.
                bool wasIptv = !string.IsNullOrEmpty(_lastPlayedSource)
                    && _lastPlayedSource.StartsWith("iptv-org:", StringComparison.OrdinalIgnoreCase);
                if (wasIptv)
                {
                    SetStatus("Returned to channels");
                }
                else
                {
                    ReopenPickerForLastMatch();
                    SetStatus("Returned to listing");
                }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("OnPlayerExitRequested", ex);
            }
        }

        // After the player fades out, re-open the picker for the match
        // the user just watched so they can pick a different stream
        // source without round-tripping back through the home tile.
        // Single-stream matches (no picker on the way in) and IPTV
        // channels (don't set _currentlyPlayingMatch) naturally fall
        // through and return to whatever surface is behind the player.
        private void ReopenPickerForLastMatch()
        {
            try
            {
                if (_currentlyPlayingMatch == null) return;
                if (!_currentlyPlayingHadPicker) return;
                if (_streamSwitcher == null) return;

                if (_cachedStreamsMatchId == _currentlyPlayingMatch.Id
                    && _cachedStreams != null
                    && _cachedStreams.Count > 1
                    && (DateTime.UtcNow - _cachedStreamsAt) < StreamsCacheTtl)
                {
                    _streamSwitcher.SetMatch(_currentlyPlayingMatch);
                    _streamSwitcher.SetStreams(_cachedStreams, null);
                    _streamSwitcher.Open();
                    _streamSwitcher.RaiseToTop();
                    return;
                }
                // Cache stale → re-fetch with fresh signed URLs.
                _ = PlayMatchAsync(_currentlyPlayingMatch);
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("ReopenPickerForLastMatch", ex);
            }
        }

        protected override void OnTerminate()
        {
            try
            {
                _shutdown.Cancel();
                if (_playerView != null)
                {
                    try { _playerView.Stop(); } catch { }
                }
                if (_nativePlayer != null)
                {
                    try { _nativePlayer.Stop(); } catch { }
                }
                _player.StopAsync().Wait(500);
            }
            catch
            {
            }
            base.OnTerminate();
        }

        // ---- IPTV (iptv-org) test path ----------------------------------
        // Plumbing for the Live TV overlay opened on the Red remote
        // button. Slated for removal once the iptv-org experiment
        // wraps up — kept self-contained so the deletion is one
        // file + this region + the field.

        // ---- Inline Live home surface (IptvHomeView) -----------
        //
        // Replaces the modal overlay path. EnterLiveMode swaps the
        // home-screen content (hide hero + sport rows; show
        // _iptvHome). _iptvHomeFocusOwned tracks where keypresses
        // route — true while focus is in the surface, flipped false
        // by OnIptvHomeFocusEscapeUp when the user D-pad-Ups off
        // the top row back to the nav pill (surface stays visible).
        // ExitLiveMode reverses both: re-show hero + rows, hide
        // surface. Both are no-ops when the surface isn't currently
        // showing, so it's safe to call from sport / ForYou chip
        // activation paths blindly.

        private bool CanEnterLiveMode()
        {
            if (_iptvHome == null) return false;
            if (_loginView != null && _loginView.IsOpen) return false;
            if (_profileSelector != null && _profileSelector.IsOpen) return false;
            if (_profileAddWizard != null && _profileAddWizard.IsOpen) return false;
            if (_profileEdit != null && _profileEdit.IsOpen) return false;
            if (_keyboard != null && _keyboard.IsOpen) return false;
            if (_avatarPicker != null && _avatarPicker.IsOpen) return false;
            if (_moreOverlay != null && _moreOverlay.IsOpen) return false;
            if (_streamSwitcher != null && _streamSwitcher.IsOpen) return false;
            if (_teamHub != null && _teamHub.IsOpen) return false;
            if (_playerVisible) return false;
            if (_nativePlayerVisible) return false;
            return true;
        }

        private void EnterLiveMode()
        {
            try
            {
                if (_iptvHome == null) return;
                // Hide hero + sport rows so the LinearLayout reflows
                // and _iptvHome takes the freed vertical space. Same
                // height=0 trick ApplyRowVisibility uses.
                if (_hero != null) { _hero.HeightSpecification = 0; _hero.Hide(); }
                ApplyRowVisibility(_liveRow, false);
                ApplyRowVisibility(_scheduledRow, false);
                ApplyRowVisibility(_recentRow, false);

                _iptvHome.SetActive(true);
                _navPill?.SetActive(NavPillView.NavSelection.LiveTV);
                // Only show "Loading channels…" when we know we're about to
                // hit the network. Cache hits return instantly; flashing
                // the loading message anyway makes the user think the
                // catalog is uncached even though it isn't.
                if (!_iptvCatalog.HasFreshCache)
                {
                    _iptvHome.SetStatus("Loading channels…");
                }
                EnterIptvHomeFocus();
                _ = LoadIptvHomeChannelsAsync();
            }
            catch (Exception ex) { Diagnostics.LogError("EnterLiveMode", ex); }
        }

        private void ExitLiveMode()
        {
            try
            {
                if (_iptvHome == null) return;
                if (!_iptvHome.IsActive && !_iptvHomeFocusOwned) return;

                _iptvHomeFocusOwned = false;
                _iptvHome.ClearFocus();
                _iptvHome.SetActive(false);

                // Re-show hero + sport rows. The row visibility
                // (live/scheduled/recent) follows whether each list
                // has content — same bookkeeping as the regular
                // load path. ReloadAsync will re-evaluate when the
                // caller switches sport; here we just re-show what
                // the catalog state currently warrants.
                if (_hero != null)
                {
                    _hero.HeightSpecification = StreamedTv.Theme.Dimens.HeroHeight;
                    _hero.Show();
                }
                ApplyRowVisibility(_liveRow, _liveMatches != null && _liveMatches.Count > 0);
                ApplyRowVisibility(_scheduledRow, _scheduledMatches != null && _scheduledMatches.Count > 0);
                ApplyRowVisibility(_recentRow, _recentMatches != null && _recentMatches.Count > 0);
            }
            catch (Exception ex) { Diagnostics.LogError("ExitLiveMode", ex); }
        }

        // Hand focus from the nav pill (or whatever else owned it)
        // INTO the surface. Drops the nav-pill focus visual so the
        // user sees the focus ring move into the IPTV row, then
        // re-applies the surface's focus visual at its current
        // (_focusRow, _focusCol).
        //
        // SetActive(true) early-returns when IsActive is already
        // true — which is the case on Up-escape→Down-re-enter
        // (surface stays visible, only the focus pointer moves up
        // and back). RefreshFocusVisual is the explicit re-paint
        // that handles that case; without it the surface re-gains
        // input but no tile shows highlighted ("selector vanishes"
        // bug).
        private void EnterIptvHomeFocus()
        {
            try
            {
                if (_iptvHome == null) return;
                _iptvHomeFocusOwned = true;
                _navPill?.ClearFocus();
                _iptvHome.SetActive(true);
                _iptvHome.RefreshFocusVisual();
            }
            catch (Exception ex) { Diagnostics.LogError("EnterIptvHomeFocus", ex); }
        }

        // Fired by IptvHomeView when D-pad-Up walks off row 0.
        // Surface stays visible — only the focus moves up to the
        // nav pill, parked on the Live chip so D-pad-Down
        // re-enters the surface.
        //
        // We call _navPill.SetFocused() DIRECTLY in addition to
        // _focus.SetFocus() because FocusManager.SetFocus early-
        // returns when zone+index already match its current state —
        // and CurrentZone/CurrentIndex were never moved away from
        // Nav+LiveTV during the surface session (we only flipped
        // _iptvHomeFocusOwned). Without the direct call, no
        // FocusChanged event fires, OnFocusChanged doesn't run,
        // and the chip scale-up + AnimateBarLift never happen —
        // the user-visible "pop" feels broken.
        private void OnIptvHomeFocusEscapeUp(object sender, EventArgs e)
        {
            try
            {
                _iptvHomeFocusOwned = false;
                _iptvHome?.ClearFocus();
                int liveIdx = _navPill?.IndexForSelection(NavPillView.NavSelection.LiveTV) ?? 0;
                _focus.SetFocus(StreamedTv.Services.FocusZone.Nav, liveIdx);
                // Drive the visual refresh directly so the chip pops
                // even when FocusManager's SetFocus was a no-op.
                _navPill?.SetFocused(NavPillView.NavSelection.LiveTV);
            }
            catch (Exception ex) { Diagnostics.LogError("OnIptvHomeFocusEscapeUp", ex); }
        }

        private async System.Threading.Tasks.Task LoadIptvHomeChannelsAsync()
        {
            try
            {
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                System.Collections.Generic.IReadOnlyList<StreamedTv.Models.IptvChannel> chans =
                    await _iptvCatalog.GetSportsChannelsAsync(cts.Token).ConfigureAwait(true);
                if (_iptvHome == null || !_iptvHome.IsActive) return; // user backed out
                _iptvHome.SetChannels(chans);
                if (chans == null || chans.Count == 0)
                {
                    _iptvHome.SetStatus("No channels available — try again later.");
                }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("LoadIptvHomeChannelsAsync", ex);
                try { _iptvHome?.SetStatus("Couldn't load channels (network error)."); } catch { }
            }
        }

        // ---- Legacy modal overlay (kept one cycle for safety) --

        // Open allowed only when no other modal owns the screen.
        // Includes the player itself — pressing Red mid-stream
        // should NOT pop the channel grid over the playback.
        private bool CanOpenIptvOverlay()
        {
            if (_iptvOverlay == null) return false;
            if (_iptvOverlay.IsOpen) return false;
            if (_loginView != null && _loginView.IsOpen) return false;
            if (_profileSelector != null && _profileSelector.IsOpen) return false;
            if (_profileAddWizard != null && _profileAddWizard.IsOpen) return false;
            if (_profileEdit != null && _profileEdit.IsOpen) return false;
            if (_keyboard != null && _keyboard.IsOpen) return false;
            if (_avatarPicker != null && _avatarPicker.IsOpen) return false;
            if (_moreOverlay != null && _moreOverlay.IsOpen) return false;
            if (_streamSwitcher != null && _streamSwitcher.IsOpen) return false;
            if (_teamHub != null && _teamHub.IsOpen) return false;
            if (_playerVisible) return false;
            if (_nativePlayerVisible) return false;
            return true;
        }

        private void OpenIptvOverlay()
        {
            try
            {
                // Same cache-aware loading flash logic as the IPTV home
                // path. Without the guard the overlay always shows
                // "Loading channels…" on open even when the cache is
                // already warm.
                if (!_iptvCatalog.HasFreshCache)
                {
                    _iptvOverlay.SetStatus("Loading channels…");
                }
                _iptvOverlay.Open();
                _iptvOverlay.RaiseToTop();
                _ = LoadIptvChannelsAsync();
            }
            catch (Exception ex) { Diagnostics.LogError("OpenIptvOverlay", ex); }
        }

        private void CloseIptvOverlay()
        {
            try { _iptvOverlay?.Close(); }
            catch (Exception ex) { Diagnostics.LogError("CloseIptvOverlay", ex); }
        }

        private async System.Threading.Tasks.Task LoadIptvChannelsAsync()
        {
            try
            {
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                System.Collections.Generic.IReadOnlyList<StreamedTv.Models.IptvChannel> chans =
                    await _iptvCatalog.GetSportsChannelsAsync(cts.Token).ConfigureAwait(true);
                if (_iptvOverlay == null || !_iptvOverlay.IsOpen) return; // user backed out
                _iptvOverlay.SetChannels(chans);
                if (chans == null || chans.Count == 0)
                {
                    _iptvOverlay.SetStatus("No channels available — try again later.");
                }
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("LoadIptvChannelsAsync", ex);
                try { _iptvOverlay?.SetStatus("Couldn't load channels (network error)."); } catch { }
            }
        }

        // Synthesize a StreamOption from the iptv channel + hand to
        // PlayerWebView. PlayerWebView treats the play URL as any
        // other wrapper page — its existing UI (overlay, hint,
        // status, key handling) all apply. Closes the overlay
        // first so the player ends up as the topmost surface.
        private void OnIptvChannelActivated(object sender, StreamedTv.Models.IptvChannel ch)
        {
            try
            {
                if (ch == null) return;
                string playUrl = _iptvCatalog.BuildPlayUrl(ch);
                if (string.IsNullOrEmpty(playUrl)) return;
                CloseIptvOverlay();
                StreamedTv.Models.StreamOption opt = new StreamedTv.Models.StreamOption
                {
                    EmbedUrl = playUrl,  // PlaybackUrl getter returns EmbedUrl
                    Source = "iptv-org:" + (ch.Country ?? ""),
                };
                _lastPlayedSource = opt.Source ?? "";
                _playerView.LoadStream(opt);
                _playerVisible = true;
                _playerView.Show();
                _playerView.RaiseToTop();
                Animation a = new Animation(160);
                a.AnimateTo(_playerView, "Opacity", 1f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
                a.Play();
                Diagnostics.Log("IPTV play: " + ch.Name + " → " + playUrl);
            }
            catch (Exception ex) { Diagnostics.LogError("OnIptvChannelActivated", ex); }
        }

        private static void Main(string[] args)
        {
            // NOTE: handlers for AppDomain.UnhandledException +
            // TaskScheduler.UnobservedTaskException are now wired in
            // CrashReporter.Init() (called from OnCreate). Used to
            // be wired here too, which produced two banner copies in
            // every shipped log (Context: UnhandledException AND
            // Context: AppDomain.UnhandledException). The single
            // wiring inside CrashReporter is enough; the banner says
            // "Context: AppDomain.UnhandledException" so we know
            // which path it came from.
            //
            // We can't suppress AppDomain.UnhandledException — the
            // .NET runtime terminates the process after the handler
            // returns regardless. Defence has to be inside the code
            // paths that throw (catch + log), not at the AppDomain
            // boundary.
            Program app = new Program();
            app.Run(args);
        }
    }
}
