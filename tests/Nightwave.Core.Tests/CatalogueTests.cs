using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nightwave.Core.Tests;

public sealed partial class CatalogueTests
{
    private static string Server([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../src/Nightwave.Server"));

    private static Dictionary<string, string> Catalogue(string language) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Server(), "i18n", $"{language}.json")))!;

    [Fact]
    public void Every_language_has_the_same_keys_with_the_same_placeholders()
    {
        var english = Catalogue("en");
        var german = Catalogue("de");

        Assert.Equal(english.Keys.Order(StringComparer.Ordinal), german.Keys.Order(StringComparer.Ordinal));

        foreach (var (key, text) in english)
        {
            Assert.Equal(
                Placeholder().Matches(text).Select(match => match.Value).Order(StringComparer.Ordinal),
                Placeholder().Matches(german[key]).Select(match => match.Value).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Every_key_the_interface_names_is_in_the_catalogue()
    {
        var english = Catalogue("en");
        var files = Directory.EnumerateFiles(Path.Combine(Server(), "Components"), "*.razor", SearchOption.AllDirectories)
            .Append(Path.Combine(Server(), "Security", "AuthEndpoints.cs"));

        var missing = files
            .SelectMany(file => TextKey().Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value))
            .Where(key => !english.ContainsKey(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(missing);
    }

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\b[Tt]\[""([a-zA-Z.]+)""")]
    private static partial Regex TextKey();
}
