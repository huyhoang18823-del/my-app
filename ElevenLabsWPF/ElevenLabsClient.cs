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
                var (proxyUri, username, password) = ParseProxy(_proxy!);
                if (proxyUri != null)
                {
                    var proxy = new WebProxy(proxyUri);
                    if (!string.IsNullOrWhiteSpace(username))
                    {
                        proxy.Credentials = new NetworkCredential(username, password);
                    }

                    handler.Proxy = proxy;
                    handler.PreAuthenticate = true;
                    handler.UseDefaultCredentials = false;
                }
            }

            var client = new HttpClient(handler, disposeHandler: true);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ElevenLabsWPF", "1.0"));
            return client;
        }

        private static (Uri? Uri, string? Username, string? Password) ParseProxy(string? proxyText)
        {
            if (string.IsNullOrWhiteSpace(proxyText))
            {
                return (null, null, null);
            }

            var trimmed = proxyText.Trim();
            if (trimmed.Contains("://", StringComparison.OrdinalIgnoreCase))
            {
                return ParseProxyUri(trimmed);
            }

            return ParseColonSeparatedProxy(trimmed);
        }

        private static (Uri? Uri, string? Username, string? Password) ParseProxyUri(string proxyText)
        {
            if (!Uri.TryCreate(proxyText, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            {
                throw new InvalidOperationException("Invalid proxy format. Use IP:PORT or http://USERNAME:PASSWORD@IP:PORT.");
            }

            ValidateProxyHost(uri.Host);
            ValidateProxyPort(uri.Port);

            string? username = null;
            string? password = null;

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':');
                if (parts.Length != 2)
                {
                    throw new InvalidOperationException("Proxy username and password must both be provided.");
                }

                username = Uri.UnescapeDataString(parts[0]);
                password = Uri.UnescapeDataString(parts[1]);

                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    throw new InvalidOperationException("Proxy username and password must both be provided.");
                }
            }

            return (uri, username, password);
        }

        private static (Uri? Uri, string? Username, string? Password) ParseColonSeparatedProxy(string proxyText)
        {
            var segments = proxyText.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 2 && segments.Length != 4)
            {
                throw new InvalidOperationException("Invalid proxy format. Use IP:PORT or IP:PORT:USERNAME:PASSWORD.");
            }

            var host = segments[0].Trim();
            var portText = segments[1].Trim();
            ValidateProxyHost(host);
            var port = ParseProxyPort(portText);

            string? username = null;
            string? password = null;
            if (segments.Length == 4)
            {
                username = segments[2].Trim();
                password = segments[3].Trim();
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    throw new InvalidOperationException("Proxy username and password must both be provided.");
                }
            }

            var builder = new UriBuilder("http", host, port);
            if (!string.IsNullOrWhiteSpace(username))
            {
                builder.UserName = Uri.EscapeDataString(username);
                builder.Password = Uri.EscapeDataString(password!);
            }

            var uri = builder.Uri;
            if (string.IsNullOrWhiteSpace(username))
            {
                return (uri, null, null);
            }

            return (uri, username, password);
        }

        private static void ValidateProxyHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new InvalidOperationException("Proxy host is required.");
            }

            var hostType = Uri.CheckHostName(host);
            if (hostType != UriHostNameType.Dns && hostType != UriHostNameType.IPv4)
            {
                throw new InvalidOperationException("Proxy host must be a valid IPv4 address or hostname.");
            }
        }

        private static void ValidateProxyPort(int port)
        {
            if (port <= 0 || port > 65535)
            {
                throw new InvalidOperationException("Proxy port must be a number between 1 and 65535.");
            }
        }

        private static int ParseProxyPort(string portText)
        {
            if (!int.TryParse(portText, out var port))
            {
                throw new InvalidOperationException("Proxy port must be a number between 1 and 65535.");
            }

            ValidateProxyPort(port);
            return port;
        }
    }

    public record CreditInfo(int Used, int Limit);
}
