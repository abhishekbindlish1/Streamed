using StreamedTv.Services;

namespace StreamedTv.Views;

public partial class LoginPage : ContentPage
{
    private CancellationTokenSource? _cts;

    public LoginPage()
    {
        InitializeComponent();
        FixtureNotice.IsVisible = ServiceLocator.IsPairingFixture;
        Controls.RemoteButtonStyle.Apply(RetryButton);
        PairingAddress.Text = "Visit " + ServiceLocator.Auth.BaseUrl + "/pair on your phone and enter this code.";
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        BeginPairing();
    }

    protected override void OnDisappearing()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        base.OnDisappearing();
    }

    private void BeginPairing()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _ = RunPairingAsync(_cts.Token);
    }

    private void OnRetryClicked(object sender, EventArgs e) => BeginPairing();

    private async Task RunPairingAsync(CancellationToken ct)
    {
        var auth = ServiceLocator.Auth;
        try
        {
            // Expired codes restart this loop without accumulating recursive
            // pairing/polling tasks for the lifetime of the sign-in screen.
            while (!ct.IsCancellationRequested)
            {
                PairCode.Text = "Loading…";
                PairingStatus.Text = "Requesting a sign-in code…";
                Spinner.IsRunning = true;
                RetryButton.IsVisible = false;
                var init = await auth.PairInitAsync(ct);
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(init?.Code) || string.IsNullOrWhiteSpace(init.PollToken))
                    throw new InvalidDataException("The pairing response did not contain a code and poll token.");
                PairCode.Text = init.Code.Length == 6 ? init.Code[..3] + " " + init.Code[3..] : init.Code;
                PairingStatus.Text = "Waiting for sign-in…";
                Spinner.IsRunning = false;
                while (true)
                {
                    await Task.Delay(3000, ct);
                    var result = await auth.PairPollAsync(init.PollToken, ct);
                    ct.ThrowIfCancellationRequested();
                    if (string.Equals(result?.Status, "expired", StringComparison.OrdinalIgnoreCase)) break;
                    if (string.Equals(result?.Status, "complete", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(result.Jwt)) throw new InvalidDataException("The completed pairing has no session token.");
                        auth.StoreJwt(result.Jwt);
                        await Shell.Current.GoToAsync("//MainPage");
                        return;
                    }
                    if (!string.Equals(result?.Status, "pending", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The pairing response has an unknown status.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch
        {
            if (ct.IsCancellationRequested) return;
            PairCode.Text = "------";
            PairingStatus.Text = "Could not connect to the sign-in service. Check your connection and retry.";
            Spinner.IsRunning = false;
            RetryButton.IsVisible = true;
            RetryButton.Focus();
        }
    }
}
