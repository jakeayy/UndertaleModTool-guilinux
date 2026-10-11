using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using UndertaleModToolAvalonia.Controls;
using UndertaleModToolAvalonia.Dialogs;

namespace UndertaleModToolAvalonia.Editors;

/// <summary>
/// An editor that buffers edits and needs to apply them (e.g. compile code) before saving or closing.
/// </summary>
public interface ICommittableEditor
{
    bool HasUnsavedChanges { get; }

    /// <summary>Applies pending edits. Returns <see langword="false"/> if that failed.</summary>
    Task<bool> CommitAsync();
}

/// <summary>
/// Picks the right editor control for an object from the data file.
/// </summary>
public static class EditorFactory
{
    public static Control Create(MainWindow mw, object obj)
    {
        try
        {
            return obj switch
            {
                null => Welcome(),
                UndertaleCode code => new CodeEditorView(mw, code),
                UndertaleSprite sprite => new SpriteEditor(mw, sprite),
                UndertaleTexturePageItem item => ImageEditor(mw, item, () => item, item.ToString()),
                UndertaleBackground bg => ImageEditor(mw, bg, () => bg.Texture, bg.Name?.Content),
                UndertaleFont font => FontEditor(mw, font),
                UndertaleEmbeddedTexture tex => new EmbeddedTextureEditor(mw, tex),
                UndertaleSound sound => new SoundEditor(mw, sound),
                UndertaleEmbeddedAudio audio => new SoundEditor(mw, audio),
                UndertaleRoom room => new RoomEditor(mw, room),
                StandaloneListView list => Shell(mw, list.Title, "List", null, ListGrid(mw, list)),
                _ => Shell(mw, ResourceInfo.DisplayName(obj), ResourceInfo.TypeName(obj), null, new PropertyGrid(mw, obj))
            };
        }
        catch (Exception e)
        {
            return new SelectableTextBlock
            {
                Text = $"Failed to create an editor for this object:\n\n{e}",
                Margin = new Thickness(12),
                TextWrapping = TextWrapping.Wrap
            };
        }
    }

