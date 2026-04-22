using System.Text.Json;

namespace Todo.Core.Services;

public class AppSettingsService
{
    private readonly string _settingsPath;
    private AppSettingsData _data = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppSettingsService(string dataDir)
    {
        _settingsPath = Path.Combine(dataDir, "settings.json");
        Load();
    }

    public string Language
    {
        get => _data.Language;
        set
        {
            if (_data.Language == value) return;
            _data.Language = value;
            Save();
            OnLanguageChanged?.Invoke();
        }
    }

    public event Action? OnLanguageChanged;

    public bool OnboardingSeen
    {
        get => _data.OnboardingSeen;
        set
        {
            if (_data.OnboardingSeen == value) return;
            _data.OnboardingSeen = value;
            Save();
        }
    }

    private void Load()
    {
        if (!File.Exists(_settingsPath)) return;
        try
        {
            var json = File.ReadAllText(_settingsPath);
            _data = JsonSerializer.Deserialize<AppSettingsData>(json, JsonOpts) ?? new();
        }
        catch { /* use defaults if settings file is corrupted */ _data = new(); }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_data, JsonOpts);
        File.WriteAllText(_settingsPath, json);
    }

    private class AppSettingsData
    {
        public string Language { get; set; } = "fr";
        public bool OnboardingSeen { get; set; } = false;
    }
}
