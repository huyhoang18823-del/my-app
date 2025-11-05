using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Windows;
using YtScraper.Desktop.Logging;
using YtScraper.Desktop.Models;
using Forms = System.Windows.Forms;

namespace YtScraper.Desktop;

public partial class MainWindow : Window
{
    private static readonly Uri BaseAddress = new("http://localhost:5199");
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        _httpClient = new HttpClient { BaseAddress = BaseAddress };
        _logger = new UiLogger(LogBox, Dispatcher);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select a folder to save the exported file",
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            FolderBox.Text = dialog.SelectedPath;
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        StartBtn.IsEnabled = false;

        try
        {
            _logger.LogInformation("Validating input...");
            var request = ValidateInputs(out var folderPath);
            Directory.CreateDirectory(folderPath);
            _logger.LogInformation($"Export will be saved to '{folderPath}'.");

            _logger.LogInformation("Sending request to YtScraper API...");
            using var response = await _httpClient.PostAsJsonAsync("/api/youtube/export", request, _cts.Token);

            _logger.LogInformation("Awaiting API response...");
            response.EnsureSuccessStatusCode();

            var export = await response.Content.ReadFromJsonAsync<ExportResponse>(_cts.Token);
            if (export is null || string.IsNullOrWhiteSpace(export.FileName) || string.IsNullOrWhiteSpace(export.FileBytesBase64))
            {
                throw new InvalidOperationException("The API returned an unexpected response.");
            }

            _logger.LogInformation($"Received metadata for {export.Count} videos.");

            var outputPath = Path.Combine(folderPath, export.FileName);
            var bytes = Convert.FromBase64String(export.FileBytesBase64);

            _logger.LogInformation("Writing file to disk...");
            await File.WriteAllBytesAsync(outputPath, bytes, _cts.Token);

            _logger.LogInformation($"File saved to: {outputPath}");
            _logger.LogInformation("Scraping completed successfully.");

            MessageBox.Show(this,
                $"Scraping completed.\nVideos exported: {export.Count}.\nSaved to: {outputPath}",
                "Export Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex.Message);
            MessageBox.Show(this,
                ex.Message,
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            StartBtn.IsEnabled = true;
        }
    }

    private ExportRequest ValidateInputs(out string folderPath)
    {
        var apiKey = ApiKeyBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("API key is required.");
        }

        var channelUrl = (UrlBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(channelUrl))
        {
            throw new InvalidOperationException("Channel URL or handle is required.");
        }

        var maxText = (MaxBox.Text ?? string.Empty).Trim();
        if (!int.TryParse(maxText, out var maxVideos) || maxVideos <= 0)
        {
            throw new InvalidOperationException("Max videos must be a positive number.");
        }

        folderPath = (FolderBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new InvalidOperationException("Please choose a folder to save the export file.");
        }

        return new ExportRequest(apiKey, channelUrl, maxVideos);
    }
}