    public static Control Welcome()
    {
        return new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Welcome to UndertaleModTool!", Classes = { "header" }, FontSize = 22 },
                new TextBlock { Text = "Open a data file (data.win, game.unx, game.ios, ...) with File → Open, or drop one onto this window.", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Click an item in the tree on the left to edit it. Middle-click or Ctrl+click opens it in a new tab.", TextWrapping = TextWrapping.Wrap, Classes = { "dim" } }
            }
        };
    }

    private static Control ListGrid(MainWindow mw, StandaloneListView list)
    {
        // Show the list's entries with the generic editor, which already handles lists
        return new PropertyGrid(mw, new ListHolder { Entries = list.Entries });
    }

    /// <summary>Lets the property grid render a bare list as an expandable property.</summary>
    private sealed class ListHolder
    {
        public System.Collections.IList Entries { get; init; }
    }

    /// <summary>
    /// Standard editor layout: header, optional toolbar, then scrollable content.
    /// </summary>
    public static Control Shell(MainWindow mw, string title, string typeName, Control toolbar, Control content, bool scroll = true)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(new SelectableTextBlock { Text = title ?? "(unnamed)", Classes = { "header" } });
        header.Children.Add(new TextBlock { Text = typeName, Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Bottom });

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        if (toolbar is not null)
        {
            toolbar.Margin = new Thickness(0, 0, 0, 8);
            DockPanel.SetDock(toolbar, Dock.Top);
            root.Children.Add(toolbar);
        }
        root.Children.Add(scroll ? new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 12, 0) } : content);
        return root;
    }

    /// <summary>
    /// A vertical split: preview on top, property grid below.
    /// </summary>
    public static Grid PreviewSplit(Control preview, Control grid)
    {
        var split = new Grid { RowDefinitions = new RowDefinitions("2*,Auto,3*") };
        Grid.SetRow(preview, 0);
        var splitter = new GridSplitter { Height = 6, ResizeDirection = GridResizeDirection.Rows, Background = Brushes.Transparent };
        Grid.SetRow(splitter, 1);
        var gridScroll = new ScrollViewer { Content = grid, Padding = new Thickness(0, 6, 12, 0), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(gridScroll, 2);
        split.Children.Add(preview);
        split.Children.Add(splitter);
        split.Children.Add(gridScroll);
        return split;
    }

    /// <summary>
    /// A scrollable, zoomable texture preview with a zoom selector.
    /// </summary>
    /// <summary>
    /// Picks an initial zoom so small images (e.g. 20x30 sprites) are comfortably visible.
    /// </summary>
    public static void AutoZoom(TextureView view, UndertaleTexturePageItem item)
    {
        if (item is null || view.Tag is not ComboBox zoom)
            return;
        int size = Math.Max(Math.Max((int)item.BoundingWidth, (int)item.TargetWidth), Math.Max((int)item.BoundingHeight, (int)item.TargetHeight));
        int index = 2; // 100%
        while (index < 5 && size > 0 && size * new[] { 0.25, 0.5, 1, 2, 4, 8 }[index + 1] <= 256)
            index++;
        zoom.SelectedIndex = index;
    }

    /// <summary>A small horizontal gap for toolbars.</summary>
    public static Control Gap() => new Border { Width = 10 };

    public static (Control Container, TextureView View, StackPanel Toolbar) TexturePreview()
    {
        var view = new TextureView { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var scroll = new ScrollViewer
        {
            Content = view,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        var zoom = new ComboBox { ItemsSource = new[] { "25%", "50%", "100%", "200%", "400%", "800%" }, SelectedIndex = 2, MinWidth = 90 };
        zoom.SelectionChanged += (_, _) => view.Zoom = new[] { 0.25, 0.5, 1, 2, 4, 8 }[Math.Max(0, zoom.SelectedIndex)];
        ToolTip.SetTip(zoom, "Zoom (or Ctrl+mouse wheel over the image)");
        view.Tag = zoom;
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { zoom } };

        var container = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        toolbar.Margin = new Thickness(0, 0, 0, 6);
        container.Children.Add(toolbar);
        container.Children.Add(new Border { Child = scroll, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) });
        return (container, view, toolbar);
    }

    /// <summary>
    /// Editor for objects backed by a single texture page item (texture page items, backgrounds).
    /// </summary>
    private static Control ImageEditor(MainWindow mw, object obj, Func<UndertaleTexturePageItem> getItem, string title)
    {
        var (preview, view, toolbar) = TexturePreview();
        view.Item = getItem();
        AutoZoom(view, view.Item);
        AddImageButtons(mw, toolbar, getItem, title, () => view.Refresh());

        var grid = new PropertyGrid(mw, obj);
        grid.ValueChanged += () => { view.Item = getItem(); };
        return Shell(mw, title, ResourceInfo.TypeName(obj), null, PreviewSplit(preview, grid), scroll: false);
    }

    /// <summary>
    /// Adds Export/Import buttons for a texture page item.
    /// </summary>
    public static void AddImageButtons(MainWindow mw, Panel toolbar, Func<UndertaleTexturePageItem> getItem, string name, Action refresh)
    {
        var export = new Button { Content = "Export PNG..." };
        export.Click += async (_, _) =>
        {
            UndertaleTexturePageItem item = getItem();
            if (item is null)
                return;
            string path = await DialogHelper.SaveFile(mw, "Export image", "PNG files (*.png)|*.png", "png", $"{name}.png");
            if (path is null)
                return;
            try
            {
                using TextureWorker worker = new();
                worker.ExportAsPNG(item, path, null, includePadding: true);
            }
            catch (Exception e)
            {
                await mw.ShowError($"Failed to export:\n{e.Message}");
            }
        };

        var import = new Button { Content = "Import..." };
        ToolTip.SetTip(import, "Replace this image's pixels on its texture page");
        import.Click += async (_, _) =>
        {
            UndertaleTexturePageItem item = getItem();
            if (item is null)
                return;
            string path = await DialogHelper.OpenFile(mw, "Import image", "Images (*.png;*.gif;*.bmp;*.jpg)|*.png;*.gif;*.bmp;*.jpg;*.jpeg|All files|*");
            if (path is null)
                return;
            try
            {
                using var image = TextureWorker.ReadBGRAImageFromFile(path);
                item.ReplaceTexture(image);
                mw.MarkDirty();
                refresh();
            }
            catch (Exception e)
            {
                await mw.ShowError($"Failed to import:\n{e.Message}");
            }
        };
        toolbar.Children.Add(export);
        toolbar.Children.Add(import);
    }

    private static Control FontEditor(MainWindow mw, UndertaleFont font)
    {
        var (preview, view, toolbar) = TexturePreview();
        view.Item = font.Texture;
        var glyphPen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x00, 0xC8, 0xFF)), 0.5);
        view.Overlay = ctx =>
        {
            if (font.Glyphs is null)
                return;
            foreach (var g in font.Glyphs)
            {
                if (g is not null)
                    ctx.DrawRectangle(null, glyphPen, new Rect(g.SourceX, g.SourceY, g.SourceWidth, g.SourceHeight));
            }
        };
        AddImageButtons(mw, toolbar, () => font.Texture, font.Name?.Content, () => view.Refresh());

        var grid = new PropertyGrid(mw, font);
        grid.ValueChanged += () => { view.Item = font.Texture; };
        return Shell(mw, font.Name?.Content, "Font", null, PreviewSplit(preview, grid), scroll: false);
    }
}

