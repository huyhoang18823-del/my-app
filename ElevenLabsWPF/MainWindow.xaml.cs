using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.IO;
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
        private DispatcherTimer? _audioTimer;
        private DispatcherTimer? _loadingTimer;
        private bool _isUserDraggingSlider;
        private bool _isGenerating;
        private bool _isPlaying;
        private byte[]? _audioBytes;
        private string? _tempAudioPath;

        public MainWindow()
        {
            InitializeComponent();
            _settings = _settingsService.Load();
            TxtApiKey.Text = _settings.ApiKey ?? string.Empty;
            TxtProxy.Text = _settings.Proxy ?? string.Empty;
            TxtVoiceId.Text = _settings.VoiceId ?? string.Empty;
            TxtSavePath.Text = _settings.LastSavePath ?? string.Empty;

            TxtInput.TextChanged += TxtInput_TextChanged;
            InitializeTimers();
            MediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
            MediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
            UpdateInputHeader();
        }

        private void InitializeTimers()
        {
            _audioTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _audioTimer.Tick += AudioTimer_Tick;

            _loadingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _loadingTimer.Tick += LoadingTimer_Tick;
        }

        private void TxtInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateInputHeader();
        }

        private void UpdateInputHeader()
        {
            int chars = TxtInput.Text?.Length ?? 0;
            int creditEstimate = ElevenLabsClient.EstimateCredits(TxtInput.Text ?? string.Empty);
            InputGroup.Header = $"Character Count: {chars} | Estimated Credit: {creditEstimate}\nInput Text";
        }

        private ElevenLabsClient CreateClient()
        {
            return new ElevenLabsClient(TxtApiKey.Text.Trim(), TxtProxy.Text.Trim(), TxtVoiceId.Text.Trim());
        }

        private async void BtnCheckCredit_Click(object sender, RoutedEventArgs e)
        {
            await LoadCreditAsync();
        }

        private async Task LoadCreditAsync(bool force = false)
        {
            try
            {
                var client = CreateClient();
                var credit = await client.GetCreditAsync(force);
                TxtCredit.Text = $"Credit: {credit.Used} / {credit.Limit}";
                TxtStatus.Text = "Credit updated";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "Failed to load credit";
                MessageBox.Show(this, ex.Message, "Credit Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
        {
            await GenerateAsync();
        }

        private async Task GenerateAsync()
        {
            if (string.IsNullOrWhiteSpace(TxtInput.Text))
            {
                MessageBox.Show(this, "Please enter some text first", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(TxtApiKey.Text) || string.IsNullOrWhiteSpace(TxtVoiceId.Text))
            {
                MessageBox.Show(this, "API Key and Voice ID are required", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(this, "Are you sure you want to generate speech for this text?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            SetGeneratingState(true);
            try
            {
                var client = CreateClient();
                ShowLoading();
                var audio = await client.GenerateSpeechAsync(TxtInput.Text);
                _audioBytes = audio;
                _tempAudioPath = Path.Combine(Path.GetTempPath(), $"tts_{DateTime.Now.Ticks}.mp3");
                await File.WriteAllBytesAsync(_tempAudioPath, audio);
                MediaPlayer.Stop();
                MediaPlayer.Source = new Uri(_tempAudioPath);
                _isPlaying = false;
                BtnPlayPause.Content = "Play";
                AudioSlider.Value = 0;
                TxtStatus.Text = "Audio generated";
                await Task.Delay(2000);
                await LoadCreditAsync();
                await LoadCreditAsync(force: true);
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "Generation failed";
                MessageBox.Show(this, ex.Message, "Generation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                HideLoading();
                SetGeneratingState(false);
            }
        }

        private void ShowLoading()
        {
            if (_loadingTimer == null)
            {
                return;
            }

            LoadingBar.Visibility = Visibility.Visible;
            TxtLoadingPercent.Visibility = Visibility.Visible;
            LoadingBar.Value = 0;
            TxtLoadingPercent.Text = "0%";
            _loadingTimer.Start();
        }

        private void HideLoading()
        {
            if (_loadingTimer == null)
            {
                return;
            }

            _loadingTimer.Stop();
            LoadingBar.Value = 100;
            TxtLoadingPercent.Text = "100%";
            LoadingBar.Visibility = Visibility.Collapsed;
            TxtLoadingPercent.Visibility = Visibility.Collapsed;
        }

        private void LoadingTimer_Tick(object? sender, EventArgs e)
        {
            if (LoadingBar.Value < 95)
            {
                LoadingBar.Value += 5;
                TxtLoadingPercent.Text = $"{(int)LoadingBar.Value}%";
            }
        }

        private void SetGeneratingState(bool generating)
        {
            _isGenerating = generating;
            BtnGenerate.IsEnabled = !generating;
            BtnGenerate.Content = generating ? "Generating..." : "Generate";
            GenerateProgress.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;
            GenerateProgress.IsIndeterminate = generating;
            TxtInput.IsEnabled = !generating;
        }

        private bool HasAudio => _audioBytes != null && _audioBytes.Length > 0;

        private void AudioTimer_Tick(object? sender, EventArgs e)
        {
            if (MediaPlayer.Source == null || !HasAudio)
            {
                return;
            }

            if (MediaPlayer.NaturalDuration.HasTimeSpan && !_isUserDraggingSlider)
            {
                AudioSlider.Maximum = MediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                AudioSlider.Value = MediaPlayer.Position.TotalSeconds;
                TxtAudioTime.Text = $"{FormatTime(MediaPlayer.Position)} / {FormatTime(MediaPlayer.NaturalDuration.TimeSpan)}";
            }
        }

        private void MediaPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (MediaPlayer.NaturalDuration.HasTimeSpan)
            {
                AudioSlider.Maximum = MediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                TxtAudioTime.Text = $"00:00 / {FormatTime(MediaPlayer.NaturalDuration.TimeSpan)}";
                _audioTimer?.Start();
                _isPlaying = false;
                BtnPlayPause.Content = "Play";
            }
        }

        private void MediaPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            _audioTimer?.Stop();
            AudioSlider.Value = 0;
            BtnPlayPause.Content = "Play";
            _isPlaying = false;
        }

        private void AudioSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUserDraggingSlider || !MediaPlayer.NaturalDuration.HasTimeSpan || !HasAudio)
            {
                return;
            }

            if (Math.Abs(MediaPlayer.Position.TotalSeconds - e.NewValue) > 0.5)
            {
                MediaPlayer.Position = TimeSpan.FromSeconds(e.NewValue);
            }
        }

        private void AudioSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isUserDraggingSlider = true;
        }

        private void AudioSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isUserDraggingSlider = false;
            if (HasAudio)
            {
                MediaPlayer.Position = TimeSpan.FromSeconds(AudioSlider.Value);
            }
        }

        private void BtnRewind_Click(object sender, RoutedEventArgs e)
        {
            if (MediaPlayer.NaturalDuration.HasTimeSpan && HasAudio)
            {
                var newPos = Math.Max(0, MediaPlayer.Position.TotalSeconds - 5);
                MediaPlayer.Position = TimeSpan.FromSeconds(newPos);
            }
        }

        private void BtnForward_Click(object sender, RoutedEventArgs e)
        {
            if (MediaPlayer.NaturalDuration.HasTimeSpan && HasAudio)
            {
                var max = MediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                var newPos = Math.Min(max, MediaPlayer.Position.TotalSeconds + 5);
                MediaPlayer.Position = TimeSpan.FromSeconds(newPos);
            }
        }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (MediaPlayer.Source == null || !HasAudio)
            {
                return;
            }

            if (_isPlaying)
            {
                MediaPlayer.Pause();
                BtnPlayPause.Content = "Play";
                _isPlaying = false;
                _audioTimer?.Stop();
            }
            else
            {
                if (MediaPlayer.NaturalDuration.HasTimeSpan && MediaPlayer.Position >= MediaPlayer.NaturalDuration.TimeSpan)
                {
                    MediaPlayer.Position = TimeSpan.Zero;
                }

                MediaPlayer.Play();
                BtnPlayPause.Content = "Pause";
                _isPlaying = true;
                _audioTimer?.Start();
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
            if (_audioBytes == null || _audioBytes.Length == 0)
            {
                MessageBox.Show(this, "No audio to save", "Save", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(TxtSavePath.Text))
            {
                MessageBox.Show(this, "Please choose a destination path", "Save", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                File.WriteAllBytes(TxtSavePath.Text, _audioBytes);
                TxtStatus.Text = "File saved";
                MessageBox.Show(this, "Audio saved successfully", "Save", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string FormatTime(TimeSpan span)
        {
            return span.ToString(span.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
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
