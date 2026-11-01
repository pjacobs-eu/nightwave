using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Nightwave.Server;

/// <summary>
/// The translated strings. English is the source and the key language: a key missing in a
/// translation falls back to it. Placeholders are written <c>{0}</c>.
/// </summary>
public sealed class Texts
{
    public static readonly IReadOnlyList<string> Languages = ["en", "de"];

    private readonly FrozenDictionary<string, FrozenDictionary<string, string>> _catalogues;

    public Texts()
    {
        var assembly = Assembly.GetExecutingAssembly();

        _catalogues = Languages.ToFrozenDictionary(language => language, language =>
        {
            using var stream = assembly.GetManifestResourceStream($"Nightwave.Server.i18n.{language}.json")
                ?? throw new InvalidOperationException($"The translation catalogue '{language}' is not in the build.");

            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!.ToFrozenDictionary();
        });
    }

    public string this[string key] =>
        (_catalogues.GetValueOrDefault(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) ?? _catalogues["en"]).GetValueOrDefault(key)
        ?? _catalogues["en"].GetValueOrDefault(key)
        ?? key;

    public string this[string key, params object?[] values] => string.Format(CultureInfo.CurrentCulture, this[key], values);

    /// <summary>All keys of a language, for the test that keeps the catalogues together.</summary>
    public IReadOnlyCollection<string> Keys(string language) => _catalogues[language].Keys;
}
