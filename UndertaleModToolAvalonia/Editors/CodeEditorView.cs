using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModLib.Project;

namespace UndertaleModToolAvalonia.Editors;

/// <summary>
/// Editor for <see cref="UndertaleCode"/>: decompiled GML and disassembly views, with compiling/assembling.
/// </summary>
public class CodeEditorView : DockPanel, ICommittableEditor
{
    private static IHighlightingDefinition gmlHighlighting;
    private static IHighlightingDefinition asmHighlighting;

    private readonly MainWindow mainWindow;
    private readonly UndertaleCode code;
    private readonly TabControl modeTabs;
    private readonly TabItem decompiledTab;
    private readonly TabItem disassemblyTab;
    private readonly TextEditor decompiledEditor;
    private readonly TextEditor disassemblyEditor;
    private readonly TextBlock statusText;
    private readonly Button applyButton;
    private readonly Button revertButton;

    private bool decompiledLoaded, disassemblyLoaded;
    private bool decompiledDirty, disassemblyDirty;
    private bool suppressDirty;
    private int pendingLine = -1;

    public CodeEditorView(MainWindow mainWindow, UndertaleCode code, bool startInDisassembly = false)
    {
        this.mainWindow = mainWindow;
        this.code = code;

        LoadHighlighting();

        decompiledEditor = CreateEditor(gmlHighlighting);
        disassemblyEditor = CreateEditor(asmHighlighting);
        decompiledEditor.TextChanged += (_, _) => { if (!suppressDirty) { decompiledDirty = true; UpdateButtons(); } };
        disassemblyEditor.TextChanged += (_, _) => { if (!suppressDirty) { disassemblyDirty = true; UpdateButtons(); } };

        decompiledTab = new TabItem { Header = "Decompiled", Content = decompiledEditor };
        disassemblyTab = new TabItem { Header = "Disassembly", Content = disassemblyEditor };
        modeTabs = new TabControl { Items = { decompiledTab, disassemblyTab }, Padding = new Thickness(0) };

        // Toolbar
        var title = new TextBlock
        {
            Text = code.Name?.Content ?? "(unnamed code)",
            Classes = { "header" },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0)
        };
        applyButton = new Button { Content = "Compile", IsEnabled = false };
        ToolTip.SetTip(applyButton, "Compile the edited code into the data file (Ctrl+Enter)");
        applyButton.Click += async (_, _) => await CommitAsync();
        revertButton = new Button { Content = "Revert", IsEnabled = false };
        ToolTip.SetTip(revertButton, "Discard edits and reload from the data file");
        revertButton.Click += (_, _) => Reload();
        statusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Classes = { "dim" }, TextTrimming = TextTrimming.CharacterEllipsis };

        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { applyButton, revertButton } };
        DockPanel.SetDock(title, Dock.Left);
        DockPanel.SetDock(buttons, Dock.Left);
        toolbar.Children.Add(title);
        toolbar.Children.Add(buttons);
        toolbar.Children.Add(statusText);

        DockPanel.SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);

        if (code.ParentEntry is not null)
        {
            var note = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 6) };
            note.Children.Add(new TextBlock { Text = "This code entry is an anonymous function inside", VerticalAlignment = VerticalAlignment.Center });
            var link = new Button { Content = PropertyGrid.Plain(code.ParentEntry.Name?.Content), Classes = { "link" } };
            link.Click += (_, _) => mainWindow.OpenInTab(code.ParentEntry, false);
            note.Children.Add(link);
            note.Children.Add(new TextBlock { Text = "— edit that entry instead.", VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(note, Dock.Top);
            Children.Add(note);
            decompiledEditor.IsReadOnly = true;
            disassemblyEditor.IsReadOnly = true;
        }
        if (mainWindow.Data?.UnsupportedBytecodeVersion == true)
        {
            decompiledEditor.IsReadOnly = true;
            disassemblyEditor.IsReadOnly = true;
        }

        Children.Add(modeTabs);

        modeTabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != modeTabs)
                return;
            _ = EnsureCurrentViewLoaded();
            UpdateButtons();
        };
        if (startInDisassembly)
            modeTabs.SelectedItem = disassemblyTab;

        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                await CommitAsync();
            }
        };

        AttachedToVisualTree += (_, _) => _ = EnsureCurrentViewLoaded();
    }

    public UndertaleCode Code => code;

    public bool HasUnsavedChanges => decompiledDirty || disassemblyDirty;

    private bool DecompiledSelected => modeTabs.SelectedItem == decompiledTab;

    private static void LoadHighlighting()
    {
        if (gmlHighlighting is not null)
            return;
        gmlHighlighting = LoadXshd("GML.xshd");
        asmHighlighting = LoadXshd("VMASM.xshd");
    }

    private static IHighlightingDefinition LoadXshd(string name)
    {
        try
        {
            using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (s is null)
                return null;
            using XmlReader reader = XmlReader.Create(s);
            return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Failed to load {name}: {e.Message}");
            return null;
        }
    }

    private static TextEditor CreateEditor(IHighlightingDefinition highlighting)
    {
        // The highlighting definitions are designed for a dark background (as in the WPF GUI)
        var editor = new TextEditor
        {
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Cascadia Mono,DejaVu Sans Mono,Liberation Mono,Consolas,Menlo,monospace"),
            FontSize = 13,
            SyntaxHighlighting = highlighting,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            LineNumbersForeground = Brushes.Gray,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.IndentationSize = 4;
        editor.Options.EnableHyperlinks = false;
        editor.Options.HighlightCurrentLine = true;
        editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x26, 0x4F, 0x78));
        editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
        editor.TextArea.Caret.CaretBrush = Brushes.White;
        return editor;
    }

    private void UpdateButtons()
    {
        bool dirty = DecompiledSelected ? decompiledDirty : disassemblyDirty;
        applyButton.Content = DecompiledSelected ? "Compile" : "Assemble";
        applyButton.IsEnabled = HasUnsavedChanges;
        revertButton.IsEnabled = HasUnsavedChanges;
        if (dirty)
            statusText.Text = "Modified — press Ctrl+Enter to apply.";
        else if (HasUnsavedChanges)
            statusText.Text = $"The {(DecompiledSelected ? "disassembly" : "decompiled")} view has unapplied edits.";
    }

    private void SetText(TextEditor editor, string text)
    {
        suppressDirty = true;
        editor.Document.Text = text ?? "";
        editor.Document.UndoStack.ClearAll();
        suppressDirty = false;
    }

    /// <summary>
    /// Scrolls to and highlights the given 1-based line, once the current view is loaded.
    /// </summary>
    public void GoToLine(int line, bool decompiled)
    {
        modeTabs.SelectedItem = decompiled ? decompiledTab : disassemblyTab;
        pendingLine = line;
        bool loaded = decompiled ? decompiledLoaded : disassemblyLoaded;
        if (loaded)
            ApplyPendingLine();
        else
            _ = EnsureCurrentViewLoaded();
    }

    private void ApplyPendingLine()
    {
        if (pendingLine <= 0)
            return;
        TextEditor editor = DecompiledSelected ? decompiledEditor : disassemblyEditor;
        int line = Math.Min(pendingLine, editor.Document.LineCount);
        pendingLine = -1;
        var docLine = editor.Document.GetLineByNumber(line);
        editor.TextArea.Caret.Line = line;
        editor.TextArea.Caret.Column = 1;
        editor.Select(docLine.Offset, docLine.Length);
        Dispatcher.UIThread.Post(() =>
        {
            editor.ScrollToLine(line);
            editor.TextArea.Focus();
        }, DispatcherPriority.Background);
    }

    private async Task EnsureCurrentViewLoaded()
    {
        if (DecompiledSelected && !decompiledLoaded)
        {
            decompiledLoaded = true;
            await LoadDecompiled();
        }
        else if (!DecompiledSelected && !disassemblyLoaded)
        {
            disassemblyLoaded = true;
            LoadDisassembly();
        }
        ApplyPendingLine();
    }

    private async Task LoadDecompiled()
    {
        SetText(decompiledEditor, "// Decompiling, please wait...");
        decompiledEditor.IsEnabled = false;
        statusText.Text = "Decompiling...";
        string text = await Task.Run(() => mainWindow.GetDecompiledText(code));
        SetText(decompiledEditor, text);
        decompiledEditor.IsEnabled = true;
        decompiledDirty = false;
        statusText.Text = "";
        UpdateButtons();
    }

    private void LoadDisassembly()
    {
        SetText(disassemblyEditor, mainWindow.GetDisassemblyText(code));
        disassemblyDirty = false;
        UpdateButtons();
    }

    private void Reload()
    {
        decompiledDirty = disassemblyDirty = false;
        decompiledLoaded = disassemblyLoaded = false;
        statusText.Text = "";
        _ = EnsureCurrentViewLoaded();
        UpdateButtons();
    }

    /// <summary>
    /// Compiles/assembles any pending edits. Returns <see langword="false"/> if it failed (edits are kept).
    /// </summary>
    public async Task<bool> CommitAsync()
    {
        if (!HasUnsavedChanges)
            return true;
        if (mainWindow.Data is null)
            return false;

        // Prefer the view the user is looking at, if both were edited
        bool useDecompiled = decompiledDirty && (DecompiledSelected || !disassemblyDirty);
        bool ok = useDecompiled ? await CompileDecompiled() : await AssembleDisassembly();
        if (!ok)
            return false;

        decompiledDirty = disassemblyDirty = false;
        mainWindow.MarkDirty();
        mainWindow.RefreshTree();

        // Reload both views from the new bytecode
        decompiledLoaded = disassemblyLoaded = false;
        await EnsureCurrentViewLoaded();
        statusText.Text = useDecompiled ? "Compiled successfully." : "Assembled successfully.";
        UpdateButtons();
        return true;
    }

    private async Task<bool> CompileDecompiled()
    {
        string source = decompiledEditor.Text;
        statusText.Text = "Compiling...";
        applyButton.IsEnabled = false;

        CompileResult result = default;
        string exception = null;
        await Task.Run(() =>
        {
            try
            {
                CompileGroup group = new(mainWindow.Data)
                {
                    MainThreadAction = f => Dispatcher.UIThread.Invoke(f)
                };
                group.QueueCodeReplace(code, source);
                result = group.Compile();
            }
            catch (Exception e)
            {
                exception = e.ToString();
            }
        });

        if (exception is not null)
        {
            statusText.Text = "Compilation failed.";
            UpdateButtons();
            await mainWindow.ShowError(Truncate(exception, 2000), "Compiler error");
            return false;
        }
        if (!result.Successful)
        {
            statusText.Text = "Compilation failed.";
            UpdateButtons();
            await mainWindow.ShowError(Truncate(result.PrintAllErrors(false), 2000), "Compiler error");
            return false;
        }
        return true;
    }

    private async Task<bool> AssembleDisassembly()
    {
        try
        {
            var instructions = Assembler.Assemble(disassemblyEditor.Text, mainWindow.Data);
            code.Replace(instructions);
        }
        catch (Exception e)
        {
            await mainWindow.ShowError(Truncate(e.ToString(), 2000), "Assembler error");
            return false;
        }

        if (mainWindow.Project is ProjectContext project && project.TryGetCodeSource(code, out _))
        {
            await mainWindow.ShowWarning("Editing disassembly while in an open project can cause desyncs with source code in the project.\n\n" +
                                         "The source code will not change unless you directly modify it, " +
                                         "or if you remove the code asset from the project entirely.");
        }
        return true;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
