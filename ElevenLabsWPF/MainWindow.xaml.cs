using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ElevenLabsWPF
{
    public partial class MainWindow : Window
    {
        private readonly SettingsService _settingsService = new();
        private AppSettings _settings;
        private readonly MediaPlayer _mediaPlayer = new();
        private readonly DispatcherTimer _audioTimer;
        private bool _isUserDraggingSlider;
        private bool _isPlaying;
        private bool _isGenerating;
        private byte[]? _audioBytes;
        private string? _tempAudioPath;
        private const string ModelId = "eleven_multilingual_v2";

        public MainWindow()
        {
            InitializeComponent();
            _settings = _settingsService.Load();
            TxtApiKey.Text = _settings.ApiKey ?? string.Empty;
            TxtProxy.Text = _settings.Proxy ?? string.Empty;
            TxtVoiceId.Text = _settings.VoiceId ?? string.Empty;
            TxtSavePath.Text = _settings.LastSavePath ?? string.Empty;

            TxtText.TextChanged += TxtText_TextChanged;

            _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
            _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;

            _audioTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _audioTimer.Tick += AudioTimer_Tick;

            UpdateCounts();
        }

        private async void BtnCheckCredit_Click(object sender, RoutedEventArgs e)
        {
            await RunWithButtonStateAsync(BtnCheckCredit, "Đang kiểm tra...", async () => await LoadCreditAsync());
        }

        private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
        {
            await GenerateAsync();
        }

        private async Task GenerateAsync()
        {
            if (_isGenerating)
            {
                return;
            }

            var text = TxtText.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show(this, "Vui lòng nhập nội dung trước.", "Thiếu dữ liệu", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(TxtApiKey.Text) || string.IsNullOrWhiteSpace(TxtVoiceId.Text))
            {
                MessageBox.Show(this, "API Key và Voice ID là bắt buộc.", "Thiếu dữ liệu", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isGenerating = true;
            await RunWithButtonStateAsync(BtnGenerate, "Đang tạo...", async () =>
            {
                try
                {
                    SetLoadingProgress(10);
                    using var client = CreateHttpClient();
                    var request = BuildTtsRequest(text);
                    SetLoadingProgress(40);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    SetLoadingProgress(80);

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException($"Tạo voice thất bại: {response.StatusCode}");
                    }

                    _audioBytes = bytes;
                    _tempAudioPath = Path.Combine(Path.GetTempPath(), $"preview_{DateTime.Now.Ticks}.mp3");
                    await File.WriteAllBytesAsync(_tempAudioPath, bytes);
                    SetLoadingProgress(100);
                    await Task.Delay(800);

                    LoadAudio();
                    AppendHistory(text.Length, _tempAudioPath);
                    TxtStatus.Text = "Tạo voice thành công";

                    await Task.Delay(2000);
                    await LoadCreditAsync();
                    await LoadCreditAsync(force: true);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Lỗi tạo voice", MessageBoxButton.OK, MessageBoxImage.Error);
                    TxtStatus.Text = "Tạo voice lỗi";
                }
                finally
                {
                    ResetLoading();
                }
            });
            _isGenerating = false;
        }

        private HttpRequestMessage BuildTtsRequest(string text)
        {
            var url = $"https://api.elevenlabs.io/v1/text-to-speech/{TxtVoiceId.Text.Trim()}";
            var payload = new
            {
                text,
                model_id = ModelId
            };
            var json = JsonSerializer.Serialize(payload);
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("xi-api-key", TxtApiKey.Text.Trim());
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return request;
        }

        private async Task LoadCreditAsync(bool force = false)
        {
            try
            {
                using var client = CreateHttpClient();
                var url = "https://api.elevenlabs.io/v1/user/subscription";
                if (force)
                {
                    url += "?t=" + DateTime.Now.Ticks;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("xi-api-key", TxtApiKey.Text.Trim());

                using var response = await client.SendAsync(request);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var used = root.GetProperty("character_count").GetInt32();
                var limit = root.GetProperty("character_limit").GetInt32();
                TxtCredit.Text = $"Credit: {used} / {limit}";
                TxtStatus.Text = "Credit updated";
            }
            catch (Exception ex)
            {
                TxtCredit.Text = "Credit: ERROR";
                TxtStatus.Text = "Failed to load credit";
                MessageBox.Show(this, ex.Message, "Credit Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler();
            try
            {
                var (proxyUri, username, password) = ParseProxy(TxtProxy.Text);
                if (proxyUri != null)
                {
                    var proxy = new WebProxy(proxyUri)
                    {
                        BypassProxyOnLocal = false
                    };
                    if (!string.IsNullOrWhiteSpace(username))
                    {
                        proxy.Credentials = new NetworkCredential(username, password ?? string.Empty);
                    }

                    handler.Proxy = proxy;
                    handler.PreAuthenticate = true;
                    handler.UseDefaultCredentials = false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Proxy Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ElevenLabsWPF/1.0");
            return client;
        }

        private static (Uri? uri, string? username, string? password) ParseProxy(string? proxyText)
        {
            if (string.IsNullOrWhiteSpace(proxyText))
            {
                return (null, null, null);
            }

            var trimmed = proxyText.Trim();
            if (trimmed.Contains("://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
                {
                    throw new InvalidOperationException("Proxy không hợp lệ.");
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
                        throw new InvalidOperationException("Proxy cần cả username và password.");
                    }
                    username = Uri.UnescapeDataString(parts[0]);
                    password = Uri.UnescapeDataString(parts[1]);
                }

                return (uri, username, password);
            }

            if (trimmed.StartsWith("["))
            {
                return ParseBracketProxy(trimmed);
            }

            return ParseHostPortProxy(trimmed);
        }

        private static (Uri? uri, string? username, string? password) ParseBracketProxy(string proxyText)
        {
            var closingIndex = proxyText.IndexOf(']');
            if (closingIndex <= 1)
            {
                throw new InvalidOperationException("Proxy IPv6 không hợp lệ.");
            }

            var host = proxyText.Substring(1, closingIndex - 1);
            var remainder = proxyText.Substring(closingIndex + 1);
            if (!remainder.StartsWith(":"))
            {
                throw new InvalidOperationException("Proxy IPv6 cần định dạng [ipv6]:port.");
            }

            var segments = remainder.TrimStart(':').Split(':');
            if (segments.Length != 1 && segments.Length != 3)
            {
                throw new InvalidOperationException("Proxy IPv6 cần port và có thể có user:pass.");
            }

            var portText = segments[0];
            var port = ParsePort(portText);
            ValidateProxyHost(host, allowIPv6: true);

            string? username = null;
            string? password = null;
            if (segments.Length == 3)
            {
                username = segments[1];
                password = segments[2];
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    throw new InvalidOperationException("Proxy cần cả username và password.");
                }
            }

            var builder = new UriBuilder("http", host, port);
            if (!string.IsNullOrWhiteSpace(username))
            {
                builder.UserName = Uri.EscapeDataString(username);
                builder.Password = Uri.EscapeDataString(password!);
            }

            return (builder.Uri, username, password);
        }

        private static (Uri? uri, string? username, string? password) ParseHostPortProxy(string proxyText)
        {
            var segments = proxyText.Split(':');
            if (segments.Length != 2 && segments.Length != 4)
            {
                throw new InvalidOperationException("Proxy cần định dạng ip:port hoặc ip:port:user:pass.");
            }

            var host = segments[0];
            var port = ParsePort(segments[1]);
            ValidateProxyHost(host, allowIPv6: true);

            string? username = null;
            string? password = null;
            if (segments.Length == 4)
            {
                username = segments[2];
                password = segments[3];
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    throw new InvalidOperationException("Proxy cần cả username và password.");
                }
            }

            var builder = new UriBuilder("http", host, port);
            if (!string.IsNullOrWhiteSpace(username))
            {
                builder.UserName = Uri.EscapeDataString(username);
                builder.Password = Uri.EscapeDataString(password!);
            }

            return (builder.Uri, username, password);
        }

        private static void ValidateProxyHost(string host, bool allowIPv6 = false)
        {
            var type = Uri.CheckHostName(host);
            var valid = type == UriHostNameType.IPv4 || type == UriHostNameType.Dns || (allowIPv6 && type == UriHostNameType.IPv6);
            if (!valid)
            {
                throw new InvalidOperationException("Proxy host không hợp lệ.");
            }
        }

        private static int ParsePort(string portText)
        {
            if (!int.TryParse(portText, out var port) || port < 1 || port > 65535)
            {
                throw new InvalidOperationException("Proxy port phải là số từ 1-65535.");
            }

            return port;
        }

        private async Task RunWithButtonStateAsync(Button button, string busyText, Func<Task> action)
        {
            var original = button.Content;
            button.IsEnabled = false;
            button.Content = busyText;
            try
            {
                await action();
            }
            finally
            {
                button.Content = original;
                button.IsEnabled = true;
            }
        }

        private void SetLoadingProgress(double percent)
        {
            LoadingBar.Value = percent;
            TxtLoadingPercent.Text = $"{(int)percent}%";
        }

        private void ResetLoading()
        {
            LoadingBar.Value = 0;
            TxtLoadingPercent.Text = "0%";
        }

        private void LoadAudio()
        {
            if (_tempAudioPath == null || !File.Exists(_tempAudioPath))
            {
                return;
            }

            _mediaPlayer.Open(new Uri(_tempAudioPath));
            _mediaPlayer.Position = TimeSpan.Zero;
            _mediaPlayer.Play();
            _isPlaying = true;
            BtnPlayPause.Content = "⏸";
            _audioTimer.Start();
        }

        private void AudioTimer_Tick(object? sender, EventArgs e)
        {
            if (!_mediaPlayer.NaturalDuration.HasTimeSpan || _isUserDraggingSlider)
            {
                return;
            }

            var duration = _mediaPlayer.NaturalDuration.TimeSpan;
            AudioSlider.Maximum = duration.TotalSeconds;
            AudioSlider.Value = _mediaPlayer.Position.TotalSeconds;
            TxtAudioTime.Text = $"{FormatTime(_mediaPlayer.Position)} / {FormatTime(duration)}";
        }

        private void MediaPlayer_MediaOpened(object? sender, EventArgs e)
        {
            if (_mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                var duration = _mediaPlayer.NaturalDuration.TimeSpan;
                AudioSlider.Maximum = duration.TotalSeconds;
                TxtAudioTime.Text = $"00:00 / {FormatTime(duration)}";
            }
        }

        private void MediaPlayer_MediaEnded(object? sender, EventArgs e)
        {
            _audioTimer.Stop();
            _mediaPlayer.Stop();
            _mediaPlayer.Position = TimeSpan.Zero;
            AudioSlider.Value = 0;
            _isPlaying = false;
            BtnPlayPause.Content = "▶";
        }

        private void AudioSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUserDraggingSlider || !_mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                return;
            }

            if (Math.Abs(_mediaPlayer.Position.TotalSeconds - e.NewValue) > 0.5)
            {
                _mediaPlayer.Position = TimeSpan.FromSeconds(e.NewValue);
            }
        }

        private void AudioSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isUserDraggingSlider = true;
        }

        private void AudioSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isUserDraggingSlider = false;
            if (_mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                _mediaPlayer.Position = TimeSpan.FromSeconds(AudioSlider.Value);
            }
        }

        private void BtnReplay_Click(object sender, RoutedEventArgs e)
        {
            if (!_mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                return;
            }

            var newPos = Math.Max(0, _mediaPlayer.Position.TotalSeconds - 5);
            _mediaPlayer.Position = TimeSpan.FromSeconds(newPos);
        }

        private void BtnForward_Click(object sender, RoutedEventArgs e)
        {
            if (!_mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                return;
            }

            var max = _mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            var newPos = Math.Min(max, _mediaPlayer.Position.TotalSeconds + 5);
            _mediaPlayer.Position = TimeSpan.FromSeconds(newPos);
        }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (!_mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                return;
            }

            if (_isPlaying)
            {
                _mediaPlayer.Pause();
                BtnPlayPause.Content = "▶";
                _isPlaying = false;
                _audioTimer.Stop();
            }
            else
            {
                if (_mediaPlayer.Position >= _mediaPlayer.NaturalDuration.TimeSpan)
                {
                    _mediaPlayer.Position = TimeSpan.Zero;
                }

                _mediaPlayer.Play();
                BtnPlayPause.Content = "⏸";
                _isPlaying = true;
                _audioTimer.Start();
            }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Audio Files|*.mp3;*.wav|All Files|*.*",
                FileName = string.IsNullOrEmpty(TxtSavePath.Text) ? "tts_audio.mp3" : Path.GetFileName(TxtSavePath.Text)
            };

            if (dlg.ShowDialog() == true)
            {
                TxtSavePath.Text = dlg.FileName;
                _settings.LastSavePath = dlg.FileName;
                _settingsService.Save(_settings);
            }
        }

        private void BtnSaveFile_Click(object sender, RoutedEventArgs e)
        {
            if (_tempAudioPath == null || !File.Exists(_tempAudioPath))
            {
                MessageBox.Show(this, "Chưa có file để lưu.", "Lưu", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(TxtSavePath.Text))
            {
                MessageBox.Show(this, "Vui lòng chọn đường dẫn lưu.", "Lưu", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(this, "Bạn có chắc chắn muốn lưu file voice này?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                File.Copy(_tempAudioPath, TxtSavePath.Text, true);
                MessageBox.Show(this, "Lưu file thành công.", "Lưu", MessageBoxButton.OK, MessageBoxImage.Information);
                OpenFolderContaining(TxtSavePath.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Lỗi lưu file", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenFolderContaining(string path)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch
            {
                // ignore
            }
        }

        private void AppendHistory(int charCount, string filePath)
        {
            var entry = $"{DateTime.Now:HH:mm:ss} | {charCount} chars | {filePath}";
            HistoryList.Items.Insert(0, entry);
        }

        private void TxtText_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateCounts();
        }

        private void UpdateCounts()
        {
            var text = TxtText.Text ?? string.Empty;
            var count = text.Length;
            InputGroup.Header = $"Số ký tự: {count} | Ước tính Credit tiêu: {count}";
        }

        private string FormatTime(TimeSpan span)
        {
            return span.ToString(span.TotalHours >= 1 ? "hh\\:mm\\:ss" : "mm\\:ss");
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _settings.ApiKey = TxtApiKey.Text.Trim();
            _settings.Proxy = TxtProxy.Text.Trim();
            _settings.VoiceId = TxtVoiceId.Text.Trim();
            _settings.LastSavePath = TxtSavePath.Text.Trim();
            _settingsService.Save(_settings);
        }
    }
}