/// <summary>
/// Sprite editor: frame browser with playback, origin marker, and frame import/export.
/// </summary>
public class SpriteEditor : DockPanel
{
    private readonly MainWindow mw;
    private readonly UndertaleSprite sprite;
    private readonly TextureView view;
    private readonly TextBlock frameLabel;
    private readonly DispatcherTimer timer;
    private int frame;

    public SpriteEditor(MainWindow mw, UndertaleSprite sprite)
    {
        this.mw = mw;
        this.sprite = sprite;

        var (preview, textureView, toolbar) = EditorFactory.TexturePreview();
        view = textureView;
        var originPen = new Pen(Brushes.Red, 0.5);
        view.Overlay = ctx =>
        {
            double ox = sprite.OriginX, oy = sprite.OriginY;
            ctx.DrawLine(originPen, new Point(ox - 4, oy), new Point(ox + 4, oy));
            ctx.DrawLine(originPen, new Point(ox, oy - 4), new Point(ox, oy + 4));
        };

        var prev = new Button { Content = "◀" };
        var next = new Button { Content = "▶" };
        var play = new ToggleButton { Content = "Play" };
        frameLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 70, TextAlignment = TextAlignment.Center };
        prev.Click += (_, _) => SetFrame(frame - 1);
        next.Click += (_, _) => SetFrame(frame + 1);

        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 15) };
        timer.Tick += (_, _) => SetFrame(frame + 1);
        play.IsCheckedChanged += (_, _) =>
        {
            if (play.IsChecked == true)
            {
                // GMS2 sprites may specify frames per second
                double fps = sprite.GMS2PlaybackSpeedType == 0 && sprite.GMS2PlaybackSpeed > 0 ? sprite.GMS2PlaybackSpeed : 15;
                timer.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(fps, 1, 120));
                timer.Start();
            }
            else
                timer.Stop();
        };
        DetachedFromVisualTree += (_, _) => timer.Stop();

        toolbar.Children.Add(EditorFactory.Gap());
        toolbar.Children.Add(prev);
        toolbar.Children.Add(frameLabel);
        toolbar.Children.Add(next);
        toolbar.Children.Add(play);
        toolbar.Children.Add(EditorFactory.Gap());
        EditorFactory.AddImageButtons(mw, toolbar, CurrentFrame, $"{sprite.Name?.Content}_{frame}", () => view.Refresh());

        var exportAll = new Button { Content = "Export all frames..." };
        exportAll.Click += async (_, _) => await ExportAllFrames();
        toolbar.Children.Add(exportAll);

        var grid = new PropertyGrid(mw, sprite);
        grid.ValueChanged += () => SetFrame(frame);

        Children.Add(EditorFactory.Shell(mw, sprite.Name?.Content, "Sprite", null, EditorFactory.PreviewSplit(preview, grid), scroll: false));
        SetFrame(0);
        EditorFactory.AutoZoom(view, CurrentFrame());
    }

    private UndertaleTexturePageItem CurrentFrame()
    {
        if (sprite.Textures is not { Count: > 0 })
            return null;
        return sprite.Textures[Math.Clamp(frame, 0, sprite.Textures.Count - 1)]?.Texture;
    }

    private void SetFrame(int f)
    {
        int count = sprite.Textures?.Count ?? 0;
        frame = count == 0 ? 0 : ((f % count) + count) % count;
        frameLabel.Text = count == 0 ? "no frames" : $"{frame + 1} / {count}";
        view.Item = CurrentFrame();
    }

    private async Task ExportAllFrames()
    {
        string folder = await DialogHelper.OpenFolder(mw, "Export frames to folder");
        if (folder is null)
            return;
        try
        {
            using TextureWorker worker = new();
            for (int i = 0; i < sprite.Textures.Count; i++)
            {
                if (sprite.Textures[i]?.Texture is UndertaleTexturePageItem item)
                    worker.ExportAsPNG(item, Path.Join(folder, $"{sprite.Name?.Content}_{i}.png"), null, includePadding: true);
            }
            await mw.ShowMessage($"Exported {sprite.Textures.Count} frame(s).");
        }
        catch (Exception e)
        {
            await mw.ShowError($"Failed to export frames:\n{e.Message}");
        }
    }
}

