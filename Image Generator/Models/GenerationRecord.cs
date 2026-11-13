using System;

namespace Image_Generator.Models;

public class GenerationRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public byte[] ImageData { get; set; } = Array.Empty<byte>();
    public string Prompt { get; set; } = string.Empty;
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public bool IsSaved { get; set; }

    /// <summary>The model that generated this image (empty when unknown).</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// What this generation cost in USD, as reported by the API's usage.cost.
    /// Null when the API didn't report a cost (BYOK, unavailable, legacy files).
    /// </summary>
    public double? Cost { get; set; }

    /// <summary>
    /// The generation parameters used for this image, keyed by parameter name.
    /// Values are request-native (string enums, numbers as strings — parsed
    /// back through the parameter descriptors on restore).
    /// </summary>
    public System.Collections.Generic.Dictionary<string, string?> Parameters { get; set; } = new();
}