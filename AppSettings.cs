using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

public record PersistedWindowState(
    string Type,
    string NoteId,
    int X,
    int Y,
    bool IsCompact = false);

public static class AppSettings
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoteUI");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly string DefaultNotesFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoteUI");

    public static string GetDefaultNotesFolder() => DefaultNotesFolder;

    // ── Master Password (DPAPI) ─────────────────────────────────

    private static readonly string MasterPasswordPath = Path.Combine(SettingsDir, "master_pw.dat");

    public static bool HasMasterPassword() => File.Exists(MasterPasswordPath);

    public static void SaveMasterPasswordHash(string sha256Hash)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var bytes = Encoding.UTF8.GetBytes(sha256Hash);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(MasterPasswordPath, encrypted);
        }
        catch { }
    }

    public static string? LoadMasterPasswordHash()
    {
        try
        {
            if (!File.Exists(MasterPasswordPath)) return null;
            var encrypted = File.ReadAllBytes(MasterPasswordPath);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch { return null; }
    }

    public static void DeleteMasterPassword()
    {
        try { if (File.Exists(MasterPasswordPath)) File.Delete(MasterPasswordPath); } catch { }
    }

    public static string HashPassword(string password)
    {
        var bytes = Encoding.UTF8.GetBytes(password);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

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

    // ── Font ─────────────────────────────────────────────────

    public static string LoadFontSetting()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("font", out var prop))
                    return prop.GetString() ?? "geist";
            }
        }
        catch { }
        return "geist";
    }

    public static void SaveFontSetting(string font)
    {
        MergeAndSaveSettings(new Dictionary<string, object> { ["font"] = font });
    }

    public static FontFamily GetFontFamily(string font)
    {
        return font switch
        {
            "segoe" => new FontFamily("Segoe UI"),
            "inter" => new FontFamily("Assets/Fonts/Inter-Regular.ttf#Inter"),
            "jetbrains" => new FontFamily("Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono"),
            _ => new FontFamily("Assets/Fonts/Geist-Regular.otf#Geist"),
        };
    }

    public static void ApplyFontToTree(FrameworkElement root, FontFamily fontFamily)
    {
        if (root is Control c)
            c.FontFamily = fontFamily;
        else if (root is TextBlock tb)
            tb.FontFamily = fontFamily;

        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
                if (child is FrameworkElement fe)
                    ApplyFontToTree(fe, fontFamily);
        }
        else if (root is ContentControl cc && cc.Content is FrameworkElement content)
        {
            ApplyFontToTree(content, fontFamily);
        }
    }

    // ── Note style ────────────────────────────────────────────

    public static string LoadNoteStyle()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("noteStyle", out var prop))
                    return prop.GetString() ?? "titlebar";
            }
        }
        catch { }
        return "titlebar";
    }

    public static void SaveNoteStyle(string style)
    {
        MergeAndSaveSettings(new Dictionary<string, object> { ["noteStyle"] = style });
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
        ref IDisposable? controller, ref SystemBackdropConfiguration? configSource)
    {
        controller?.Dispose();
        controller = null;
        configSource = null;
        window.SystemBackdrop = null;

        if (settings.Type == "none")
            return;

        controller = CreateBackdropController(settings);
        if (controller is null)
            return;

        configSource = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Default
        };

        if (window.Content is FrameworkElement fe)
            configSource.Theme = (SystemBackdropTheme)fe.ActualTheme;

        ConfigureBackdropController(
            controller,
            window.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>(),
            configSource);
    }

    private static IDisposable? CreateBackdropController(BackdropSettings settings)
    {
        return settings.Type switch
        {
            "mica" when MicaController.IsSupported() => new MicaController(),
            "mica_alt" when MicaController.IsSupported() => new MicaController { Kind = MicaKind.BaseAlt },
            "acrylic_custom" when DesktopAcrylicController.IsSupported() => new DesktopAcrylicController
            {
                TintOpacity = (float)settings.TintOpacity,
                LuminosityOpacity = (float)settings.LuminosityOpacity,
                TintColor = ParseColor(settings.TintColor),
                FallbackColor = ParseColor(settings.FallbackColor),
                Kind = settings.Kind == "Thin"
                    ? DesktopAcrylicKind.Thin
                    : DesktopAcrylicKind.Base,
            },
            _ when DesktopAcrylicController.IsSupported() => new DesktopAcrylicController(),
            _ => null
        };
    }

    private static void ConfigureBackdropController(
        IDisposable controller,
        Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target,
        SystemBackdropConfiguration config)
    {
        switch (controller)
        {
            case MicaController mica:
                mica.AddSystemBackdropTarget(target);
                mica.SetSystemBackdropConfiguration(config);
                break;
            case DesktopAcrylicController acrylic:
                acrylic.AddSystemBackdropTarget(target);
                acrylic.SetSystemBackdropConfiguration(config);
                break;
        }
    }

    public static void ApplyThemeToWindow(Window window, string theme,
        SystemBackdropConfiguration? configSource = null)
    {
        if (window.Content is FrameworkElement fe)
        {
            fe.RequestedTheme = theme switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };

            if (configSource != null)
                configSource.Theme = (SystemBackdropTheme)fe.ActualTheme;
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

    public static (int X, int Y)? LoadMainWindowPosition()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return null;

            var json = File.ReadAllText(SettingsPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("mainWindowX", out var xProp) &&
                root.TryGetProperty("mainWindowY", out var yProp) &&
                xProp.TryGetInt32(out var x) &&
                yProp.TryGetInt32(out var y))
            {
                return (x, y);
            }
        }
        catch { }
        return null;
    }

    public static bool LoadMainWindowCompact()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return false;

            var json = File.ReadAllText(SettingsPath);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("mainWindowCompact", out var compactProp) &&
                (compactProp.ValueKind == JsonValueKind.True || compactProp.ValueKind == JsonValueKind.False))
            {
                return compactProp.GetBoolean();
            }
        }
        catch { }
        return false;
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

    public static void SaveMainWindowPosition(Windows.Graphics.PointInt32 position)
    {
        MergeAndSaveSettings(new Dictionary<string, object>
        {
            ["mainWindowX"] = position.X,
            ["mainWindowY"] = position.Y
        });
    }

    public static void SaveMainWindowCompact(bool isCompact)
    {
        MergeAndSaveSettings(new Dictionary<string, object>
        {
            ["mainWindowCompact"] = isCompact
        });
    }

    public static List<PersistedWindowState> LoadOpenWindows()
    {
        var result = new List<PersistedWindowState>();
        try
        {
            if (!File.Exists(SettingsPath))
                return result;

            var json = File.ReadAllText(SettingsPath);
            var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("openWindows", out var windowsProp))
            {
                return result;
            }

            var arrayElement = windowsProp;
            JsonDocument? nestedDoc = null;
            if (windowsProp.ValueKind == JsonValueKind.String)
            {
                var raw = windowsProp.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    nestedDoc = JsonDocument.Parse(raw);
                    arrayElement = nestedDoc.RootElement;
                }
            }

            if (arrayElement.ValueKind != JsonValueKind.Array)
            {
                nestedDoc?.Dispose();
                return result;
            }

            foreach (var item in arrayElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var type = item.TryGetProperty("type", out var typeProp)
                    ? typeProp.GetString() ?? ""
                    : "";
                var noteId = item.TryGetProperty("noteId", out var noteIdProp)
                    ? noteIdProp.GetString() ?? ""
                    : "";
                if (!item.TryGetProperty("x", out var xProp) ||
                    !item.TryGetProperty("y", out var yProp) ||
                    !xProp.TryGetInt32(out var x) ||
                    !yProp.TryGetInt32(out var y) ||
                    string.IsNullOrWhiteSpace(type))
                {
                    continue;
                }

                var isCompact =
                    item.TryGetProperty("isCompact", out var compactProp) &&
                    (compactProp.ValueKind == JsonValueKind.True || compactProp.ValueKind == JsonValueKind.False) &&
                    compactProp.GetBoolean();

                result.Add(new PersistedWindowState(type, noteId, x, y, isCompact));
            }

            nestedDoc?.Dispose();
        }
        catch { }
        return result;
    }

    public static void SaveOpenWindows(IEnumerable<PersistedWindowState> windows)
    {
        var payload = windows.Select(w => new Dictionary<string, object>
        {
            ["type"] = w.Type,
            ["noteId"] = w.NoteId,
            ["x"] = w.X,
            ["y"] = w.Y,
            ["isCompact"] = w.IsCompact
        }).ToList();

        MergeAndSaveSettings(new Dictionary<string, object>
        {
            ["openWindows"] = payload
        });
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
                    existing[prop.Name] = ConvertJsonElement(prop.Value) ?? "";
                }
            }

            foreach (var kv in newValues)
                existing[kv.Key] = kv.Value;

            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(existing));
        }
        catch { }
    }

    private static object? ConvertJsonElement(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject()
                .ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            JsonValueKind.Array => value.EnumerateArray()
                .Select(ConvertJsonElement)
                .ToList(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value.GetRawText()
        };
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
