using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using StreamedTv.Platform;
using StreamedTv.Services;

namespace StreamedTv.Views;

public partial class PlayerPage : ContentPage, IQueryAttributable
{
    private string? _streamUrl;
    private bool _active;
    private bool _reportedVideo;

    public PlayerPage()
    {
        InitializeComponent();
        foreach (var button in new[] { BackBtn, PlayPauseBtn, RetryBtn }) Controls.RemoteButtonStyle.Apply(button);
        NativePlayer.MediaOpened += (_, _) =>
        {
            AppLog.Info("StreamedTv", $"Native media opened: {NativePlayer.MediaWidth}x{NativePlayer.MediaHeight}");
            UpdateNativeStatus();
        };
        NativePlayer.PositionChanged += (_, _) => UpdateNativeStatus();
        NativePlayer.StateChanged += (_, _) => UpdateNativeStatus();
        NativePlayer.MediaFailed += (_, failure) =>
        {
            if (!_active) return;
            var detail = System.Text.RegularExpressions.Regex.Replace(failure.ErrorMessage ?? "Playback error", @"https?://\S+", "[media URL]");
            AppLog.Error("StreamedTv", "Native playback failed: " + detail);
            PlaybackStatus.Text = "Could not play this video. Retry or choose another stream.";
            RetryBtn.IsVisible = true;
            RetryBtn.Focus();
        };
        Unloaded += (_, _) => NativePlayer.Handler?.DisconnectHandler();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        _streamUrl = query.TryGetValue("url", out var url) ? url as string : null;
        StreamTitleLabel.Text = query.TryGetValue("title", out var title) ? title as string ?? "Stream" : "Stream";
        if (_active) OpenStream();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _active = true;
        OpenStream();
    }

    protected override void OnDisappearing()
    {
        _active = false;
        StopPlayback();
        base.OnDisappearing();
    }

    private void OpenStream()
    {
        StopPlayback();
        RetryBtn.IsVisible = false;
        _reportedVideo = false;
        PlaybackStatus.Text = "Loading…";
        try
        {
            var target = PlaybackTarget.Parse(_streamUrl);
            NativePlayer.IsVisible = target.IsDirectMedia;
            EmbedPlayer.IsVisible = !target.IsDirectMedia;
            PlayPauseBtn.IsVisible = target.IsDirectMedia;
            if (target.IsDirectMedia)
            {
                NativePlayer.MetadataTitle = StreamTitleLabel.Text;
                NativePlayer.ShouldLoopPlayback = ServiceLocator.IsSandbox;
                NativePlayer.Source = MediaSource.FromUri(target.Url);
            }
            else
            {
                PlaybackStatus.Text = "Loading web player…";
                EmbedPlayer.Source = new UrlWebViewSource { Url = target.Url };
            }
        }
        catch (ArgumentException)
        {
            PlaybackStatus.Text = "This stream does not have a valid video address.";
            PlayPauseBtn.IsVisible = false;
        }
    }

    private void UpdateNativeStatus()
    {
        if (!_active || !NativePlayer.IsVisible) return;
        if (!_reportedVideo && NativePlayer.Position > TimeSpan.Zero && NativePlayer.MediaWidth > 0 && NativePlayer.MediaHeight > 0)
        {
            _reportedVideo = true;
            AppLog.Info("StreamedTv", $"Native video rendering: {NativePlayer.MediaWidth}x{NativePlayer.MediaHeight}");
        }
        PlayPauseBtn.Text = NativePlayer.CurrentState == MediaElementState.Playing ? "Pause" : "Play";
        PlaybackStatus.Text = NativePlayer.CurrentState switch
        {
            MediaElementState.Playing => "Playing · " + NativePlayer.Position.ToString(@"mm\:ss"),
            MediaElementState.Paused => "Paused · " + NativePlayer.Position.ToString(@"mm\:ss"),
            MediaElementState.Buffering => "Buffering…",
            MediaElementState.Stopped => "Stopped",
            MediaElementState.Failed => "Could not play this video. Retry or choose another stream.",
            _ => "Loading…"
        };
    }

    private void StopPlayback()
    {
        if (NativePlayer.Source is not null) NativePlayer.Stop();
        NativePlayer.Source = null;
        EmbedPlayer.Source = new HtmlWebViewSource { Html = "<!doctype html><html></html>" };
        AppLog.Info("StreamedTv", "Player stopped");
    }

    public bool HandlePlaybackKey(bool? play = null)
    {
        if (!_active || !NativePlayer.IsVisible) return false;
        var shouldPlay = play ?? NativePlayer.CurrentState != MediaElementState.Playing;
        if (shouldPlay) NativePlayer.Play(); else NativePlayer.Pause();
        return true;
    }

    private void OnPlayPauseClicked(object sender, EventArgs e) => HandlePlaybackKey();
    private void OnRetryClicked(object sender, EventArgs e) => OpenStream();
    private async void OnBackClicked(object sender, EventArgs e)
    {
        StopPlayback();
        await Shell.Current.GoToAsync("..");
    }

    private void OnWebNavigated(object sender, WebNavigatedEventArgs e)
    {
        if (!_active || !EmbedPlayer.IsVisible) return;
        PlaybackStatus.Text = e.Result == WebNavigationResult.Success ? "Web player" : "Could not load the web player. Retry or choose another stream.";
        RetryBtn.IsVisible = e.Result != WebNavigationResult.Success;
    }

    private void OnWebNavigating(object sender, WebNavigatingEventArgs e)
    {
        if (e.Url == "about:blank" || e.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) e.Cancel = true;
    }
}
