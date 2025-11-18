using System;
using System.IO;
using System.Text.Json;

namespace ElevenLabsWPF
{
    public class SettingsService
    {
        private readonly string _settingsPath;

        public SettingsService()
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElevenLabsWPF");
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _settingsPath = Path.Combine(directory, "settings.json");
        }

        public AppSettings Load()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        return settings;
                    }
                }
            }
            catch
            {
                // ignore corrupt files and fallback to defaults
            }

            return new AppSettings();
        }

        public void Save(AppSettings settings)
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
    }

    public class AppSettings
    {
        public string? ApiKey { get; set; }
        public string? Proxy { get; set; }
        public string? VoiceId { get; set; }
        public string? LastSavePath { get; set; }
    }
}
