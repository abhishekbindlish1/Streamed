using StreamedTv.Models;
using StreamedTv.Services;

namespace StreamedTv.Views
{
    public partial class MainPage : ContentPage
    {
        private IReadOnlyList<Sport> _sports = Array.Empty<Sport>();
        private string _activeSportId = "";
        private CancellationTokenSource _cts;

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
                if (_sports.Count > 0 && !_sports.Any(sport => sport.Id == _activeSportId))
                    _activeSportId = _sports[0].Id;
                BuildSportNav();
                if (_sports.Count > 0)
                {
                    await LoadMatchesAsync(_activeSportId, ct).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                ShowMessage("Failed to load sports. Check your connection.");
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
                btn.Clicked += async (_, _) =>
                {
                    if (_activeSportId == capturedId) return;
                    _activeSportId = capturedId;
                    RefreshSportNavColors();
                    _cts?.Cancel();
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
                BuildMatchList(matches, ct);
            }
            catch (OperationCanceledException) { }
            catch
            {
                ShowMessage("Failed to load matches.");
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
                ShowMessage("No matches available.");
                return;
            }

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

            var card = new Border
            {
                HeightRequest = 132,
                MaximumHeightRequest = 132,
                VerticalOptions = LayoutOptions.Start,
                BackgroundColor = Color.FromArgb("#1e1e1e"),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
                StrokeThickness = 0,
                Padding = new Thickness(16, 12),
            };

            var stack = new VerticalStackLayout { Spacing = 4 };
            stack.Children.Add(new Label
            {
                Text = title,
                HeightRequest = 44,
                MaxLines = 2,
                TextColor = Colors.White,
                FontSize = 16,
            });
            if (match.Status == "live")
            {
                stack.Children.Add(new Label
                {
                    Text = "LIVE",
                    HeightRequest = 20,
                    TextColor = Color.FromArgb("#ff4444"),
                    FontSize = 12,
                    FontAttributes = FontAttributes.Bold,
                });
            }
            card.Content = stack;

            var tapGesture = new TapGestureRecognizer();
            tapGesture.Tapped += async (_, _) => await OnMatchTappedAsync(match, ct);
            card.GestureRecognizers.Add(tapGesture);

            MatchList.Children.Add(card);
        }

        private async Task OnMatchTappedAsync(CatalogMatch match, CancellationToken ct)
        {
            try
            {
                var streams = await ServiceLocator.Catalog.GetStreamsAsync(match, ct).ConfigureAwait(true);
                if (streams.Count == 0)
                {
                    await DisplayAlert("No Streams", "No playable streams found for this match.", "OK").ConfigureAwait(true);
                    return;
                }

                string rawUrl = streams[0].EmbedUrl;
                string resolvedUrl = await ServiceLocator.Resolver.ResolveAsync(rawUrl, ct).ConfigureAwait(true);

                string home = match.Teams?.Home?.Name ?? "";
                string away = match.Teams?.Away?.Name ?? "";
                string title = string.IsNullOrEmpty(home) || string.IsNullOrEmpty(away) ? match.Title : home + " vs " + away;

                await Shell.Current.GoToAsync(
                    $"PlayerPage?url={Uri.EscapeDataString(resolvedUrl)}&title={Uri.EscapeDataString(title)}"
                ).ConfigureAwait(true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await DisplayAlert("Error", "Could not load stream: " + ex.Message, "OK").ConfigureAwait(true);
            }
        }

        private void ShowMessage(string msg)
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
        }
    }
}
