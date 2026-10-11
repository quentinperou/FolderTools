using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace FolderTools;

/// <summary>
/// UI strings from Properties/Strings.resx (English, the default) and Strings.fr.resx (French).
/// The language follows the Windows display language (CultureInfo.CurrentUICulture).
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Manager = new("FolderTools.Properties.Strings", typeof(Strings).Assembly);

    public static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? $"[{key}]";

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>
    /// Singular or plural text: "<paramref name="key"/>_One" or "<paramref name="key"/>_Other". French uses the
    /// singular for 0 and 1 ("0 dossier vide"), English for 1 only ("0 empty folders").
    /// </summary>
    public static string Plural(string key, long count, params object[] args)
    {
        bool singular = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? count < 2 : count == 1;
        return Format(key + (singular ? "_One" : "_Other"), args);
    }
}

/// <summary>In XAML, <c>Text="{local:Tr Close}"</c> shows the "Close" string in the UI language.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}