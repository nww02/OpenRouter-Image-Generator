using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Image_Generator.Models;

/// <summary>
/// The kind of parameter descriptor reported by the provider's model API.
/// </summary>
public enum ModelParameterKind
{
    /// <summary>One of a fixed set of string values.</summary>
    Enum,
    /// <summary>Any number within an inclusive range.</summary>
    Range,
    /// <summary>A supported-or-not flag (present means the parameter is accepted).</summary>
    Boolean
}

/// <summary>
/// Schema for one model parameter, as reported by the provider API
/// (OpenRouter GET /images/models → supported_parameters). Pure data:
/// this is what gets fetched on model selection and stored in preferences.
/// </summary>
public class ModelParameterDescriptor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ModelParameterKind Kind { get; set; }

    [JsonPropertyName("values")]
    public List<string> Values { get; set; } = new();

    [JsonPropertyName("min")]
    public double? Min { get; set; }

    [JsonPropertyName("max")]
    public double? Max { get; set; }
}

/// <summary>
/// A per-image-window copy of one parameter: the descriptor plus the user's
/// chosen value. Value == null means "use the model's default" — the
/// parameter is omitted from the generation request entirely.
/// </summary>
public class ModelParameterValue : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public ModelParameterDescriptor Descriptor { get; }

    private string? _value;
    public string? Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public ModelParameterValue(ModelParameterDescriptor descriptor, string? value = null)
    {
        Descriptor = descriptor;
        _value = value;
    }

    public ModelParameterValue Clone() => new(Descriptor, Value);
}

/// <summary>
/// Helpers for turning user-chosen parameter values into JSON request values
/// and for deciding which parameters the UI should expose.
/// </summary>
public static class ModelParameters
{
    /// <summary>
    /// Parameters that need structured payloads (image references, provider
    /// routing, streaming, tracing) or are request plumbing — not scalar
    /// values a user would set in this UI. "size" is excluded because the
    /// main window's Width/Height fields already derive it.
    /// </summary>
    private static readonly HashSet<string> ExcludedFromUi = new()
    {
        "input_references", "markup", "provider", "stream", "trace",
        "session_id", "user", "response_format", "size"
    };

    public static bool IsUserFacing(ModelParameterDescriptor d) =>
        !ExcludedFromUi.Contains(d.Name);

    /// <summary>
    /// Parameters that shape the output size and are therefore mandatory:
    /// their checkbox starts checked and cannot be turned off (the control
    /// is always enabled, though the value may still be "(default)").
    /// </summary>
    public static bool IsMandatory(string name) =>
        name is "resolution" or "aspect_ratio" or "width" or "height";

    /// <summary>
    /// Converts a user-entered string into the JSON value the /images
    /// endpoint expects for the given parameter. Integer-valued parameters
    /// are parsed as numbers so the request JSON is well-typed.
    /// Returns null when the value cannot be converted (the caller should
    /// have validated first).
    /// </summary>
    public static object? ToRequestValue(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        if (IsIntegerParameter(name))
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                ? i
                : null;
        }

        return value;
    }

    /// <summary>
    /// Whether the parameter takes an integer value in the request
    /// (vs a string enum/tier value). Seed is exposed by OpenRouter as a
    /// boolean capability but its value is an integer.
    /// </summary>
    public static bool IsIntegerParameter(string name) =>
        name is "n" or "output_compression" or "seed" or "frame_count" or "frame_duration_ms";
}
