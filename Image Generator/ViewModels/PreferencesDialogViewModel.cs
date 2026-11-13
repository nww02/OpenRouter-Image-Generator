using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Image_Generator.Models;
using Image_Generator.Services;

namespace Image_Generator.ViewModels;

/// <summary>
/// ViewModel for the Preferences dialog. Works on a copy of AppPreferences
/// so changes are only committed on Save.
/// </summary>
public partial class PreferencesDialogViewModel : ViewModelBase
{
    private readonly AppPreferences _original;
    private readonly IProviderFactory _providerFactory;

    // Available providers
    public List<ProviderDescriptor> AvailableProviders { get; }

    // Theme options
    public List<string> ThemeOptions { get; } = new() { "Dark", "Light", "System" };

    [ObservableProperty]
    private ProviderDescriptor? _selectedProviderDescriptor;

    [ObservableProperty]
    private string _openRouterApiKey;

    [ObservableProperty]
    private string _openRouterGenerationModel;

    [ObservableProperty]
    private string _openRouterVisionModel;

    [ObservableProperty]
    private string _openRouterPromptGenModel;

    [ObservableProperty]
    private string _customProviderName;

    [ObservableProperty]
    private string _customProviderBaseUrl;

    [ObservableProperty]
    private string _customProviderApiKey;

    [ObservableProperty]
    private string _customGenerationModel;

    [ObservableProperty]
    private string _customVisionModel;

    [ObservableProperty]
    private string _theme;

    // Whether we're showing OpenRouter-specific controls
    public bool IsOpenRouter => SelectedProviderDescriptor?.Id == "openrouter";

    // Whether we're showing custom/local provider controls
    public bool IsCustomProvider => SelectedProviderDescriptor?.Id != "openrouter";

    // The selected provider ID string (derived from the descriptor)
    public string SelectedProviderId => SelectedProviderDescriptor?.Id ?? "openrouter";

    /// <summary>
    /// Parameter schema for the chosen image generation model, fetched from the
    /// provider when the model is picked. Copied back into preferences on Save.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<ModelParameterDescriptor> GenerationModelParameters { get; } = new();

    /// <summary>
    /// The model id the current GenerationModelParameters were fetched for.
    /// Empty means the parameters are stale/unknown — the caller refetches.
    /// </summary>
    public string GenerationModelParametersFor { get; set; } = string.Empty;

    public PreferencesDialogViewModel(AppPreferences existing, IProviderFactory providerFactory)
    {
        _original = existing;
        _providerFactory = providerFactory;

        AvailableProviders = providerFactory.GetAvailableProviders();

        // Copy all values from the original
        _selectedProviderDescriptor = AvailableProviders.Find(p => p.Id == existing.SelectedProviderId);
        _openRouterApiKey = existing.OpenRouterApiKey;
        _openRouterGenerationModel = existing.OpenRouterGenerationModel;
        _openRouterVisionModel = existing.OpenRouterVisionModel;
        _openRouterPromptGenModel = existing.OpenRouterPromptGenModel;
        _customProviderName = existing.CustomProviderName;
        _customProviderBaseUrl = existing.CustomProviderBaseUrl;
        _customProviderApiKey = existing.CustomProviderApiKey;
        _customGenerationModel = existing.CustomGenerationModel;
        _customVisionModel = existing.CustomVisionModel;
        _theme = existing.Theme;

        foreach (var d in existing.OpenRouterGenerationModelParameters)
            GenerationModelParameters.Add(d);

        GenerationModelParametersFor = existing.OpenRouterGenerationModelParametersFor;
    }

    partial void OnOpenRouterGenerationModelChanged(string value)
    {
        // Manual edit of the model id: the loaded schema belongs to some other
        // model. Keep it as a fallback but mark origin unknown so the save path
        // re-fetches for the new model.
        if (value != GenerationModelParametersFor)
            GenerationModelParametersFor = string.Empty;
    }

    partial void OnSelectedProviderDescriptorChanged(ProviderDescriptor? value)
    {
        OnPropertyChanged(nameof(IsOpenRouter));
        OnPropertyChanged(nameof(IsCustomProvider));
        OnPropertyChanged(nameof(SelectedProviderId));

        // Set default BaseUrl when switching to a known provider
        if (value is not null && !string.IsNullOrEmpty(value.DefaultBaseUrl) && string.IsNullOrEmpty(CustomProviderBaseUrl))
        {
            CustomProviderBaseUrl = value.DefaultBaseUrl;
        }
    }

    /// <summary>
    /// Applies the dialog's values back to the given AppPreferences object.
    /// </summary>
    public void ApplyTo(AppPreferences target)
    {
        target.SelectedProviderId = SelectedProviderId;
        target.OpenRouterApiKey = OpenRouterApiKey;
        target.OpenRouterGenerationModel = OpenRouterGenerationModel;
        target.OpenRouterVisionModel = OpenRouterVisionModel;
        target.OpenRouterPromptGenModel = OpenRouterPromptGenModel;
        target.CustomProviderName = CustomProviderName;
        target.CustomProviderBaseUrl = CustomProviderBaseUrl;
        target.CustomProviderApiKey = CustomProviderApiKey;
        target.CustomGenerationModel = CustomGenerationModel;
        target.CustomVisionModel = CustomVisionModel;
        target.OpenRouterGenerationModelParameters = GenerationModelParameters.ToList();
        target.OpenRouterGenerationModelParametersFor = GenerationModelParametersFor;
        target.Theme = Theme;
    }
}