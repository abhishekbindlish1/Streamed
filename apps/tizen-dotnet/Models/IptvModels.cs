using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace StreamedTv.Models
{
    /// <summary>
    /// One IPTV channel from the iptv-org catalog (via /v1/iptv/channels).
    /// Slim shape — we project on the backend; the TV doesn't need the
    /// owners/network/altnames cruft.
    /// </summary>
    public sealed class IptvChannel
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("country")]
        public string Country { get; set; } = "";

        [JsonPropertyName("logo")]
        public string Logo { get; set; } = "";

        [JsonPropertyName("categories")]
        public List<string> Categories { get; set; } = new List<string>();

        // Sorted server-side by quality (1080p first). The TV uses
        // Streams[0] by default; richer logic (per-stream picker)
        // can be added later.
        [JsonPropertyName("streams")]
        public List<IptvStream> Streams { get; set; } = new List<IptvStream>();
    }

    /// <summary>
    /// One playable URL for a channel. UA / Referrer are per-stream
    /// overrides from iptv-org; not all streams have them.
    /// </summary>
    public sealed class IptvStream
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [JsonPropertyName("quality")]
        public string Quality { get; set; } = "";

        [JsonPropertyName("user_agent")]
        public string UserAgent { get; set; } = "";

        [JsonPropertyName("referrer")]
        public string Referrer { get; set; } = "";
    }
}
