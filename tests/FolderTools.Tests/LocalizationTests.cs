using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FolderTools.Tests;

/// <summary>Keeps the English and French UI strings (src/FolderTools/Properties/*.resx) in step with each other and with the code.</summary>
public class LocalizationTests
{
    private static readonly string AppDir = Path.Combine(FindRepositoryRoot(), "src", "FolderTools");

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FolderTools.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root (FolderTools.slnx) not found.");
    }

    private static Dictionary<string, string> ReadStrings(string fileName) =>
        XDocument.Load(Path.Combine(AppDir, "Properties", fileName)).Root!
            .Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).ToHashSet();

    [Fact]
    public void FrenchTranslation_HasExactlyTheEnglishKeys()
    {
        Assert.Equal(
            ReadStrings("Strings.resx").Keys.Order(),
            ReadStrings("Strings.fr.resx").Keys.Order());
    }

    [Fact]
    public void Translations_UseTheSamePlaceholders()
    {
        var french = ReadStrings("Strings.fr.resx");
        foreach (var (key, english) in ReadStrings("Strings.resx"))
            Assert.True(Placeholders(english).SetEquals(Placeholders(french[key])), $"Placeholders differ for \"{key}\".");
    }

    [Fact]
    public void EveryKeyUsedByTheApp_Exists()
    {
        var keys = ReadStrings("Strings.resx").Keys.ToHashSet();
        var sources = Directory.EnumerateFiles(AppDir)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml"))
            .Select(File.ReadAllText)
            .ToList();
        var used = sources
            .SelectMany(text => Regex.Matches(text, @"\{local:Tr (\w+)\}|Strings\.(?:Get|Format)\(""(\w+)""|""(Confirm\w+)"""))
            .Select(m => m.Groups.Values.Skip(1).First(g => g.Success).Value)
            .ToList();
        // Strings.Plural("X", ...) and Strings.Plural(condition ? "X" : "Y", ...) use X_One and X_Other.
        used.AddRange(sources
            .SelectMany(text => Regex.Matches(text, @"Strings\.Plural\(([^,]+),"))
            .SelectMany(m => Regex.Matches(m.Groups[1].Value, @"""(\w+)""").Select(k => k.Groups[1].Value))
            .SelectMany(key => new[] { key + "_One", key + "_Other" }));

        Assert.NotEmpty(used);
        Assert.All(used, key => Assert.Contains(key, keys));
    }
}