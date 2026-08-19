using System.Text.Json;
using System.Globalization;

namespace KittyClaw.Core.Services;

public class AppSettingsService
{
    private readonly string _settingsPath;
    private AppSettingsData _data;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppSettingsService(string dataDir, CultureInfo? systemCulture = null)
    {
        _settingsPath = Path.Combine(dataDir, "settings.json");
        var defaultLanguage = ResolveDefaultLanguage(systemCulture ?? CultureInfo.CurrentUICulture);
        _data = new AppSettingsData { Language = defaultLanguage };
        Load(defaultLanguage);
    }

    internal static string ResolveDefaultLanguage(CultureInfo culture)
    {
        var language = culture.TwoLetterISOLanguageName.ToLowerInvariant();
        if (language == "pt") return "pt-BR";
        return language is "en" or "fr" or "es" or "de" or "it" or "ja" ? language : "en";
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

    public bool McpEnabled
    {
        get => _data.McpEnabled;
        set
        {
            if (_data.McpEnabled == value) return;
            _data.McpEnabled = value;
            Save();
        }
    }

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

    public string? UpdateDismissedVersion
    {
        get => _data.UpdateDismissedVersion;
        set
        {
            if (_data.UpdateDismissedVersion == value) return;
            _data.UpdateDismissedVersion = value;
            Save();
        }
    }

    public DateTime? UpdateCheckLastRun
    {
        get => _data.UpdateCheckLastRun;
        set
        {
            if (_data.UpdateCheckLastRun == value) return;
            _data.UpdateCheckLastRun = value;
            Save();
        }
    }

    /// <summary>Random, non-reversible instance identifier; generated once, never tied to any user data.</summary>
    public string TelemetryInstanceId
    {
        get
        {
            if (string.IsNullOrEmpty(_data.TelemetryInstanceId))
            {
                _data.TelemetryInstanceId = Guid.NewGuid().ToString();
                Save();
            }
            return _data.TelemetryInstanceId;
        }
    }

    public DateTime? TelemetryLastSent
    {
        get => _data.TelemetryLastSent;
        set
        {
            if (_data.TelemetryLastSent == value) return;
            _data.TelemetryLastSent = value;
            Save();
        }
    }

    private void Load(string defaultLanguage)
    {
        if (!File.Exists(_settingsPath)) return;
        try
        {
            var json = File.ReadAllText(_settingsPath);
            _data = JsonSerializer.Deserialize<AppSettingsData>(json, JsonOpts) ?? new();
            if (string.IsNullOrWhiteSpace(_data.Language))
                _data.Language = defaultLanguage;
        }
        catch { /* use defaults if settings file is corrupted */ _data = new AppSettingsData { Language = defaultLanguage }; }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_data, JsonOpts);
        File.WriteAllText(_settingsPath, json);
    }

    private class AppSettingsData
    {
        public string Language { get; set; } = "";
        public bool McpEnabled { get; set; }
        public bool OnboardingSeen { get; set; } = false;
        public string? UpdateDismissedVersion { get; set; }
        public DateTime? UpdateCheckLastRun { get; set; }
        public string? TelemetryInstanceId { get; set; }
        public DateTime? TelemetryLastSent { get; set; }
    }
}
