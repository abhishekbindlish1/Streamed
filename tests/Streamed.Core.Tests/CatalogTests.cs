using System.Net;
using System.Text;
using StreamedTv.Models;
using StreamedTv.Services;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public sealed class CatalogTests
{
    [Fact]
    public void RelativeApiPathsStayHttpAndEscapeUserInput()
    {
        StreamedEndpoints.BaseUrl = "https://sandbox.invalid/";
        Assert.Equal("https", StreamedEndpoints.MatchesBySport("football").Scheme);
        Assert.Equal("https://sandbox.invalid/api/matches/football", StreamedEndpoints.MatchesBySport("football").AbsoluteUri);
        Assert.Contains("a%2Fb", StreamedEndpoints.StreamsBySource("alpha", "a/b").AbsoluteUri);
    }

    [Fact]
    public void LiveMatchesComeBeforeUpcomingFavorites()
    {
        var ranker = new StreamRanker(new[] { "arsenal" });
        var ranked = ranker.RankMatches(new[] {
            new CatalogMatch { Id = "future", Title = "Arsenal", Status = "scheduled" },
            new CatalogMatch { Id = "live", Title = "Other teams", Status = "live" }
        });
        Assert.Equal("live", ranked[0].Id);
    }

    [Fact]
    public void FavoritesWinWithinTheSameMatchStatus()
    {
        var ranked = new StreamRanker(new[] { "ARSENAL" }).RankMatches(new[] {
            new CatalogMatch { Id = "other", Title = "Other teams", Status = "live", Popular = true },
            new CatalogMatch { Id = "favorite", Title = "Arsenal vs Chelsea", Status = "live" }
        });
        Assert.Equal("favorite", ranked[0].Id);
    }

    [Fact]
    public async Task SportsUseInjectedHttpClientAndCacheTheResponse()
    {
        var handler = new FixtureHandler("[{\"id\":\"football\",\"name\":\"Football\"}]");
        var catalog = new StreamedCatalogService(new HttpClient(handler));
        Assert.Single(await catalog.GetSportsAsync(CancellationToken.None));
        Assert.Single(await catalog.GetSportsAsync(CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task EmptyBackendResponseIsNotMisrepresentedAsLiveFixtures()
    {
        var catalog = new StreamedCatalogService(new HttpClient(new FixtureHandler("[]")));
        var matches = await catalog.GetMatchesBySportAsync("football", Array.Empty<string>(), CancellationToken.None);
        Assert.Empty(matches);
        Assert.Contains("0 matches", catalog.LastNetworkError);
    }

    [Fact]
    public async Task TemporaryNetworkFailureDoesNotCacheAnEmptyCatalog()
    {
        var handler = new RecoveringHandler();
        var catalog = new StreamedCatalogService(new HttpClient(handler));
        Assert.Empty(await catalog.GetSportsAsync(CancellationToken.None));
        Assert.NotNull(catalog.LastNetworkError);
        Assert.Single(await catalog.GetSportsAsync(CancellationToken.None));
        Assert.Null(catalog.LastNetworkError);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task CancelledRequestDoesNotBecomeACachedEmptyResponse()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var catalog = new StreamedCatalogService(new HttpClient(new FixtureHandler("[{\"id\":\"football\",\"name\":\"Football\"}]")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.GetSportsAsync(cancelled.Token));
        Assert.Single(await catalog.GetSportsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancellingAnInFlightRequestStillAllowsANewRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new BlockingOnceHandler();
        var catalog = new StreamedCatalogService(new HttpClient(handler));
        var pending = catalog.GetSportsAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(await catalog.GetSportsAsync(CancellationToken.None));
    }

    private sealed class BlockingOnceHandler : HttpMessageHandler
    {
        private int _requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++_requests == 1) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"id\":\"football\",\"name\":\"Football\"}]", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class RecoveringHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(Requests == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(Requests == 1 ? "Temporary outage" : "[{\"id\":\"football\",\"name\":\"Football\"}]", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FixtureHandler(string json) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
