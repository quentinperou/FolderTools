using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FolderTools.Core;
using Microsoft.Win32;

namespace FolderTools;

/// <summary>Light or dark appearance; <see cref="System"/> follows Windows.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>User preferences, saved in %LOCALAPPDATA%\FolderTools\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Follow the Windows display language.</summary>
    public const string SystemLanguage = "system";

    /// <summary>UI languages with a translation (Properties/Strings*.resx). English is the default.</summary>
    public static readonly string[] SupportedLanguages = ["en", "fr"];

    /// <summary>Files that do not keep a folder from being empty, unless the user changes the list.</summary>
    public static readonly string[] DefaultIgnoredFiles = ["Thumbs.db", "desktop.ini", ".DS_Store"];

    /// <summary>Folders never looked into nor deleted: version control repositories need their empty folders.</summary>
    public static readonly string[] DefaultIgnoredFolders = [".git", ".svn", ".hg"];

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderTools", "settings.json");

    /// <summary><see cref="SystemLanguage"/> or one of <see cref="SupportedLanguages"/>.</summary>
    public string Language { get; set; } = SystemLanguage;

    [JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Empty folders go to the recycle bin (true) or are deleted for good.</summary>
    public bool DeleteToRecycleBin { get; set; } = true;

    /// <summary>File names (wildcards allowed) that do not count as content when looking for empty folders.</summary>
    public List<string> IgnoredFiles { get; set; } = [.. DefaultIgnoredFiles];

    /// <summary>Folder names (wildcards allowed) that count as content and are never looked into (1.0.1).</summary>
    public List<string> IgnoredFolders { get; set; } = [.. DefaultIgnoredFolders];

    /// <summary>Date folders made by “Sort photos by date”.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<PhotoLayout>))]
    public PhotoLayout PhotoLayout { get; set; } = PhotoLayout.YearDay;

    /// <summary>What “Sort photos by date” does with files that have no date taken.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<UndatedPolicy>))]
    public UndatedPolicy PhotoUndated { get; set; } = UndatedPolicy.FileDate;

    /// <summary>How deep “Flatten folder” goes (the last choice made in its window).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<FlattenDepth>))]
    public FlattenDepth FlattenDepth { get; set; } = FlattenDepth.AllLevels;

    /// <summary>What a name already taken becomes when flattening a folder.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<FlattenNaming>))]
    public FlattenNaming FlattenNaming { get; set; } = FlattenNaming.Suffix;

    /// <summary>Ask GitHub for a newer version when the app starts.</summary>
    public bool CheckForUpdatesAtStartup { get; set; } = true;

    /// <summary>Releases offered as updates: stable only, or betas too.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<UpdateChannel>))]
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

    // Settings of a newer version (a dev build, a beta) are written back as they are, not dropped.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? OtherSettings { get; set; }

    // A missing or damaged file just means default settings: preferences are never worth an error message.
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) is { } settings)
            {
                if (!SupportedLanguages.Contains(settings.Language))
                    settings.Language = SystemLanguage;
                settings.IgnoredFiles ??= [.. DefaultIgnoredFiles];
                settings.IgnoredFolders ??= [.. DefaultIgnoredFolders];
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---------------------------------------------------------------- Windows 11 context menu

    // Read by FolderToolsMenu.dll (src/FolderTools.Shell) each time File Explorer builds its menu, hence the
    // registry rather than settings.json: no JSON parsing in the shell extension, and no admin rights needed.
    private const string MenuKey = @"Software\FolderTools";

    /// <summary>"FolderTools ▸" in the new Windows 11 context menu; it always stays under "Show more options".</summary>
    public static bool ShowInNewContextMenu
    {
        get => IsShown("HideInNewContextMenu");
        set => SetShown("HideInNewContextMenu", value);
    }

    /// <summary>The sub-entries of "FolderTools ▸" in the new menu (1.1.0). Same names as in dllmain.cpp.</summary>
    public static bool ShowSizeEntry
    {
        get => IsShown("HideSizeEntry");
        set => SetShown("HideSizeEntry", value);
    }

    public static bool ShowCleanEntry
    {
        get => IsShown("HideCleanEntry");
        set => SetShown("HideCleanEntry", value);
    }

    public static bool ShowPhotosEntry
    {
        get => IsShown("HidePhotosEntry");
        set => SetShown("HidePhotosEntry", value);
    }

    public static bool ShowFlattenEntry
    {
        get => IsShown("HideFlattenEntry");
        set => SetShown("HideFlattenEntry", value);
    }

    public static bool ShowColorEntry
    {
        get => IsShown("HideColorEntry");
        set => SetShown("HideColorEntry", value);
    }

    private static bool IsShown(string hideValue)
    {
        using var key = Registry.CurrentUser.OpenSubKey(MenuKey);
        return key?.GetValue(hideValue) is not int hidden || hidden == 0;
    }

    private static void SetShown(string hideValue, bool shown)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(MenuKey);
            key.SetValue(hideValue, shown ? 0 : 1, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }
    }
}
