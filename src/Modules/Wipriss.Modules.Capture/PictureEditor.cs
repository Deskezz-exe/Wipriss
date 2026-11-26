using System.Windows;
using System.Windows.Media.Imaging;

namespace Wipriss.Modules.Capture;

/// <summary>The screenshot editor (brush, marker, arrow, frame, blur, crop) for a picture of another module.</summary>
public static class PictureEditor
{
    /// <param name="done">Gets the picture with the drawings when the owner presses "Готово"; not called on close.</param>
    public static Window Open(BitmapSource image, Window? owner, Action<BitmapSource> done)
    {
        var editor = new EditorWindow(image, done) { Owner = owner };
        editor.Show();
        editor.Activate();
        return editor;
    }
}
