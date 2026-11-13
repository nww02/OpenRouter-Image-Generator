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
                return prefs ?? new AppPreferences();
            }
        }
        catch (Exception ex)
        {
            // Log error if needed
            Console.WriteLine($"Failed to load preferences: {ex.Message}");
        }
        return new AppPreferences();
    }

    public void Save(AppPreferences preferences)
    {
        try
        {
            var json = JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save preferences: {ex.Message}");
        }
    }
}