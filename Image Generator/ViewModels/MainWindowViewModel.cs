using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using Image_Generator.Models;
using Image_Generator.Services;
using Image_Generator.ViewModels.Documents;
using Image_Generator.Views;

namespace Image_Generator.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IPreferencesService _preferencesService;
    private readonly AppPreferences _preferences;
    private readonly ProviderFactory _providerFactory;
    private readonly IImageIOService _imageIOService;
    private readonly DockFactory _dockFactory;

    [ObservableProperty]
    private IRootDock? _layout;

    // Status bar state -------------------------------------------------------

    /// <summary>Remaining OpenRouter account credit; null until fetched.</summary>
    [ObservableProperty]
    private double? _creditBalance;

    /// <summary>Session spend across all windows (images, prompts, vision). Resets each program load.</summary>
    [ObservableProperty]
    private double _sessionSpent;

    /// <summary>How many image documents are currently open.</summary>
    [ObservableProperty]
    private int _openWindowCount;

    public string BalanceText => CreditBalance is { } b
        ? $"Balance: ${b:0.00####}"
        : "Balance: —";

    public string SpentText => $"Spent: ${SessionSpent:0.00####}";

    public string WindowsText => OpenWindowCount == 1
        ? "1 window open"
        : $"{OpenWindowCount} windows open";

    partial void OnCreditBalanceChanged(double? value) => OnPropertyChanged(nameof(BalanceText));
    partial void OnSessionSpentChanged(double value) => OnPropertyChanged(nameof(SpentText));
    partial void OnOpenWindowCountChanged(int value) => OnPropertyChanged(nameof(WindowsText));

    /// <summary>
    /// Adds a reported API cost to the session total. Called by documents
    /// after every billable request (image generation, prompt building,
    /// image-to-prompt analysis). Null costs are ignored.
    /// </summary>
    public void AddSessionCost(double? cost)
    {
        if (cost is { } c)
            SessionSpent += c;
    }

    /// <summary>
    /// Refreshes the account balance from OpenRouter. Called at startup and
    /// after each generation, so the display stays current.
    /// </summary>
    public async Task RefreshBalanceAsync()
    {
        try
        {
            var provider = _providerFactory.CreateProvider(_preferences.SelectedProviderId, _preferences);
            CreditBalance = await provider.GetCreditBalanceAsync(_preferences.OpenRouterApiKey, CancellationToken.None);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindowViewModel] Balance refresh failed: {ex.Message}");
        }
    }

    public DockFactory DockFactory => _dockFactory;

    public MainWindowViewModel(
        IPreferencesService preferencesService,
        AppPreferences preferences,
        ProviderFactory providerFactory,
        IImageIOService imageIOService)
    {
        _preferencesService = preferencesService;
        _preferences = preferences;
        _providerFactory = providerFactory;
        _imageIOService = imageIOService;
        _dockFactory = new DockFactory(preferencesService, preferences, providerFactory, imageIOService);

        // Documents report their API costs and open/close events here so the
        // status bar can track the session total and open-window count. Each
        // cost also triggers a balance refresh so the two stay in step.
        _dockFactory.SessionCostAdded += c =>
        {
            AddSessionCost(c);
            if (c is not null)
                _ = RefreshBalanceAsync();
        };
        _dockFactory.WindowCountChanged += n => OpenWindowCount = n;

        var layout = _dockFactory.CreateLayout();
        if (layout is not null)
        {
            _dockFactory.InitLayout(layout);
        }
        Layout = layout;

        // Start with one blank image window open
        _dockFactory.CreateNewImageDocument();

        // Ensure the generation model's parameter schema is present: fetch it
        // from the OpenRouter API when the config has none (first run, or a
        // config saved before this feature existed). Fire-and-forget — the
        // blank window opens immediately and its parameters populate when the
        // fetch completes.
        _ = EnsureModelParametersAsync();

        // Session spend starts at zero on program load; refresh the balance.
        SessionSpent = 0;
        _ = RefreshBalanceAsync();
    }

    /// <summary>
    /// Loads the parameter schema for the configured generation model when the
    /// config has none, then stores it into preferences so future loads skip
    /// the API. Windows created before the fetch completes are backfilled.
    /// </summary>
    private async Task EnsureModelParametersAsync()
    {
        if (_preferences.OpenRouterGenerationModelParameters.Count > 0)
            return; // config already has the schema — nothing to do

        var model = _preferences.OpenRouterGenerationModel;
        if (string.IsNullOrWhiteSpace(model))
            return;

        try
        {
            var provider = _providerFactory.CreateProvider(_preferences.SelectedProviderId, _preferences);
            var descriptors = await provider.GetModelParameterDescriptorsAsync(
                model, _preferences.OpenRouterApiKey, CancellationToken.None);

            var userFacing = descriptors.Where(Image_Generator.Models.ModelParameters.IsUserFacing).ToList();
            if (userFacing.Count == 0)
                return; // model not in the image listing — keep config as-is

            _preferences.OpenRouterGenerationModelParameters = userFacing;
            _preferencesService.Save(_preferences);

            // Backfill windows that were created before the fetch completed
            // (e.g. the blank window from startup).
            foreach (var doc in _dockFactory.DocumentDock?.VisibleDockables?
                .OfType<ImageDocumentViewModel>() ?? Enumerable.Empty<ImageDocumentViewModel>())
            {
                if (doc.ModelParameters.Count == 0)
                {
                    foreach (var d in userFacing)
                        doc.ModelParameters.Add(new ModelParameterValue(d));
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"[MainWindowViewModel] Fetched {userFacing.Count} parameters for '{model}' on first load");
        }
        catch (Exception ex)
        {
            // Non-fatal: the ⋯ dialog will simply show "no parameters" until
            // the model is re-picked in Preferences.
            System.Diagnostics.Debug.WriteLine($"[MainWindowViewModel] Startup parameter fetch failed: {ex.Message}");
        }
    }

    public void InitLayout()
    {
        if (Layout is not null)
        {
            _dockFactory.InitLayout(Layout);
        }
    }

    [RelayCommand]
    private void NewImage()
    {
        _dockFactory.CreateNewImageDocument();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } }
            }
        });

        if (files.Count == 0) return;

        var file = files[0];
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var ms = new System.IO.MemoryStream();
            await stream.CopyToAsync(ms);
            var imageData = ms.ToArray();

            // Load the image with its .prompt metadata (prompt text, model,
            // generation parameters) — the loaded window restores saved
            // parameter values when the metadata's model matches the
            // configured generation model.
            var filePath = file.Path.LocalPath;
            var (_, metadata) = await _imageIOService.LoadImageWithMetadataAsync(filePath);

            _dockFactory.CreateLoadedImageDocument(
                imageData, metadata?.Prompt, file.Name,
                metadata?.Model, metadata?.Parameters, metadata?.Cost);
        }
        catch (Exception ex)
        {
            var errMsg = ex is ApiException apiEx ? apiEx.ToDisplayString() : $"Load failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[LoadAsync] {errMsg}");
            await ShowErrorAsync(errMsg);
        }
    }

    [RelayCommand]
    private async Task QuitAsync()
    {
        if (HasUnsavedDocuments())
        {
            var confirmed = await ConfirmQuitAsync();
            if (!confirmed) return;
        }

        if (GetTopLevel() is Window window)
        {
            window.Close();
        }
    }

    [RelayCommand]
    private async Task AboutAsync()
    {
        if (GetTopLevel() is Window window)
        {
            var dialog = new AboutDialog();
            await dialog.ShowDialog(window);
        }
    }

    [RelayCommand]
    private async Task PreferencesAsync()
    {
        if (GetTopLevel() is Window window)
        {
            var dialog = new PreferencesDialog();
            dialog.Initialize(_preferences, _providerFactory);
            var result = await dialog.ShowDialog<AppPreferences?>(window);
            if (result is not null)
            {
                // Save the preferences
                _preferences.OpenRouterApiKey = result.OpenRouterApiKey;
                _preferences.OpenRouterGenerationModel = result.OpenRouterGenerationModel;
                _preferences.OpenRouterVisionModel = result.OpenRouterVisionModel;
                _preferences.OpenRouterPromptGenModel = result.OpenRouterPromptGenModel;
                _preferences.SelectedProviderId = result.SelectedProviderId;
                _preferences.CustomProviderName = result.CustomProviderName;
                _preferences.CustomProviderBaseUrl = result.CustomProviderBaseUrl;
                _preferences.CustomProviderApiKey = result.CustomProviderApiKey;
                _preferences.CustomGenerationModel = result.CustomGenerationModel;
                _preferences.CustomVisionModel = result.CustomVisionModel;
                _preferences.OpenRouterGenerationModelParameters = result.OpenRouterGenerationModelParameters;
                _preferences.OpenRouterGenerationModelParametersFor = result.OpenRouterGenerationModelParametersFor;
                _preferences.Theme = result.Theme;

                // If the generation model changed, the stored parameter schema
                // belongs to the old model — re-fetch for the new one and
                // replace it in the config (mid-flight windows keep their own
                // copies; new windows get the fresh schema).
                if (_preferences.OpenRouterGenerationModelParametersFor != _preferences.OpenRouterGenerationModel)
                {
                    await RefreshModelParametersAsync();
                }

                _preferencesService.Save(_preferences);

                // Apply theme immediately
                if (Avalonia.Application.Current is App app)
                {
                    app.ApplyTheme(_preferences.Theme);
                }
            }
        }
    }

    /// <summary>
    /// Re-fetches the parameter schema for the currently configured generation
    /// model and replaces the stored copy in preferences. Existing image
    /// windows keep their own parameter values; only the shared schema is
    /// swapped so future windows use the new model's parameters.
    /// </summary>
    private async Task RefreshModelParametersAsync()
    {
        var model = _preferences.OpenRouterGenerationModel;
        if (string.IsNullOrWhiteSpace(model))
            return;

        try
        {
            var provider = _providerFactory.CreateProvider(_preferences.SelectedProviderId, _preferences);
            var descriptors = await provider.GetModelParameterDescriptorsAsync(
                model, _preferences.OpenRouterApiKey, CancellationToken.None);

            var userFacing = descriptors.Where(Image_Generator.Models.ModelParameters.IsUserFacing).ToList();
            if (userFacing.Count == 0)
                return; // new model exposes nothing — clear the stale schema below

            _preferences.OpenRouterGenerationModelParameters = userFacing;
            _preferences.OpenRouterGenerationModelParametersFor = model;
            System.Diagnostics.Debug.WriteLine(
                $"[MainWindowViewModel] Replaced parameters with {userFacing.Count} for '{model}'");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[MainWindowViewModel] Parameter refresh failed for '{model}': {ex.Message}");
        }
    }

    private bool HasUnsavedDocuments()
    {
        return _dockFactory.DocumentDock?.VisibleDockables?
            .OfType<ImageDocumentViewModel>()
            .Any(vm => vm.IsDirty) == true;
    }

    private async Task<bool> ConfirmQuitAsync()
    {
        if (GetTopLevel() is Window window)
        {
            var dialog = new QuitConfirmDialog();
            return await dialog.ShowDialog<bool>(window);
        }
        return false;
    }

    private async Task ShowErrorAsync(string message)
    {
        if (GetTopLevel() is Window window)
        {
            var dialog = new ErrorDialog(message);
            await dialog.ShowDialog(window);
        }
    }

    private TopLevel? GetTopLevel()
    {
        return Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
    }
}