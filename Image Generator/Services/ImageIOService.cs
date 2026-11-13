using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Image_Generator.Models;

namespace Image_Generator.Services;

/// <summary>
/// Companion .prompt metadata saved alongside each image. JSON since the
/// parameter-metadata feature; plain-text .prompt files from older versions
/// still load (treated as prompt-only).
/// </summary>
public class PromptFileMetadata
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public Dictionary<string, string?>? Parameters { get; set; }

    /// <summary>What the generation cost in USD, when the API reported it.</summary>
    [JsonPropertyName("cost")]
    public double? Cost { get; set; }
}

public class ImageIOService : IImageIOService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task SaveImageAsync(string filePath, byte[] imageData, string prompt,
        string? model = null, Dictionary<string, string?>? parameters = null, double? cost = null)
    {
        // Save the PNG image
        await File.WriteAllBytesAsync(filePath, imageData);

        // Save the companion .prompt file as JSON metadata: prompt, model,
        // and the parameters used to generate the image.
        var metadata = new PromptFileMetadata
        {
            Prompt = prompt,
            Model = model ?? string.Empty,
            Parameters = parameters,
            Cost = cost
        };
        var promptPath = filePath + ".prompt";
        await File.WriteAllTextAsync(promptPath, JsonSerializer.Serialize(metadata, JsonOptions));
    }

    public async Task<(byte[] imageData, string? prompt)> LoadImageAsync(string filePath)
    {
        var (imageData, metadata) = await LoadImageWithMetadataAsync(filePath);
        return (imageData, metadata?.Prompt);
    }

    /// <summary>
    /// Loads an image plus its full .prompt metadata (prompt, model,
    /// parameters). Returns null metadata when no .prompt file exists;
    /// a legacy plain-text .prompt yields metadata with only Prompt set.
    /// </summary>
    public async Task<(byte[] imageData, PromptFileMetadata? metadata)> LoadImageWithMetadataAsync(string filePath)
    {
        var imageData = await File.ReadAllBytesAsync(filePath);

        var promptPath = filePath + ".prompt";
        if (!File.Exists(promptPath))
        {
            return (imageData, null);
        }

        var raw = await File.ReadAllTextAsync(promptPath);
        return (imageData, ParsePromptFile(raw));
    }

    /// <summary>
    /// Parses .prompt content: JSON metadata when possible, falling back to
    /// treating the whole file as the prompt text (legacy behaviour).
    /// </summary>
    public static PromptFileMetadata? ParsePromptFile(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new PromptFileMetadata();

        // Try JSON first (current format).
        if (raw.TrimStart().StartsWith("{"))
        {
            try
            {
                var meta = JsonSerializer.Deserialize<PromptFileMetadata>(raw);
                if (meta is not null)
                    return meta;
            }
            catch (JsonException)
            {
                // Fall through to legacy handling.
            }
        }

        // Legacy plain-text prompt file.
        return new PromptFileMetadata { Prompt = raw, Model = string.Empty };
    }

    public string GenerateDefaultFileName()
    {
        return GenerateDefaultFileName(DateTime.Now);
    }

    public string GenerateDefaultFileName(DateTime timestamp)
    {
        return $"Generated-{timestamp:yyyyMMdd-HHmmss}.png";
    }
}
