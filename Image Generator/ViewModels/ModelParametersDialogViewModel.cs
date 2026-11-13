using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Image_Generator.Models;
using Image_Generator.ViewModels.Documents;

namespace Image_Generator.ViewModels;

/// <summary>
/// Editable row in the Model Parameters dialog. Wraps one ModelParameterValue
/// with UI-friendly bindings: text for ranges, selection for enums, and
/// validation state for ranges (value must parse as a number within bounds).
/// </summary>
public partial class ModelParameterRowViewModel : ObservableObject
{
    public ModelParameterValue Source { get; }

    public ModelParameterDescriptor Descriptor => Source.Descriptor;

    // "(default)" sentinel: shown in every enum dropdown as the first entry.
    public const string DefaultSentinel = "(default)";

    /// <summary>Options for enum parameters: "(default)" + the allowed values.</summary>
    public ObservableCollection<string> EnumOptions { get; } = new() { DefaultSentinel };

    private string _textValue = string.Empty;
    private string _validationError = string.Empty;
    private bool _isSelected;

    public ModelParameterRowViewModel(ModelParameterValue source)
    {
        Source = source;

        if (Descriptor.Kind == ModelParameterKind.Enum)
        {
            foreach (var v in Descriptor.Values)
                EnumOptions.Add(v);
        }

        _textValue = source.Value ?? string.Empty;

        // Mandatory parameters (resolution, aspect_ratio, …) start selected and
        // their checkbox is disabled; everything else starts unchecked.
        _isSelected = Models.ModelParameters.IsMandatory(source.Descriptor.Name);
    }

