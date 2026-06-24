using System.Windows;

namespace GoldbergGUI.Core.Utils;

public static class BuildFeatures
{
#if NO_ALI213
    public static readonly Visibility Ali213Visibility = Visibility.Collapsed;
#else
    public static readonly Visibility Ali213Visibility = Visibility.Visible;
#endif
}
