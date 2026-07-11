using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace KittyClaw.Core.Services;

public class LocalizationService
{
    private readonly AppSettingsService _settings;
    private readonly Dictionary<string, Dictionary<string, string>> _cache = [];

    public LocalizationService(AppSettingsService settings)
    {
        _settings = settings;
        _settings.OnLanguageChanged += () => OnLanguageChanged?.Invoke();
        Load();
    }

    public string Lang => _settings.Language;
    public event Action? OnLanguageChanged;

    public string this[string key] => Get(key);

    public string Get(string key)
    {
        // Fallback chain: active language → English → the raw key. Without the English hop,
        // any en/fr drift shows literal keys in the UI of the lagging language.
        if (_cache.TryGetValue(Lang, out var dict) && dict.TryGetValue(key, out var value))
            return value;
        if (_cache.TryGetValue("en", out var en) && en.TryGetValue(key, out var enValue))
            return enValue;
        return key;
    }

    public string Get(string key, params object[] args)
    {
        var template = Get(key);
        try
        {
            // Invariant so numbers/dates don't follow the server culture; guarded because a
            // missing key returns the raw key, which may contain braces and make Format throw.
            return string.Format(CultureInfo.InvariantCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private void Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = "KittyClaw.Core.Localization.";

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix) || !resourceName.EndsWith(".json"))
                continue;

            // e.g. KittyClaw.Core.Localization.Board.fr.json → lang = "fr"
            var fileName = resourceName[prefix.Length..]; // e.g. Board.fr.json
            var parts = fileName.Split('.');
            if (parts.Length < 3) continue;
            var lang = parts[^2]; // second-to-last part

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;

            if (!_cache.TryGetValue(lang, out var dict))
            {
                dict = [];
                _cache[lang] = dict;
            }

            foreach (var (k, v) in entries)
                dict[k] = v;
        }
    }
}
