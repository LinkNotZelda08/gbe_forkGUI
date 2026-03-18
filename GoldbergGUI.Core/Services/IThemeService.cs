using System.Collections.Generic;

namespace GoldbergGUI.Core.Services
{
    public interface IThemeService
    {
        List<string> Themes { get; }
        string CurrentTheme { get; }
        void ApplyTheme(string themeName);
        string LoadSavedTheme();
    }
}
