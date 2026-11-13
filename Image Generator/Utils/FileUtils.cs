using System;
using System.IO;

namespace Image_Generator.Utils;

public static class FileUtils
{
    public static string GenerateDefaultFileName()
    {
        var now = DateTime.Now;
        return $"Generated-{now:yyyyMMdd-HHmmss}.png";
    }

    public static string GetPromptFilePath(string imageFilePath)
    {
        return imageFilePath + ".prompt";
    }

    public static bool HasPromptFile(string imageFilePath)
    {
        return File.Exists(GetPromptFilePath(imageFilePath));
    }
}