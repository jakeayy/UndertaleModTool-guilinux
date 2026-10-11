using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModLib.Project;
using UndertaleModLib.Util;
using UndertaleModToolAvalonia.Controls;
using UndertaleModToolAvalonia.Dialogs;
using UndertaleModToolAvalonia.Editors;

namespace UndertaleModToolAvalonia;

public partial class MainWindow : Window
{
    public static string Version => typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown";

    private readonly List<EditorTab> tabs = new();
    private readonly HashSet<string> expandedCategories = new();
    private readonly DispatcherTimer filterTimer;
    private bool isDirty;
    private bool closeConfirmed;
    private bool suppressTreeSelection;

    // Loaded audio group files, for playing sounds stored outside the main data file
    private readonly Dictionary<string, UndertaleData> audioGroupCache = new();

    public UndertaleData Data { get; private set; }
    public string FilePath { get; private set; }
    public ProjectContext Project { get; private set; }

    /// <summary>The object highlighted in the resource tree.</summary>
    public object Highlighted { get; private set; }

    /// <summary>The object open in the current tab.</summary>
    public object Selected => Dispatcher.UIThread.CheckAccess()
        ? CurrentTab?.Object
        : Dispatcher.UIThread.Invoke(() => CurrentTab?.Object);

    public bool CanSave { get; private set; }
    private bool canSafelySave;

    public string ExePath { get; } = AppContext.BaseDirectory;

