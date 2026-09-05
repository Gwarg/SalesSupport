using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace SalesSupport.Client;

/// <summary>
/// Themes are first-class (D35): a theme is one resource dictionary defining the fixed set of
/// <c>Theme.*</c> keys, merged at index 0 of the application resources. Views only ever use
/// DynamicResource, so <see cref="Apply"/> restyles the running app. The choice persists per user.
/// </summary>
public static class ThemeManager
{
    public sealed record ThemeInfo(string Id, string DisplayName, string Source);

    public static readonly ThemeInfo[] Themes =
    [
        new("control-room", "Control room", "Themes/ControlRoom.xaml"),
        new("calm-instrument", "Calm instrument", "Themes/CalmInstrument.xaml"),
    ];

    public static ThemeInfo Current { get; private set; } = Themes[0];

    /// <summary>Raised after a theme is applied; windows re-tint their native chrome on it.</summary>
    public static event Action? Changed;

    public static IEnumerable<string> DisplayNames => Themes.Select(t => t.DisplayName);

    /// <summary>True when the active theme asks for a dark title bar.</summary>
    public static bool DarkChrome => Application.Current?.TryFindResource("Theme.DarkChrome") is true;

    public static void Initialize()
    {
        var saved = ClientSettings.Load().Theme;
        var theme = Themes.FirstOrDefault(t => t.Id == saved) ?? Themes[0];
        Apply(theme, persist: false);
    }

    public static void ApplyByDisplayName(string displayName)
    {
        var theme = Themes.FirstOrDefault(t => t.DisplayName == displayName);
        if (theme is not null && theme != Current) Apply(theme, persist: true);
    }

    private static void Apply(ThemeInfo theme, bool persist)
    {
        var app = Application.Current;
        if (app is null) return;
        var dictionary = new ResourceDictionary { Source = new Uri(theme.Source, UriKind.Relative) };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count == 0) merged.Add(dictionary);
        else merged[0] = dictionary;
        Current = theme;
        if (persist) ClientSettings.Save(ClientSettings.Load() with { Theme = theme.Id });
        Changed?.Invoke();
    }
}

/// <summary>Per-user client preferences, kept out of the repo (%LOCALAPPDATA%\SalesSupport\client.json).</summary>
public sealed record ClientSettings(string? Theme)
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalesSupport", "client.json");

    public static ClientSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(Path)) ?? new ClientSettings(Theme: null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new ClientSettings(Theme: null);
    }

    public static void Save(ClientSettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Tints the Windows title bar to match the theme (immersive dark mode via DWM).</summary>
internal static class NativeChrome
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Call once the window has a handle (SourceInitialized) and again whenever the theme changes.</summary>
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = ThemeManager.DarkChrome ? 1 : 0;
        try { _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    /// <summary>Keeps a window's chrome in step with the theme for its lifetime.</summary>
    public static void Track(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window);
        Action onChanged = () => Apply(window);
        ThemeManager.Changed += onChanged;
        window.Closed += (_, _) => ThemeManager.Changed -= onChanged;
    }
}
