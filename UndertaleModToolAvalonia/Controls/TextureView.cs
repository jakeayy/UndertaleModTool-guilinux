using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using UndertaleModLib.Models;

namespace UndertaleModToolAvalonia.Controls;

/// <summary>
/// Displays either a single texture page item (with its padding/bounding box) or a whole texture page,
/// on a transparency checkerboard, with nearest-neighbour zoom (Ctrl+wheel).
/// </summary>
public class TextureView : Control
{
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<TextureView, double>(nameof(Zoom), 1.0);

    private UndertaleTexturePageItem item;
    private UndertaleEmbeddedTexture page;

    /// <summary>Optional overlay drawn on top, in image coordinates (e.g. glyph rectangles).</summary>
    public Action<DrawingContext> Overlay { get; set; }

    static TextureView()
    {
        AffectsMeasure<TextureView>(ZoomProperty);
        AffectsRender<TextureView>(ZoomProperty);
    }

    public TextureView()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        ClipToBounds = true;
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, Math.Clamp(value, 0.05, 32));
    }

    public UndertaleTexturePageItem Item
    {
        get => item;
        set { item = value; page = null; InvalidateMeasure(); InvalidateVisual(); }
    }

    public UndertaleEmbeddedTexture Page
    {
        get => page;
        set { page = value; item = null; InvalidateMeasure(); InvalidateVisual(); }
    }

    /// <summary>Forces a redraw (e.g. after the underlying image was replaced).</summary>
    public void Refresh()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    private Size ContentSize
    {
        get
        {
            if (item is not null)
                return new Size(Math.Max(item.BoundingWidth, item.TargetX + item.TargetWidth),
                                Math.Max(item.BoundingHeight, item.TargetY + item.TargetHeight));
            Bitmap bmp = TextureCache.Get(page);
            if (bmp is not null)
                return new Size(bmp.PixelSize.Width, bmp.PixelSize.Height);
            return new Size(0, 0);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Size s = ContentSize;
        return new Size(s.Width * Zoom, s.Height * Zoom);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Zoom = e.Delta.Y > 0 ? Zoom * 1.25 : Zoom / 1.25;
            e.Handled = true;
            return;
        }
        base.OnPointerWheelChanged(e);
    }

    public override void Render(DrawingContext ctx)
    {
        Size s = ContentSize;
        if (s.Width <= 0 || s.Height <= 0)
        {
            return;
        }
        var bounds = new Rect(0, 0, s.Width * Zoom, s.Height * Zoom);
        TextureCache.DrawCheckerboard(ctx, bounds, 8);

        using (ctx.PushTransform(Matrix.CreateScale(Zoom, Zoom)))
        {
            if (item is not null)
            {
                TextureCache.DrawItem(ctx, item, 0, 0);
            }
            else if (page is not null)
            {
                Bitmap bmp = TextureCache.Get(page);
                if (bmp is not null)
                    ctx.DrawImage(bmp, new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height));
            }
            Overlay?.Invoke(ctx);
        }
    }
}
