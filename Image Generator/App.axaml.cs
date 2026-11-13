using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Dock.Settings;
using Image_Generator.Services;
using Image_Generator.ViewModels;

namespace Image_Generator;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Enable in-app floating MDI windows (drag-and-drop within the main window)
        DockSettings.UseManagedWindows = true;

        // Application services
        var preferencesService = new PreferencesService();
        var preferences = preferencesService.Load();
        var providerFactory = new ProviderFactory();
        var imageIOService = new ImageIOService();

        // Apply saved theme before main window is created (avoids flash of wrong theme)
        ApplyTheme(preferences.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainViewModel = new MainWindowViewModel(preferencesService, preferences, providerFactory, imageIOService);
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainViewModel
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Applies the theme variant across the entire application.
    /// </summary>
    public void ApplyTheme(string theme)
    {
        RequestedThemeVariant = theme switch
        {
            "Dark" => Avalonia.Styling.ThemeVariant.Dark,
            "Light" => Avalonia.Styling.ThemeVariant.Light,
            _ => Avalonia.Styling.ThemeVariant.Default // "System"
        };
    }
}