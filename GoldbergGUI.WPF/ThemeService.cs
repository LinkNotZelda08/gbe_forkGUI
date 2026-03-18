using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using GoldbergGUI.Core.Services;

namespace GoldbergGUI.WPF
{
    public class ThemeService : IThemeService
    {
        private const string ThemeSettingsPath = "theme.txt";
        private string _currentTheme = "Blue Dark";

        private static readonly Dictionary<string, ThemeColors> _themes = new()
        {
            ["Blue Dark"] = new ThemeColors
            {
                Background    = "#1E1E2E",
                Surface       = "#2A2A3E",
                SurfaceAlt    = "#313145",
                Border        = "#44445A",
                Accent        = "#7E9CD8",
                AccentHover   = "#9BB0E8",
                Foreground    = "#CDD6F4",
                ForegroundDim = "#9399B2",
                Selection     = "#364163",
                IsDark        = true,
            },
            ["Classic Dark"] = new ThemeColors
            {
                Background    = "#1C1C1C",
                Surface       = "#2D2D2D",
                SurfaceAlt    = "#383838",
                Border        = "#555555",
                Accent        = "#0078D4",
                AccentHover   = "#1A8FE3",
                Foreground    = "#E0E0E0",
                ForegroundDim = "#A0A0A0",
                Selection     = "#0050A0",
                IsDark        = true,
            },
            ["Light"] = new ThemeColors
            {
                Background    = "#F3F3F3",
                Surface       = "#FFFFFF",
                SurfaceAlt    = "#E8E8E8",
                Border        = "#CCCCCC",
                Accent        = "#0078D4",
                AccentHover   = "#006CBE",
                Foreground    = "#1A1A1A",
                ForegroundDim = "#666666",
                Selection     = "#CCE4F7",
                IsDark        = false,
            },
        };

        public List<string> Themes => new(_themes.Keys);
        public string CurrentTheme => _currentTheme;

        public void ApplyTheme(string themeName)
        {
            if (!_themes.TryGetValue(themeName, out var colors)) return;
            _currentTheme = themeName;

            Application.Current.Dispatcher.Invoke(() =>
            {
                var res = Application.Current.Resources;
                res["BackgroundBrush"]    = Brush(colors.Background);
                res["SurfaceBrush"]       = Brush(colors.Surface);
                res["SurfaceAltBrush"]    = Brush(colors.SurfaceAlt);
                res["BorderBrush"]        = Brush(colors.Border);
                res["AccentBrush"]        = Brush(colors.Accent);
                res["AccentHoverBrush"]   = Brush(colors.AccentHover);
                res["ForegroundBrush"]    = Brush(colors.Foreground);
                res["ForegroundDimBrush"] = Brush(colors.ForegroundDim);
                res["SelectionBrush"]     = Brush(colors.Selection);

                // Update window background and title bar
                foreach (Window window in Application.Current.Windows)
                {
                    window.Background = Brush(colors.Background);
                    MainWindow.SetTitleBarDarkMode(window, colors.IsDark);
                }
            });

            File.WriteAllText(ThemeSettingsPath, themeName);
        }

        public string LoadSavedTheme()
        {
            if (File.Exists(ThemeSettingsPath))
            {
                var saved = File.ReadAllText(ThemeSettingsPath).Trim();
                if (_themes.ContainsKey(saved)) return saved;
            }
            return "Blue Dark";
        }

        private static SolidColorBrush Brush(string hex) =>
            new((Color)ColorConverter.ConvertFromString(hex));
    }

    public class ThemeColors
    {
        public string Background    { get; set; }
        public string Surface       { get; set; }
        public string SurfaceAlt    { get; set; }
        public string Border        { get; set; }
        public string Accent        { get; set; }
        public string AccentHover   { get; set; }
        public string Foreground    { get; set; }
        public string ForegroundDim { get; set; }
        public string Selection     { get; set; }
        public bool   IsDark        { get; set; }
    }
}