/// <summary>
/// Editor for whole texture pages, with PNG export/import.
/// </summary>
public class EmbeddedTextureEditor : DockPanel
{
    public EmbeddedTextureEditor(MainWindow mw, UndertaleEmbeddedTexture tex)
    {
        var (preview, view, toolbar) = EditorFactory.TexturePreview();
        view.Page = tex;

        GMImage image = tex.TextureData?.Image;
        var info = new TextBlock
        {
            Text = image is null ? "No texture data (external or not loaded)." : $"{image.Width}×{image.Height}, {image.Format}",
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "dim" }
        };

        var export = new Button { Content = "Export PNG..." };
        export.Click += async (_, _) =>
        {
            if (tex.TextureData?.Image is null)
                return;
            string path = await DialogHelper.SaveFile(mw, "Export texture", "PNG files (*.png)|*.png", "png", $"{tex.Name?.Content ?? "texture"}.png");
            if (path is null)
                return;
            try
            {
                using FileStream fs = new(path, FileMode.Create);
                tex.TextureData.Image.SavePng(fs);
            }
            catch (Exception e)
            {
                await mw.ShowError($"Failed to export:\n{e.Message}");
            }
        };

        var import = new Button { Content = "Import..." };
        import.Click += async (_, _) =>
        {
            if (tex.TextureData?.Image is not GMImage old)
                return;
            string path = await DialogHelper.OpenFile(mw, "Import texture", "Images (*.png;*.gif;*.bmp;*.jpg)|*.png;*.gif;*.bmp;*.jpg;*.jpeg|All files|*");
            if (path is null)
                return;
            try
            {
                using var img = TextureWorker.ReadBGRAImageFromFile(path);
                if ((img.Width != old.Width || img.Height != old.Height) &&
                    !await mw.ShowQuestion($"The new image is {img.Width}×{img.Height}, but the current texture is {old.Width}×{old.Height}. " +
                                           "Texture page items will keep their coordinates. Continue?"))
                    return;
                tex.TextureData.Image = GMImage.FromMagickImage(img).ConvertToFormat(old.Format);
                info.Text = $"{tex.TextureData.Image.Width}×{tex.TextureData.Image.Height}, {tex.TextureData.Image.Format}";
                view.Refresh();
                mw.MarkDirty();
            }
            catch (Exception e)
            {
                await mw.ShowError($"Failed to import:\n{e.Message}");
            }
        };
        toolbar.Children.Add(export);
        toolbar.Children.Add(import);
        toolbar.Children.Add(info);

        Children.Add(EditorFactory.Shell(mw, tex.Name?.Content, "Embedded texture", null,
                                         EditorFactory.PreviewSplit(preview, new PropertyGrid(mw, tex)), scroll: false));
    }
}

/// <summary>
/// Editor for sounds and embedded audio, with playback through an external player.
/// </summary>
public class SoundEditor : DockPanel
{
    private readonly MainWindow mw;
    private readonly UndertaleSound sound;
    private readonly UndertaleEmbeddedAudio audio;

    public SoundEditor(MainWindow mw, UndertaleSound sound) : this(mw, sound, null) { }
    public SoundEditor(MainWindow mw, UndertaleEmbeddedAudio audio) : this(mw, null, audio) { }

