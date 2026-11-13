using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Image_Generator.Models;
using Image_Generator.Services;

namespace Image_Generator.ViewModels;

/// <summary>
/// ViewModel for the Prompt Generator dialog — dropdowns for humour, style,
/// topic, colour, time of day, mood, plus Randomize and Generate Prompt.
/// </summary>
public partial class PromptGeneratorDialogViewModel : ViewModelBase
{
    private readonly AppPreferences _preferences;
    private readonly ProviderFactory _providerFactory;

    /// <summary>
    /// Receives the USD cost of each Build Prompt request, so the session
    /// total can include prompt-building spend. Null = not reported.
    /// </summary>
    public event System.Action<double?>? CostReported;

    /// <summary>
    /// Sentinel shown in every dropdown. Choosing it means "leave this aspect
    /// out of the LLM request entirely" — the line is omitted from the message
    /// sent to the prompt-generation model.
    /// </summary>
    public const string SkipOption = "<skip>";

    public static List<string> HumourOptions { get; } = new()
        { SkipOption, "Deadly Serious", "Mostly Serious", "Balanced", "Light-hearted", "Total Comedy" };

    public static List<string> StyleOptions { get; } = new()
        { SkipOption, "Cartoon", "Pencil Drawing", "Oil Painting", "Watercolour", "Digital Art",
          "Pixel Art", "3D Render", "Sketch", "Anime/Manga", "Photorealistic",
          "Impressionist", "Surrealist", "Minimalist", "Steampunk", "Cyberpunk",
          "Abstract", "Pop Art", "Fantasy Art", "Gothic", "Renaissance" };

    public static List<string> TopicOptions { get; } = new()
        { SkipOption, "Trains", "Space", "Animals", "Cooking", "Castles", "Dragons", "Forests",
          "Oceans", "Robots", "Dinosaurs", "Cities", "Deserts", "Mountains",
          "Underwater", "Space Stations", "Steampunk Airships", "Ancient Ruins",
          "Cyberpunk Streets", "Floating Islands", "Crystal Caves",
          "Enchanted Libraries", "Volcanoes", "Solar Systems", "Deep Sea Trenches",
          "Fairy Tale Villages", "Mechanical Jungles", "Alien Landscapes",
          "Time Travel Portals", "Ghost Towns", "Bioluminescent Gardens",
          "Mythical Creatures" };

    public static List<string> ColourOptions { get; } = new()
        { SkipOption, "Muted", "Black and White", "Greyscale", "Vivid", "Garish", "Pastel",
          "Neon", "Sepia", "Monochrome", "Rainbow" };

    public static List<string> TimeOfDayOptions { get; } = new()
        { SkipOption, "Dawn", "Morning", "Midday", "Afternoon", "Dusk", "Night", "Midnight" };

    public static List<string> MoodOptions { get; } = new()
        { SkipOption, "Peaceful", "Chaotic", "Mysterious", "Joyful", "Melancholic", "Tense",
          "Dreamy", "Epic", "Cozy", "Gloomy", "Whimsical", "Serene", "Dramatic" };

    [ObservableProperty]
    private string _humour = HumourOptions[1];

    [ObservableProperty]
    private string _style = StyleOptions[1];

    [ObservableProperty]
    private string _topic = TopicOptions[1];

    [ObservableProperty]
    private string _colour = ColourOptions[1];

    [ObservableProperty]
    private string _timeOfDay = TimeOfDayOptions[1];

    [ObservableProperty]
    private string _mood = MoodOptions[1];

    [ObservableProperty]
    private bool _isGenerating;

    [ObservableProperty]
    private string? _generatedPrompt;

    [ObservableProperty]
    private string _customText = string.Empty;

    public bool HasGeneratedPrompt => !string.IsNullOrEmpty(GeneratedPrompt);

    private readonly Random _random = new();

    public PromptGeneratorDialogViewModel(AppPreferences preferences, ProviderFactory providerFactory)
    {
        _preferences = preferences;
        _providerFactory = providerFactory;

        // Ensure all defaults are explicitly set so no ComboBox starts blank.
        // Start from the first real option (index 1) — <skip> at index 0 is a
        // deliberate choice the user can make, not a sensible default.
        _humour = HumourOptions[1];
        _style = StyleOptions[1];
        _topic = TopicOptions[1];
        _colour = ColourOptions[1];
        _timeOfDay = TimeOfDayOptions[1];
        _mood = MoodOptions[1];
    }

    private static T PickRandom<T>(IReadOnlyList<T> options, Random rng)
    {
        return options[rng.Next(options.Count)];
    }

