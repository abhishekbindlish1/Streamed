using StreamedTv.Services;

namespace StreamedTv.Views
{
    public partial class LoginPage : ContentPage
    {
        private CancellationTokenSource _cts;

        public LoginPage()
        {
            InitializeComponent();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _ = StartPairingAsync(_cts.Token);
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _cts?.Cancel();
        }

        private async Task StartPairingAsync(CancellationToken ct)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                PairCode.Text = "Loading…";
                Spinner.IsRunning = true;
            });

            try
            {
                var auth = ServiceLocator.Auth;
                var init = await auth.PairInitAsync(ct).ConfigureAwait(true);

                string formatted = init.Code?.Length == 6
                    ? init.Code.Substring(0, 3) + " " + init.Code.Substring(3)
                    : init.Code ?? "------";

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    PairCode.Text = formatted;
                    Spinner.IsRunning = false;
                });

                await PollAsync(auth, init.PollToken, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // navigating away — normal
            }
            catch (Exception ex)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    PairCode.Text = "Error – retrying…";
                    Spinner.IsRunning = true;
                });
                try { await Task.Delay(5000, ct).ConfigureAwait(true); } catch { return; }
                if (!ct.IsCancellationRequested)
                    await StartPairingAsync(ct).ConfigureAwait(true);
            }
        }

        private async Task PollAsync(BackendAuthService auth, string pollToken, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(3000, ct).ConfigureAwait(true); } catch { return; }
                if (ct.IsCancellationRequested) return;

                try
                {
                    var result = await auth.PairPollAsync(pollToken, ct).ConfigureAwait(true);
                    if (result.Status == "complete")
                    {
                        auth.StoreJwt(result.Jwt);
                        await Shell.Current.GoToAsync("//MainPage").ConfigureAwait(true);
                        return;
                    }
                    if (result.Status == "expired")
                    {
                        await StartPairingAsync(ct).ConfigureAwait(true);
                        return;
                    }
                    // "pending" — keep polling
                }
                catch (OperationCanceledException) { return; }
                catch
                {
                    // transient error — keep polling
                }
            }
        }
    }
}
