using Image_Generator.Models;

namespace Image_Generator.Services;

public interface IPreferencesService
{
    AppPreferences Load();
    void Save(AppPreferences preferences);
}