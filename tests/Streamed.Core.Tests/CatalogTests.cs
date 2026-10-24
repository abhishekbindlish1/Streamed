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
