using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using FolderTools.Core;

namespace FolderTools;

public partial class App : Application
{
    // Windows display language, kept to come back to it when the user picks "System language".
    private static readonly CultureInfo SystemUICulture = CultureInfo.CurrentUICulture;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = AppSettings.Load();

        // UI language: the one chosen in the settings, else the Windows one (French, otherwise English).
        // FOLDERTOOLS_LANG=en|fr forces one, handy to test both; it also forces the number and date formats
        // (en-US, fr-FR), which otherwise follow the Windows regional settings (README screenshot).
        if (Environment.GetEnvironmentVariable("FOLDERTOOLS_LANG") is { Length: > 0 } forced)
        {
            ApplyLanguage(forced);
            var format = CultureInfo.CreateSpecificCulture(forced);
            CultureInfo.DefaultThreadCurrentCulture = format;
            CultureInfo.CurrentCulture = format;
        }
        else
            ApplyLanguage(settings.Language);

        // Theme: the one chosen in the settings, Windows' light/dark by default.
        // FOLDERTOOLS_THEME=Light|Dark forces one, handy to test both.
        ApplyTheme(Environment.GetEnvironmentVariable("FOLDERTOOLS_THEME") switch
        {
            "Light" => AppTheme.Light,
            "Dark" => AppTheme.Dark,
            _ => settings.Theme,
        });

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, Strings.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // The context menu starts "FolderTools.exe size|clean|photos|flatten <folder>"; the Start menu, no argument.
        var request = LaunchRequest.Parse(e.Args);
        Window window = request.Action switch
        {
            LaunchAction.Size => new SizeWindow(request.Path!),
            LaunchAction.Clean => new CleanWindow(request.Path!),
            LaunchAction.Photos => new PhotoSortWindow(request.Path!),
            LaunchAction.Flatten => new FlattenWindow(request.Path!),
            _ => new HomeWindow(request.Path),
        };
        MainWindow = window;
        window.Show();

        // Started from Explorer, the window may come up minimized or behind Explorer: bring it forward.
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();

        // Every window shows the new-version bar, so every start checks (unless turned off in Settings).
        _ = Updater.CheckAtStartupAsync();
    }

    /// <summary>
    /// Sets the theme of every window, at once: all colors come from the Fluent theme through DynamicResource.
    /// </summary>
    public static void ApplyTheme(AppTheme theme) =>
        Current.ThemeMode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };

    /// <summary>Sets the UI language: <see cref="AppSettings.SystemLanguage"/> or a culture name ("en", "fr").</summary>
    public static void ApplyLanguage(string language)
    {
        var culture = language == AppSettings.SystemLanguage ? SystemUICulture : CultureInfo.GetCultureInfo(language);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>
    /// Opens the settings over <paramref name="owner"/>. Texts are read once, when a window is built: after a
    /// language change, every window is rebuilt in place (same position, same folder) and the settings reopen
    /// over the window that asked for them.
    /// </summary>
    public static void OpenSettings(Window owner, SettingsPage page = SettingsPage.General)
    {
        var settings = new SettingsWindow(page) { Owner = owner };
        settings.ShowDialog();
        if (!settings.LanguageChanged)
            return;

        ApplyLanguage(AppSettings.Load().Language);
        Window? newOwner = null;
        foreach (var window in Current.Windows.OfType<IRebuildable>().ToList())
        {
            if (window.Rebuild() is not { } rebuilt)
                continue; // busy (deleting folders): it keeps its language until closed
            var old = (Window)window;
            var bounds = old.WindowState == WindowState.Normal ? new Rect(old.Left, old.Top, old.Width, old.Height) : old.RestoreBounds;
            rebuilt.WindowStartupLocation = WindowStartupLocation.Manual;
            rebuilt.Left = bounds.Left;
            rebuilt.Top = bounds.Top;
            rebuilt.Width = bounds.Width;
            rebuilt.Height = bounds.Height;
            rebuilt.WindowState = old.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            if (Current.MainWindow == old)
                Current.MainWindow = rebuilt;
            rebuilt.Show();
            old.Close();
            if (old == owner)
                newOwner = rebuilt;
        }
        if (newOwner is not null)
            newOwner.Dispatcher.BeginInvoke(() => OpenSettings(newOwner), DispatcherPriority.ApplicationIdle);
    }
}

/// <summary>A window that can be rebuilt in another language, showing the same folder.</summary>
public interface IRebuildable
{
    /// <summary>A new window showing the same thing, or null when this one cannot be replaced now.</summary>
    Window? Rebuild();
}
