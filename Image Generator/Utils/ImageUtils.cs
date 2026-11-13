using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia;
using System.IO;

namespace Image_Generator.Utils;

public static class ImageUtils
{
    public static Bitmap? CreateThumbnail(byte[] imageBytes, int maxWidth = 160)
    {
        try
        {
            using var ms = new MemoryStream(imageBytes);
            var bitmap = new Bitmap(ms);

            // Calculate scaled dimensions preserving aspect ratio
            double scale = (double)maxWidth / bitmap.PixelSize.Width;
            int thumbWidth = maxWidth;
            int thumbHeight = (int)(bitmap.PixelSize.Height * scale);

            // Create scaled bitmap
            return bitmap.CreateScaledBitmap(new PixelSize(thumbWidth, thumbHeight), BitmapInterpolationMode.HighQuality);
        }
        catch
        {
            return null;
        }
    }

    public static Bitmap? BytesToBitmap(byte[] imageBytes)
    {
        try
        {
            using var ms = new MemoryStream(imageBytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    public static byte[]? BitmapToPngBytes(Bitmap bitmap)
    {
        try
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }
}