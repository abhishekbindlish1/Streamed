#nullable enable
using StreamedTv.Services;
using Xunit;

public sealed class PlaybackTests
{
    [Theory]
    [InlineData("https://media.invalid/live.M3U8?token=a%2Bb&expires=123", true)]
    [InlineData("https://media.invalid/movie.mp4?download=false", true)]
    [InlineData("https://media.invalid/dash/manifest.mpd", true)]
    [InlineData("https://player.invalid/embed/123", false)]
    public void MediaAndEmbedPagesUseTheAppropriatePlayerWithoutChangingTokens(string url, bool native)
    {
        var target = PlaybackTarget.Parse(url);
        Assert.Equal(native, target.IsDirectMedia);
        Assert.Equal(url, target.Url);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///data/private.mp4")]
    [InlineData("not a URL")]
    [InlineData(null)]
    public void NonWebAddressesAreRejected(string? url) => Assert.Throws<ArgumentException>(() => PlaybackTarget.Parse(url));
}
