using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace VideoKompressor;

/// <summary>
/// View-layer converters. This lives on the Avalonia side of the fence so the
/// ViewModel can stay UI-free and only ever hand out plain file paths.
/// </summary>
public static class Converters
{
    /// <summary>Turns a file path (string) into a Bitmap for Image.Source.</summary>
    public static readonly IValueConverter PathToBitmap =
        new FuncValueConverter<string?, Bitmap?>(path =>
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            // Load through a stream so the file isn't kept locked — that lets the
            // ViewModel delete stale temp thumbnails when a new file is picked.
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        });
}
