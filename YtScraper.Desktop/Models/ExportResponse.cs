using System.Text.Json.Serialization;

namespace YtScraper.Desktop.Models;

public sealed class ExportResponse
{
    [JsonPropertyName("fileName")]
    public string? FileName { get; init; }

    [JsonPropertyName("fileBytesBase64")]
    public string? FileBytesBase64 { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }
}
