using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Image_Generator.Models;
using Image_Generator.Services;
using Image_Generator.Utils;
using Image_Generator.ViewModels;
using Image_Generator.Views;

namespace Image_Generator.ViewModels.Documents;

/// <summary>
/// One image document: MDI floating window with canvas, history,
/// dimensions, prompt and generate/save buttons.
/// </summary>
public partial class ImageDocumentViewModel : Document
{
    private readonly IPreferencesService _preferencesService;
    private readonly AppPreferences _preferences;
    private readonly ProviderFactory _providerFactory;
    private readonly IImageIOService _imageIOService;

    // Shared prompt generator VM so selections persist across dialog opens
    private PromptGeneratorDialogViewModel? _promptGenViewModel;

    public ObservableCollection<GenerationRecordViewModel> History { get; } = new();

    /// <summary>
    /// This window's own copy of the generation model's parameters: the
    /// shared descriptors from preferences plus this window's chosen values.
    /// A null value means "use the model's default" (parameter omitted from
    /// the request). New windows start with every value unset.
    /// </summary>
    public ObservableCollection<ModelParameterValue> ModelParameters { get; } = new();

    /// <summary>
    /// Raised whenever this window makes a billable API call that reported a
    /// cost (image generation, prompt building, image-to-prompt). The dock
    /// factory forwards it to the main window's session total.
    /// </summary>
    public event System.Action<double?>? CostReported;

    /// <summary>
    /// This window's own image generation model. Copied from the configured
    /// default when the window is created; the Model Parameters dialog can
    /// override it per-window (re-fetching that model's parameters from the
    /// API). Generation uses this, not the config value.
    /// </summary>
    [ObservableProperty]
    private string _generationModel = string.Empty;

    [ObservableProperty]
    private GenerationRecordViewModel? _currentRecord;

    [ObservableProperty]
    private string _currentPrompt = string.Empty;

    [ObservableProperty]
    private Bitmap? _currentImageBitmap;

    [ObservableProperty]
    private bool _isGenerating;

    [ObservableProperty]
    private bool _isDirty;

    public bool HasCurrentImage => CurrentImageBitmap is not null;

    /// <summary>
    /// Builds a deterministic lookup key for the cost-history dictionary from a
    /// model ID and its parameters. E.g.:
    ///   "black-forest-labs/flux-3-image-klein|aspect_ratio=1:1|output_format=png"
    /// </summary>
    public static string MakeCostKey(string model, IReadOnlyDictionary<string, string?>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return model;

        var sorted = parameters
            .Where(p => p.Value is not null)
            .OrderBy(p => p.Key)
            .Select(p => $"{p.Key}={p.Value}");

        return model + "|" + string.Join("|", sorted);
    }

    /// <summary>Tooltip for the Prompt→Image button.</summary>
    public string GenerateButtonTooltip
    {
        get
        {
            var model = GetGenerationModel();
            var key = MakeCostKey(model, GetParameterValues());
            return FormatCostTooltip(key, "Generate image");
        }
    }

    /// <summary>Tooltip for the Image→Prompt button.</summary>
    public string ImageToPromptButtonTooltip
    {
        get
        {
            var model = GetVisionModel();
            return FormatCostTooltip(model, "Analyse image and build prompt");
        }
    }

    private string FormatCostTooltip(string key, string action)
    {
        if (_preferences.ModelCostAverages.TryGetValue(key, out var avg) && avg > 0)
            return $"{action} (~${avg:F4})";
        return $"{action} (cost unknown)";
    }

    private void RefreshTooltips()
    {
        OnPropertyChanged(nameof(GenerateButtonTooltip));
        OnPropertyChanged(nameof(ImageToPromptButtonTooltip));
    }

    /// <summary>
    /// Updates the running-average cost for a model/parameter combination and
    /// persists it so the tooltip estimate survives restarts.
    /// </summary>
    private void UpdateCostAverage(string key, double newCost)
    {
        if (_preferences.ModelCostAverages.TryGetValue(key, out var prev))
            _preferences.ModelCostAverages[key] = (prev + newCost) / 2.0;
        else
            _preferences.ModelCostAverages[key] = newCost;

        _preferencesService.Save(_preferences);
        RefreshTooltips();
    }

