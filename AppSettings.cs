using System.Text.Json;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT;

namespace NoteUI;

public record BackdropSettings(
    string Type,
    double TintOpacity,
    double LuminosityOpacity,
    string TintColor = "#000000",
    string FallbackColor = "#000000",
    string Kind = "Base");

public static class AppSettings
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoteUI");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly string DefaultNotesFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoteUI");

    public static string GetDefaultNotesFolder() => DefaultNotesFolder;

    // ── Language ────────────────────────────────────────────────

    public static string LoadLanguage()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("language", out var prop))
                    return prop.GetString() ?? "en";
            }
        }
        catch { }
        return "en";
    }

    public static void SaveLanguage(string lang)
    {
        MergeAndSaveSettings(new Dictionary<string, object> { ["language"] = lang });
    }

    // ── Slash commands ──────────────────────────────────────────

    public static bool LoadSlashEnabled()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("slashEnabled", out var prop))
                    return prop.GetBoolean();
            }
        }
        catch { }
        return true;
    }

    public static void SaveSlashEnabled(bool enabled)
    {
        MergeAndSaveSettings(new Dictionary<string, object> { ["slashEnabled"] = enabled });
    }

    public static string LoadNotesFolder()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("notesFolder", out var prop))
                {
                    var folder = prop.GetString();
                    if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                        return folder;
                }
            }
        }
        catch { }
        return DefaultNotesFolder;
    }

    public static void SaveNotesFolder(string folder)
    {
        MergeAndSaveSettings(new Dictionary<string, object> { ["notesFolder"] = folder });
    }

    // ── Firebase ───────────────────────────────────────────────

    public static (string Url, string ApiKey, string RefreshToken) LoadFirebaseSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var doc = JsonDocument.Parse(json);
                var url = doc.RootElement.TryGetProperty("firebaseUrl", out var uProp)
                    ? uProp.GetString() ?? "" : "";
                var key = doc.RootElement.TryGetProperty("firebaseApiKey", out var kProp)
                    ? kProp.GetString() ?? "" : "";
                var token = doc.RootElement.TryGetProperty("firebaseRefreshToken", out var tProp)
                    ? tProp.GetString() ?? "" : "";
                return (url, key, token);
            }
        }
        catch { }
        return ("", "", "");
    }

    public static void SaveFirebaseSettings(string url, string apiKey, string refreshToken = "")
    {
        MergeAndSaveSettings(new Dictionary<string, object>
        {
            ["firebaseUrl"] = url,
            ["firebaseApiKey"] = apiKey,
            ["firebaseRefreshToken"] = refreshToken
        });
    }

    // ── WebDAV ─────────────────────────────────────────────────

    public static (string Url, string Username, string Password) LoadWebDavSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var doc = JsonDocument.Parse(json);
                var url = doc.RootElement.TryGetProperty("webdavUrl", out var uProp)
                    ? uProp.GetString() ?? "" : "";
                var user = doc.RootElement.TryGetProperty("webdavUser", out var userProp)
                    ? userProp.GetString() ?? "" : "";
                var pass = doc.RootElement.TryGetProperty("webdavPass", out var pProp)
                    ? pProp.GetString() ?? "" : "";
                return (url, user, pass);
            }
        }
        catch { }
        return ("", "", "");
    }

    public static void SaveWebDavSettings(string url, string username, string password)
    {
        MergeAndSaveSettings(new Dictionary<string, object>
        {
            ["webdavUrl"] = url,
            ["webdavUser"] = username,
            ["webdavPass"] = password
        });
    }

    // ── Apply to any window ─────────────────────────────────────

    public static void ApplyToWindow(Window window, BackdropSettings settings,
        ref DesktopAcrylicController? controller, ref SystemBackdropConfiguration? configSource)
    {
        controller?.Dispose();
        controller = null;

        if (settings.Type == "acrylic_custom")
        {
            window.SystemBackdrop = null;

            configSource = new SystemBackdropConfiguration();
            configSource.IsInputActive = true;
            if (window.Content is FrameworkElement fe)
                configSource.Theme = (SystemBackdropTheme)fe.ActualTheme;

            controller = new DesktopAcrylicController
            {
                TintOpacity = (float)settings.TintOpacity,
                LuminosityOpacity = (float)settings.LuminosityOpacity,
                TintColor = ParseColor(settings.TintColor),
                FallbackColor = ParseColor(settings.FallbackColor),
                Kind = settings.Kind == "Thin"
                    ? DesktopAcrylicKind.Thin
                    : DesktopAcrylicKind.Base,
            };

            controller.AddSystemBackdropTarget(
                window.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
            controller.SetSystemBackdropConfiguration(configSource);
        }
        else
        {
            configSource = null;
            window.SystemBackdrop = settings.Type switch
            {
                "mica" => new MicaBackdrop(),
                "mica_alt" => new MicaBackdrop { Kind = MicaKind.BaseAlt },
                "none" => null,
                _ => new DesktopAcrylicBackdrop()
            };
        }
    }

    public static void ApplyThemeToWindow(Window window, string theme)
    {
        if (window.Content is FrameworkElement fe)
        {
            fe.RequestedTheme = theme switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
    }

    // ── Persistence ─────────────────────────────────────────────

    public static BackdropSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var type = root.TryGetProperty("backdrop", out var bProp)
                    ? bProp.GetString() ?? "acrylic" : "acrylic";
                var tintOpacity = root.TryGetProperty("tintOpacity", out var tProp)
                    ? tProp.GetDouble() : 0.8;
                var luminosity = root.TryGetProperty("luminosityOpacity", out var lProp)
                    ? lProp.GetDouble() : 0.8;
                var tintColor = root.TryGetProperty("tintColor", out var tcProp)
                    ? tcProp.GetString() ?? "#000000" : "#000000";
                var fallbackColor = root.TryGetProperty("fallbackColor", out var fcProp)
                    ? fcProp.GetString() ?? "#000000" : "#000000";
                var kind = root.TryGetProperty("kind", out var kProp)
                    ? kProp.GetString() ?? "Base" : "Base";

                return new BackdropSettings(type, tintOpacity, luminosity, tintColor, fallbackColor, kind);
            }
        }
        catch { }
        return new BackdropSettings("acrylic", 0.8, 0.8);
    }

    public static string LoadThemeSetting()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("theme", out var prop))
                    return prop.GetString() ?? "system";
            }
        }
        catch { }
        return "system";
    }

    public static void SaveBackdropSettings(BackdropSettings settings)
    {
        MergeAndSaveSettings(new Dictionary<string, object>
        {
            ["backdrop"] = settings.Type,
            ["tintOpacity"] = settings.TintOpacity,
            ["luminosityOpacity"] = settings.LuminosityOpacity,
            ["tintColor"] = settings.TintColor,
            ["fallbackColor"] = settings.FallbackColor,
            ["kind"] = settings.Kind
        });
    }

    public static void SaveThemeSetting(string theme)
    {
        MergeAndSaveSettings(new Dictionary<string, object> { ["theme"] = theme });
    }

    private static void MergeAndSaveSettings(Dictionary<string, object> newValues)
    {
        try
        {
            var existing = new Dictionary<string, object>();
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var doc = JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (newValues.ContainsKey(prop.Name)) continue;
                    existing[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                        JsonValueKind.String => prop.Value.GetString()!,
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => prop.Value.GetRawText()
                    };
                }
            }

            foreach (var kv in newValues)
                existing[kv.Key] = kv.Value;

            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(existing));
        }
        catch { }
    }

    public static Windows.UI.Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6)
            return new Windows.UI.Color { A = 255, R = 0, G = 0, B = 0 };
        return new Windows.UI.Color
        {
            A = 255,
            R = Convert.ToByte(hex[..2], 16),
            G = Convert.ToByte(hex[2..4], 16),
            B = Convert.ToByte(hex[4..6], 16)
        };
    }
}
