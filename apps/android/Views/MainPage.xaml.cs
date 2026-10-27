using StreamedTv.Models;
using StreamedTv.Services;

namespace StreamedTv.Views
{
    public partial class MainPage : ContentPage
    {
        private IReadOnlyList<Sport> _sports = Array.Empty<Sport>();
        private string _activeSportId = "";
        private CancellationTokenSource? _cts;
        private string? _selectedMatchId;
        private bool _openingStream;

        public MainPage()
        {
            InitializeComponent();
            SandboxNotice.IsVisible = ServiceLocator.IsSandbox;
            PageLayout.RowDefinitions[0].Height = ServiceLocator.IsSandbox ? new GridLength(32) : new GridLength(0);
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _ = LoadSportsAsync(_cts.Token);
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _cts?.Cancel();
        }

        private async Task LoadSportsAsync(CancellationToken ct)
        {
            try
            {
                _sports = await ServiceLocator.Catalog.GetSportsAsync(ct).ConfigureAwait(true);
                ct.ThrowIfCancellationRequested();
                if (_sports.Count > 0 && !_sports.Any(sport => sport.Id == _activeSportId))
                    _activeSportId = _sports[0].Id;
                BuildSportNav();
                if (_sports.Count > 0)
                {
                    await LoadMatchesAsync(_activeSportId, ct).ConfigureAwait(true);
                }
                else ShowMessage(ServiceLocator.Catalog.LastRequestFailed
                    ? "Could not load sports. Check your connection and try again."
                    : "No sports are available right now.", retrySports: true);
            }
            catch (OperationCanceledException) { }
            catch
            {
                if (!ct.IsCancellationRequested) ShowMessage("Failed to load sports. Check your connection.", retrySports: true);
            }
        }

        private void BuildSportNav()
        {
            SportNav.Children.Clear();
            foreach (var sport in _sports)
            {
                var btn = new Button
                {
                    Text = sport.Name,
                    TextColor = sport.Id == _activeSportId ? Colors.White : Color.FromArgb("#888888"),
                    BackgroundColor = Colors.Transparent,
                    FontSize = 16,
                    WidthRequest = 128,
                    HeightRequest = 48,
                    Padding = new Thickness(12, 0),
                    CommandParameter = sport.Id,
                };
                string capturedId = sport.Id;
                StyleRemoteButton(btn);
                btn.Clicked += async (_, _) =>
                {
                    if (_activeSportId == capturedId) return;
                    _activeSportId = capturedId;
                    RefreshSportNavColors();
                    _cts?.Cancel();
                    _cts?.Dispose();
                    _cts = new CancellationTokenSource();
                    await LoadMatchesAsync(capturedId, _cts.Token).ConfigureAwait(true);
                };
                SportNav.Children.Add(btn);
            }
        }

        private void RefreshSportNavColors()
        {
            foreach (var child in SportNav.Children)
            {
                if (child is Button btn && btn.CommandParameter is string id)
                    btn.TextColor = id == _activeSportId ? Colors.White : Color.FromArgb("#888888");
            }
        }

        private async Task LoadMatchesAsync(string sportId, CancellationToken ct)
        {
            ShowMessage("Loading…");
            try
            {
                var matches = await ServiceLocator.Catalog
                    .GetMatchesBySportAsync(sportId, Array.Empty<string>(), ct)
                    .ConfigureAwait(true);
                ct.ThrowIfCancellationRequested();
                BuildMatchList(matches, ct);
            }
            catch (OperationCanceledException) { }
            catch
            {
                if (!ct.IsCancellationRequested) ShowMessage("Could not load matches. Check your connection and try again.");
            }
        }

        private void BuildMatchList(IReadOnlyList<CatalogMatch> matches, CancellationToken ct)
        {
            MatchList.Children.Clear();

            var live = matches.Where(m => m.Status == "live").ToList();
            var upcoming = matches.Where(m => m.Status == "scheduled").ToList();
            var recent = matches.Where(m => m.Status == "recent").ToList();

            if (live.Count == 0 && upcoming.Count == 0 && recent.Count == 0)
            {
                ShowMessage(ServiceLocator.Catalog.LastRequestFailed
                    ? "Could not load matches. Check your connection and try again."
                    : "No matches available right now.");
                return;
            }
            if (_selectedMatchId is null || !matches.Any(match => match.Id == _selectedMatchId))
                _selectedMatchId = live.Concat(upcoming).Concat(recent).First().Id;

            if (live.Count > 0)
            {
                AddSectionHeader("🔴 Live");
                foreach (var m in live) AddMatchCard(m, ct);
            }
            if (upcoming.Count > 0)
            {
                AddSectionHeader("Upcoming");
                foreach (var m in upcoming) AddMatchCard(m, ct);
            }
            if (recent.Count > 0)
            {
                AddSectionHeader("Recent");
                foreach (var m in recent) AddMatchCard(m, ct);
            }
        }

