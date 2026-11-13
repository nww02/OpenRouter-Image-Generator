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

    /// <summary>
    /// Sortable price for the model list. Image models sort by their dominant
    /// fee (per-image, then per-request); chat/vision models sort by their
    /// per-million-token output price, falling back to input tokens. Free
    /// models sort as 0 (cheapest); models with unknown pricing sort last.
    /// </summary>
    public double SortPrice
    {
        get
        {
            if (Pricing is null) return double.MaxValue;

            if (Capabilities.SupportsImageGeneration)
            {
                if (Pricing.PerImage is { } img && img > 0) return (double)img;
                if (Pricing.PerRequest is { } r && r > 0) return (double)r;
                if (Pricing.PerOutputToken is { } o && o > 0) return (double)o * 1_000_000;
                if (Pricing.PerInputToken is { } i && i > 0) return (double)i * 1_000_000;
            }
            else
            {
                if (Pricing.PerOutputToken is { } o && o > 0) return (double)o * 1_000_000;
                if (Pricing.PerInputToken is { } i && i > 0) return (double)i * 1_000_000;
                if (Pricing.PerImage is { } img && img > 0) return (double)img;
                if (Pricing.PerRequest is { } r && r > 0) return (double)r;
            }

            return 0; // free (all prices zero)
        }
    }
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

    /// <summary>Price per input (prompt) token, USD.</summary>
    public decimal? PerInputToken { get; set; }

    /// <summary>Price per output (completion) token, USD.</summary>
    public decimal? PerOutputToken { get; set; }

    /// <summary>Flat per-request fee charged by some providers/models.</summary>
    public decimal? PerRequest { get; set; }

    public string? Currency { get; set; } = "USD";
}

/// <summary>
/// Formats model pricing into the compact text shown in model lists.
/// Token prices are displayed per million tokens (MTok), with input and
/// output broken out where the provider reports both.
/// </summary>
public static class ModelPricingFormatter
{
    public static string Format(ModelPricing? pricing)
    {
        if (pricing is null) return "Free / Unknown";

        var parts = new List<string>();

        if (pricing.PerImage is { } img && img > 0)
            parts.Add($"{Fmt(img)}/img");
        if (pricing.PerMegapixel is { } mp && mp > 0)
            parts.Add($"{Fmt(mp)}/MP");

        // Token prices: per million tokens, input and output broken out.
        if (pricing.PerInputToken is { } inTok && inTok > 0 &&
            pricing.PerOutputToken is { } outTok && outTok > 0)
        {
            parts.Add($"{Fmt(inTok * 1_000_000)} in / {Fmt(outTok * 1_000_000)} out per MTok");
        }
        else if (pricing.PerInputToken is { } i && i > 0)
        {
            parts.Add($"{Fmt(i * 1_000_000)} per MTok");
        }
        else if (pricing.PerOutputToken is { } o && o > 0)
        {
            parts.Add($"{Fmt(o * 1_000_000)} per MTok (out)");
        }

        if (pricing.PerRequest is { } req && req > 0)
            parts.Add($"{Fmt(req)}/req");

        return parts.Count > 0 ? string.Join(" + ", parts) : "Free / Unknown";
    }

    /// <summary>
    /// Formats a USD amount: up to 4 decimals, trailing zeros trimmed
    /// ($1.50, $0.075, $3).
    /// </summary>
    private static string Fmt(decimal value) =>
        "$" + value.ToString("F4").TrimEnd('0').TrimEnd('.');
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