    private SoundEditor(MainWindow mw, UndertaleSound sound, UndertaleEmbeddedAudio audio)
    {
        this.mw = mw;
        this.sound = sound;
        this.audio = audio;

        var play = new Button { Content = "▶ Play" };
        var stop = new Button { Content = "■ Stop" };
        var export = new Button { Content = "Export..." };
        var import = new Button { Content = "Import..." };
        ToolTip.SetTip(play, "Plays using ffplay, mpv, pw-play, paplay or aplay if installed; otherwise the default application");
        play.Click += async (_, _) => await Play();
        stop.Click += (_, _) => AudioPlayer.Stop();
        export.Click += async (_, _) => await Export();
        import.Click += async (_, _) => await Import();
        DetachedFromVisualTree += (_, _) => AudioPlayer.Stop();

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { play, stop, export, import } };
        object target = (object)sound ?? audio;
        string title = sound?.Name?.Content ?? audio?.Name?.Content ?? "Embedded audio";
        Children.Add(EditorFactory.Shell(mw, title, ResourceInfo.TypeName(target), toolbar, new PropertyGrid(mw, target)));
    }

    /// <summary>
    /// Resolves the audio bytes, from the embedded audio, an audio group file, or an external file.
    /// Returns (data, null) or (null, external file path).
    /// </summary>
    private (byte[] Data, string ExternalPath) Resolve()
    {
        if (audio is not null)
            return (audio.Data, null);

        string dir = Path.GetDirectoryName(mw.FilePath) ?? "";
        bool embedded = sound.Flags.HasFlag(UndertaleSound.AudioEntryFlags.IsEmbedded);
        bool compressed = sound.Flags.HasFlag(UndertaleSound.AudioEntryFlags.IsCompressed);
        if (!embedded && !compressed)
        {
            string file = sound.File?.Content ?? throw new InvalidDataException("Sound has no file name.");
            if (!file.Contains('.'))
                file += ".ogg";
            string path = Paths.JoinVerifyWithinDirectory(dir, file);
            if (!File.Exists(path))
                throw new FileNotFoundException($"External audio file not found: {path}");
            return (null, path);
        }

        if (sound.GroupID != 0 && sound.AudioID != -1)
        {
            string relative = sound.AudioGroup is UndertaleAudioGroup { Path.Content: string custom } ? custom : $"audiogroup{sound.GroupID}.dat";
            string path = Paths.JoinVerifyWithinDirectory(dir, relative);
            UndertaleData group = mw.LoadAudioGroup(path);
            return (group.EmbeddedAudio[sound.AudioID].Data, null);
        }

        return (sound.AudioFile?.Data ?? throw new InvalidDataException("Sound has no audio data."), null);
    }

    private async Task Play()
    {
        try
        {
            var (data, external) = Resolve();
            if (external is not null)
                AudioPlayer.PlayFile(external);
            else
                AudioPlayer.Play(data);
        }
        catch (Exception e)
        {
            await mw.ShowError($"Failed to play audio:\n{e.Message}", "Audio failure");
        }
    }

    private async Task Export()
    {
        try
        {
            var (data, external) = Resolve();
            string ext = external is not null ? Path.GetExtension(external) : AudioPlayer.DetectExtension(data);
            string name = (sound?.Name?.Content ?? audio?.Name?.Content ?? "audio") + ext;
            string path = await DialogHelper.SaveFile(mw, "Export audio", $"Audio (*{ext})|*{ext}|All files|*", ext, name);
            if (path is null)
                return;
            if (external is not null)
                File.Copy(external, path, true);
            else
                await File.WriteAllBytesAsync(path, data);
        }
        catch (Exception e)
        {
            await mw.ShowError($"Failed to export audio:\n{e.Message}");
        }
    }

    private async Task Import()
    {
        UndertaleEmbeddedAudio target = audio ?? (sound.GroupID == 0 ? sound.AudioFile : null);
        if (target is null)
        {
            await mw.ShowWarning("Only audio embedded in the main data file can be replaced here. " +
                                 "For external files or audio groups, replace the file on disk or use an import script.");
            return;
        }
        string path = await DialogHelper.OpenFile(mw, "Import audio", "Audio (*.wav;*.ogg)|*.wav;*.ogg|All files|*");
        if (path is null)
            return;
        try
        {
            target.Data = await File.ReadAllBytesAsync(path);
            mw.MarkDirty();
            mw.RefreshCurrentTab();
        }
        catch (Exception e)
        {
            await mw.ShowError($"Failed to import audio:\n{e.Message}");
        }
    }
}

