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

public class OpenAICompatibleProvider : IImageGenerationProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public OpenAICompatibleProvider(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _httpClient = new HttpClient();
    }

    public string Name => "OpenAI Compatible";
    public string BaseUrl => _baseUrl;
    public bool RequiresApiKey => false; // Most local providers don't require API keys

    public async Task<List<ProviderModelInfo>> ListModelsAsync(string? apiKey = null, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/models");

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        try
        {
            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var modelsResponse = JsonSerializer.Deserialize<OpenAIModelsResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var results = new List<ProviderModelInfo>();

            if (modelsResponse?.Data != null)
            {
                foreach (var model in modelsResponse.Data)
                {
                    // For OpenAI-compatible, we don't know capabilities from /models endpoint
                    // Assume all models support chat, and we'll let user select appropriate ones
                    results.Add(new ProviderModelInfo
                    {
                        Id = model.Id,
                        Name = model.Id, // OpenAI-compatible often just returns ID
                        Capabilities = new ModelCapabilities
                        {
                            SupportsImageGeneration = true, // Assume - user will pick correct one
                            SupportsVision = model.Id.Contains("vision", StringComparison.OrdinalIgnoreCase) ||
                                           model.Id.Contains("gpt-4o", StringComparison.OrdinalIgnoreCase) ||
                                           model.Id.Contains("llava", StringComparison.OrdinalIgnoreCase),
                            SupportsChat = true
                        },
                        Pricing = null // Local models are typically free
                    });
                }
            }

            return results;
        }
        catch
        {
            // If /models fails, return empty list
            return new List<ProviderModelInfo>();
        }
    }


    /// <summary>
    /// OpenAI-compatible endpoints don't expose per-model parameter metadata;
    /// report none so the parameters button stays inert for custom providers.
    /// </summary>
    public Task<List<ModelParameterDescriptor>> GetModelParameterDescriptorsAsync(string model, string? apiKey, CancellationToken ct)
    {
        return Task.FromResult(new List<ModelParameterDescriptor>());
    }

    public async Task<ImageGenerationResult> GenerateImageAsync(string prompt, int width, int height, string model, string? apiKey, CancellationToken ct, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var requestBody = new
        {
            model = model,
            prompt = prompt,
            n = 1,
            size = $"{width}x{height}"
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/images/generations")
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var imageResponse = JsonSerializer.Deserialize<OpenAIImageResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (imageResponse?.Data?.FirstOrDefault() is not { } imageData)
            throw new Exception("No image data in response");

        if (!string.IsNullOrEmpty(imageData.B64Json))
        {
            return new ImageGenerationResult { ImageData = Convert.FromBase64String(imageData.B64Json) };
        }
        else if (!string.IsNullOrEmpty(imageData.Url))
        {
            return new ImageGenerationResult { ImageData = await _httpClient.GetByteArrayAsync(imageData.Url, ct) };
        }

        throw new Exception("No image data available in response");
    }


    /// <summary>OpenAI-compatible servers don't expose a credit balance API.</summary>
    public Task<double?> GetCreditBalanceAsync(string? apiKey, CancellationToken ct)
    {
        return Task.FromResult<double?>(null);
    }

    /// <summary>Chat with cost: cost is unavailable on generic endpoints.</summary>
    public async Task<(string reply, double? cost)> ChatCompletionWithCostAsync(List<ChatMessage> messages, string model, string? apiKey, CancellationToken ct)
    {
        var reply = await ChatCompletionAsync(messages, model, apiKey, ct);
        return (reply, null);
    }

    public async Task<string> ChatCompletionAsync(List<ChatMessage> messages, string model, string? apiKey, CancellationToken ct)
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

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var chatResponse = JsonSerializer.Deserialize<OpenAIChatResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return chatResponse?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
    }


    /// <summary>Vision chat with cost; cost unavailable on generic endpoints.</summary>
    public async Task<(string reply, double? cost)> VisionChatCompletionWithCostAsync(string prompt, byte[] imageBytes, string model, string? apiKey, CancellationToken ct)
    {
        var reply = await VisionChatCompletionAsync(prompt, imageBytes, model, apiKey, ct);
        return (reply, null);
    }

    public async Task<string> VisionChatCompletionAsync(string prompt, byte[] imageBytes, string model, string? apiKey, CancellationToken ct)
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

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var chatResponse = JsonSerializer.Deserialize<OpenAIChatResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return chatResponse?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
    }

    // DTOs for OpenAI-compatible API responses
    private class OpenAIModelsResponse
    {
        [JsonPropertyName("data")]
        public List<OpenAIModel>? Data { get; set; }
    }

    private class OpenAIModel
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }

    private class OpenAIImageResponse
    {
        [JsonPropertyName("data")]
        public List<OpenAIImageData>? Data { get; set; }
    }

    private class OpenAIImageData
    {
        [JsonPropertyName("b64_json")]
        public string? B64Json { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    private class OpenAIChatResponse
    {
        [JsonPropertyName("choices")]
        public List<OpenAIChatChoice>? Choices { get; set; }
    }

    private class OpenAIChatChoice
    {
        [JsonPropertyName("message")]
        public OpenAIChatMessage? Message { get; set; }
    }

    private class OpenAIChatMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}