using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Image_Generator.Models;
using Image_Generator.Services;

namespace Image_Generator.ViewModels;

/// <summary>Columns the model list can be sorted by.</summary>
public enum ModelSortColumn
{
    Name,
    Price
}

/// <summary>
/// ViewModel for the Model Browser dialog. Shows available models from the
/// connected provider with pricing, search filtering, and lets the user pick one.
/// </summary>
public partial class ModelBrowserDialogViewModel : ViewModelBase
{
    private readonly IImageGenerationProvider _provider;
    private readonly string? _apiKey;

    // Active sort (default: name ascending, matching the original behaviour)
    private ModelSortColumn _sortColumn = ModelSortColumn.Name;
    private bool _sortDescending;

    /// <summary>Filter choices shown in the dropdown.</summary>
    public static readonly List<string> FilterOptions = new() { "Image Gen", "Vision", "All Models" };

    public static string FilterTypeFromLabel(string label) => label switch
    {
        "Image Gen" => "generation",
        "Vision" => "vision",
        _ => "all"
    };

    private static string LabelFromFilterType(string filterType) => filterType switch
    {
        "generation" => "Image Gen",
        "vision" => "Vision",
        _ => "All Models"
    };

    // All models loaded from the API (unfiltered)
    private List<ProviderModelInfo> _allModels = new();

    // Filtered + searched models displayed in the list
    public ObservableCollection<ProviderModelInfo> Models { get; } = new();

    [ObservableProperty]
    private ProviderModelInfo? _selectedModel;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _showFreeOnly;

    [ObservableProperty]
    private string _selectedFilter = "All Models";

    public bool HasSelection => SelectedModel is not null;

    public bool IsEmpty => Models.Count == 0 && !IsLoading;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public string EmptyMessage => ErrorMessage ?? (IsLoading ? "Loading..." : "Click 'Refresh' to load models from the provider.");

    /// <summary>Column-header text for the Name column, with sort arrow.</summary>
    public string NameSortHeader => _sortColumn == ModelSortColumn.Name
        ? (_sortDescending ? "Name ▼" : "Name ▲")
        : "Name";

    /// <summary>Column-header text for the Price column, with sort arrow.</summary>
    public string PriceSortHeader => _sortColumn == ModelSortColumn.Price
        ? (_sortDescending ? "Price ▼" : "Price ▲")
        : "Price";

    /// <summary>
    /// Sorts the list by the given column. Clicking the already-active column
    /// toggles between ascending and descending; switching columns resets
    /// to ascending.
    /// </summary>
    public void SortBy(ModelSortColumn column)
    {
        if (_sortColumn == column)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            _sortDescending = false;
        }

        OnPropertyChanged(nameof(NameSortHeader));
        OnPropertyChanged(nameof(PriceSortHeader));
        ApplyClientSideFilter();
    }

    /// <summary>
    /// The filter type identifier for the code-behind to read.
    /// </summary>
    public string CurrentFilterType => FilterTypeFromLabel(SelectedFilter);

    public ModelBrowserDialogViewModel(
        IImageGenerationProvider provider,
        string? apiKey,
        string filterType) // "generation", "vision", or "all"
    {
        _provider = provider;
        _apiKey = apiKey;
        _selectedFilter = LabelFromFilterType(filterType);
    }

    partial void OnSelectedModelChanged(ProviderModelInfo? value)
    {
        OnPropertyChanged(nameof(HasSelection));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyClientSideFilter();
    }

    partial void OnShowFreeOnlyChanged(bool value)
    {
        ApplyClientSideFilter();
    }

    partial void OnSelectedFilterChanged(string value)
    {
        OnPropertyChanged(nameof(CurrentFilterType));
        ApplyClientSideFilter();
    }

    /// <summary>
    /// Loads models from the provider and applies current filter + search.
    /// </summary>
    [RelayCommand]
    private async Task LoadModelsAsync()
    {
        if (IsLoading) return;

        IsLoading = true;
        ErrorMessage = null;
        Models.Clear();

        try
        {
            _allModels = await _provider.ListModelsAsync(_apiKey, CancellationToken.None);
            ApplyClientSideFilter();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load models: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>
    /// Applies the current filter type and search text to the full model list.
    /// </summary>
    private void ApplyClientSideFilter()
    {
        Models.Clear();

        IEnumerable<ProviderModelInfo> query = _allModels;

        // Apply capability filter
        query = CurrentFilterType switch
        {
            "generation" => query.Where(m => m.Capabilities.SupportsImageGeneration),
            "vision" => query.Where(m => m.Capabilities.SupportsVision),
            _ => query // "all" — no filter
        };

        // Apply "Free" filter (match models with ":free" suffix or "(free)" in name)
        if (ShowFreeOnly)
        {
            query = query.Where(m =>
                m.Id.EndsWith(":free", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("(free)", StringComparison.OrdinalIgnoreCase));
        }

        // Apply search text filter (case-insensitive match on ID or Name)
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            query = query.Where(m =>
                m.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var model in SortModels(query))
        {
            Models.Add(model);
        }

        if (Models.Count == 0 && _allModels.Count > 0)
        {
            ErrorMessage = CurrentFilterType == "all"
                ? "No models match your search."
                : $"No {CurrentFilterType} models match your search.";
        }
        else if (Models.Count == 0 && _allModels.Count == 0)
        {
            ErrorMessage = "No models found. Click Refresh to try again.";
        }
        else
        {
            ErrorMessage = null; // clear error when results exist
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    /// <summary>
    /// Formats model pricing for display (shared implementation in
    /// ModelPricingFormatter: per-image/per-request fees plus token prices
    /// shown per million tokens with input/output broken out).
    /// </summary>
    public static string FormatPricing(ModelPricing? pricing)
        => ModelPricingFormatter.Format(pricing);

    /// <summary>
    /// Orders the filtered models by the active sort column: alphabetical
    /// name, or by SortPrice (per-MTok output tokens for chat/vision models,
    /// per-image/per-request fees for image models). Models with unknown
    /// pricing always sort last regardless of direction.
    /// </summary>
    private IEnumerable<ProviderModelInfo> SortModels(IEnumerable<ProviderModelInfo> models)
    {
        if (_sortColumn == ModelSortColumn.Price)
        {
            // Unknown pricing (double.MaxValue) is pinned to the bottom in
            // both directions: when descending, treat it as the smallest.
            return _sortDescending
                ? models
                    .OrderByDescending(m => m.SortPrice == double.MaxValue ? double.MinValue : m.SortPrice)
                    .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                : models
                    .OrderBy(m => m.SortPrice)
                    .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase);
        }

        return _sortDescending
            ? models.OrderByDescending(m => m.Name, StringComparer.OrdinalIgnoreCase)
            : models.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase);
    }
}