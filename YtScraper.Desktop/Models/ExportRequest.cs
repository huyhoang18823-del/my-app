using System.Text.Json.Serialization;

namespace YtScraper.Desktop.Models;

public sealed class ExportRequest
{
    public ExportRequest(string apiKey, string channelUrl, int maxVideos)
    {
        ApiKey = apiKey;
        ChannelUrl = channelUrl;
        MaxVideos = maxVideos;
    }

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; }

    [JsonPropertyName("channelUrl")]
    public string ChannelUrl { get; }

    [JsonPropertyName("maxVideos")]
    public int MaxVideos { get; }
}
