#nullable enable
namespace StreamedTv.Services;

public sealed record PlaybackTarget(string Url, bool IsDirectMedia)
{
    public static PlaybackTarget Parse(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("The stream must have an HTTP or HTTPS address.", nameof(value));
        var extension = Path.GetExtension(uri.AbsolutePath);
        var direct = new[] { ".m3u8", ".mpd", ".mp4", ".m4v", ".webm", ".mov", ".mp3", ".m4a", ".aac", ".ts" }
            .Contains(extension, StringComparer.OrdinalIgnoreCase);
        return new PlaybackTarget(value!, direct);
    }
}