    public MainWindow()
    {
        InitializeComponent();
        InitializeScripting();

        ResourceTree.ItemTemplate = new FuncDataTemplate<TreeRow>((row, _) =>
        {
            var tb = new TextBlock
            {
                [!TextBlock.TextProperty] = new Avalonia.Data.Binding(nameof(TreeRow.Text)),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (row is not null)
            {
                tb.Margin = new Thickness(row.Indent * 16, 0, 0, 0);
                if (row.IsHeader)
                    tb.FontWeight = FontWeight.SemiBold;
                else if (row.Item is null)
                    tb.Opacity = 0.5;
            }
            return tb;
        }, supportsRecycling: false);

        ResourceTree.SelectionChanged += ResourceTree_SelectionChanged;
        ResourceTree.AddHandler(PointerPressedEvent, ResourceTree_PointerPressed, RoutingStrategies.Tunnel);
        ResourceTree.ContextRequested += ResourceTree_ContextRequested;
        ResourceTree.KeyDown += ResourceTree_KeyDown;

        filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        filterTimer.Tick += (_, _) => { filterTimer.Stop(); RebuildTree(); };
        FilterBox.TextChanged += (_, _) => { filterTimer.Stop(); filterTimer.Start(); };

        EditorTabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == EditorTabs)
                UpdateTitle();
        };

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);

        KeyDown += MainWindow_KeyDown;
        Closing += MainWindow_Closing;

        BuildScriptsMenu();
        BuildRecentMenu();
        OpenWelcomeTab();
        UpdateUIState();
    }

    #region Messages

    public Task ShowMessage(string message, string title = "Message") => DialogHelper.ShowMessage(this, title, message, MessageKind.Info);
    public Task ShowWarning(string message, string title = "Warning") => DialogHelper.ShowMessage(this, title, message, MessageKind.Warning);
    public Task ShowError(string message, string title = "Error") => DialogHelper.ShowMessage(this, title, message, MessageKind.Error);
    public Task<bool> ShowQuestion(string message, string title = "Question") => DialogHelper.ShowMessage(this, title, message, MessageKind.Question);

    #endregion

    #region State and title

    public void MarkDirty()
    {
        if (!isDirty)
        {
            isDirty = true;
            UpdateTitle();
        }
    }

    private void UpdateTitle()
    {
        string file = FilePath is null ? null : Path.GetFileName(FilePath);
        string tab = CurrentTab?.Object is { } obj ? $" — {ResourceInfo.DisplayName(obj)}" : "";
        Title = file is null
            ? "UndertaleModTool"
            : $"UndertaleModTool — {file}{(isDirty ? " *" : "")}{tab}";
    }

    private void UpdateUIState()
    {
        bool loaded = Data is not null;
        SaveMenuItem.IsEnabled = loaded && CanSave;
        SaveAsMenuItem.IsEnabled = loaded && CanSave;
        CloseMenuItem.IsEnabled = loaded;
        UpdateTitle();
    }

    #endregion

    #region Resource tree

    /// <summary>A row in the flattened resource tree.</summary>
    public sealed class TreeRow : INotifyPropertyChanged
    {
        private string text;
        public ResourceCategory Category { get; init; }
        public object Item { get; init; }
        public bool IsHeader { get; init; }
        public int Indent { get; init; }
        public int Count { get; init; }

        public string Text
        {
            get => text;
            set { text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>Rebuilds the tree from the data (after adding/removing resources, scripts, etc).</summary>
    public void RefreshTree() => RebuildTree();

    /// <summary>Refreshes visible labels (after renames) without rebuilding.</summary>
    public void RefreshTreeLabels()
    {
        if (ResourceTree.ItemsSource is not IEnumerable<TreeRow> rows)
            return;
        foreach (TreeRow row in rows)
        {
            if (row.IsHeader || row.Category?.GetStandalone is not null)
                continue;
            string text = ResourceInfo.DisplayName(row.Item);
            if (row.Text != text)
                row.Text = text;
        }
        UpdateTitle();
        foreach (var tab in tabs)
            tab.UpdateHeader();
    }

    private void RebuildTree()
    {
        var rows = new List<TreeRow>();
        string filter = FilterBox.Text?.Trim() ?? "";
        bool filtering = filter.Length > 0;

        if (Data is not null)
        {
            foreach (ResourceCategory cat in ResourceInfo.Categories)
            {
                if (cat.GetStandalone is not null)
                {
                    object obj = cat.GetStandalone(Data);
                    if (obj is null || (filtering && !cat.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    rows.Add(new TreeRow { Category = cat, Item = obj, Text = cat.Name, Indent = 0 });
                    continue;
                }

                IList list = cat.GetList(Data);
                if (list is null)
                    continue;

                bool showNull = AppSettings.Instance.ShowNullEntriesInResourceTree;
                var items = new List<object>(filtering ? 16 : list.Count);
                foreach (object item in list)
                {
                    if (item is null && !showNull)
                        continue;
                    if (filtering && (item is null || !ResourceInfo.Matches(item, filter)))
                        continue;
                    items.Add(item);
                }
                if (filtering && items.Count == 0)
                    continue;

                bool expanded = filtering || expandedCategories.Contains(cat.Name);
                rows.Add(new TreeRow
                {
                    Category = cat,
                    IsHeader = true,
                    Count = items.Count,
                    Text = $"{(expanded ? "▾" : "▸")} {cat.Name} ({items.Count})"
                });
                if (expanded)
                {
                    foreach (object item in items)
                        rows.Add(new TreeRow { Category = cat, Item = item, Indent = 1, Text = ResourceInfo.DisplayName(item) });
                }
            }
        }

        suppressTreeSelection = true;
        object previouslySelected = (ResourceTree.SelectedItem as TreeRow)?.Item;
        double offset = (ResourceTree.Scroll as ScrollViewer)?.Offset.Y ?? 0;
        ResourceTree.ItemsSource = rows;
        if (previouslySelected is not null)
            ResourceTree.SelectedItem = rows.FirstOrDefault(r => !r.IsHeader && ReferenceEquals(r.Item, previouslySelected));
        if (ResourceTree.Scroll is ScrollViewer sv)
            Dispatcher.UIThread.Post(() => sv.Offset = sv.Offset.WithY(offset), DispatcherPriority.Background);
        suppressTreeSelection = false;
    }

    private void ToggleCategory(ResourceCategory cat)
    {
        if (!expandedCategories.Remove(cat.Name))
            expandedCategories.Add(cat.Name);
        RebuildTree();
    }

    private async void ResourceTree_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressTreeSelection || ResourceTree.SelectedItem is not TreeRow row)
            return;
        if (row.IsHeader)
        {
            if (string.IsNullOrEmpty(FilterBox.Text))
                ToggleCategory(row.Category);
            suppressTreeSelection = true;
            ResourceTree.SelectedItem = null;
            suppressTreeSelection = false;
            return;
        }
        if (row.Item is null)
            return;
        Highlighted = row.Item;
        await OpenInTabAsync(row.Item, false);
    }

    private TreeRow RowFromEvent(RoutedEventArgs e)
    {
        for (var v = e.Source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is ListBoxItem { DataContext: TreeRow row })
                return row;
        }
        return null;
    }

    private void ResourceTree_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(ResourceTree).Properties;
        bool newTab = props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control));
        if (!newTab)
            return;
        if (RowFromEvent(e) is { IsHeader: false, Item: not null } row)
        {
            Highlighted = row.Item;
            _ = OpenInTabAsync(row.Item, true);
            e.Handled = true;
        }
    }

    private void ResourceTree_KeyDown(object sender, KeyEventArgs e)
    {
        if (ResourceTree.SelectedItem is not TreeRow row || row.IsHeader || row.Item is null)
            return;
        if (e.Key == Key.Delete && row.Category?.GetList is not null)
        {
            _ = DeleteResource(row.Category, row.Item);
            e.Handled = true;
        }
    }

    private void ResourceTree_ContextRequested(object sender, ContextRequestedEventArgs e)
    {
        TreeRow row = RowFromEvent(e);
        if (row is null || Data is null)
            return;

        var menu = new ContextMenu();
        if (row.IsHeader)
        {
            var toggle = new MenuItem { Header = expandedCategories.Contains(row.Category.Name) ? "Collapse" : "Expand" };
            toggle.Click += (_, _) => ToggleCategory(row.Category);
            menu.Items.Add(toggle);
            if (row.Category.CanAdd && row.Category.ItemType is not null)
            {
                var add = new MenuItem { Header = $"Add new..." };
                add.Click += async (_, _) => await AddResource(row.Category);
                menu.Items.Add(add);
            }
        }
        else if (row.Item is not null)
        {
            var open = new MenuItem { Header = "Open in new tab" };
            open.Click += (_, _) => _ = OpenInTabAsync(row.Item, true);
            menu.Items.Add(open);
            var copy = new MenuItem { Header = "Copy name" };
            copy.Click += async (_, _) =>
            {
                if (Clipboard is { } cb)
                    await cb.SetTextAsync(ResourceInfo.DisplayName(row.Item));
            };
            menu.Items.Add(copy);
            if (row.Category?.GetList is not null)
            {
                menu.Items.Add(new Separator());
                if (row.Category.CanAdd && row.Category.ItemType is not null)
                {
                    var add = new MenuItem { Header = "Add new..." };
                    add.Click += async (_, _) => await AddResource(row.Category);
                    menu.Items.Add(add);
                }
                var delete = new MenuItem { Header = "Delete" };
                delete.Click += async (_, _) => await DeleteResource(row.Category, row.Item);
                menu.Items.Add(delete);
            }
        }
        if (menu.Items.Count > 0)
        {
            menu.Open(ResourceTree);
            e.Handled = true;
        }
    }

    private void FocusFilter_Click(object sender, RoutedEventArgs e)
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    private void RefreshTree_Click(object sender, RoutedEventArgs e)
    {
        RebuildTree();
        RefreshCurrentTab();
    }

    #endregion

    #region Tabs

    private sealed class EditorTab
    {
        public object Object { get; set; }
        public Control Editor { get; set; }
        public TabItem Item { get; init; }
        public TextBlock HeaderText { get; init; }

        public void UpdateHeader()
        {
            HeaderText.Text = Object is null ? "Welcome" : ResourceInfo.DisplayName(Object);
            ToolTip.SetTip(Item, Object is null ? null : $"{ResourceInfo.DisplayName(Object)} ({ResourceInfo.TypeName(Object)})");
        }
    }

    private EditorTab CurrentTab => tabs.FirstOrDefault(t => t.Item == EditorTabs.SelectedItem);

    private EditorTab CreateTab(object obj)
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button
        {
            Content = "✕",
            Padding = new Thickness(4, 0),
            Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Children = { text, close } };
        var item = new TabItem { Header = header };
        var tab = new EditorTab { Object = obj, Item = item, HeaderText = text };
        close.Click += async (_, _) => await CloseTab(tab);
        header.PointerReleased += async (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Middle)
                await CloseTab(tab);
        };
        tab.UpdateHeader();
        return tab;
    }

    private void SetTabContent(EditorTab tab, object obj)
    {
        tab.Object = obj;
        tab.Editor = EditorFactory.Create(this, obj);
        tab.Item.Content = tab.Editor;
        tab.UpdateHeader();
    }

    private void OpenWelcomeTab()
    {
        var tab = CreateTab(null);
        SetTabContent(tab, null);
        tabs.Add(tab);
        EditorTabs.Items.Add(tab.Item);
        EditorTabs.SelectedItem = tab.Item;
    }

    /// <summary>
    /// Opens an object, either replacing the current tab's content or in a new tab.
    /// If the object is already open in a tab, that tab is selected instead.
    /// </summary>
    public void OpenInTab(object obj, bool newTab) => _ = OpenInTabAsync(obj, newTab);

    private async Task<EditorTab> OpenInTabAsync(object obj, bool newTab)
    {
        if (obj is null)
            return null;

        EditorTab existing = tabs.FirstOrDefault(t => ReferenceEquals(t.Object, obj));
        if (existing is not null)
        {
            EditorTabs.SelectedItem = existing.Item;
            return existing;
        }

        EditorTab current = CurrentTab;
        if (!newTab && current is not null)
        {
            if (!await CommitEditor(current))
                return null;
            SetTabContent(current, obj);
            UpdateTitle();
            return current;
        }

        var tab = CreateTab(obj);
        SetTabContent(tab, obj);
        tabs.Add(tab);
        EditorTabs.Items.Add(tab.Item);
        EditorTabs.SelectedItem = tab.Item;
        return tab;
    }

    /// <summary>
    /// Applies an editor's pending changes. If that fails, offers to discard them.
    /// Returns <see langword="false"/> if the caller should abort.
    /// </summary>
    private async Task<bool> CommitEditor(EditorTab tab)
    {
        if (tab.Editor is not ICommittableEditor { HasUnsavedChanges: true } editor)
            return true;
        if (await editor.CommitAsync())
            return true;
        return await ShowQuestion("The changes in this tab could not be applied. Discard them?", "Unapplied changes");
    }

    /// <summary>Applies pending changes in all tabs. Returns <see langword="false"/> if any failed.</summary>
    private async Task<bool> CommitAllEditors()
    {
        foreach (var tab in tabs.ToList())
        {
            if (tab.Editor is ICommittableEditor { HasUnsavedChanges: true })
            {
                EditorTabs.SelectedItem = tab.Item;
                if (!await CommitEditor(tab))
                    return false;
            }
        }
        return true;
    }

    private async Task CloseTab(EditorTab tab)
    {
        if (!await CommitEditor(tab))
            return;
        int index = tabs.IndexOf(tab);
        tabs.Remove(tab);
        EditorTabs.Items.Remove(tab.Item);
        if (tabs.Count == 0)
            OpenWelcomeTab();
        else
            EditorTabs.SelectedItem = tabs[Math.Clamp(index, 0, tabs.Count - 1)].Item;
        UpdateTitle();
    }

    private void CloseAllTabs()
    {
        tabs.Clear();
        EditorTabs.Items.Clear();
        OpenWelcomeTab();
    }

    /// <summary>Recreates the current tab's editor (e.g. after the object changed externally).</summary>
    public void RefreshCurrentTab()
    {
        if (CurrentTab is { } tab)
            SetTabContent(tab, tab.Object);
    }

    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTab is { } tab)
            await CloseTab(tab);
    }

    /// <summary>
    /// Opens a code entry by name, optionally scrolling to a 1-based line.
    /// </summary>
    public async void OpenCodeEntry(string name, int lineNum, bool decompiled)
    {
        UndertaleCode code = Data?.Code?.ByName(name);
        if (code is null)
        {
            await ShowError($"Can't find code entry \"{name}\".\n(Probably, different game data was loaded.)");
            return;
        }
        EditorTab tab = await OpenInTabAsync(code, false);
        if (tab?.Editor is CodeEditorView editor && lineNum > 0)
            editor.GoToLine(lineNum, decompiled);
        else if (tab?.Editor is CodeEditorView editor2 && !decompiled)
            editor2.GoToLine(1, false);
    }

    #endregion

    #region Adding and deleting resources

    private static bool IsValidAssetIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        if (!char.IsAsciiLetter(name[0]) && name[0] != '_')
            return false;
        return name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    private bool IsExtProductIDEligible =>
        (Data?.GeneralInfo?.Major ?? 0) >= 2 ||
        ((Data?.GeneralInfo?.Major ?? 0) == 1 && ((Data?.GeneralInfo?.Build ?? 0) >= 1773 || (Data?.GeneralInfo?.Build ?? 0) == 1539));

    /// <summary>
    /// Creates a new resource in a category, mirroring the defaults of the Windows GUI.
    /// </summary>
    private async Task AddResource(ResourceCategory cat)
    {
        IList list = cat.GetList(Data);
        if (list is null)
            return;
        object obj;
        try
        {
            obj = Activator.CreateInstance(cat.ItemType);
        }
        catch (Exception e)
        {
            await ShowError($"Cannot create a new {cat.ItemType.Name}:\n{e.Message}");
            return;
        }

        if (obj is UndertaleNamedResource named)
        {
            string notDataName = obj switch
            {
                UndertaleTexturePageItem => "PageItem " + list.Count,
                UndertaleEmbeddedAudio => "EmbeddedSound " + list.Count,
                UndertaleEmbeddedTexture => "Texture " + list.Count,
                _ => null
            };
            if (notDataName is not null)
            {
                named.Name = new UndertaleString(notDataName); // not stored in the string table
            }
            else
            {
                string typeName = obj.GetType().Name.Replace("Undertale", "").Replace("GameObject", "Object").ToLowerInvariant();
                string newName = ResourceInfo.UniqueName(list, typeName);
                string userName = await DialogHelper.ShowTextInput(this, $"New {typeName}", "Name of new asset:", newName, submitText: "Create");
                if (userName is null)
                    return;
                if (IsValidAssetIdentifier(userName))
                    newName = userName;
                else if (!await ShowQuestion($"Asset name \"{userName}\" is not a valid identifier. Use the auto-generated name \"{newName}\" instead?", "Invalid name"))
                    return;
                named.Name = Data.Strings.MakeString(newName);

                switch (obj)
                {
                    case UndertaleRoom room:
                        if (Data.IsGameMaker2())
                        {
                            room.Caption = null;
                            room.Backgrounds.Clear();
                            if (Data.IsVersionAtLeast(2024, 13))
                            {
                                room.Flags |= UndertaleRoom.RoomEntryFlags.IsGM2024_13;
                                room.InstanceCreationOrderIDs ??= new();
                            }
                            else
                            {
                                room.Flags |= UndertaleRoom.RoomEntryFlags.IsGMS2;
                                if (Data.IsVersionAtLeast(2, 3))
                                    room.Flags |= UndertaleRoom.RoomEntryFlags.IsGMS2_3;
                            }
                        }
                        else
                            room.Caption = Data.Strings.MakeString("");
                        if (await ShowQuestion("Add the new room to the end of the room order list?", "Room order"))
                            Data.GeneralInfo.RoomOrder.Add(new(room));
                        break;
                    case UndertaleScript script:
                        if (Data.IsVersionAtLeast(2, 3))
                        {
                            script.Code = UndertaleCode.CreateEmptyEntry(Data, $"gml_GlobalScript_{newName}");
                            Data.GlobalInitScripts?.Add(new UndertaleGlobalInit { Code = script.Code });
                        }
                        else
                            script.Code = UndertaleCode.CreateEmptyEntry(Data, $"gml_Script_{newName}");
                        Project?.MarkAssetForExport(script.Code);
                        break;
                    case UndertaleCode code:
                        if (Data.CodeLocals is not null)
                        {
                            code.LocalsCount = 1;
                            UndertaleCodeLocals.CreateEmptyEntry(Data, code.Name);
                        }
                        else
                            code.WeirdLocalFlag = true;
                        break;
                    case UndertaleExtension ext when IsExtProductIDEligible:
                        ext.FolderName = Data.Strings.MakeString("");
                        ext.ClassName = Data.Strings.MakeString("");
                        ext.Version = Data.Strings.MakeString("1.0.0");
                        Data.FORM.EXTN.productIdData.Add([0xBA, 0x5E, 0xBA, 0x11, 0xBA, 0xDD, 0x06, 0x60, 0xBE, 0xEF, 0xED, 0xBA, 0x0B, 0xAB, 0xBA, 0xBE]);
                        break;
                    case UndertaleShader shader:
                        shader.GLSL_ES_Vertex = Data.Strings.MakeString("", true);
                        shader.GLSL_ES_Fragment = Data.Strings.MakeString("", true);
                        shader.GLSL_Vertex = Data.Strings.MakeString("", true);
                        shader.GLSL_Fragment = Data.Strings.MakeString("", true);
                        shader.HLSL9_Vertex = Data.Strings.MakeString("", true);
                        shader.HLSL9_Fragment = Data.Strings.MakeString("", true);
                        break;
                }
            }
        }
        else if (obj is UndertaleString str)
        {
            str.Content = "string" + list.Count;
        }

        list.Add(obj);
        if (Project is not null && obj is IProjectAsset asset)
            Project.MarkAssetForExport(asset);

        MarkDirty();
        expandedCategories.Add(cat.Name);
        RebuildTree();
        await OpenInTabAsync(obj, true);
    }

    private async Task DeleteResource(ResourceCategory cat, object obj)
    {
        IList list = cat.GetList(Data);
        if (list is null)
            return;
        bool isLast = list.IndexOf(obj) == list.Count - 1;
        string msg = $"Delete {ResourceInfo.DisplayName(obj)}?" +
                     (isLast ? "" : "\n\nNote that the code often references objects by ID, so this operation is likely to break things because other items will shift up!");
        if (!await DialogHelper.ShowMessage(this, "Confirmation", msg, MessageKind.Question))
            return;

        list.Remove(obj);
        if (Project is not null && obj is IProjectAsset asset)
            Project.UnmarkAssetForExport(asset);

        foreach (var tab in tabs.Where(t => ReferenceEquals(t.Object, obj)).ToList())
        {
            tabs.Remove(tab);
            EditorTabs.Items.Remove(tab.Item);
        }
        if (tabs.Count == 0)
            OpenWelcomeTab();

        MarkDirty();
        RebuildTree();
    }

    #endregion

    #region Loading and saving

    private void ApplyToolInfo(UndertaleData data)
    {
        data.ToolInfo.DecompilerSettings = AppSettings.Instance.DecompilerSettings;
        data.ToolInfo.InstanceIdPrefix = () => AppSettings.Instance.InstanceIdPrefix;
    }

    private void DisposeGameData()
    {
        if (Data is null)
            return;
        CloseAllTabs();
        ResourceTree.ItemsSource = null;
        Data.Dispose();
        Data = null;
        Project = null;
        foreach (var d in audioGroupCache.Values)
            d.Dispose();
        audioGroupCache.Clear();
        TextureCache.Clear();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
    }

    /// <summary>
    /// Asks whether to discard unsaved changes. Returns <see langword="true"/> if it's fine to continue.
    /// </summary>
    private async Task<bool> ConfirmDiscardChanges()
    {
        if (Data is null || !isDirty)
            return true;
        return await ShowQuestion("The data file has unsaved changes, which will be lost. Continue?", "Unsaved changes");
    }

    public async Task LoadFile(string filename)
    {
        if (!File.Exists(filename))
        {
            await ShowError($"File not found:\n{filename}");
            return;
        }

        var dialog = new LoaderDialog("Loading", "Loading, please wait...");
        DisposeGameData();
        GameSpecificResolver.BaseDirectory = ExePath;

        var warnings = new List<string>();
        bool hadImportantWarnings = false;
        UndertaleData data = null;
        Exception error = null;

        Task loadTask = Task.Run(() =>
        {
            try
            {
                using var stream = new FileStream(filename, FileMode.Open, FileAccess.Read);
                data = UndertaleIO.Read(stream, (warning, isImportant) =>
                {
                    lock (warnings)
                        warnings.Add(warning);
                    if (isImportant)
                        hadImportantWarnings = true;
                }, message => dialog.ReportStatus(message));
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        _ = loadTask.ContinueWith(_ => dialog.ForceClose(), TaskScheduler.Default);
        await dialog.ShowDialog(this);
        await loadTask;

        if (error is not null)
        {
            Debug.WriteLine(error);
            await ShowError("An error occurred while trying to load:\n" + error.Message, "Load error");
            UpdateUIState();
            return;
        }
        foreach (string w in warnings)
            await ShowWarning(w, "Loading warning");

        if (data.UnsupportedBytecodeVersion)
        {
            await ShowWarning("Only bytecode versions 13 to 17 are supported for now, you are trying to load " + data.GeneralInfo.BytecodeVersion +
                              ". A lot of code is disabled and will likely break something. Saving/exporting is disabled.", "Unsupported bytecode version");
            CanSave = false;
            canSafelySave = false;
        }
        else if (hadImportantWarnings)
        {
            await ShowWarning("Warnings occurred during loading. Data loss will likely occur when trying to save!", "Loading problems");
            CanSave = true;
            canSafelySave = false;
        }
        else
        {
            CanSave = true;
            canSafelySave = true;
        }

        if (data.IsYYC())
            await ShowWarning("This game uses YYC (YoYo Compiler), which means the code is embedded into the game executable. " +
                              "This configuration is currently not fully supported; continue at your own risk.", "YYC");
        if (data.GeneralInfo is { IsDebuggerDisabled: false })
            await ShowWarning("This game is set to run with the GameMaker Studio debugger and the normal runtime will simply hang after loading if the debugger is not running. " +
                              "You can turn this off in General Info by checking the \"Disable Debugger\" box and saving.", "GMS Debugger");
        if (data.EmbeddedTextures?.Any(t => t?.TextureData?.Image?.Format == GMImage.ImageFormat.Unknown) == true)
            await ShowWarning("This game contains texture(s) with unknown or unsupported image formats. These will save/load, but will not display in editors, " +
                              "and many operations will fail regarding them. Proceed with caution.", "Unsupported textures");

        ApplyToolInfo(data);
        Data = data;
        FilePath = filename;
        isDirty = false;
        AppSettings.Instance.LastOpenedDirectory = Path.GetDirectoryName(filename);
        AppSettings.Instance.AddRecentFile(filename);
        BuildRecentMenu();

        expandedCategories.Clear();
        RebuildTree();
        UpdateUIState();

        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
    }

    public async Task<bool> SaveFile(string filename)
    {
        if (Data is null || Data.UnsupportedBytecodeVersion)
            return false;

        if (!await CommitAllEditors())
            return false;

        if (!canSafelySave && !await ShowQuestion("Warnings occurred while loading this file, so saving will likely cause data loss. Save anyway?", "Unsafe save"))
            return false;

        if (Data.GeneralInfo is { IsDebuggerDisabled: false })
            await ShowWarning("You are saving the game in GameMaker Studio debug mode. Unless the debugger is running, the normal runtime will simply hang after loading. " +
                              "You can turn this off in General Info by checking the \"Disable Debugger\" box and saving.", "GMS Debugger");

        var dialog = new LoaderDialog("Saving", "Saving, please wait...");
        Exception error = null;
        string tempPath = filename + "temp";
        Task saveTask = Task.Run(() =>
        {
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                    UndertaleIO.Write(stream, Data, message => dialog.ReportStatus(message));
                File.Move(tempPath, filename, true);
            }
            catch (Exception e)
            {
                error = e;
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // nothing more we can do
                }
            }
        });
        _ = saveTask.ContinueWith(_ => dialog.ForceClose(), TaskScheduler.Default);
        await dialog.ShowDialog(this);
        await saveTask;

        if (error is not null)
        {
            await ShowError("An error occurred while trying to save:\n" + error.Message, "Save error");
            return false;
        }

        if (FilePath != filename && Path.GetDirectoryName(FilePath) != Path.GetDirectoryName(filename))
        {
            audioGroupCache.Clear();
        }
        FilePath = filename;
        isDirty = false;
        AppSettings.Instance.AddRecentFile(filename);
        BuildRecentMenu();
        UpdateUIState();
        return true;
    }

    /// <summary>
    /// Loads (and caches) an audio group file next to the data file.
    /// </summary>
    public UndertaleData LoadAudioGroup(string path)
    {
        if (audioGroupCache.TryGetValue(path, out var cached))
            return cached;
        if (!File.Exists(path))
            throw new FileNotFoundException($"Audio group file not found: {path}");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        UndertaleData data = UndertaleIO.Read(stream, (warning, _) => throw new InvalidDataException(warning));
        audioGroupCache[path] = data;
        return data;
    }

    private const string DataFileFilter = "GameMaker data files (*.win;*.unx;*.ios;*.droid;audiogroup*.dat)|*.win;*.unx;*.ios;*.droid;audiogroup*.dat|All files|*";

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardChanges())
            return;
        MakeNewDataFileInternal();
    }

    private void MakeNewDataFileInternal()
    {
        DisposeGameData();
        Data = UndertaleData.CreateNew();
        ApplyToolInfo(Data);
        FilePath = null;
        CanSave = true;
        canSafelySave = true;
        isDirty = true;
        expandedCategories.Clear();
        RebuildTree();
        UpdateUIState();
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardChanges())
            return;
        string path = await DialogHelper.OpenFile(this, "Open data file", DataFileFilter, AppSettings.Instance.LastOpenedDirectory);
        if (path is not null)
            await LoadFile(path);
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await DoSave(false);
    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await DoSave(true);

    private async Task DoSave(bool saveAs)
    {
        if (Data is null || !CanSave)
            return;
        string path = FilePath;
        if (saveAs || path is null)
        {
            path = await DialogHelper.SaveFile(this, "Save data file", DataFileFilter, "win",
                                               FilePath is null ? "data.win" : Path.GetFileName(FilePath),
                                               FilePath is null ? AppSettings.Instance.LastOpenedDirectory : Path.GetDirectoryName(FilePath));
            if (path is null)
                return;
        }
        await SaveFile(path);
    }

    private async void CloseFile_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardChanges())
            return;
        DisposeGameData();
        FilePath = null;
        isDirty = false;
        CanSave = false;
        UpdateUIState();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string dir = FilePath is null ? ExePath : Path.GetDirectoryName(FilePath);
        try
        {
            AudioPlayer.OpenWithDefaultApp(dir);
        }
        catch (Exception ex)
        {
            _ = ShowError($"Could not open the folder:\n{ex.Message}");
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async void MainWindow_Closing(object sender, WindowClosingEventArgs e)
    {
        if (closeConfirmed || Data is null || (!isDirty && !tabs.Any(t => t.Editor is ICommittableEditor { HasUnsavedChanges: true })))
        {
            AudioPlayer.Stop();
            return;
        }
        if (!AppSettings.Instance.WarnOnClose)
            return;

        e.Cancel = true;
        bool hasEdits = tabs.Any(t => t.Editor is ICommittableEditor { HasUnsavedChanges: true });
        string what = isDirty ? "The data file has unsaved changes" : "Some code edits were not compiled";
        if (await ShowQuestion($"{what}{(hasEdits && isDirty ? ", and some code edits were not compiled" : "")}. Quit anyway?", "Unsaved changes"))
        {
            closeConfirmed = true;
            Close();
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        string path = e.DataTransfer.TryGetFile()?.TryGetLocalPath();
        if (path is null)
            return;
        if (Path.GetExtension(path).Equals(".csx", StringComparison.OrdinalIgnoreCase))
        {
            if (await ShowQuestion($"Run the script {Path.GetFileName(path)}?", "Run script"))
                await RunScript(path);
            return;
        }
        if (!await ConfirmDiscardChanges())
            return;
        await LoadFile(path);
    }

    private void BuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        foreach (string file in AppSettings.Instance.RecentFiles)
        {
            var item = new MenuItem { Header = file.Replace("_", "__") };
            string f = file;
            item.Click += async (_, _) =>
            {
                if (await ConfirmDiscardChanges())
                    await LoadFile(f);
            };
            RecentMenu.Items.Add(item);
        }
        RecentMenu.IsEnabled = RecentMenu.Items.Count > 0;
    }

    #endregion

    #region Keyboard

    private async void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (!ctrl)
        {
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                RebuildTree();
            }
            return;
        }
        switch (e.Key)
        {
            case Key.O:
                e.Handled = true;
                Open_Click(this, null);
                break;
            case Key.S:
                e.Handled = true;
                await DoSave(shift);
                break;
            case Key.W:
                e.Handled = true;
                if (CurrentTab is { } tab)
                    await CloseTab(tab);
                break;
            case Key.F when shift:
                e.Handled = true;
                SearchInCode_Click(this, null);
                break;
            case Key.F:
                e.Handled = true;
                FocusFilter_Click(this, null);
                break;
            case Key.Tab:
                e.Handled = true;
                if (tabs.Count > 1)
                {
                    int i = tabs.IndexOf(CurrentTab);
                    i = (i + (shift ? -1 : 1) + tabs.Count) % tabs.Count;
                    EditorTabs.SelectedItem = tabs[i].Item;
                }
                break;
        }
    }

    #endregion

    #region Misc menus

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(this);
        window.Closed += (_, _) =>
        {
            if (Data is not null)
                ApplyToolInfo(Data);
            RebuildTree();
        };
        _ = window.ShowDialog(this);
    }

    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        // This GUI lives in an unofficial fork; send people there rather than to the upstream project
        ScriptOpenURL("https://github.com/jakeayy/UndertaleModTool-guilinux");
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        await ShowMessage($"UndertaleModTool by krzys_h and the Underminers team\nVersion {Version}\n\n" +
                          "Cross-platform GUI built with Avalonia, from the unofficial fork jakeayy/UndertaleModTool-guilinux.\n" +
                          "This GUI is not part of the official project; please report its issues on the fork.\n\n" +
                          $"Running on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, " +
                          $".NET {Environment.Version}", "About");
    }

    #endregion
}
