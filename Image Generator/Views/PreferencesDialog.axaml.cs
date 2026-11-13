using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Image_Generator.Models;
using Image_Generator.Services;
using Image_Generator.ViewModels;

namespace Image_Generator.Views;

public partial class PreferencesDialog : Window
{
    private PreferencesDialogViewModel? _viewModel;
    private AppPreferences? _preferences;
    private IProviderFactory? _providerFactory;

    public PreferencesDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Sets up the dialog with references to the current preferences so it can
    /// build a copy. Call before ShowDialog&lt;AppPreferences?&gt;().
    /// </summary>
    public void Initialize(AppPreferences existing, IProviderFactory providerFactory)
    {
        _preferences = existing;
        _providerFactory = providerFactory;
        _viewModel = new PreferencesDialogViewModel(existing, providerFactory);
        DataContext = _viewModel;
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();

        if (_viewModel is null)
        {
            _viewModel = new PreferencesDialogViewModel(new AppPreferences(), new ProviderFactory());
            DataContext = _viewModel;
        }
    }

    private async void OnBrowseGenerationModel(object? sender, RoutedEventArgs e)
    {
        await BrowseModel("generation");
    }

    private async void OnBrowsePromptGenModel(object? sender, RoutedEventArgs e)
    {
        await BrowseModel("promptgen");
    }

    private async void OnBrowseVisionModel(object? sender, RoutedEventArgs e)
    {
        await BrowseModel("vision");
    }

    private async Task BrowseModel(string filterType)
    {
        if (_preferences is null || _providerFactory is null)
            return;

        var provider = _providerFactory.CreateProvider(_preferences.SelectedProviderId, _preferences);
        var apiKey = _preferences.SelectedProviderId == "openrouter"
            ? _preferences.OpenRouterApiKey
            : _preferences.CustomProviderApiKey;

        var dialog = new ModelBrowserDialog();
        dialog.Initialize(provider, apiKey, filterType);
        var result = await dialog.ShowDialog<string?>(this);

        if (result is not null && _viewModel is not null)
        {
            if (filterType == "generation")
            {
                _viewModel.OpenRouterGenerationModel = result;

                // Fetch the chosen model's parameter schema so new image windows
                // can offer its parameters (enums/ranges/flags). Failure is
                // non-fatal — the parameters button simply offers nothing.
                if (_preferences.SelectedProviderId == "openrouter")
                {
                    try
                    {
                        var descriptors = await provider.GetModelParameterDescriptorsAsync(result, apiKey, CancellationToken.None);
                        _viewModel.GenerationModelParameters.Clear();
                        foreach (var d in descriptors.Where(ModelParameters.IsUserFacing))
                            _viewModel.GenerationModelParameters.Add(d);
                        _viewModel.GenerationModelParametersFor = result;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Preferences] Parameter fetch failed: {ex.Message}");
                        _viewModel.GenerationModelParametersFor = string.Empty;
                    }
                }
            }
            else if (filterType == "promptgen")
                _viewModel.OpenRouterPromptGenModel = result;
            else
                _viewModel.OpenRouterVisionModel = result;
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;

        var result = new AppPreferences();
        _viewModel.ApplyTo(result);
        Close(result);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}