    partial void OnGeneratedPromptChanged(string? value)
    {
        OnPropertyChanged(nameof(HasGeneratedPrompt));
    }

    [RelayCommand]
    private void Randomize()
    {
        // Randomize skips the <skip> sentinel — a skipped topic is a deliberate
        // user choice, not something to roll.
        Humour = PickRandom(HumourOptions.Where(o => o != SkipOption).ToList(), _random);
        Style = PickRandom(StyleOptions.Where(o => o != SkipOption).ToList(), _random);
        Topic = PickRandom(TopicOptions.Where(o => o != SkipOption).ToList(), _random);
        Colour = PickRandom(ColourOptions.Where(o => o != SkipOption).ToList(), _random);
        TimeOfDay = PickRandom(TimeOfDayOptions.Where(o => o != SkipOption).ToList(), _random);
        Mood = PickRandom(MoodOptions.Where(o => o != SkipOption).ToList(), _random);
    }

    /// <summary>
    /// Builds the user message for the prompt-generation LLM from the current
    /// selections. Topics set to <see cref="SkipOption"/> are omitted entirely;
    /// custom instructions are appended when present. Returns null when there
    /// is nothing to generate from (everything skipped, no custom text).
    /// </summary>
    public string? BuildUserInput()
    {
        var lines = new List<(string Label, string? Value)>
        {
            ("Humour", Humour),
            ("Style", Style),
            ("Topic", Topic),
            ("Colour", Colour),
            ("Time of Day", TimeOfDay),
            ("Mood", Mood)
        };

        var chosen = lines
            .Where(l => !string.IsNullOrEmpty(l.Value) && l.Value != SkipOption)
            .Select(l => $"{l.Label}: {l.Value}")
            .ToList();

        var hasCustom = !string.IsNullOrWhiteSpace(CustomText);
        if (chosen.Count == 0 && !hasCustom)
        {
            return null;
        }

        var userInput = string.Join("\n", chosen);
        if (hasCustom)
        {
            userInput = chosen.Count > 0
                ? userInput + $"\nCustom instructions: {CustomText}"
                : $"Custom instructions: {CustomText}";
        }

        return userInput;
    }

    /// <summary>
    /// Gathers current selections and sends them to the LLM for prompt generation.
    /// </summary>
    [RelayCommand]
    private async Task BuildPromptAsync()
    {
        if (IsGenerating) return;

        IsGenerating = true;
        GeneratedPrompt = null;

        try
        {
            var provider = _providerFactory.CreateProvider(_preferences.SelectedProviderId, _preferences);
            var apiKey = _preferences.SelectedProviderId == "openrouter"
                ? _preferences.OpenRouterApiKey
                : _preferences.CustomProviderApiKey;
            var chatModel = _preferences.SelectedProviderId == "openrouter"
                ? _preferences.OpenRouterPromptGenModel
                : _preferences.CustomVisionModel;

            var userInput = BuildUserInput();
            if (userInput is null)
            {
                GeneratedPrompt = "Nothing to generate from — every topic is set to <skip> and no custom instructions were given.";
                return;
            }

            var messages = new List<ChatMessage>
            {
                new()
                {
                    Role = "system",
                    Content = "You are a creative prompt engineer. Generate a single, detailed image-generation prompt based on the following parameters. Output ONLY the prompt text, no introduction or explanation."
                },
                new()
                {
                    Role = "user",
                    Content = userInput
                }
            };

            var (result, cost) = await provider.ChatCompletionWithCostAsync(messages, chatModel, apiKey, CancellationToken.None);
            CostReported?.Invoke(cost);
            GeneratedPrompt = result?.Trim();
        }
        catch (Exception ex)
        {
            var errMsg = ex is ApiException apiEx ? apiEx.ToDisplayString() : $"Error: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[PromptGenerator] {errMsg}");
            GeneratedPrompt = errMsg;
        }
        finally
        {
            IsGenerating = false;
        }
    }

    public PromptGenerationOptions ToOptions()
    {
        return new PromptGenerationOptions
        {
            // <skip> is a request-time instruction, not a stored preference —
            // persist the concrete value instead (or null when skipped).
            Humour = Humour == SkipOption ? null : Humour,
            Style = Style == SkipOption ? null : Style,
            Topic = Topic == SkipOption ? null : Topic,
            Colour = Colour == SkipOption ? null : Colour,
            TimeOfDay = TimeOfDay == SkipOption ? null : TimeOfDay,
            Mood = Mood == SkipOption ? null : Mood
        };
    }
}