    /// <summary>
    /// Recomputes the inherited Document.Title shown in the MDI titlebar.
    /// Format: first 20 characters of prompt + "…" + (W×H).
    /// When there is no prompt or no record, shows "(no prompt)".
    /// </summary>
    private void UpdateTitle()
    {
        if (CurrentRecord?.Record is { } rec)
        {
            var prompt = rec.Prompt;
            var promptPart = string.IsNullOrEmpty(prompt)
                ? "(no prompt)"
                : prompt.Length > 20
                    ? prompt[..20] + "…"
                    : prompt;
            Title = $"{promptPart} ({rec.Width}×{rec.Height})";
        }
        else
        {
            Title = "(no prompt)";
        }
    }

    public ImageDocumentViewModel(
        IPreferencesService preferencesService,
        AppPreferences preferences,
        ProviderFactory providerFactory,
        IImageIOService imageIOService)
    {
        _preferencesService = preferencesService;
        _preferences = preferences;
        _providerFactory = providerFactory;
        _imageIOService = imageIOService;

        // Refresh button tooltips whenever the window's parameters change.
        ModelParameters.CollectionChanged += (_, _) => RefreshTooltips();
    }

    partial void OnCurrentImageBitmapChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasCurrentImage));
    }

    partial void OnIsDirtyChanged(bool value)
    {
        UpdateTitle();
    }

    partial void OnCurrentRecordChanged(GenerationRecordViewModel? value)
    {
        UpdateTitle();
    }

    partial void OnGenerationModelChanged(string value)
    {
        RefreshTooltips();
    }

    /// <summary>
    /// Loads an image (from File > Load) into this document as the first
    /// history record, marked as already saved. The sidecar's model becomes
    /// THIS window's generation model — loading an image made with a
    /// different model gives you a window configured for that model, so
    /// further generations continue where the original left off. The saved
    /// parameters are restored once the model's schema is in place (fetched
    /// from the API when it isn't the configured model).
    /// </summary>
    public void LoadFromImage(byte[] imageData, string? prompt,
        string? model = null, Dictionary<string, string?>? parameters = null, double? cost = null)
    {
        var record = new GenerationRecord
        {
            ImageData = imageData,
            Prompt = prompt ?? string.Empty,
            IsSaved = true,
            Model = model ?? string.Empty,
            Cost = cost,
            Parameters = parameters ?? new Dictionary<string, string?>()
        };

        var recordVm = new GenerationRecordViewModel(record);
        History.Add(recordVm);

        CurrentRecord = recordVm;
        CurrentPrompt = prompt ?? string.Empty;
        CurrentImageBitmap = ImageUtils.BytesToBitmap(imageData);
        IsDirty = false;

        // Adopt the sidecar's model (if any) as this window's model, and
        // queue its saved parameters. The ModelParameters collection is
        // filled by AddDocument afterwards; when the sidecar's model differs
        // from the config model, the factory fetches that model's schema
        // from the API before the deferred values are applied.
        if (!string.IsNullOrEmpty(record.Model))
        {
            GenerationModel = record.Model;
            _pendingParameterRestore = record.Parameters;
        }

        UpdateTitle();
    }

    /// <summary>
    /// Parameter values waiting to be restored once this window's parameter
    /// descriptors are populated (see AddDocument in DockFactory).
    /// </summary>
    private Dictionary<string, string?>? _pendingParameterRestore;

    /// <summary>
    /// Called by the dock factory after the parameter descriptors have been
    /// added — applies any parameter values deferred from LoadFromImage.
    /// </summary>
    public void ApplyPendingParameterRestore()
    {
        if (_pendingParameterRestore is not null)
        {
            ApplyParameterValues(_pendingParameterRestore);
            _pendingParameterRestore = null;
        }
    }

    /// <summary>
    /// Sets this window's parameter values from a name→value map. Unknown
    /// names (the model's schema changed since the image was saved) are
    /// ignored; known names get their value restored.
    /// </summary>
    public void ApplyParameterValues(Dictionary<string, string?>? values)
    {
        if (values is null)
            return;

        foreach (var param in ModelParameters)
        {
            if (values.TryGetValue(param.Descriptor.Name, out var v))
                param.Value = v;
        }
    }

    /// <summary>
    /// The parameter values this window currently uses, keyed by name —
    /// saved into the .prompt metadata on save.
    /// </summary>
    public Dictionary<string, string?> GetParameterValues() =>
        ModelParameters.Where(p => p.Value is not null)
            .ToDictionary(p => p.Descriptor.Name, p => p.Value);

    [RelayCommand]
    private void RestoreRecord(GenerationRecordViewModel record)
    {
        CurrentRecord = record;
        CurrentPrompt = record.Record.Prompt;
        CurrentImageBitmap = ImageUtils.BytesToBitmap(record.Record.ImageData);

        // History entries recorded what they were generated with; restoring
        // also restores those parameter values into the window.
        ApplyParameterValues(record.Record.Parameters);

        foreach (var item in History)
            item.IsSelected = item == record;
    }

    [RelayCommand]
    private async Task GenerateImageAsync()
    {
        if (IsGenerating || string.IsNullOrWhiteSpace(CurrentPrompt))
            return;

        IsGenerating = true;
        try
        {
            var provider = _providerFactory.CreateProvider(_preferences.SelectedProviderId, _preferences);
            var apiKey = _preferences.SelectedProviderId == "openrouter"
                ? _preferences.OpenRouterApiKey
                : _preferences.CustomProviderApiKey;

            // Merge this window's chosen model parameters into the request.
            // Null values (unset/"default") are omitted by the provider.
            // Merge this window's chosen model parameters into the request.
            // Null values (unset/"default") are omitted by the provider.
            var parameters = ModelParameters
                .Where(p => p.Value is not null)
                .ToDictionary(p => p.Descriptor.Name, p => Models.ModelParameters.ToRequestValue(p.Descriptor.Name, p.Value));

            var generationModel = GetGenerationModel();
            var result = await provider.GenerateImageAsync(
                CurrentPrompt, 0, 0,
                generationModel, apiKey, CancellationToken.None, parameters);
            var imageBytes = result.ImageData;

            // Record the actual output dimensions from the decoded bitmap.
            int outWidth = 0, outHeight = 0;
            using (var ms = new System.IO.MemoryStream(imageBytes))
            {
                using var bmp = new Bitmap(ms);
                outWidth = bmp.PixelSize.Width;
                outHeight = bmp.PixelSize.Height;
            }

            var record = new GenerationRecord
            {
                ImageData = imageBytes,
                Prompt = CurrentPrompt,
                Width = outWidth,
                Height = outHeight,
                IsSaved = false,
                // Remember the model, parameters and cost used, so saving can
                // write them into the .prompt metadata and restoring (via
                // history click or file load) can put them back.
                Model = generationModel,
                Cost = result.Cost,
                Parameters = ModelParameters
                    .Where(p => p.Value is not null)
                    .ToDictionary(p => p.Descriptor.Name, p => p.Value)
            };

            CostReported?.Invoke(result.Cost);

            // Record the generation cost into the running average so the
            // Prompt→Image tooltip can show an estimate next time.
            if (result.Cost.HasValue)
            {
                var costKey = MakeCostKey(generationModel, GetParameterValues());
                UpdateCostAverage(costKey, result.Cost.Value);
            }

            var recordVm = new GenerationRecordViewModel(record);
            History.Add(recordVm);

            CurrentRecord = recordVm;
            CurrentPrompt = record.Prompt;
            CurrentImageBitmap = ImageUtils.BytesToBitmap(imageBytes);
            IsDirty = true;
            UpdateTitle();

            foreach (var item in History)
                item.IsSelected = item == recordVm;
        }
        catch (Exception ex)
        {
            var errMsg = ex is ApiException apiEx ? apiEx.ToDisplayString() : $"Generation failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[GenerateImageAsync] {errMsg}");
            await ShowErrorAsync(errMsg);
        }
        finally
        {
            IsGenerating = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SaveImageAsync()
    {
        if (CurrentRecord is null || CurrentImageBitmap is null)
            return;

        try
        {
            var topLevel = GetTopLevel();
            if (topLevel is null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Image",
                DefaultExtension = "png",
                SuggestedFileName = _imageIOService.GenerateDefaultFileName(CurrentRecord.Record.Timestamp),
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } }
                }
            });

            if (file is null) return;

            var filePath = file.Path.LocalPath;
            await _imageIOService.SaveImageAsync(
                filePath, CurrentRecord.Record.ImageData, CurrentPrompt,
                CurrentRecord.Record.Model,
                CurrentRecord.Record.Parameters.Count > 0 ? CurrentRecord.Record.Parameters : null,
                CurrentRecord.Record.Cost);

            CurrentRecord.Record.IsSaved = true;
            IsDirty = History.Any(r => !r.Record.IsSaved);
            UpdateTitle();
        }
        catch (Exception ex)
        {
            var errMsg = ex is ApiException apiEx ? apiEx.ToDisplayString() : $"Save failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[SaveImageAsync] {errMsg}");
            await ShowErrorAsync(errMsg);
        }
    }

    #region History context-menu actions

    /// <summary>
    /// Saves one history record to a file, using the record's own timestamp
    /// to generate the default filename.
    /// </summary>
    public async Task SaveRecordAsync(GenerationRecordViewModel recordVm)
    {
        var topLevel = GetTopLevel();
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Image",
            DefaultExtension = "png",
            SuggestedFileName = _imageIOService.GenerateDefaultFileName(recordVm.Record.Timestamp),
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } }
            }
        });

        if (file is null) return;

        var filePath = file.Path.LocalPath;
        await _imageIOService.SaveImageAsync(
            filePath, recordVm.Record.ImageData, recordVm.Record.Prompt,
            recordVm.Record.Model,
            recordVm.Record.Parameters.Count > 0 ? recordVm.Record.Parameters : null,
            recordVm.Record.Cost);

        recordVm.Record.IsSaved = true;
        IsDirty = History.Any(r => !r.Record.IsSaved);
        UpdateTitle();
    }

    /// <summary>
    /// Saves all history records to a chosen folder. Prompts the user for an
    /// output folder and an optional filename prefix. Each file is named
    /// "prefix-Generated-YYYYMMDD-HHmmss.png".
    /// </summary>
    public async Task SaveAllRecordsAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel is null || History.Count == 0) return;

        // Ask for the filename prefix
        var dialog = new Views.InputDialog(
            "Save All Images",
            "Enter a filename prefix:",
            "images");
        if (topLevel is Window window)
            await dialog.ShowDialog(window);
        else
            return;
        if (dialog.Result is null) return; // cancelled

        var prefix = string.IsNullOrWhiteSpace(dialog.Result) ? "images" : dialog.Result.Trim();

        // Ask for the output folder
        var folder = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select output folder for saved images",
            AllowMultiple = false,
        });

        if (folder.Count == 0) return;
        var folderPath = folder[0].Path.LocalPath;

        foreach (var recordVm in History)
        {
            var timestamp = recordVm.Record.Timestamp;
            var fileName = $"{prefix}-Generated-{timestamp:yyyyMMdd-HHmmss}.png";
            var filePath = System.IO.Path.Combine(folderPath, fileName);

            await _imageIOService.SaveImageAsync(
                filePath, recordVm.Record.ImageData, recordVm.Record.Prompt,
                recordVm.Record.Model,
                recordVm.Record.Parameters.Count > 0 ? recordVm.Record.Parameters : null,
                recordVm.Record.Cost);

            recordVm.Record.IsSaved = true;
        }

        IsDirty = History.Any(r => !r.Record.IsSaved);
        UpdateTitle();
    }

    /// <summary>
    /// Removes a history record from the list (discards it permanently).
    /// </summary>
    public void RemoveRecord(GenerationRecordViewModel recordVm)
    {
        History.Remove(recordVm);
        if (History.Count > 0)
        {
            // If the removed record was the current one, restore the last.
            if (CurrentRecord == recordVm)
            {
                var last = History[^1];
                RestoreRecord(last);
            }
        }
        else
        {
            CurrentRecord = null;
            CurrentPrompt = string.Empty;
            CurrentImageBitmap = null;
        }
    }

    /// <summary>
    /// Copies the generation prompt of a history record to the system clipboard.
    /// </summary>
    public async Task CopyPromptAsync(GenerationRecordViewModel recordVm)
    {
        var topLevel = GetTopLevel();
        if (topLevel is null) return;

        var clipboard = topLevel.Clipboard;
        if (clipboard is null) return;

        await clipboard.SetTextAsync(recordVm.Record.Prompt ?? string.Empty);
    }

    /// <summary>
    /// Copies the image of a history record to the system clipboard as a
    /// bitmap, via Avalonia's SetBitmapAsync clipboard extension. Windows and
    /// macOS support image clipboard natively; on Linux it depends on the
    /// clipboard backend.
    /// </summary>
    public async Task CopyImageAsync(GenerationRecordViewModel recordVm)
    {
        var topLevel = GetTopLevel();
        if (topLevel?.Clipboard is not { } clipboard) return;

        try
        {
            // A fresh Bitmap the clipboard can own — the platform clipboard
            // may hold a reference for delayed rendering after this method
            // returns, so it must NOT be disposed here.
            var bitmap = ImageUtils.BytesToBitmap(recordVm.Record.ImageData);

            await clipboard.SetBitmapAsync(bitmap);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CopyImageAsync] Failed: {ex.Message}");
            await ShowErrorAsync($"Failed to copy image to clipboard: {ex.Message}");
        }
    }

    #endregion

    [RelayCommand]
    private async Task EditModelParametersAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel is Window window)
        {
            var dialog = new ModelParametersDialog();
            dialog.Initialize(this);
            await dialog.ShowDialog<bool>(window);
        }
    }

    [RelayCommand]
    private async Task GeneratePromptAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel is Window window)
        {
            var dialog = new Views.PromptGeneratorDialog();
            if (_promptGenViewModel is not null)
            {
                dialog.Initialize(_promptGenViewModel);
            }
            else
            {
                _promptGenViewModel = new PromptGeneratorDialogViewModel(_preferences, _providerFactory);
                // Forward this window's prompt-building costs to the session total.
                _promptGenViewModel.CostReported += c => CostReported?.Invoke(c);
                dialog.Initialize(_promptGenViewModel);
            }
            var result = await dialog.ShowDialog<string?>(window);
            if (!string.IsNullOrEmpty(result))
            {
                CurrentPrompt = result;
            }
        }
    }

    [RelayCommand]
    private async Task GeneratePromptFromImageAsync()
    {
        if (CurrentRecord is null || CurrentImageBitmap is null)
            return;

        IsGenerating = true;
        try
        {
            var provider = _providerFactory.CreateProvider(_preferences.SelectedProviderId, _preferences);
            var apiKey = _preferences.SelectedProviderId == "openrouter"
                ? _preferences.OpenRouterApiKey
                : _preferences.CustomProviderApiKey;
            var visionModel = GetVisionModel();

            var userPrompt = "Describe this image in vivid detail as a prompt for an image generation AI. Focus on: subject, setting, lighting, colours, composition, style, mood.";

            var (description, visionCost) = await provider.VisionChatCompletionWithCostAsync(
                userPrompt, CurrentRecord.Record.ImageData, visionModel, apiKey, CancellationToken.None);

            CurrentPrompt = description;
            CostReported?.Invoke(visionCost);

            // Record the vision-analysis cost for the Image→Prompt tooltip.
            if (visionCost.HasValue)
                UpdateCostAverage(visionModel, visionCost.Value);
        }
        catch (Exception ex)
        {
            var errMsg = ex is ApiException apiEx ? apiEx.ToDisplayString() : $"Image analysis failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[GeneratePromptFromImageAsync] {errMsg}");
            await ShowErrorAsync(errMsg);
        }
        finally
        {
            IsGenerating = false;
        }
    }

    /// <summary>
    /// The model this window generates with: the per-window copy, falling back
    /// to the configured default when the window was created before the copy
    /// was introduced (or the copy was never set).
    /// </summary>
    private string GetGenerationModel()
    {
        if (!string.IsNullOrEmpty(GenerationModel))
            return GenerationModel;

        return _preferences.SelectedProviderId == "openrouter"
            ? _preferences.OpenRouterGenerationModel
            : _preferences.CustomGenerationModel;
    }

    /// <summary>
    /// Loads this model's parameters from the API and replaces this window's
    /// parameter collection: old descriptors are removed and the new model's
    /// descriptors added, all values unset. Used when the window overrides
    /// the generation model in the Model Parameters dialog.
    /// </summary>
    public async Task<bool> ReloadModelParametersAsync()
    {
        var model = GetGenerationModel();
        if (string.IsNullOrWhiteSpace(model))
            return false;

        var provider = _providerFactory.CreateProvider(_preferences.SelectedProviderId, _preferences);
        var apiKey = _preferences.OpenRouterApiKey;
        var descriptors = await provider.GetModelParameterDescriptorsAsync(model, apiKey, CancellationToken.None);

        var userFacing = descriptors.Where(Models.ModelParameters.IsUserFacing).ToList();

        ModelParameters.Clear();
        foreach (var d in userFacing)
            ModelParameters.Add(new ModelParameterValue(d));

        return true;
    }

    public string GetApiKey() =>
        _preferences.SelectedProviderId == "openrouter"
            ? _preferences.OpenRouterApiKey
            : _preferences.CustomProviderApiKey;

    /// <summary>
    /// Creates the provider this window generates with (OpenRouter).
    /// Exposed so the Model Parameters dialog can fetch a different model's
    /// parameters when the window overrides the generation model.
    /// </summary>
    public IImageGenerationProvider CreateProvider() =>
        _providerFactory.CreateProvider(_preferences.SelectedProviderId, _preferences);

    private string GetVisionModel()
    {
        return _preferences.SelectedProviderId == "openrouter"
            ? _preferences.OpenRouterVisionModel
            : _preferences.CustomVisionModel;
    }

    private async Task ShowErrorAsync(string message)
    {
        // Dispatch to UI thread if needed
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is not null)
            {
                var dialog = new ErrorDialog(message);
                await dialog.ShowDialog(desktop.MainWindow);
            }
        }
    }

    private TopLevel? GetTopLevel()
    {
        return Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
    }

    /// <summary>
    /// Called by DockFactory when the document is being removed.
    /// </summary>
    public void OnClosing()
    {
        // Future: any cleanup needed
    }
}

/// <summary>
/// Wrapper for a GenerationRecord displayed in the history panel.
/// </summary>
public partial class GenerationRecordViewModel : ObservableObject
{
    public GenerationRecord Record { get; }

    public Bitmap? Thumbnail { get; }

    [ObservableProperty]
    private bool _isSelected;

    public GenerationRecordViewModel(GenerationRecord record)
    {
        Record = record;
        Thumbnail = ImageUtils.CreateThumbnail(record.ImageData, 160);
    }

    /// <summary>
    /// Tooltip for the history thumbnail: when it was made, what it cost,
    /// and which model generated it.
    /// </summary>
    public string TooltipText
    {
        get
        {
            var lines = new List<string>
            {
                Record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
            };

            // Dimensions
            lines.Add($"{Record.Width}×{Record.Height}");

            if (Record.Cost is { } cost)
                lines.Add($"Cost: ${cost:0.0000}");

            if (!string.IsNullOrEmpty(Record.Model))
                lines.Add($"Model: {Record.Model}");

            var prompt = Record.Prompt;
            if (!string.IsNullOrEmpty(prompt))
                lines.Add("Prompt: " + (prompt.Length > 200 ? prompt[..200] + "…" : prompt));

            return string.Join("\n", lines);
        }
    }
}