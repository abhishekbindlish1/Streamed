using System;
using System.Threading;
using StreamedTv.Services;
using StreamedTv.Theme;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace StreamedTv.Views
{
    /// <summary>
    /// Full-screen login flow that drives the pairing handshake. Shows
    /// the user-facing code + a QR for the web pairing page + a poll
    /// status, and raises <see cref="LoggedIn"/> the moment the
    /// backend says the code was claimed.
    ///
    /// Polls the backend every <see cref="PollIntervalMs"/> ms while
    /// open. Auto-rotates the code on TTL expiry. Network errors are
    /// surfaced into the status caption so the user sees what's
    /// happening; the polling loop keeps trying.
    ///
    /// Layout (top to bottom):
    ///   1. "STREAMED" wordmark
    ///   2. "Sign in to Streamed" title
    ///   3. "Pair this TV with your account" subtitle
    ///   4. The pairing code in oversize monospace
    ///   5. QR for the web pairing URL
    ///   6. The pairing URL text (so users without a phone camera can type it)
    ///   7. A status caption ("Waiting for sign-in…", "Network error", etc.)
    /// </summary>
    public sealed class LoginView : View
    {
        private const int PollIntervalMs = 3000;

        // Layout
        private readonly View _column;
        private readonly TextLabel _codeLabel;
        private readonly TextLabel _urlLabel;
        private readonly TextLabel _statusLabel;
        private readonly ImageView _qrImage;

        private readonly BackendAuthService _auth;
        private CancellationTokenSource _cts;
        // Fully qualified — System.Threading also defines Timer with a
        // different shape, and we want the NUI scheduler that fires on
        // the UI thread.
        private Tizen.NUI.Timer _pollTimer;
        private string _pollToken;
        private DateTime _expiresAt;
        private Animation _modalOpacityAnim;
        private bool _refreshingCode;

        public bool IsOpen { get; private set; }

        /// <summary>Raised once the backend confirms the user has
        /// claimed the code on the web. Payload is the JWT (also
        /// already persisted by <see cref="BackendAuthService.StoreJwt"/>).</summary>
        public event EventHandler<string> LoggedIn;

        public LoginView(BackendAuthService auth)
        {
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));

            // Full-screen opaque BgBase — login is the gate; nothing
            // should bleed through behind it.
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.FillToParent;
            BackgroundColor = Tokens.BgBase;
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
                    CellPadding = new Size2D(0, 18),
                },
            };
            Add(_column);

            _column.Add(BuildLabel("STREAMED", 14f, Tokens.AccentLive, "bold", new Extents(0, 0, 0, 4)));
            _column.Add(BuildLabel("Sign in to Streamed", 44f, Tokens.TextPrimary, "bold", new Extents(0, 0, 0, 6)));
            _column.Add(BuildLabel("Pair this TV with your account", 22f, Tokens.TextSecondary, "medium", new Extents(0, 0, 0, 36)));

            _codeLabel = new TextLabel
            {
                Text = "      ",
                TextColor = Tokens.TextPrimary,
                PointSize = 72f,
                FontFamily = "monospace",
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Margin = new Extents(0, 0, 0, 30),
            };
            _column.Add(_codeLabel);

            _qrImage = new ImageView
            {
                ResourceUrl = HeroView.ResolveBundledAssetPath("pairing_qr.png"),
                WidthSpecification = 280,
                HeightSpecification = 280,
                FittingMode = FittingModeType.ScaleToFill,
                DesiredWidth = 560,
                DesiredHeight = 560,
                Margin = new Extents(0, 0, 0, 22),
            };
            _column.Add(_qrImage);

            _urlLabel = new TextLabel
            {
                Text = ShortUrl(_auth.BaseUrl) + "/pair",
                TextColor = Tokens.TextSecondary,
                PointSize = 18f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("medium")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Margin = new Extents(0, 0, 0, 30),
            };
            _column.Add(_urlLabel);

            _statusLabel = new TextLabel
            {
                Text = "",
                TextColor = Tokens.TextTertiary,
                PointSize = 14f,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue("medium")),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
            };
            _column.Add(_statusLabel);

            Opacity = 0f;
            Hide();
        }

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            Show();
            RaiseToTop();
            _modalOpacityAnim?.Stop();
            _modalOpacityAnim = new Animation(Dimens.AnimChromeFade);
            _modalOpacityAnim.AnimateTo(this, "Opacity", 1f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            _modalOpacityAnim.Play();

            _ = RefreshCodeAsync();
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;

            _cts?.Cancel();
            _cts = null;
            CancelPollTimer();

            _modalOpacityAnim?.Stop();
            _modalOpacityAnim = new Animation(Dimens.AnimChromeFade);
            _modalOpacityAnim.AnimateTo(this, "Opacity", 0f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseIn));
            _modalOpacityAnim.Finished += (s, e) => Hide();
            _modalOpacityAnim.Play();
        }

        /// <summary>The host's key handler routes here while LoginView
        /// is on top. We deliberately ignore Back — login is the gate;
        /// the user's only path forward is to claim the code on their
        /// phone. (App-level Back-to-exit still works at the OS level.)</summary>
        public void HandleKey(string key)
        {
            // Future: any "refresh code now" key could go here.
            _ = key;
        }

        // ---- Polling internals ----

        private async System.Threading.Tasks.Task RefreshCodeAsync()
        {
            if (_refreshingCode) return;
            _refreshingCode = true;
            try
            {
                CancelPollTimer();
                _pollToken = null;
                SetStatus("Loading code…");
                _codeLabel.Text = "      ";
                CancellationToken ct = _cts != null ? _cts.Token : CancellationToken.None;
                BackendAuthService.PairInitResult init = await _auth.PairInitAsync(ct).ConfigureAwait(true);
                if (!IsOpen || ct.IsCancellationRequested) return;
                _pollToken = init.PollToken;
                _expiresAt = init.ExpiresAt;
                _codeLabel.Text = FormatCode(init.Code);
                SetStatus("Waiting for sign-in on your phone…");
                StartPollTimer();
            }
            catch (Exception ex)
            {
                if (!IsOpen) return;
                SetStatus("Couldn't reach backend: " + Truncate(ex.Message, 80));
                // Retry the init in 5s — likely a flaky connection.
                _ = System.Threading.Tasks.Task.Delay(5000).ContinueWith(_ =>
                {
                    if (IsOpen) _ = RefreshCodeAsync();
                });
            }
            finally
            {
                _refreshingCode = false;
            }
        }

        private void StartPollTimer()
        {
            CancelPollTimer();
            _pollTimer = new Tizen.NUI.Timer(PollIntervalMs);
            _pollTimer.Tick += OnPollTick;
            _pollTimer.Start();
        }

        private void CancelPollTimer()
        {
            if (_pollTimer != null)
            {
                try { _pollTimer.Stop(); } catch { }
                _pollTimer = null;
            }
        }

        private bool OnPollTick(object source, Tizen.NUI.Timer.TickEventArgs e)
        {
            if (!IsOpen) return false;

            // Code-expiry check — Firestore TTL will eventually clear
            // the doc, but we want to refresh early so the user always
            // has a usable code on screen.
            if (DateTime.UtcNow > _expiresAt.AddSeconds(-5))
            {
                _ = RefreshCodeAsync();
                return false;
            }
            if (string.IsNullOrEmpty(_pollToken)) return true;

            _ = PollOnceAsync();
            return true;
        }

        private async System.Threading.Tasks.Task PollOnceAsync()
        {
            try
            {
                CancellationToken ct = _cts != null ? _cts.Token : CancellationToken.None;
                BackendAuthService.PairPollResult result = await _auth.PairPollAsync(_pollToken, ct).ConfigureAwait(true);
                if (!IsOpen || ct.IsCancellationRequested) return;
                if (result == null) return;
                if (string.Equals(result.Status, "complete", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(result.Jwt))
                {
                    _auth.StoreJwt(result.Jwt);
                    SetStatus("Linked! Loading your profile…");
                    CancelPollTimer();
                    LoggedIn?.Invoke(this, result.Jwt);
                    return;
                }
                if (string.Equals(result.Status, "expired", StringComparison.OrdinalIgnoreCase))
                {
                    _ = RefreshCodeAsync();
                    return;
                }
                // Status == "pending" — keep polling, no UI change.
            }
            catch (Exception ex)
            {
                if (!IsOpen) return;
                SetStatus("Connection problem (will retry)…  " + Truncate(ex.Message, 60));
            }
        }

        private void SetStatus(string text)
        {
            if (_statusLabel != null) _statusLabel.Text = text ?? "";
        }

        // ---- Helpers ----

        // FormatCode inserts a thin space between the two halves of a
        // 6-char code so it reads as "ABC DEF" rather than a wall of
        // characters. Easier to copy correctly to a phone keyboard.
        private static string FormatCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return "      ";
            if (code.Length == 6) return code.Substring(0, 3) + "  " + code.Substring(3);
            return code;
        }

        // ShortUrl strips the "https://" prefix from the base URL so
        // the on-screen text is shorter (Cloud Run URLs are long).
        // The QR encodes the full URL; this is just for the eyeball
        // fallback path.
        private static string ShortUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url.Substring("https://".Length);
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))  return url.Substring("http://".Length);
            return url;
        }

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= n) return s ?? "";
            return s.Substring(0, n) + "…";
        }

        private static TextLabel BuildLabel(string text, float pointSize, Color color, string weight, Extents margin)
        {
            return new TextLabel
            {
                Text = text,
                TextColor = color,
                PointSize = pointSize,
                FontFamily = Tokens.FontFamily,
                FontStyle = new PropertyMap().Add("weight", new PropertyValue(weight)),
                MultiLine = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                WidthSpecification = LayoutParamPolicies.WrapContent,
                HeightSpecification = LayoutParamPolicies.WrapContent,
                Margin = margin,
            };
        }
    }
}
