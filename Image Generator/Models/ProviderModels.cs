using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Image_Generator.Models;

public interface IImageGenerationProvider
{
    string Name { get; }
    string BaseUrl { get; }
    bool RequiresApiKey { get; }

    Task<List<ProviderModelInfo>> ListModelsAsync(string? apiKey = null, CancellationToken ct = default);
    Task<ImageGenerationResult> GenerateImageAsync(string prompt, int width, int height, string model, string? apiKey, CancellationToken ct, IReadOnlyDictionary<string, object?>? parameters = null);
    Task<string> ChatCompletionAsync(List<ChatMessage> messages, string model, string? apiKey, CancellationToken ct);
    Task<string> VisionChatCompletionAsync(string prompt, byte[] imageBytes, string model, string? apiKey, CancellationToken ct);

    /// <summary>
    /// Fetches the image-generation parameter schema for the given model:
    /// each parameter's kind (enum/range/boolean) and, where applicable,
    /// its allowed values or numeric bounds. Returns an empty list when the
    /// provider does not expose parameter metadata (custom/local providers).
    /// </summary>
    Task<List<ModelParameterDescriptor>> GetModelParameterDescriptorsAsync(string model, string? apiKey, CancellationToken ct);

    /// <summary>
    /// Fetches the account's remaining credit balance in USD, or null when the
    /// provider doesn't expose a balance API (custom/local providers).
    /// </summary>
    Task<double?> GetCreditBalanceAsync(string? apiKey, CancellationToken ct);

    /// <summary>
    /// Runs a chat completion and returns the assistant's reply text plus the
    /// usage cost in USD (null when the API didn't report one). Used by the
    /// prompt generator and image-to-prompt features so their (small) costs
    /// can be added to the session total.
    /// </summary>
    Task<(string reply, double? cost)> ChatCompletionWithCostAsync(List<ChatMessage> messages, string model, string? apiKey, CancellationToken ct);

    /// <summary>
    /// Runs a vision (image-in, text-out) chat completion and returns the
    /// reply plus the usage cost in USD (null when unreported). Used by the
    /// image-to-prompt feature so its costs join the session total.
    /// </summary>
    Task<(string reply, double? cost)> VisionChatCompletionWithCostAsync(string prompt, byte[] imageBytes, string model, string? apiKey, CancellationToken ct);
}

/// <summary>
/// Result of one image generation: the image bytes plus what the API
/// reported about the request — chiefly usage.cost in USD.
/// </summary>
public class ImageGenerationResult
{
    public byte[] ImageData { get; set; } = Array.Empty<byte>();

    /// <summary>Cost in USD as reported by the API; null when unreported.</summary>
    public double? Cost { get; set; }
}

public class ProviderModelInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ModelCapabilities Capabilities { get; set; } = new();
    public ModelPricing? Pricing { get; set; }

    /// <summary>
    /// Formatted pricing string for display in the model list.
    /// Set externally after adding to the list.
    /// </summary>
    public string PricingText { get; set; } = "Free / Unknown";
}

public class ModelCapabilities
{
    public bool SupportsImageGeneration { get; set; }
    public bool SupportsVision { get; set; }
    public bool SupportsChat { get; set; }
    public List<string> SupportedResolutions { get; set; } = new();
    public List<string> SupportedAspectRatios { get; set; } = new();
}

public class ModelPricing
{
    public decimal? PerImage { get; set; }
    public decimal? PerMegapixel { get; set; }
    public decimal? PerToken { get; set; }

    /// <summary>Flat per-request fee charged by some providers/models.</summary>
    public decimal? PerRequest { get; set; }

    public string? Currency { get; set; } = "USD";
}

public class ChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public List<ChatImageContent>? Images { get; set; }
}

public class ChatImageContent
{
    public string Type { get; set; } = "image_url";
    public string ImageUrl { get; set; } = string.Empty;
}

public class ProviderDescriptor
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool RequiresCustomUrl { get; set; }
    public string DefaultBaseUrl { get; set; } = string.Empty;
}

public interface IProviderFactory
{
    IImageGenerationProvider CreateProvider(string providerId, AppPreferences prefs);
    List<ProviderDescriptor> GetAvailableProviders();
}