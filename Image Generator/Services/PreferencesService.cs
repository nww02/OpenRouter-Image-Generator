using System;
using System.IO;
using System.Text.Json;
using Image_Generator.Models;

namespace Image_Generator.Services;

public class PreferencesService : IPreferencesService
{
    private readonly string _settingsPath;

    public PreferencesService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appData, "ImageGenerator");
        Directory.CreateDirectory(appFolder);
        _settingsPath = Path.Combine(appFolder, "preferences.json");
    }

    public AppPreferences Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var prefs = JsonSerializer.Deserialize<AppPreferences>(json);
                if (prefs is not null)
                {
                    // Decrypt API keys — handles both encrypted and legacy
                    // plaintext values automatically.
                    prefs.OpenRouterApiKey = KeyProtection.Decrypt(prefs.OpenRouterApiKey);
                    prefs.CustomProviderApiKey = KeyProtection.Decrypt(prefs.CustomProviderApiKey);
                }
                return prefs ?? new AppPreferences();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load preferences: {ex.Message}");
        }
        return new AppPreferences();
    }

    public void Save(AppPreferences preferences)
    {
        try
        {
            // Clone via round-trip so encrypting the copy never touches
            // the in-memory preferences that the rest of the app reads.
            var cloneJson = JsonSerializer.Serialize(preferences);
            var forSave = JsonSerializer.Deserialize<AppPreferences>(cloneJson)!;

            // Encrypt sensitive fields before writing to disk
            forSave.OpenRouterApiKey = KeyProtection.Encrypt(forSave.OpenRouterApiKey);
            forSave.CustomProviderApiKey = KeyProtection.Encrypt(forSave.CustomProviderApiKey);

            var json = JsonSerializer.Serialize(forSave, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save preferences: {ex.Message}");
        }
    }
}