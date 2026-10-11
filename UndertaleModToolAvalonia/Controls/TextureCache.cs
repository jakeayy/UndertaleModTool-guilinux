using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace UndertaleModToolAvalonia.Controls;

/// <summary>
/// Caches decoded texture pages as Avalonia bitmaps, keyed by the underlying <see cref="GMImage"/> instance
/// (so replacing a texture's image automatically invalidates the cache entry).
/// </summary>
public static class TextureCache
{
    private static ConditionalWeakTable<GMImage, Bitmap> cache = new();
    private static readonly object cacheLock = new();

    public static void Clear()
    {
        lock (cacheLock)
            cache = new();
    }

    /// <summary>
    /// Returns a bitmap for the given texture page, or <see langword="null"/> if it cannot be decoded.
    /// </summary>
    public static Bitmap Get(UndertaleEmbeddedTexture texture)
    {
        GMImage image = texture?.TextureData?.Image;
        if (image is null || image.Format == GMImage.ImageFormat.Unknown)
            return null;

        lock (cacheLock)
        {
            if (cache.TryGetValue(image, out Bitmap cached))
                return cached;
        }

        Bitmap bitmap;
        try
        {
            bitmap = ToBitmap(image);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Failed to decode texture {texture.Name?.Content}: {e.Message}");
            return null;
        }

        lock (cacheLock)
        {
            cache.AddOrUpdate(image, bitmap);
        }
        return bitmap;
    }

    public static unsafe Bitmap ToBitmap(GMImage image)
    {
        GMImage raw = image.ConvertToRawBgra();
        Span<byte> data = raw.GetRawImageData();
        fixed (byte* ptr = data)
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, (IntPtr)ptr,
                              new PixelSize(raw.Width, raw.Height), new Vector(96, 96), raw.Width * 4);
        }
    }

    /// <summary>
    /// Draws a texture page item, with its target offset/scale applied, at the given position.
    /// </summary>
    public static void DrawItem(DrawingContext ctx, UndertaleTexturePageItem item, double x, double y,
                                double scaleX = 1, double scaleY = 1, double opacity = 1)
    {
        if (item?.TexturePage is null)
            return;
        Bitmap bmp = Get(item.TexturePage);
        if (bmp is null || item.SourceWidth == 0 || item.SourceHeight == 0)
            return;

        var src = new Rect(item.SourceX, item.SourceY, item.SourceWidth, item.SourceHeight);
        var dst = new Rect(x + item.TargetX * scaleX, y + item.TargetY * scaleY,
                           item.TargetWidth * scaleX, item.TargetHeight * scaleY).Normalize();
        if (opacity < 1)
        {
            using (ctx.PushOpacity(opacity))
                ctx.DrawImage(bmp, src, dst);
        }
        else
            ctx.DrawImage(bmp, src, dst);
    }

    /// <summary>
    /// Draws a transparency checkerboard over the given rectangle.
    /// </summary>
    public static void DrawCheckerboard(DrawingContext ctx, Rect rect, double cell = 8)
    {
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)), rect);
        var dark = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        using (ctx.PushClip(rect))
        {
            int cols = (int)Math.Ceiling(rect.Width / cell);
            int rows = (int)Math.Ceiling(rect.Height / cell);
            // Avoid huge draw counts on very large images
            if ((long)cols * rows > 40000)
                return;
            for (int r = 0; r < rows; r++)
            {
                for (int c = (r & 1); c < cols; c += 2)
                    ctx.FillRectangle(dark, new Rect(rect.X + c * cell, rect.Y + r * cell, cell, cell));
            }
        }
    }
}
