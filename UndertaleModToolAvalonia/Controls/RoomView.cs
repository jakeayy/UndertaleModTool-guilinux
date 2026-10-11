using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using UndertaleModLib.Models;
using static UndertaleModLib.Models.UndertaleRoom;

namespace UndertaleModToolAvalonia.Controls;

/// <summary>
/// Renders a room (GMS1 backgrounds/tiles/instances, and GMS2 layers), and lets the user
/// select and drag instances, tiles and sprite assets around.
/// </summary>
public class RoomView : Control
{
    private const uint TileIndexMask = 0x7FFFF;

    private readonly UndertaleRoom room;
    private double zoom = 1;
    private object selected;
    private Point dragStart;
    private (int X, int Y) dragOrigin;
    private bool dragging;

    public bool ShowGrid { get; set; }
    public int GridSize { get; set; } = 16;
    public bool SnapToGrid { get; set; }
    public bool ShowTiles { get; set; } = true;
    public bool ShowInstances { get; set; } = true;
    public bool ShowBackgrounds { get; set; } = true;

    /// <summary>Raised when the selection changes (instance, tile, sprite instance, or null).</summary>
    public event Action<object> SelectionChanged;

    /// <summary>Raised after an object was moved by dragging.</summary>
    public event Action<object> ObjectMoved;

    public RoomView(UndertaleRoom room)
    {
        this.room = room;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        ClipToBounds = true;
        Focusable = true;
    }

