using System.Windows;
using System.Windows.Media;

namespace Wipriss.App;

/// <summary>Theme colors for code that can't use XAML resources, like window frames.</summary>
internal static class ThemeColors
{
    public static Color Window => Get("WindowColor");
    public static Color Hairline => Get("HairlineColor");
    public static Color Text => Get("TextColor");

    private static Color Get(string key) => (Color)Application.Current.FindResource(key);
}