/// <summary>
/// Room editor: rendered preview with selection/dragging, plus property panels.
/// </summary>
public class RoomEditor : DockPanel
{
    private readonly MainWindow mw;
    private readonly RoomView view;
    private readonly ContentControl selectionHost;
    private readonly TabControl sideTabs;
    private readonly TabItem selectionTab;

    public RoomEditor(MainWindow mw, UndertaleRoom room)
    {
        this.mw = mw;
        view = new RoomView(room) { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };

        // Toolbar
        var zoom = new ComboBox { ItemsSource = new[] { "25%", "50%", "100%", "200%", "400%" }, SelectedIndex = 2, MinWidth = 90 };
        zoom.SelectionChanged += (_, _) => view.Zoom = new[] { 0.25, 0.5, 1, 2, 4 }[Math.Max(0, zoom.SelectedIndex)];
        ToolTip.SetTip(zoom, "Zoom (or Ctrl+mouse wheel over the room)");
        CheckBox Toggle(string text, bool initial, Action<bool> set)
        {
            var cb = new CheckBox { Content = text, IsChecked = initial };
            cb.IsCheckedChanged += (_, _) => { set(cb.IsChecked == true); view.Refresh(); };
            return cb;
        }
        var gridSize = new NumericUpDown { Value = 16, Minimum = 2, Maximum = 512, Increment = 1, Width = 110, FormatString = "0" };
        gridSize.ValueChanged += (_, _) => { view.GridSize = (int)(gridSize.Value ?? 16); view.Refresh(); };
        var toolbar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                zoom,
                Toggle("Backgrounds", true, v => view.ShowBackgrounds = v),
                Toggle("Tiles/assets", true, v => view.ShowTiles = v),
                Toggle("Instances", true, v => view.ShowInstances = v),
                Toggle("Grid", false, v => view.ShowGrid = v),
                Toggle("Snap", false, v => view.SnapToGrid = v),
                gridSize
            }
        };
        foreach (var c in toolbar.Children)
            c.Margin = new Thickness(0, 0, 10, 0);

        var canvasScroll = new ScrollViewer
        {
            Content = view,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        var canvasPane = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        toolbar.Margin = new Thickness(0, 0, 0, 6);
        canvasPane.Children.Add(toolbar);
        canvasPane.Children.Add(new Border { Child = canvasScroll, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) });

        // Side panel: selected object and room properties
        selectionHost = new ContentControl
        {
            Content = new TextBlock { Text = "Click an instance, tile or sprite in the room to select it. Drag to move it.", TextWrapping = TextWrapping.Wrap, Classes = { "dim" } }
        };
        var roomGrid = new PropertyGrid(mw, room);
        roomGrid.ValueChanged += () => { view.InvalidateMeasure(); view.Refresh(); };
        selectionTab = new TabItem { Header = "Selection", Content = new ScrollViewer { Content = selectionHost, Padding = new Thickness(0, 4, 12, 0) } };
        sideTabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Room", Content = new ScrollViewer { Content = roomGrid, Padding = new Thickness(0, 4, 12, 0) } },
                selectionTab
            }
        };

        view.SelectionChanged += ShowSelection;
        view.ObjectMoved += obj => { mw.MarkDirty(); ShowSelection(obj); };

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,380") };
        Grid.SetColumn(canvasPane, 0);
        var splitter = new GridSplitter { Width = 6, ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(sideTabs, 2);
        split.Children.Add(canvasPane);
        split.Children.Add(splitter);
        split.Children.Add(sideTabs);

        Children.Add(EditorFactory.Shell(mw, room.Name?.Content, "Room", null, split, scroll: false));
    }

    private void ShowSelection(object obj)
    {
        if (obj is null)
        {
            selectionHost.Content = new TextBlock { Text = "Nothing selected.", Classes = { "dim" } };
            return;
        }
        var grid = new PropertyGrid(mw, obj);
        grid.ValueChanged += () => view.Refresh();
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = $"{ResourceInfo.TypeName(obj)}", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(grid);
        selectionHost.Content = panel;
        sideTabs.SelectedItem = selectionTab;
    }
}
