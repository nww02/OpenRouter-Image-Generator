using System.Collections.Generic;
using System.Threading.Tasks;
using Image_Generator.Models;

namespace Image_Generator.Services;

public interface IImageIOService
{
    Task SaveImageAsync(string filePath, byte[] imageData, string prompt,
        string? model = null, Dictionary<string, string?>? parameters = null, double? cost = null);
    Task<(byte[] imageData, string? prompt)> LoadImageAsync(string filePath);
    Task<(byte[] imageData, PromptFileMetadata? metadata)> LoadImageWithMetadataAsync(string filePath);
    string GenerateDefaultFileName();
}