    /// <summary>
    /// Whether this parameter is included in the generation payload.
    /// Unchecking it disables its editor control and removes the value.
    /// Mandatory parameters are permanently checked.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (IsMandatory)
                return; // can't opt out of a size-shaping parameter
            if (SetProperty(ref _isSelected, value))
                OnPropertyChanged(nameof(IsControlEnabled));
        }
    }

    /// <summary>True when the editor control should accept input.</summary>
    public bool IsControlEnabled => IsSelected;

    /// <summary>True when the checkbox itself is clickable (mandatory → locked on).</summary>
    public bool IsCheckboxEnabled => !IsMandatory;

    /// <summary>Mandatory size-shaping parameters can't be opted out.</summary>
    public bool IsMandatory => Models.ModelParameters.IsMandatory(Descriptor.Name);

    /// <summary>
    /// Selected enum value ("(default)" when unset). Never null.
    /// </summary>
    public string? SelectedEnum
    {
        get => Source.Value ?? DefaultSentinel;
        set
        {
            if (value == DefaultSentinel || string.IsNullOrEmpty(value))
                Source.Value = null;
            else
                Source.Value = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Text shown for range/boolean parameters; empty means "use default".
    /// </summary>
    public string TextValue
    {
        get => _textValue;
        set
        {
            if (SetProperty(ref _textValue, value))
                Validate();
        }
    }

    /// <summary>Non-empty when the current text is outside the allowed range or not numeric.</summary>
    public string ValidationError
    {
        get => _validationError;
        private set => SetProperty(ref _validationError, value);
    }

    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);

    public bool IsRange => Descriptor.Kind == ModelParameterKind.Range;
    public bool IsEnum => Descriptor.Kind == ModelParameterKind.Enum;
    public bool IsBoolean => Descriptor.Kind == ModelParameterKind.Boolean;

    /// <summary>
    /// Watermark for the value textbox, e.g. "0 – 100 (empty = default)".
    /// </summary>
    public string RangeHint
    {
        get
        {
            var kind = Descriptor.Kind switch
            {
                ModelParameterKind.Boolean => "e.g. 12345",
                _ => "number"
            };
            if (Descriptor.Min.HasValue || Descriptor.Max.HasValue)
            {
                var min = Descriptor.Min?.ToString("0.##", CultureInfo.InvariantCulture) ?? "…";
                var max = Descriptor.Max?.ToString("0.##", CultureInfo.InvariantCulture) ?? "…";
                return $"{min} – {max} (empty = default)";
            }
            return $"{kind} (empty = default)";
        }
    }

    /// <summary>
    /// Parses and validates the text for a range/boolean parameter. Returns the
    /// numeric value, or null when the field is empty ("use default"), deselected
    /// or invalid (in which case ValidationError describes the problem).
    /// Enum parameters commit through their dropdown and are always valid.
    /// </summary>
    public double? Validate()
    {
        ValidationError = string.Empty;
        OnPropertyChanged(nameof(HasValidationError));

        if (IsEnum || !IsSelected)
            return null; // enums pick from allowed values; deselected rows send nothing

        if (string.IsNullOrWhiteSpace(TextValue))
            return null; // "use the model default"

        if (!double.TryParse(TextValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            ValidationError = "Enter a number";
            OnPropertyChanged(nameof(HasValidationError));
            return null;
        }

        if (Descriptor.Min.HasValue && v < Descriptor.Min.Value)
        {
            ValidationError = $"Minimum {Descriptor.Min.Value.ToString("0.##", CultureInfo.InvariantCulture)}";
            OnPropertyChanged(nameof(HasValidationError));
        }
        else if (Descriptor.Max.HasValue && v > Descriptor.Max.Value)
        {
            ValidationError = $"Maximum {Descriptor.Max.Value.ToString("0.##", CultureInfo.InvariantCulture)}";
            OnPropertyChanged(nameof(HasValidationError));
        }

        return v;
    }

    /// <summary>
    /// Commits the edited text back into the source value (called on OK).
    /// Deselected parameters commit to null (omitted from the request).
    /// For selected range parameters the validated numeric value is stored;
    /// empty text also stores null (use the model default).
    /// </summary>
    public void Commit()
    {
        if (!IsSelected)
        {
            Source.Value = null;
            return;
        }

        if (IsRange || IsBoolean)
        {
            var v = Validate();
            Source.Value = v is null
                ? null
                : FormatNumber(v.Value);
        }
        // Enum values commit directly through SelectedEnum.
    }

    private static string FormatNumber(double v) =>
        v == Math.Floor(v) && !double.IsInfinity(v)
            ? ((long)v).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>
/// ViewModel for the Model Parameters dialog. Works on a deep copy of the
/// window's parameter values; OK copies validated edits back, Cancel
/// discards them. The window's generation model is shown and can be
/// overridden per-window — changing it clears the rows and re-reads the
/// new model's parameters from the API.
/// </summary>
public partial class ModelParametersDialogViewModel : ViewModelBase
{
    private readonly ImageDocumentViewModel _owner;

    public ObservableCollection<ModelParameterRowViewModel> Rows { get; } = new();

    public bool HasRows => Rows.Count > 0;

    [ObservableProperty]
    private string _generationModel = string.Empty;

    [ObservableProperty]
    private bool _isLoadingParameters;

    /// <summary>True when the dialog's model differs from the window's — needs OK to apply.</summary>
    public bool ModelChanged => GenerationModel != _owner.GenerationModel;

    public ModelParametersDialogViewModel(ImageDocumentViewModel owner)
    {
        _owner = owner;
        _generationModel = owner.GenerationModel;

        foreach (var p in owner.ModelParameters)
            Rows.Add(new ModelParameterRowViewModel(new ModelParameterValue(p.Descriptor, p.Value)));
    }

    partial void OnGenerationModelChanged(string value)
    {
        OnPropertyChanged(nameof(ModelChanged));
    }

    /// <summary>
    /// Re-reads the given model's parameters from the API and rebuilds the
    /// rows: old rows are removed, the new model's descriptors are added with
    /// all values unset. Call after the model is changed (via the ellipsis
    /// browse). Returns false when the fetch fails (rows are left untouched).
    /// </summary>
    public async Task<bool> LoadParametersForModelAsync(string model)
    {
        if (IsLoadingParameters)
            return false;

        IsLoadingParameters = true;
        try
        {
            var provider = _owner.CreateProvider();
            var descriptors = await provider.GetModelParameterDescriptorsAsync(
                model, _owner.GetApiKey(), System.Threading.CancellationToken.None);
            var userFacing = descriptors.Where(Image_Generator.Models.ModelParameters.IsUserFacing).ToList();

            Rows.Clear();
            foreach (var d in userFacing)
                Rows.Add(new ModelParameterRowViewModel(new ModelParameterValue(d)));
            OnPropertyChanged(nameof(HasRows));
            return true;
        }
        finally
        {
            IsLoadingParameters = false;
        }
    }

    /// <summary>
    /// True when every range text field parses and sits within bounds.
    /// </summary>
    public bool AllValid => Rows.All(r => !r.HasValidationError);

    /// <summary>
    /// Validates all rows (refreshing their error states) and, if valid,
    /// copies the edited values back into the source collection.
    /// </summary>
    public bool TryCommit()
    {
        foreach (var row in Rows)
            row.Validate();
        OnPropertyChanged(nameof(AllValid));

        if (!AllValid)
            return false;

        foreach (var row in Rows)
            row.Commit();
        return true;
    }
}
