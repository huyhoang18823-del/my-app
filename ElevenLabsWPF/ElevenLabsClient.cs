using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ElevenLabsWPF
{
    public class ElevenLabsClient
    {
        private readonly string _apiKey;
        private readonly string? _proxy;
        private readonly string _voiceId;

        public ElevenLabsClient(string apiKey, string? proxy, string voiceId)
        {
            _apiKey = apiKey ?? string.Empty;
            _proxy = string.IsNullOrWhiteSpace(proxy) ? null : proxy;
            _voiceId = voiceId ?? string.Empty;
        }

        public static int EstimateCredits(string text)
        {
            return text?.Length ?? 0;
        }

        public async Task<CreditInfo> GetCreditAsync(bool force = false)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException("API key is required");
            }

            using var client = CreateHttpClient();
            var url = "https://api.elevenlabs.io/v1/user/subscription";
            if (force)
            {
                url += "?t=" + DateTimeOffset.UtcNow.Ticks;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("xi-api-key", _apiKey);

            using var response = await client.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var used = root.GetProperty("character_count").GetInt32();
            var limit = root.GetProperty("character_limit").GetInt32();
            return new CreditInfo(used, limit);
        }

        public async Task<byte[]> GenerateSpeechAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException("API key is required");
            }

            if (string.IsNullOrWhiteSpace(_voiceId))
            {
                throw new InvalidOperationException("Voice ID is required");
            }

            using var client = CreateHttpClient();
            var url = $"https://api.elevenlabs.io/v1/text-to-speech/{_voiceId}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("xi-api-key", _apiKey);
            var payload = new
            {
                text,
                model_id = "eleven_multilingual_v2"
            };
            var json = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }

        private HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler();
            if (!string.IsNullOrWhiteSpace(_proxy))
            {
                handler.Proxy = BuildProxy(_proxy!);
                handler.UseProxy = true;
            }

            var client = new HttpClient(handler, disposeHandler: true);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ElevenLabsWPF", "1.0"));
            return client;
        }

        private static IWebProxy? BuildProxy(string proxyText)
        {
            if (string.IsNullOrWhiteSpace(proxyText))
            {
                return null;
            }

            try
            {
                var uriText = proxyText.Contains("http", StringComparison.OrdinalIgnoreCase) ? proxyText : $"http://{proxyText}";
                var uri = new Uri(uriText);
                return new WebProxy(uri);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Invalid proxy format", ex);
            }
        }
    }

    public record CreditInfo(int Used, int Limit);
}
