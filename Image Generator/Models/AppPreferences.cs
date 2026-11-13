using System.Collections.Generic;

namespace Image_Generator.Models;

public class AppPreferences
{
    // Provider selection
    public string SelectedProviderId { get; set; } = "openrouter";

    // OpenRouter settings
    public string OpenRouterApiKey { get; set; } = string.Empty;
    public string OpenRouterGenerationModel { get; set; } = "openrouter/auto";
    public string OpenRouterVisionModel { get; set; } = "openai/gpt-4o";
    public string OpenRouterPromptGenModel { get; set; } = "openai/gpt-4o-mini";

    /// <summary>
    /// Parameter schema for the OpenRouter image generation model, fetched from
    /// GET /images/models when the model was chosen in Preferences. Descriptors
    /// only (kinds, enums, ranges) — each image window holds its own value copy.
    /// </summary>
    public List<ModelParameterDescriptor> OpenRouterGenerationModelParameters { get; set; } = new();

    /// <summary>
    /// The model id the stored parameter schema belongs to. When the generation
    /// model differs from this, the schema is stale and gets re-fetched.
    /// </summary>
    public string OpenRouterGenerationModelParametersFor { get; set; } = string.Empty;

    // Custom provider settings
    public string CustomProviderName { get; set; } = "Custom";
    public string CustomProviderBaseUrl { get; set; } = "http://localhost:11434/v1";
    public string CustomProviderApiKey { get; set; } = string.Empty;
    public string CustomGenerationModel { get; set; } = "";
    public string CustomVisionModel { get; set; } = "";

    // UI theme: "Dark", "Light", or "System" (default)
    public string Theme { get; set; } = "System";

    /// <summary>
    /// Running average cost per generation, keyed by
    /// "model|param1=value1|param2=value2…" (params sorted alphabetically).
    /// Updated after each successful generation; used for tooltip estimates.
    /// </summary>
    public Dictionary<string, double> ModelCostAverages { get; set; } = new();
}

public class PromptGenerationOptions
{
    // Null means the aspect was set to <skip> in the dialog and is left
    // out of the generation request.
    public string? Humour { get; set; } = "Balanced";
    public string? Style { get; set; } = "Photorealistic";
    public string? Topic { get; set; } = "Landscape";
    public string? Colour { get; set; } = "Vivid";
    public string? TimeOfDay { get; set; } = "Midday";
    public string? Mood { get; set; } = "Peaceful";
}