        private void AddSectionHeader(string title)
        {
            MatchList.Children.Add(new Label
            {
                Text = title,
                TextColor = Colors.White,
                FontSize = 20,
                HeightRequest = 32,
                FontAttributes = FontAttributes.Bold,
                Margin = new Thickness(0, 16, 0, 4),
            });
        }

        private void AddMatchCard(CatalogMatch match, CancellationToken ct)
        {
            string home = match.Teams?.Home?.Name ?? "";
            string away = match.Teams?.Away?.Name ?? "";
            string title = string.IsNullOrEmpty(home) || string.IsNullOrEmpty(away)
                ? match.Title
                : home + " vs " + away;

            var card = new Button
            {
                Text = title + "\n" + match.Status.ToUpperInvariant(),
                AutomationId = "Match_" + match.Id,
                TextColor = Colors.White,
                FontSize = 16,
                HeightRequest = 132,
                MaximumHeightRequest = 132,
                VerticalOptions = LayoutOptions.Start,
                BackgroundColor = Color.FromArgb("#1e1e1e"),
                CornerRadius = 6,
                BorderWidth = 2,
                BorderColor = Color.FromArgb("#3a3a3a"),
                Padding = new Thickness(16, 12),
            };

            StyleRemoteButton(card);
            card.Clicked += async (_, _) => await OnMatchTappedAsync(match, ct);
            if (match.Id == _selectedMatchId)
            {
                var needsFocus = true;
                void FocusWhenReady()
                {
                    if (!needsFocus) return;
                    Dispatcher.Dispatch(() =>
                    {
                        if (needsFocus) needsFocus = !Controls.RemoteButtonStyle.TryFocus(card);
                    });
                }
                card.Loaded += (_, _) => FocusWhenReady();
                card.SizeChanged += (_, _) => FocusWhenReady();
            }
            MatchList.Children.Add(card);
        }

        private static void StyleRemoteButton(Button button)
        {
            Controls.RemoteButtonStyle.Apply(button);
        }

        private async Task OnMatchTappedAsync(CatalogMatch match, CancellationToken ct)
        {
            if (_openingStream || ct.IsCancellationRequested) return;
            _openingStream = true;
            _selectedMatchId = match.Id;
            try
            {
                var streams = await ServiceLocator.Catalog.GetStreamsAsync(match, ct).ConfigureAwait(true);
                ct.ThrowIfCancellationRequested();
                if (streams.Count == 0)
                {
                    await DisplayAlert("No Streams", "No playable streams found for this match.", "OK").ConfigureAwait(true);
                    return;
                }

                string rawUrl = streams[0].EmbedUrl;
                string resolvedUrl = await ServiceLocator.Resolver.ResolveAsync(rawUrl, ct).ConfigureAwait(true);
                ct.ThrowIfCancellationRequested();

                string home = match.Teams?.Home?.Name ?? "";
                string away = match.Teams?.Away?.Name ?? "";
                string title = string.IsNullOrEmpty(home) || string.IsNullOrEmpty(away) ? match.Title : home + " vs " + away;

                await Shell.Current.GoToAsync(nameof(PlayerPage), new ShellNavigationQueryParameters
                {
                    ["url"] = resolvedUrl,
                    ["title"] = title
                }).ConfigureAwait(true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) await DisplayAlert("Error", "Could not load the stream. Check your connection and try again.", "OK").ConfigureAwait(true);
            }
            finally { _openingStream = false; }
        }

        private void ShowMessage(string msg, bool retrySports = false)
        {
            MatchList.Children.Clear();
            MatchList.Children.Add(new Label
            {
                Text = msg,
                TextColor = Color.FromArgb("#888888"),
                FontSize = 18,
                HeightRequest = 32,
                HorizontalOptions = LayoutOptions.Center,
                Margin = new Thickness(0, 80, 0, 0),
            });
            if (msg == "Loading…") return;
            var retry = new Button { Text = "Retry", AutomationId = "CatalogRetry", WidthRequest = 180, HorizontalOptions = LayoutOptions.Center };
            StyleRemoteButton(retry);
            retry.Clicked += async (_, _) =>
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                if (retrySports) await LoadSportsAsync(_cts.Token);
                else await LoadMatchesAsync(_activeSportId, _cts.Token);
            };
            MatchList.Children.Add(retry);
        }
    }
}