    public double Zoom
    {
        get => zoom;
        set
        {
            zoom = Math.Clamp(value, 0.1, 16);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public object Selected
    {
        get => selected;
        set
        {
            selected = value;
            InvalidateVisual();
            SelectionChanged?.Invoke(value);
        }
    }

    public void Refresh() => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) => new(room.Width * zoom, room.Height * zoom);

    #region Color helpers

    /// <summary>GameMaker colors are stored as 0xAABBGGRR.</summary>
    private static Color GmColor(uint c, bool ignoreAlpha = false) =>
        Color.FromArgb(ignoreAlpha ? (byte)255 : (byte)(c >> 24), (byte)c, (byte)(c >> 8), (byte)(c >> 16));

    private static double GmAlpha(uint c) => ((c >> 24) & 0xFF) / 255.0;

    #endregion

    #region Rendering

    public override void Render(DrawingContext ctx)
    {
        var roomRect = new Rect(0, 0, room.Width, room.Height);
        using (ctx.PushTransform(Matrix.CreateScale(zoom, zoom)))
        {
            using (ctx.PushClip(roomRect))
            {
                bool isGms2 = room.Layers is { Count: > 0 } || room.Flags.HasFlag(RoomEntryFlags.IsGMS2);
                if (!isGms2 || room.Layers is null || room.Layers.Count == 0)
                    RenderGms1(ctx);
                else
                    RenderLayers(ctx);
            }

            if (ShowGrid && GridSize > 1)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1 / zoom);
                for (int x = GridSize; x < room.Width; x += GridSize)
                    ctx.DrawLine(pen, new Point(x, 0), new Point(x, room.Height));
                for (int y = GridSize; y < room.Height; y += GridSize)
                    ctx.DrawLine(pen, new Point(0, y), new Point(room.Width, y));
            }

            if (selected is not null && GetBounds(selected) is Rect sel)
            {
                ctx.DrawRectangle(null, new Pen(Brushes.Yellow, 2 / zoom), sel);
                ctx.DrawRectangle(null, new Pen(Brushes.Black, 1 / zoom, new DashStyle([2, 2], 0)), sel);
            }
        }
    }

    private void RenderGms1(DrawingContext ctx)
    {
        if (room.DrawBackgroundColor)
            ctx.FillRectangle(new SolidColorBrush(GmColor(room.BackgroundColor, true)), new Rect(0, 0, room.Width, room.Height));

        if (ShowBackgrounds)
        {
            foreach (var bg in room.Backgrounds.Where(b => b.Enabled && !b.Foreground))
                DrawRoomBackground(ctx, bg);
        }

        // Tiles and instances are interleaved by depth (higher depth is drawn first)
        var drawables = new List<(int Depth, Action Draw)>();
        if (ShowTiles)
        {
            foreach (var tile in room.Tiles)
                drawables.Add((tile.TileDepth, () => DrawTile(ctx, tile)));
        }
        if (ShowInstances)
        {
            foreach (var inst in room.GameObjects)
                drawables.Add((inst.ObjectDefinition?.Depth ?? 0, () => DrawInstance(ctx, inst)));
        }
        foreach (var d in drawables.OrderByDescending(d => d.Depth))
            d.Draw();

        if (ShowBackgrounds)
        {
            foreach (var bg in room.Backgrounds.Where(b => b.Enabled && b.Foreground))
                DrawRoomBackground(ctx, bg);
        }
    }

    private void RenderLayers(DrawingContext ctx)
    {
        foreach (var layer in room.Layers.OrderByDescending(l => l.LayerDepth))
        {
            if (!layer.IsVisible)
                continue;
            switch (layer.Data)
            {
                case Layer.LayerBackgroundData bg when ShowBackgrounds:
                    DrawLayerBackground(ctx, layer, bg);
                    break;
                case Layer.LayerTilesData tiles when ShowTiles:
                    DrawTileLayer(ctx, layer, tiles);
                    break;
                case Layer.LayerInstancesData insts when ShowInstances:
                    foreach (var inst in insts.Instances)
                        DrawInstance(ctx, inst);
                    break;
                case Layer.LayerAssetsData assets when ShowTiles:
                    if (assets.LegacyTiles is not null)
                    {
                        foreach (var tile in assets.LegacyTiles.OrderByDescending(t => t.TileDepth))
                            DrawTile(ctx, tile);
                    }
                    if (assets.Sprites is not null)
                    {
                        foreach (var spr in assets.Sprites)
                            DrawSpriteInstance(ctx, spr);
                    }
                    break;
            }
        }
    }

    private static UndertaleTexturePageItem Frame(UndertaleSprite sprite, int index)
    {
        if (sprite?.Textures is not { Count: > 0 } textures)
            return null;
        int i = ((index % textures.Count) + textures.Count) % textures.Count;
        return textures[i]?.Texture;
    }

    private static void DrawTransformed(DrawingContext ctx, UndertaleTexturePageItem tex, double x, double y,
                                        double originX, double originY, double scaleX, double scaleY,
                                        double rotation, double opacity)
    {
        if (tex is null)
            return;
        // GameMaker rotation is counter-clockwise in degrees
        var m = Matrix.CreateTranslation(-originX, -originY)
              * Matrix.CreateScale(scaleX, scaleY)
              * Matrix.CreateRotation(-rotation * Math.PI / 180.0)
              * Matrix.CreateTranslation(x, y);
        using (ctx.PushTransform(m))
            TextureCache.DrawItem(ctx, tex, 0, 0, 1, 1, opacity);
    }

    private void DrawInstance(DrawingContext ctx, GameObject inst)
    {
        UndertaleSprite sprite = inst.ObjectDefinition?.Sprite;
        UndertaleTexturePageItem tex = Frame(sprite, inst.ImageIndex);
        if (tex is null)
        {
            // Objects without sprites are drawn as a small marker
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x60, 0x40, 0x80, 0xFF)), new Pen(Brushes.SteelBlue, 1 / zoom),
                              new Rect(inst.X - 8, inst.Y - 8, 16, 16));
            return;
        }
        DrawTransformed(ctx, tex, inst.X, inst.Y, sprite.OriginX, sprite.OriginY, inst.ScaleX, inst.ScaleY,
                        inst.Rotation, GmAlpha(inst.Color) is var a && a > 0 ? a : 1);
    }

    private void DrawSpriteInstance(DrawingContext ctx, SpriteInstance spr)
    {
        UndertaleTexturePageItem tex = Frame(spr.Sprite, (int)spr.FrameIndex);
        if (tex is null)
            return;
        DrawTransformed(ctx, tex, spr.X, spr.Y, spr.Sprite.OriginX, spr.Sprite.OriginY, spr.ScaleX, spr.ScaleY,
                        spr.Rotation, GmAlpha(spr.Color) is var a && a > 0 ? a : 1);
    }

    /// <summary>
    /// Draws a region of a texture page item's (unpadded) image space, mapping it onto the texture page.
    /// </summary>
    private static void DrawItemRegion(DrawingContext ctx, UndertaleTexturePageItem tex, Rect region, Rect dest)
    {
        if (tex?.TexturePage is null)
            return;
        Bitmap bmp = TextureCache.Get(tex.TexturePage);
        if (bmp is null || tex.TargetWidth == 0 || tex.TargetHeight == 0)
            return;

        // Clip the requested region to the area actually covered by the item on the page
        var covered = new Rect(tex.TargetX, tex.TargetY, tex.TargetWidth, tex.TargetHeight);
        Rect clipped = region.Intersect(covered);
        if (clipped.Width <= 0 || clipped.Height <= 0)
            return;

        double sx = tex.SourceWidth / (double)tex.TargetWidth;
        double sy = tex.SourceHeight / (double)tex.TargetHeight;
        var src = new Rect(tex.SourceX + (clipped.X - tex.TargetX) * sx, tex.SourceY + (clipped.Y - tex.TargetY) * sy,
                           clipped.Width * sx, clipped.Height * sy);
        double kx = dest.Width / region.Width, ky = dest.Height / region.Height;
        var dst = new Rect(dest.X + (clipped.X - region.X) * kx, dest.Y + (clipped.Y - region.Y) * ky,
                           clipped.Width * kx, clipped.Height * ky);
        ctx.DrawImage(bmp, src, dst);
    }

    private void DrawTile(DrawingContext ctx, Tile tile)
    {
        UndertaleTexturePageItem tex = tile.spriteMode ? Frame(tile.SpriteDefinition, 0) : tile.BackgroundDefinition?.Texture;
        if (tex is null)
            return;
        var region = new Rect(tile.SourceX, tile.SourceY, tile.Width, tile.Height);
        var dest = new Rect(tile.X, tile.Y, tile.Width * Math.Abs(tile.ScaleX), tile.Height * Math.Abs(tile.ScaleY));
        if (tile.ScaleX < 0 || tile.ScaleY < 0)
        {
            // Mirror around the tile's position
            var m = Matrix.CreateTranslation(-tile.X, -tile.Y)
                  * Matrix.CreateScale(Math.Sign(tile.ScaleX), Math.Sign(tile.ScaleY))
                  * Matrix.CreateTranslation(tile.X, tile.Y);
            using (ctx.PushTransform(m))
                DrawItemRegion(ctx, tex, region, dest);
        }
        else
            DrawItemRegion(ctx, tex, region, dest);
    }

    private void DrawRoomBackground(DrawingContext ctx, Background bg)
    {
        UndertaleTexturePageItem tex = bg.BackgroundDefinition?.Texture;
        if (tex is null)
            return;
        double w = tex.BoundingWidth, h = tex.BoundingHeight;
        if (w <= 0 || h <= 0)
            return;
        double scaleX = bg.Stretch ? room.Width / w : 1;
        double scaleY = bg.Stretch ? room.Height / h : 1;
        DrawTiledImage(ctx, tex, bg.X, bg.Y, w * scaleX, h * scaleY, scaleX, scaleY, bg.TiledHorizontally, bg.TiledVertically, 1);
    }

    private void DrawLayerBackground(DrawingContext ctx, Layer layer, Layer.LayerBackgroundData bg)
    {
        if (!bg.Visible)
            return;
        if (bg.Sprite is null)
        {
            // Solid color layer
            ctx.FillRectangle(new SolidColorBrush(GmColor(bg.Color)), new Rect(0, 0, room.Width, room.Height));
            return;
        }
        UndertaleTexturePageItem tex = Frame(bg.Sprite, (int)bg.FirstFrame);
        if (tex is null)
            return;
        double w = tex.BoundingWidth, h = tex.BoundingHeight;
        if (w <= 0 || h <= 0)
            return;
        double scaleX = bg.Stretch ? room.Width / w : 1;
        double scaleY = bg.Stretch ? room.Height / h : 1;
        DrawTiledImage(ctx, tex, layer.XOffset, layer.YOffset, w * scaleX, h * scaleY, scaleX, scaleY,
                       bg.TiledHorizontally, bg.TiledVertically, GmAlpha(bg.Color) is var a && a > 0 ? a : 1);
    }

    private void DrawTiledImage(DrawingContext ctx, UndertaleTexturePageItem tex, double x, double y, double w, double h,
                                double scaleX, double scaleY, bool tileX, bool tileY, double opacity)
    {
        double startX = tileX ? x - Math.Ceiling(x / w) * w : x;
        double startY = tileY ? y - Math.Ceiling(y / h) * h : y;
        double endX = tileX ? room.Width : startX + 1;
        double endY = tileY ? room.Height : startY + 1;
        int count = 0;
        for (double py = startY; py < endY; py += h)
        {
            for (double px = startX; px < endX; px += w)
            {
                TextureCache.DrawItem(ctx, tex, px, py, scaleX, scaleY, opacity);
                if (++count > 10000)
                    return;
            }
        }
    }

    private static void DrawTileLayer(DrawingContext ctx, Layer layer, Layer.LayerTilesData data)
    {
        UndertaleBackground tileset = data.Background;
        UndertaleTexturePageItem tex = tileset?.Texture;
        if (tex is null || data.TileData is null || tileset.GMS2TileColumns == 0)
            return;

        int w = (int)tileset.GMS2TileWidth, h = (int)tileset.GMS2TileHeight;
        int bx = (int)tileset.GMS2OutputBorderX, by = (int)tileset.GMS2OutputBorderY;
        uint columns = tileset.GMS2TileColumns;

        for (int ty = 0; ty < data.TileData.Length; ty++)
        {
            uint[] row = data.TileData[ty];
            if (row is null)
                continue;
            for (int tx = 0; tx < row.Length; tx++)
            {
                uint raw = row[tx];
                uint id = raw & TileIndexMask;
                if (id == 0)
                    continue;
                var region = new Rect((id % columns) * (w + 2 * bx) + bx, (id / columns) * (h + 2 * by) + by, w, h);
                double dx = layer.XOffset + tx * w, dy = layer.YOffset + ty * h;
                var dest = new Rect(dx, dy, w, h);

                uint flags = raw >> 28;
                if (flags == 0)
                {
                    DrawItemRegion(ctx, tex, region, dest);
                    continue;
                }
                // bit 0: mirror (flip X), bit 1: flip (Y), bit 2: rotate 90° clockwise; flips happen before rotation
                double cx = dx + w / 2.0, cy = dy + h / 2.0;
                var m = Matrix.CreateTranslation(-cx, -cy)
                      * Matrix.CreateScale((flags & 1) != 0 ? -1 : 1, (flags & 2) != 0 ? -1 : 1)
                      * ((flags & 4) != 0 ? Matrix.CreateRotation(Math.PI / 2) : Matrix.Identity)
                      * Matrix.CreateTranslation(cx, cy);
                using (ctx.PushTransform(m))
                    DrawItemRegion(ctx, tex, region, dest);
            }
        }
    }

    #endregion

    #region Hit testing and dragging

    private static Rect? SpriteBounds(UndertaleSprite sprite, double x, double y, double scaleX, double scaleY)
    {
        if (sprite is null)
            return new Rect(x - 8, y - 8, 16, 16);
        double w = sprite.Width * scaleX, h = sprite.Height * scaleY;
        var r = new Rect(x - sprite.OriginX * scaleX, y - sprite.OriginY * scaleY, w, h);
        return r.Normalize();
    }

    private static Rect? GetBounds(object obj) => obj switch
    {
        GameObject g => SpriteBounds(g.ObjectDefinition?.Sprite, g.X, g.Y, g.ScaleX, g.ScaleY),
        SpriteInstance s => SpriteBounds(s.Sprite, s.X, s.Y, s.ScaleX, s.ScaleY),
        Tile t => new Rect(t.X, t.Y, t.Width * t.ScaleX, t.Height * t.ScaleY).Normalize(),
        _ => null
    };

    /// <summary>Topmost selectable object at a room position.</summary>
    private object HitTest(Point p)
    {
        var candidates = new List<object>();
        if (room.Layers is { Count: > 0 })
        {
            // Front-most layers have the lowest depth
            foreach (var layer in room.Layers.Where(l => l.IsVisible).OrderBy(l => l.LayerDepth))
            {
                if (layer.Data is Layer.LayerInstancesData insts && ShowInstances)
                    candidates.AddRange(insts.Instances.AsEnumerable().Reverse());
                else if (layer.Data is Layer.LayerAssetsData assets && ShowTiles)
                {
                    if (assets.Sprites is not null)
                        candidates.AddRange(assets.Sprites.AsEnumerable().Reverse());
                    if (assets.LegacyTiles is not null)
                        candidates.AddRange(assets.LegacyTiles.OrderBy(t => t.TileDepth));
                }
            }
        }
        else
        {
            var drawables = new List<(int Depth, object Obj)>();
            if (ShowInstances)
                drawables.AddRange(room.GameObjects.Select(g => (g.ObjectDefinition?.Depth ?? 0, (object)g)));
            if (ShowTiles)
                drawables.AddRange(room.Tiles.Select(t => (t.TileDepth, (object)t)));
            // Drawn last (lowest depth) is on top; OrderBy is stable, so reverse within equal depth
            candidates.AddRange(drawables.Select((d, i) => (d.Depth, d.Obj, i))
                                         .OrderBy(d => d.Depth).ThenByDescending(d => d.i)
                                         .Select(d => d.Obj));
        }
        return candidates.FirstOrDefault(c => GetBounds(c) is Rect r && r.Contains(p));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Point p = e.GetPosition(this) / zoom;
        Selected = HitTest(p);
        if (selected is not null)
        {
            dragStart = p;
            dragOrigin = GetPosition(selected);
            dragging = true;
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!dragging || selected is null)
            return;
        Point p = e.GetPosition(this) / zoom;
        int nx = dragOrigin.X + (int)Math.Round(p.X - dragStart.X);
        int ny = dragOrigin.Y + (int)Math.Round(p.Y - dragStart.Y);
        if (SnapToGrid && GridSize > 1)
        {
            nx = (int)Math.Round(nx / (double)GridSize) * GridSize;
            ny = (int)Math.Round(ny / (double)GridSize) * GridSize;
        }
        if ((nx, ny) != GetPosition(selected))
        {
            SetPosition(selected, nx, ny);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragging && selected is not null && GetPosition(selected) != dragOrigin)
            ObjectMoved?.Invoke(selected);
        dragging = false;
        e.Pointer.Capture(null);
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

    private static (int X, int Y) GetPosition(object obj) => obj switch
    {
        GameObject g => (g.X, g.Y),
        SpriteInstance s => (s.X, s.Y),
        Tile t => (t.X, t.Y),
        _ => (0, 0)
    };

    private static void SetPosition(object obj, int x, int y)
    {
        switch (obj)
        {
            case GameObject g: g.X = x; g.Y = y; break;
            case SpriteInstance s: s.X = x; s.Y = y; break;
            case Tile t: t.X = x; t.Y = y; break;
        }
    }

    #endregion
}
