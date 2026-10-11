using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace UndertaleModToolAvalonia.Dialogs;

public enum MessageKind
{
    Info,
    Warning,
    Error,
    Question
}

/// <summary>
/// Simple modal dialogs built in code (message boxes, text input/output) and file picker helpers.
/// All methods must be called on the UI thread.
/// </summary>
public static class DialogHelper
{
    private static Window CreateDialogWindow(string title, double width = 480)
    {
        return new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 700,
            CanResize = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
    }

    private static StackPanel ButtonRow(params Button[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0)
        };
        foreach (var b in buttons)
        {
            b.MinWidth = 80;
            b.HorizontalContentAlignment = HorizontalAlignment.Center;
            row.Children.Add(b);
        }
        return row;
    }

    private static string KindGlyph(MessageKind kind) => kind switch
    {
        MessageKind.Warning => "⚠",
        MessageKind.Error => "⛔",
        MessageKind.Question => "?",
        _ => "ℹ"
    };

    private static async Task ShowOrRun(Window dialog, Window owner)
    {
        if (owner is not null && owner.IsVisible)
            await dialog.ShowDialog(owner);
        else
        {
            var tcs = new TaskCompletionSource();
            dialog.Closed += (_, _) => tcs.TrySetResult();
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Show();
            await tcs.Task;
        }
    }

    /// <summary>
    /// Shows a message box. For <see cref="MessageKind.Question"/>, returns whether "Yes" was clicked.
    /// </summary>
    public static async Task<bool> ShowMessage(Window owner, string title, string message, MessageKind kind = MessageKind.Info)
    {
        var dialog = CreateDialogWindow(title);
        bool result = false;

        var text = new SelectableTextBlock
        {
            Text = message ?? "",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        var glyph = new TextBlock
        {
            Text = KindGlyph(kind),
            FontSize = 28,
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = kind switch
            {
                MessageKind.Error => Brushes.IndianRed,
                MessageKind.Warning => Brushes.Orange,
                _ => Brushes.SteelBlue
            }
        };
        var body = new DockPanel();
        DockPanel.SetDock(glyph, Dock.Left);
        body.Children.Add(glyph);
        body.Children.Add(new ScrollViewer { Content = text, MaxHeight = 520 });

        StackPanel buttons;
        if (kind == MessageKind.Question)
        {
            var yes = new Button { Content = "_Yes", IsDefault = true };
            var no = new Button { Content = "_No", IsCancel = true };
            yes.Click += (_, _) => { result = true; dialog.Close(); };
            no.Click += (_, _) => dialog.Close();
            buttons = ButtonRow(yes, no);
        }
        else
        {
            var copy = new Button { Content = "Copy" };
            copy.Click += async (_, _) =>
            {
                if (dialog.Clipboard is { } cb)
                    await cb.SetTextAsync(message ?? "");
            };
            var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true };
            ok.Click += (_, _) => { result = true; dialog.Close(); };
            buttons = ButtonRow(copy, ok);
        }

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(body);
        dialog.Content = root;

        await ShowOrRun(dialog, owner);
        return result;
    }

    /// <summary>
    /// Prompts for text input. Returns <see langword="null"/> if cancelled.
    /// </summary>
    public static async Task<string> ShowTextInput(Window owner, string title, string label, string defaultValue,
                                                    bool multiline = false, string submitText = "OK",
                                                    string cancelText = "Cancel", bool preventClose = false)
    {
        var dialog = CreateDialogWindow(title, multiline ? 640 : 440);
        string result = null;
        bool submitted = false;

        var input = new TextBox
        {
            Text = defaultValue ?? "",
            AcceptsReturn = multiline,
            AcceptsTab = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 240 : 0,
            MaxHeight = multiline ? 480 : double.PositiveInfinity
        };
        if (multiline)
            input.FontFamily = new FontFamily("monospace");

        var submit = new Button { Content = string.IsNullOrEmpty(submitText) ? "OK" : submitText, IsDefault = !multiline };
        var cancel = new Button { Content = string.IsNullOrEmpty(cancelText) ? "Cancel" : cancelText, IsCancel = true };
        submit.Click += (_, _) => { submitted = true; result = input.Text; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();

        if (preventClose)
        {
            dialog.Closing += (_, e) =>
            {
                if (!submitted && result is null && !e.IsProgrammatic)
                    e.Cancel = true;
            };
        }

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        if (!string.IsNullOrEmpty(label))
            root.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(input);
        root.Children.Add(ButtonRow(submit, cancel));
        dialog.Content = root;
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };

        await ShowOrRun(dialog, owner);
        return submitted ? result : null;
    }

    /// <summary>
    /// Shows read-only text output in a resizable window.
    /// </summary>
    public static Task ShowTextOutput(Window owner, string title, string label, string text, bool modal = true)
    {
        var window = new Window
        {
            Title = title,
            Width = 720,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var box = new TextBox
        {
            Text = text ?? "",
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("monospace")
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => window.Close();

        var root = new DockPanel { Margin = new Thickness(12) };
        if (!string.IsNullOrEmpty(label))
        {
            var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(lbl, Dock.Top);
            root.Children.Add(lbl);
        }
        var buttons = ButtonRow(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(box);
        window.Content = root;

        if (modal)
            return ShowOrRun(window, owner);
        if (owner is not null)
            window.Show(owner);
        else
            window.Show();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Lets the user pick one item out of a list, with filtering. Returns <see langword="null"/> if cancelled.
    /// </summary>
    public static async Task<object> PickFromList(Window owner, string title, IList<object> items, Func<object, string> display, bool allowNone = false)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 560,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        object result = null;
        bool chosen = false;

        var entries = items.Select(i => new PickEntry(i, display(i))).ToList();
        if (allowNone)
            entries.Insert(0, new PickEntry(null, "(none)"));

        var filter = new TextBox { Watermark = "Filter..." };
        var list = new ListBox { ItemsSource = entries, SelectionMode = SelectionMode.Single };
        filter.TextChanged += (_, _) =>
        {
            string f = filter.Text ?? "";
            list.ItemsSource = string.IsNullOrEmpty(f)
                ? entries
                : entries.Where(e => e.Text.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        };

        void Accept()
        {
            if (list.SelectedItem is PickEntry pe)
            {
                result = pe.Value;
                chosen = true;
                dialog.Close();
            }
        }
        list.DoubleTapped += (_, _) => Accept();
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };

        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => dialog.Close();

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(filter, Dock.Top);
        filter.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(filter);
        var buttons = ButtonRow(ok, cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(list);
        dialog.Content = root;
        dialog.Opened += (_, _) => filter.Focus();

        await ShowOrRun(dialog, owner);
        return chosen ? (result ?? PickNone) : null;
    }

    /// <summary>
    /// Sentinel returned by <see cref="PickFromList"/> when "(none)" was chosen.
    /// </summary>
    public static readonly object PickNone = new();

    private sealed record PickEntry(object Value, string Text)
    {
        public override string ToString() => Text;
    }

    #region File pickers

    /// <summary>
    /// Converts a WinForms/WPF-style filter (e.g. <c>"PNG files (*.png)|*.png|All files|*"</c>) to Avalonia file types.
    /// </summary>
    public static List<FilePickerFileType> ParseFilter(string filter)
    {
        var result = new List<FilePickerFileType>();
        if (string.IsNullOrWhiteSpace(filter))
            return result;

        string[] parts = filter.Split('|');
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            var patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                       .Select(p => p == "*.*" ? "*" : p)
                                       .ToList();
            result.Add(new FilePickerFileType(parts[i]) { Patterns = patterns });
        }
        // Lone pattern without description
        if (parts.Length == 1)
            result.Add(new FilePickerFileType(parts[0]) { Patterns = [parts[0]] });
        return result;
    }

    private static async Task<IStorageFolder> TryGetFolder(TopLevel top, string path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.Directory.Exists(path))
            return null;
        try
        {
            return await top.StorageProvider.TryGetFolderFromPathAsync(path);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string> OpenFile(TopLevel top, string title, string filter, string startDirectory = null)
    {
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ParseFilter(filter),
            SuggestedStartLocation = await TryGetFolder(top, startDirectory)
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public static async Task<string> SaveFile(TopLevel top, string title, string filter, string defaultExt = null,
                                              string suggestedName = null, string startDirectory = null)
    {
        if (defaultExt is not null)
            defaultExt = defaultExt.TrimStart('.');
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            FileTypeChoices = ParseFilter(filter),
            DefaultExtension = defaultExt,
            SuggestedFileName = suggestedName,
            ShowOverwritePrompt = true,
            SuggestedStartLocation = await TryGetFolder(top, startDirectory)
        });
        return file?.TryGetLocalPath();
    }

    public static async Task<string> OpenFolder(TopLevel top, string title, string startDirectory = null)
    {
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await TryGetFolder(top, startDirectory)
        });
        string path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (path is not null && !path.EndsWith(System.IO.Path.DirectorySeparatorChar))
            path += System.IO.Path.DirectorySeparatorChar;
        return path;
    }

    #endregion
}
