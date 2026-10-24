using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace StreamedTv.Models
{
    public sealed class Sport
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
    }

    public sealed class ApiMatch
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "";

        [JsonPropertyName("date")]
        public long Date { get; set; }

        [JsonPropertyName("poster")]
        public string Poster { get; set; } = "";

        [JsonPropertyName("popular")]
        public bool Popular { get; set; }

        [JsonPropertyName("teams")]
        public MatchTeams Teams { get; set; }

        [JsonPropertyName("sources")]
        public List<StreamSourceRef> Sources { get; set; } = new List<StreamSourceRef>();
    }

    public sealed class MatchTeams
    {
        [JsonPropertyName("home")]
        public TeamInfo Home { get; set; }

        [JsonPropertyName("away")]
        public TeamInfo Away { get; set; }
    }

    public sealed class TeamInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("badge")]
        public string Badge { get; set; } = "";
    }

    public sealed class StreamSourceRef
    {
        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("id")]
        public string Id { get; set; } = "";
    }

    public sealed class StreamOption
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("streamNo")]
        public int StreamNo { get; set; }

        [JsonPropertyName("language")]
        public string Language { get; set; } = "";

        [JsonPropertyName("hd")]
        public bool Hd { get; set; }

        [JsonPropertyName("embedUrl")]
        public string EmbedUrl { get; set; } = "";

        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonIgnore]
        public string PlaybackUrl
        {
            get { return EmbedUrl; }
        }

        // Populated by StreamExtractorService when extraction succeeded for a
        // wrapper-host (embedsports.top etc.). Carries the RAW proxied m3u8
        // URL — distinct from EmbedUrl which is the hls.js wrapper page used
        // by PlayerWebView. Null/empty when extraction was skipped or failed,
        // signalling Program.ShowPlayerView to fall back to PlayerWebView
        // rather than handing off to the native NUI VideoView player.
        // [JsonIgnore] because this is purely internal state — never serialised
        // back over the wire.
        [JsonIgnore]
        public string ResolvedHlsUrl { get; set; }
    }

    public sealed class CatalogMatch
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("sport")]
        public string Sport { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "";

        [JsonPropertyName("competition")]
        public string Competition { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("startTime")]
        public string StartTime { get; set; } = "";

        [JsonPropertyName("date")]
        public long Date { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "scheduled";

        [JsonPropertyName("popular")]
        public bool Popular { get; set; }

        [JsonPropertyName("poster")]
        public string Poster { get; set; } = "";

        [JsonPropertyName("teams")]
        public MatchTeams Teams { get; set; }

        [JsonPropertyName("streams")]
        public List<LocalStream> Streams { get; set; } = new List<LocalStream>();

        [JsonPropertyName("streamSources")]
        public List<StreamSourceRef> StreamSources { get; set; } = new List<StreamSourceRef>();

        [JsonIgnore]
        public bool IsLive
        {
            get { return string.Equals(Status, "live", StringComparison.OrdinalIgnoreCase); }
        }

        [JsonIgnore]
        public bool HasPlayableSource
        {
            get { return Streams.Count > 0 || StreamSources.Count > 0; }
        }
    }

    public sealed class LocalStream
    {
        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";
    }
}
