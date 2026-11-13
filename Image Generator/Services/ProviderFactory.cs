using System.Collections.Generic;
using Image_Generator.Models;

namespace Image_Generator.Services;

public class ProviderFactory : IProviderFactory
{
    public List<ProviderDescriptor> GetAvailableProviders()
    {
        return new List<ProviderDescriptor>
        {
            new ProviderDescriptor
            {
                Id = "openrouter",
                DisplayName = "OpenRouter",
                Description = "Access to 100+ models via OpenRouter gateway (requires API key)",
                RequiresCustomUrl = false,
                DefaultBaseUrl = "https://openrouter.ai/api/v1"
            }
        };
    }

    public IImageGenerationProvider CreateProvider(string providerId, AppPreferences prefs)
    {
        // OpenRouter only — every other provider id falls back to it too,
        // so stale configs from older versions keep working.
        return new OpenRouterProvider();
    }
}
