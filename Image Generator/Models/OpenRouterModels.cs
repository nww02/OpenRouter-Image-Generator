using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Image_Generator.Models;

public class OpenRouterModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("pricing")]
    public OpenRouterPricing? Pricing { get; set; }

    [JsonPropertyName("architecture")]
    public OpenRouterArchitecture? Architecture { get; set; }
}

public class OpenRouterPricing
{
    [JsonPropertyName("prompt")]
    public decimal? Prompt { get; set; }

    [JsonPropertyName("completion")]
    public decimal? Completion { get; set; }

    [JsonPropertyName("image")]
    public decimal? Image { get; set; }
}

public class OpenRouterArchitecture
{
    [JsonPropertyName("modality")]
    public string? Modality { get; set; }

    [JsonPropertyName("input_modalities")]
    public List<string>? InputModalities { get; set; }

    [JsonPropertyName("output_modalities")]
    public List<string>? OutputModalities { get; set; }
}

public class OpenRouterModelsResponse
{
    [JsonPropertyName("data")]
    public List<OpenRouterModel>? Data { get; set; }
}

public class OpenRouterImageResponse
{
    [JsonPropertyName("data")]
    public List<OpenRouterImageData>? Data { get; set; }

    [JsonPropertyName("usage")]
    public OpenRouterImageUsage? Usage { get; set; }

    [JsonPropertyName("error")]
    public OpenRouterApiError? Error { get; set; }
}

/// <summary>
/// usage block of the /images response: token counts plus cost in USD.
/// Cost is null when the API didn't report one.
/// </summary>
public class OpenRouterImageUsage
{
    [JsonPropertyName("cost")]
    public double? Cost { get; set; }

    [JsonPropertyName("prompt_tokens")]
    public int? PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; set; }
}

public class OpenRouterImageData
{
    [JsonPropertyName("b64_json")]
    public string? B64Json { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public class OpenRouterChatResponse
{
    [JsonPropertyName("choices")]
    public List<OpenRouterChatChoice>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public OpenRouterImageUsage? Usage { get; set; }

    [JsonPropertyName("error")]
    public OpenRouterApiError? Error { get; set; }
}

public class OpenRouterChatChoice
{
    [JsonPropertyName("message")]
    public OpenRouterChatMessage? Message { get; set; }
}

public class OpenRouterChatMessage
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("images")]
    public List<OpenRouterChatImage>? Images { get; set; }
}

public class OpenRouterChatImage
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("image_url")]
    public OpenRouterChatImageUrl? ImageUrl { get; set; }
}

public class OpenRouterChatImageUrl
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>
/// Response from GET /images/models — every image-capable model with its
/// supported-parameter descriptors.
/// </summary>
public class OpenRouterImageModelsResponse
{
    [JsonPropertyName("data")]
    public List<OpenRouterImageModelEntry>? Data { get; set; }
}

public class OpenRouterImageModelEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("architecture")]
    public OpenRouterArchitecture? Architecture { get; set; }

    [JsonPropertyName("supported_parameters")]
    public Dictionary<string, OpenRouterCapability>? SupportedParameters { get; set; }
}

/// <summary>
/// One parameter capability descriptor: {"type":"enum","values":[...]},
/// {"type":"range","min":n,"max":m} or {"type":"boolean"}.
/// </summary>
public class OpenRouterCapability
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("values")]
    public List<string>? Values { get; set; }

    [JsonPropertyName("min")]
    public double? Min { get; set; }

    [JsonPropertyName("max")]
    public double? Max { get; set; }
}

/// <summary>
/// Response from GET /credits — purchased vs used account credit.
/// </summary>
public class OpenRouterCreditsResponse
{
    [JsonPropertyName("data")]
    public OpenRouterCreditsData? Data { get; set; }
}

public class OpenRouterCreditsData
{
    [JsonPropertyName("total_credits")]
    public double TotalCredits { get; set; }

    [JsonPropertyName("total_usage")]
    public double TotalUsage { get; set; }
}

public class OpenRouterApiError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("code")]
    public int? Code { get; set; }
}