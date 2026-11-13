using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Image_Generator.Models;

namespace Image_Generator.Services;

public class OpenRouterProvider : IImageGenerationProvider
{
    private readonly HttpClient _httpClient;
    private const string BaseUrlValue = "https://openrouter.ai/api/v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    public OpenRouterProvider()
    {
        _httpClient = new HttpClient();
        // Image generation can take several minutes on slower models;
        // the HttpClient default of 100s cuts long generations off mid-flight.
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
        _httpClient.DefaultRequestHeaders.Add("HTTP-Referer", "https://localhost/");
        _httpClient.DefaultRequestHeaders.Add("X-Title", "DBM: OpenRouter Image Generator");
    }

    public string Name => "OpenRouter";
    public string BaseUrl => BaseUrlValue;
    public bool RequiresApiKey => true;

    public async Task<List<ProviderModelInfo>> ListModelsAsync(string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            // Fetch BOTH catalogues in parallel:
            //  - /images/models is the authoritative image-generation catalogue
            //    (66+ models incl. seedream, flux, qwen, ... that never appear
            //    in /models) and carries each model's supported_parameters.
            //  - /models is the full chat catalogue and has pricing, which
            //    /images/models lacks; entries are merged by id.
            var imagesTask = GetAsync($"{BaseUrlValue}/images/models", apiKey, ct);
            var modelsTask = GetAsync($"{BaseUrlValue}/models", apiKey, ct);
            var imagesRaw = await imagesTask;
            var modelsRaw = await modelsTask;

            // Pricing lookup from the full catalogue (may miss newer image models).
            var pricingById = new Dictionary<string, OpenRouterPricing?>();
            var modelsResponse = JsonSerializer.Deserialize<OpenRouterModelsResponse>(modelsRaw, JsonOptions);
            foreach (var m in modelsResponse?.Data ?? Enumerable.Empty<OpenRouterModel>())
                pricingById[m.Id] = m.Pricing;

            var results = new List<ProviderModelInfo>();

            // 1. Every model from the image catalogue — these all generate images.
            var imagesResponse = JsonSerializer.Deserialize<OpenRouterImageModelsResponse>(imagesRaw, JsonOptions);
            var seen = new HashSet<string>();
            foreach (var entry in imagesResponse?.Data ?? Enumerable.Empty<OpenRouterImageModelEntry>())
            {
                seen.Add(entry.Id);

                // Try pricing from the /models catalogue first (has image cost);
                // fall back to the image catalogue's own pricing field if present;
                // fall back to request-only pricing from /models.
                ModelPricing? pricing = null;
                if (pricingById.TryGetValue(entry.Id, out var p) && p is not null)
                {
                    pricing = new ModelPricing
                    {
                        PerImage = p.Image,
                        PerToken = p.Prompt,
                        PerRequest = p.Request,
                        Currency = "USD"
                    };
                }
                else if (entry.Pricing is not null)
                {
                    pricing = new ModelPricing
                    {
                        PerImage = entry.Pricing.Image,
                        PerToken = entry.Pricing.Prompt,
                        PerRequest = entry.Pricing.Request,
                        Currency = "USD"
                    };
                }

                results.Add(new ProviderModelInfo
                {
                    Id = entry.Id,
                    Name = entry.Name,
                    Capabilities = new ModelCapabilities
                    {
                        SupportsImageGeneration = true,
                        SupportsVision = entry.Architecture?.InputModalities?.Contains("image") == true,
                        SupportsChat = entry.Architecture?.OutputModalities?.Contains("text") == true
                    },
                    Pricing = pricing,
                    PricingText = FormatPricing(pricing)
                });
            }

            // 2. Remaining models from the full catalogue (chat/vision models,
            //    and image models not yet in the image listing).
            foreach (var model in modelsResponse?.Data ?? Enumerable.Empty<OpenRouterModel>())
            {
                if (seen.Contains(model.Id))
                    continue; // already added from the richer image catalogue

                var supportsImageGen = model.Architecture?.OutputModalities?.Contains("image") == true;
                var supportsVision = model.Architecture?.InputModalities?.Contains("image") == true;

                var pricing = model.Pricing != null ? new ModelPricing
                {
                    PerImage = model.Pricing.Image,
                    PerToken = model.Pricing.Prompt,
                    PerRequest = model.Pricing.Request,
                    Currency = "USD"
                } : null;

                results.Add(new ProviderModelInfo
                {
                    Id = model.Id,
                    Name = model.Name,
                    Capabilities = new ModelCapabilities
                    {
                        SupportsImageGeneration = supportsImageGen,
                        SupportsVision = supportsVision,
                        SupportsChat = model.Architecture?.OutputModalities?.Contains("text") == true
                    },
                    Pricing = pricing,
                    PricingText = FormatPricing(pricing)
                });
            }

            System.Diagnostics.Debug.WriteLine($"[OpenRouter] listed {results.Count} models ({seen.Count} from /images/models)");
            return results;
        }
        catch (ApiException) { throw; }
        catch (JsonException ex)
        {
            throw new ApiException(
                "Failed to parse OpenRouter models response as JSON",
                0, "", ex.Message);
        }
        catch (TaskCanceledException)
        {
            throw new ApiException(
                "Request to OpenRouter timed out",
                0, "", "The connection took too long to respond. Check your internet connection.");
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException(
                $"Network error connecting to OpenRouter: {ex.Message}",
                0, "", ex.ToString());
        }
    }

    /// <summary>GETs a URL with optional bearer auth and returns the raw body.</summary>
    private async Task<string> GetAsync(string url, string? apiKey, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        var response = await _httpClient.SendAsync(request, ct);
        var rawBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(
                $"OpenRouter {url} returned {(int)response.StatusCode} {response.ReasonPhrase}",
                (int)response.StatusCode, rawBody, TryExtractErrorDetail(rawBody));
        }
        return rawBody;
    }

    /// <summary>
    /// Formats model pricing for display.
    /// </summary>
    private static string FormatPricing(ModelPricing? pricing)
    {
        if (pricing is null) return "Free / Unknown";

        var parts = new List<string>();
        if (pricing.PerImage.HasValue && pricing.PerImage.Value > 0)
            parts.Add($"${pricing.PerImage.Value:F4}/img");
        if (pricing.PerMegapixel.HasValue && pricing.PerMegapixel.Value > 0)
            parts.Add($"${pricing.PerMegapixel.Value:F4}/MP");
        if (pricing.PerToken.HasValue && pricing.PerToken.Value > 0)
            parts.Add($"${pricing.PerToken.Value:F6}/tok");
        if (pricing.PerRequest.HasValue && pricing.PerRequest.Value > 0)
            parts.Add($"${pricing.PerRequest.Value:F4}/req");

        return parts.Count > 0 ? string.Join(" + ", parts) : "Free / Unknown";
    }

    public async Task<ImageGenerationResult> GenerateImageAsync(string prompt, int width, int height, string model, string? apiKey, CancellationToken ct, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ApiException(
                "OpenRouter API key is required — set it in Edit > Preferences",
                0, "", "The API key field is empty in Preferences.");

        try
        {
            // Build the request body as a dictionary so user-chosen model
            // parameters (resolution, aspect_ratio, quality, seed, ...) merge in
            // with their request-native types. Output dimensions come solely
            // from the parameters — the models the API exposes don't accept
            // width/height fields.
            var body = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["prompt"] = prompt,
                ["output_format"] = "png"
            };

            if (parameters is not null)
            {
                foreach (var (name, value) in parameters)
                {
                    if (value is null)
                        continue; // parameter not set — omit, use the API default

                    body[name] = value;
                }
            }

            var requestJson = JsonSerializer.Serialize(body);
            System.Diagnostics.Debug.WriteLine($"[OpenRouter] POST {BaseUrlValue}/images");
            System.Diagnostics.Debug.WriteLine($"[OpenRouter] Model: {model}");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrlValue}/images")
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await _httpClient.SendAsync(request, ct);

            var rawBody = await response.Content.ReadAsStringAsync(ct);

            // Try to parse as JSON to check for errors
            OpenRouterImageResponse? imageResponse = null;
            try
            {
                imageResponse = JsonSerializer.Deserialize<OpenRouterImageResponse>(rawBody, JsonOptions);
            }
            catch (JsonException ex)
            {
                // Response wasn't JSON at all
                var preview = rawBody.Length > 500 ? rawBody[..500] + "..." : rawBody;
                throw new ApiException(
                    $"OpenRouter returned non-JSON response (HTTP {(int)response.StatusCode})",
                    (int)response.StatusCode, rawBody,
                    $"Expected JSON but got:\n{preview}\n\nParse error: {ex.Message}");
            }

            // Check for OpenRouter API-level errors (returned with any HTTP status)
            if (imageResponse?.Error is { Message: not null } apiErr)
            {
                throw new ApiException(
                    $"OpenRouter API error: {apiErr.Message}",
                    apiErr.Code ?? (int)response.StatusCode, rawBody,
                    $"Error code: {apiErr.Code}\nEndpoint: /images\nModel: {model}");
            }

            if (!response.IsSuccessStatusCode)
            {
                var preview = rawBody.Length > 500 ? rawBody[..500] + "..." : rawBody;
                throw new ApiException(
                    $"OpenRouter /images returned {(int)response.StatusCode} {response.ReasonPhrase}",
                    (int)response.StatusCode, rawBody,
                    $"HTTP {(int)response.StatusCode} when generating image with model '{model}'\nResponse body:\n{preview}");
            }

            // OpenRouter's dedicated image endpoint returns the image as
            // data[0].b64_json (base64-encoded image bytes), with data[0].media_type
            // describing the format.
            var b64Json = imageResponse?.Data?
                .FirstOrDefault()?
                .B64Json;

            if (!string.IsNullOrEmpty(b64Json))
            {
                try
                {
                    return new ImageGenerationResult
                    {
                        ImageData = Convert.FromBase64String(b64Json),
                        // usage.cost: actual USD cost of this generation,
                        // when the API reports one (null otherwise).
                        Cost = imageResponse?.Usage?.Cost
                    };
                }
                catch (FormatException ex)
                {
                    throw new ApiException(
                        "Failed to decode base64 image from OpenRouter response",
                        (int)response.StatusCode, rawBody,
                        $"The /images response contained 'b64_json' but its payload was not valid base64.\nDecode error: {ex.Message}");
                }
            }

            var bodyPreview = rawBody.Length > 1000 ? rawBody[..1000] + "..." : rawBody;
            throw new ApiException(
                "OpenRouter returned no image in response",
                (int)response.StatusCode, rawBody,
                $"The API responded successfully but 'data[0].b64_json' contained no image data.\nFull response:\n{bodyPreview}");
        }
        catch (ApiException) { throw; }
        catch (TaskCanceledException)
        {
            throw new ApiException(
                "Image generation request to OpenRouter timed out",
                0, "", "The request took too long. Try a different model or check your internet connection.");
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException(
                $"Network error connecting to OpenRouter: {ex.Message}",
                0, "", ex.ToString());
        }
        catch (Exception ex)
        {
            throw new ApiException(
                $"Unexpected error during image generation: {ex.Message}",
                0, "", ex.ToString());
        }
    }

    public async Task<string> ChatCompletionAsync(List<ChatMessage> messages, string model, string? apiKey, CancellationToken ct)
    {
        var (reply, _) = await ChatCompletionCoreAsync(messages, model, apiKey, ct);
        return reply;
    }

    private async Task<(string reply, double? cost)> ChatCompletionCoreAsync(List<ChatMessage> messages, string model, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ApiException(
                "OpenRouter API key is required for chat completion — set it in Edit > Preferences",
                0, "", "The API key field is empty in Preferences.");

        try
        {
            var requestBody = new
            {
                model = model,
                messages = messages.Select(m => new
                {
                    role = m.Role,
                    content = m.Content
                }).ToList()
            };

            System.Diagnostics.Debug.WriteLine($"[OpenRouter] POST chat/completions (model: {model})");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrlValue}/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await _httpClient.SendAsync(request, ct);

            var rawBody = await response.Content.ReadAsStringAsync(ct);

            OpenRouterChatResponse? chatResponse;
            try
            {
                chatResponse = JsonSerializer.Deserialize<OpenRouterChatResponse>(rawBody, JsonOptions);
            }
            catch (JsonException ex)
            {
                var preview = rawBody.Length > 500 ? rawBody[..500] + "..." : rawBody;
                throw new ApiException(
                    $"OpenRouter returned non-JSON response (HTTP {(int)response.StatusCode})",
                    (int)response.StatusCode, rawBody,
                    $"Expected JSON but got:\n{preview}\n\nParse error: {ex.Message}");
            }

            if (chatResponse?.Error is { Message: not null } apiErr)
                throw new ApiException(
                    $"OpenRouter API error: {apiErr.Message}",
                    apiErr.Code ?? (int)response.StatusCode, rawBody,
                    $"Error code: {apiErr.Code}\nModel: {model}");

            if (!response.IsSuccessStatusCode)
            {
                var preview = rawBody.Length > 500 ? rawBody[..500] + "..." : rawBody;
                throw new ApiException(
                    $"OpenRouter /chat/completions returned {(int)response.StatusCode}",
                    (int)response.StatusCode, rawBody,
                    preview);
            }

            return (
                chatResponse?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty,
                chatResponse?.Usage?.Cost);
        }
        catch (ApiException) { throw; }
        catch (TaskCanceledException)
        {
            throw new ApiException(
                "Chat completion request to OpenRouter timed out",
                0, "", "The request took too long. Try a different model.");
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException(
                $"Network error connecting to OpenRouter: {ex.Message}",
                0, "", ex.ToString());
        }
        catch (Exception ex)
        {
            throw new ApiException(
                $"Unexpected error during chat completion: {ex.Message}",
                0, "", ex.ToString());
        }
    }

    /// <summary>
    /// Same as ChatCompletionAsync but also returns the request's usage cost
    /// (USD) so the session total can include prompt-building spend.
    /// </summary>
    public Task<(string reply, double? cost)> ChatCompletionWithCostAsync(List<ChatMessage> messages, string model, string? apiKey, CancellationToken ct)
        => ChatCompletionCoreAsync(messages, model, apiKey, ct);

    /// <summary>
    /// Fetches the account's remaining credit balance from GET /credits:
    /// total purchased minus total used, in USD.
    /// </summary>
    /// <summary>
    /// Fetches the account's remaining credit balance from GET /credits:
    /// total purchased minus total used, in USD.
    /// </summary>
    public async Task<double?> GetCreditBalanceAsync(string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        try
        {
            var raw = await GetAsync($"{BaseUrlValue}/credits", apiKey, ct);
            var credits = JsonSerializer.Deserialize<OpenRouterCreditsResponse>(raw, JsonOptions);
            if (credits?.Data is { } d)
                return d.TotalCredits - d.TotalUsage;
            return null;
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ApiException(
                $"Failed to fetch OpenRouter credit balance: {ex.Message}",
                0, "", ex.ToString());
        }
    }

    public async Task<string> VisionChatCompletionAsync(string prompt, byte[] imageBytes, string model, string? apiKey, CancellationToken ct)
    {
        var (reply, _) = await VisionChatCompletionCoreAsync(prompt, imageBytes, model, apiKey, ct);
        return reply;
    }

    /// <summary>
    /// Same as VisionChatCompletionAsync but also returns the request's usage
    /// cost so the session total can include image-to-prompt spend.
    /// </summary>
    public Task<(string reply, double? cost)> VisionChatCompletionWithCostAsync(string prompt, byte[] imageBytes, string model, string? apiKey, CancellationToken ct)
        => VisionChatCompletionCoreAsync(prompt, imageBytes, model, apiKey, ct);

    private async Task<(string reply, double? cost)> VisionChatCompletionCoreAsync(string prompt, byte[] imageBytes, string model, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ApiException(
                "OpenRouter API key is required for vision analysis — set it in Edit > Preferences",
                0, "", "The API key field is empty in Preferences.");

        try
        {
            var base64Image = Convert.ToBase64String(imageBytes);
            var dataUrl = $"data:image/png;base64,{base64Image}";

            var requestBody = new
            {
                model = model,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = prompt },
                            new { type = "image_url", image_url = new { url = dataUrl } }
                        }
                    }
                }
            };

            System.Diagnostics.Debug.WriteLine($"[OpenRouter] POST vision chat/completions (model: {model}, image: {imageBytes.Length} bytes)");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrlValue}/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await _httpClient.SendAsync(request, ct);

            var rawBody = await response.Content.ReadAsStringAsync(ct);

            OpenRouterChatResponse? chatResponse;
            try
            {
                chatResponse = JsonSerializer.Deserialize<OpenRouterChatResponse>(rawBody, JsonOptions);
            }
            catch (JsonException ex)
            {
                var preview = rawBody.Length > 500 ? rawBody[..500] + "..." : rawBody;
                throw new ApiException(
                    $"OpenRouter returned non-JSON response (HTTP {(int)response.StatusCode})",
                    (int)response.StatusCode, rawBody,
                    $"Expected JSON but got:\n{preview}\n\nParse error: {ex.Message}\nModel: {model}");
            }

            if (chatResponse?.Error is { Message: not null } apiErr)
                throw new ApiException(
                    $"OpenRouter API error: {apiErr.Message}",
                    apiErr.Code ?? (int)response.StatusCode, rawBody,
                    $"Error code: {apiErr.Code}\nModel: {model}\nImage size: {imageBytes.Length} bytes");

            if (!response.IsSuccessStatusCode)
            {
                var preview = rawBody.Length > 500 ? rawBody[..500] + "..." : rawBody;
                throw new ApiException(
                    $"OpenRouter vision request returned {(int)response.StatusCode}",
                    (int)response.StatusCode, rawBody,
                    preview);
            }

            return (
                chatResponse?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty,
                chatResponse?.Usage?.Cost);
        }
        catch (ApiException) { throw; }
        catch (TaskCanceledException)
        {
            throw new ApiException(
                "Vision analysis request to OpenRouter timed out",
                0, "", "The request took too long. The image may be too large.");
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException(
                $"Network error connecting to OpenRouter: {ex.Message}",
                0, "", ex.ToString());
        }
        catch (Exception ex)
        {
            throw new ApiException(
                $"Unexpected error during vision analysis: {ex.Message}",
                0, "", ex.ToString());
        }
    }

    /// <summary>
    /// Fetches the parameter schema for an image model from OpenRouter's
    /// dedicated image-models listing (GET /images/models), which reports
    /// supported_parameters as typed descriptors: enum (with values),
    /// range (with min/max), or boolean (accepted-or-not).
    /// </summary>
    public async Task<List<ModelParameterDescriptor>> GetModelParameterDescriptorsAsync(string model, string? apiKey, CancellationToken ct)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrlValue}/images/models");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var response = await _httpClient.SendAsync(request, ct);
            var rawBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new ApiException(
                    $"OpenRouter /images/models returned {(int)response.StatusCode} {response.ReasonPhrase}",
                    (int)response.StatusCode, rawBody,
                    TryExtractErrorDetail(rawBody));
            }

            var modelsResponse = JsonSerializer.Deserialize<OpenRouterImageModelsResponse>(rawBody, JsonOptions);
            var entry = modelsResponse?.Data?.FirstOrDefault(m => m.Id == model);
            if (entry is null)
            {
                // Model not in the image listing — it has no image-parameter metadata.
                return new List<ModelParameterDescriptor>();
            }

            var result = new List<ModelParameterDescriptor>();
            foreach (var (paramName, capability) in entry.SupportedParameters ?? new Dictionary<string, OpenRouterCapability>())
            {
                var descriptor = new ModelParameterDescriptor { Name = paramName };

                switch (capability?.Type)
                {
                    case "enum":
                        descriptor.Kind = ModelParameterKind.Enum;
                        descriptor.Values = capability.Values ?? new List<string>();
                        break;
                    case "range":
                        descriptor.Kind = ModelParameterKind.Range;
                        descriptor.Min = capability.Min;
                        descriptor.Max = capability.Max;
                        break;
                    case "boolean":
                        descriptor.Kind = ModelParameterKind.Boolean;
                        break;
                    default:
                        // Unknown descriptor type — expose it as a free string field.
                        descriptor.Kind = ModelParameterKind.Enum;
                        descriptor.Values = new List<string>();
                        break;
                }

                result.Add(descriptor);
            }

            return result;
        }
        catch (ApiException) { throw; }
        catch (Exception ex)
        {
            throw new ApiException(
                $"Failed to fetch model parameters from OpenRouter: {ex.Message}",
                0, "", ex.ToString());
        }
    }

    /// <summary>
    /// Attempts to extract a user-friendly error message from a raw response body.
    /// </summary>
    private static string TryExtractErrorDetail(string rawBody)
    {
        try
        {
            if (string.IsNullOrEmpty(rawBody)) return "";

            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;

            // OpenRouter error format: { "error": { "message": "...", "code": ... } }
            if (root.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("message", out var msg))
                {
                    var detail = msg.GetString() ?? "";
                    if (error.TryGetProperty("code", out var code))
                        detail += $" (code: {code})";
                    return detail;
                }
            }

            // OpenAI-compatible error: { "error": { "message": "...", "type": "..." } }
            if (root.TryGetProperty("error", out var error2))
            {
                if (error2.TryGetProperty("message", out var msg2))
                {
                    var detail = msg2.GetString() ?? "";
                    if (error2.TryGetProperty("type", out var type))
                        detail += $" (type: {type})";
                    return detail;
                }
            }

            return "";
        }
        catch
        {
            return "";
        }
    }
}

/// <summary>
/// Custom exception that carries HTTP status, raw response body, and diagnostic detail
/// so error dialogs can show rich information.
/// </summary>
public class ApiException : Exception
{
    public int HttpStatusCode { get; }
    public string RawResponseBody { get; }
    public string DiagnosticDetail { get; }

    public ApiException(string message, int httpStatusCode, string rawResponseBody, string diagnosticDetail)
        : base(message)
    {
        HttpStatusCode = httpStatusCode;
        RawResponseBody = rawResponseBody;
        DiagnosticDetail = diagnosticDetail;
    }

    /// <summary>
    /// Formats the full error for display in the error dialog.
    /// </summary>
    public string ToDisplayString()
    {
        var parts = new List<string>();
        parts.Add($"Error: {Message}");

        if (!string.IsNullOrEmpty(DiagnosticDetail))
        {
            // If the diagnostic is long, only include relevant parts
            parts.Add($"\nDetails:\n{DiagnosticDetail}");
        }

        parts.Add($"\n(HTTP {HttpStatusCode})");

        return string.Join("", parts);
    }
}