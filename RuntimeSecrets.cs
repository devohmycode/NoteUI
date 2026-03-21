using System.Text.Json;

namespace NoteUI;

public static class RuntimeSecrets
{
    private const string FirebaseUrlEnv = "NOTEUI_FIREBASE_URL";
    private const string FirebaseApiKeyEnv = "NOTEUI_FIREBASE_API_KEY";
    private const string GoogleClientIdEnv = "NOTEUI_GOOGLE_CLIENT_ID";
    private const string GoogleClientSecretEnv = "NOTEUI_GOOGLE_CLIENT_SECRET";
    private const string BundledConfigFileName = "firebase.public.json";

    public static bool TryGetFirebaseConfig(out string firebaseUrl, out string firebaseApiKey)
    {
        var bundled = LoadBundledConfig();
        var (savedUrl, savedApiKey, _) = AppSettings.LoadFirebaseSettings();
        firebaseUrl = FirstNonEmpty(
            Environment.GetEnvironmentVariable(FirebaseUrlEnv),
            savedUrl,
            bundled.FirebaseUrl);
        firebaseApiKey = FirstNonEmpty(
            Environment.GetEnvironmentVariable(FirebaseApiKeyEnv),
            savedApiKey,
            bundled.FirebaseApiKey);
        return !string.IsNullOrWhiteSpace(firebaseUrl) && !string.IsNullOrWhiteSpace(firebaseApiKey);
    }

    public static bool TryGetGoogleClientId(out string googleClientId)
    {
        var bundled = LoadBundledConfig();
        googleClientId = FirstNonEmpty(
            Environment.GetEnvironmentVariable(GoogleClientIdEnv),
            bundled.GoogleClientId);
        return !string.IsNullOrWhiteSpace(googleClientId);
    }

    public static bool TryGetGoogleClientSecret(out string googleClientSecret)
    {
        var bundled = LoadBundledConfig();
        googleClientSecret = FirstNonEmpty(
            Environment.GetEnvironmentVariable(GoogleClientSecretEnv),
            bundled.GoogleClientSecret);
        return !string.IsNullOrWhiteSpace(googleClientSecret);
    }

    private static (string FirebaseUrl, string FirebaseApiKey, string GoogleClientId, string GoogleClientSecret) LoadBundledConfig()
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, BundledConfigFileName);
            if (!File.Exists(configPath))
                return ("", "", "", "");

            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;

            return (
                GetString(root, "firebaseUrl"),
                GetString(root, "firebaseApiKey"),
                GetString(root, "googleClientId"),
                GetString(root, "googleClientSecret"));
        }
        catch
        {
            return ("", "", "", "");
        }
    }

    private static string GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
            return "";
        return prop.GetString()?.Trim() ?? "";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return "";
    }
}
