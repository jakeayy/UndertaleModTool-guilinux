using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModToolAvalonia.Dialogs;

namespace UndertaleModToolAvalonia.Editors;

/// <summary>
/// A reflection-based editor that can display and edit any object from the data file.
/// Primitive values, strings, enums and resource references get dedicated editors;
/// lists and nested objects are shown as lazily-populated expanders.
/// </summary>
public class PropertyGrid : Grid
{
    private const int MaxDepth = 12;
    private const int ListPageSize = 200;

    private static readonly HashSet<string> HiddenProperties =
    [
        // Internal bookkeeping that is noisy and not useful to edit by hand
        "ChildObjectsSize", "ChildObjectCount",
        // Project system bookkeeping (IProjectAsset)
        "ProjectName", "ProjectAssetType", "ProjectExportable"
    ];

    /// <summary>
    /// Text content that is shown literally. Plain strings in buttons/checkboxes treat '_' as an
    /// access key marker, which would mangle GameMaker names like "spr_player".
    /// </summary>
    public static TextBlock Plain(string text) => new() { Text = text };

    private readonly MainWindow mainWindow;
    private readonly object target;
    private readonly int depth;

    /// <summary>Raised after any value in this grid (or nested grids) was changed by the user.</summary>
    public event Action ValueChanged;

    private readonly bool affectsData;

    /// <param name="affectsData">Whether edits modify the data file (and so mark it as unsaved).</param>
    public PropertyGrid(MainWindow mainWindow, object target, int depth = 0, bool affectsData = true)
    {
        this.mainWindow = mainWindow;
        this.target = target;
        this.depth = depth;
        this.affectsData = affectsData;

        ColumnDefinitions = new ColumnDefinitions("Auto,*");
        Build();
    }

    private UndertaleData Data => mainWindow.Data;

    private void MarkDirty()
    {
        if (affectsData)
            mainWindow.MarkDirty();
    }

    private void Build()
    {
        Children.Clear();
        RowDefinitions.Clear();

        if (target is null)
        {
            AddFullRow(new TextBlock { Text = "(null)", Classes = { "dim" } });
            return;
        }

        var props = target.GetType()
                          .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                          .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !HiddenProperties.Contains(p.Name))
                          .OrderBy(p => DeclarationDepth(p))
                          .ToList();

        foreach (PropertyInfo prop in props)
        {
            if (prop.GetCustomAttribute<ObsoleteAttribute>() is not null)
                continue;
            try
            {
                AddProperty(prop);
            }
            catch (Exception e)
            {
                AddRow(prop.Name, new TextBlock { Text = $"<error: {e.GetBaseException().Message}>", Foreground = Brushes.IndianRed });
            }
        }
    }

    /// <summary>Orders base class properties before derived ones, while keeping declaration order.</summary>
    private static int DeclarationDepth(PropertyInfo p)
    {
        int d = 0;
        for (Type t = p.DeclaringType; t?.BaseType is not null; t = t.BaseType)
            d++;
        return d;
    }

    #region Layout helpers

    private void AddRow(string label, Control editor, string tooltip = null)
    {
        int row = RowDefinitions.Count;
        RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var lbl = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 12, 2),
            MinWidth = 150
        };
        if (tooltip is not null)
            ToolTip.SetTip(lbl, tooltip);
        SetRow(lbl, row);
        SetColumn(lbl, 0);
        Children.Add(lbl);

        editor.Margin = new Thickness(0, 2);
        SetRow(editor, row);
        SetColumn(editor, 1);
        Children.Add(editor);
    }

    private void AddFullRow(Control control)
    {
        int row = RowDefinitions.Count;
        RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        control.Margin = new Thickness(0, 2);
        SetRow(control, row);
        SetColumn(control, 0);
        SetColumnSpan(control, 2);
        Children.Add(control);
    }

    #endregion

    private void AddProperty(PropertyInfo prop)
    {
        Type type = prop.PropertyType;
        bool canWrite = prop.CanWrite && prop.SetMethod?.IsPublic == true;
        object value = prop.GetValue(target);

        Control editor = CreateValueEditor(
            type, value, canWrite,
            v => { prop.SetValue(target, v); OnValueChanged(prop.Name); },
            prop.Name, out bool fullRow);

        if (fullRow)
            AddFullRow(editor);
        else
            AddRow(prop.Name, editor, type.Name);
    }

    private void OnValueChanged(string propertyName)
    {
        ValueChanged?.Invoke();
        MarkDirty();
        if (propertyName is "Name" or "Content")
            mainWindow.RefreshTreeLabels();
    }

    /// <summary>
    /// Creates an editor control for a value of the given type.
    /// </summary>
    /// <param name="fullRow">Set when the returned control should span both columns (expanders).</param>
    private Control CreateValueEditor(Type type, object value, bool canWrite, Action<object> setter, string label, out bool fullRow)
    {
        fullRow = false;

        // Unwrap nullable types
        Type underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            type = underlying;

        if (type == typeof(string))
            return StringEditor(value as string, canWrite, setter);

        if (type == typeof(bool))
        {
            var cb = new CheckBox { IsChecked = value as bool? ?? false, IsEnabled = canWrite };
            cb.IsCheckedChanged += (_, _) => setter(cb.IsChecked == true);
            return cb;
        }

        if (type.IsEnum)
            return EnumEditor(type, value, canWrite, setter);

        if (IsNumeric(type))
            return NumericEditor(type, value, canWrite, setter);

        if (type == typeof(UndertaleString))
            return UndertaleStringEditor(value as UndertaleString, canWrite, setter);

        if (type == typeof(byte[]))
            return ByteArrayEditor(value as byte[], canWrite, setter, label);

        if (typeof(UndertaleResourceRef).IsAssignableFrom(type))
        {
            // UndertaleResourceById<T, ChunkT>: edit the wrapped resource reference
            var rr = value as UndertaleResourceRef;
            Type resType = type.IsGenericType ? type.GetGenericArguments()[0] : typeof(object);
            if (rr is null)
                return new TextBlock { Text = "(null)", Classes = { "dim" } };
            return ReferenceEditor(resType, rr.Resource, true, v => { rr.Resource = v; ValueChanged?.Invoke(); MarkDirty(); });
        }

        // References to top-level resources (sprites, code, ...) are shown as links
        if (ResourceInfo.CategoryFor(type) is not null)
            return ReferenceEditor(type, value, canWrite, setter);

        if (value is null)
        {
            return new TextBlock { Text = "(null)", Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center };
        }

        if (value is IList list)
        {
            fullRow = true;
            return ListEditor(label, list);
        }

        if (type.IsPrimitive || type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(TimeSpan))
            return new SelectableTextBlock { Text = Convert.ToString(value, CultureInfo.InvariantCulture), VerticalAlignment = VerticalAlignment.Center };

        if (value is Delegate)
            return new TextBlock { Text = "(function)", Classes = { "dim" } };

        // Nested object
        fullRow = true;
        return NestedObjectEditor(label, value);
    }

    #region Value editors

    private static bool IsNumeric(Type t) =>
        t == typeof(int) || t == typeof(uint) || t == typeof(short) || t == typeof(ushort) ||
        t == typeof(byte) || t == typeof(sbyte) || t == typeof(long) || t == typeof(ulong) ||
        t == typeof(float) || t == typeof(double);

    private static void CommitOnEnterOrLostFocus(TextBox box, Action commit)
    {
        box.LostFocus += (_, _) => commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !box.AcceptsReturn)
            {
                commit();
                e.Handled = true;
            }
        };
    }

    private Control StringEditor(string value, bool canWrite, Action<object> setter)
    {
        bool multiline = value is not null && (value.Contains('\n') || value.Length > 120);
        var box = new TextBox
        {
            Text = value ?? "",
            IsReadOnly = !canWrite,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MaxHeight = 320
        };
        string last = value;
        CommitOnEnterOrLostFocus(box, () =>
        {
            if (!canWrite || box.Text == (last ?? ""))
                return;
            last = box.Text;
            setter(box.Text);
        });
        return box;
    }

    private Control NumericEditor(Type type, object value, bool canWrite, Action<object> setter)
    {
        var box = new TextBox
        {
            Text = Convert.ToString(value, CultureInfo.InvariantCulture),
            IsReadOnly = !canWrite,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 160
        };
        string last = box.Text;
        CommitOnEnterOrLostFocus(box, () =>
        {
            if (!canWrite || box.Text == last)
                return;
            try
            {
                object parsed = Convert.ChangeType(box.Text.Trim(), type, CultureInfo.InvariantCulture);
                setter(parsed);
                last = box.Text;
                box.ClearValue(TextBox.BorderBrushProperty);
                ToolTip.SetTip(box, null);
            }
            catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException)
            {
                box.BorderBrush = Brushes.IndianRed;
                ToolTip.SetTip(box, $"Invalid {type.Name} value; it was not applied.");
            }
            catch (TargetInvocationException e)
            {
                box.BorderBrush = Brushes.IndianRed;
                ToolTip.SetTip(box, e.InnerException?.Message ?? e.Message);
            }
        });
        return box;
    }

    private Control EnumEditor(Type type, object value, bool canWrite, Action<object> setter)
    {
        if (type.GetCustomAttribute<FlagsAttribute>() is not null)
        {
            var panel = new WrapPanel { Orientation = Orientation.Horizontal };
            ulong current = value is null ? 0 : Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            ulong known = 0;
            foreach (string flagName in Enum.GetNames(type))
            {
                object flag = Enum.Parse(type, flagName);
                ulong bits = Convert.ToUInt64(flag, CultureInfo.InvariantCulture);
                if (bits == 0 || (bits & (bits - 1)) != 0 || (known & bits) != 0)
                    continue; // only single-bit flags get checkboxes, and aliases are skipped
                known |= bits;
                var cb = new CheckBox
                {
                    Content = Plain(flagName),
                    IsChecked = (current & bits) != 0,
                    IsEnabled = canWrite,
                    Margin = new Thickness(0, 0, 12, 0)
                };
                cb.IsCheckedChanged += (_, _) =>
                {
                    current = cb.IsChecked == true ? current | bits : current & ~bits;
                    setter(Enum.ToObject(type, current));
                };
                panel.Children.Add(cb);
            }
            ulong rest = current & ~known;
            if (rest != 0)
            {
                // Remaining bits may still form a named multi-bit value (e.g. "Regular = 0x64")
                object restValue = Enum.ToObject(type, rest);
                string restText = Enum.IsDefined(type, restValue) ? $"(+ {restValue})" : $"(+ other bits 0x{rest:X})";
                panel.Children.Add(new TextBlock { Text = restText, Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center });
            }
            return panel;
        }

        var values = Enum.GetValues(type).Cast<object>().ToList();
        if (value is not null && !values.Contains(value))
            values.Add(value); // value not defined in the enum; still show it
        var combo = new ComboBox
        {
            ItemsSource = values,
            SelectedItem = value,
            IsEnabled = canWrite,
            MinWidth = 200
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not null && !Equals(combo.SelectedItem, value))
            {
                value = combo.SelectedItem;
                setter(value);
            }
        };
        return combo;
    }

    private Control UndertaleStringEditor(UndertaleString value, bool canWrite, Action<object> setter)
    {
        string content = value?.Content;
        bool multiline = content is not null && (content.Contains('\n') || content.Length > 120);
        var box = new TextBox
        {
            Text = content ?? "",
            Watermark = value is null ? "(null)" : null,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MaxHeight = 320,
            IsReadOnly = !canWrite && value is null
        };
        ToolTip.SetTip(box, "Editing replaces the reference with a matching (or new) string, so other users of the old string are unaffected.");
        string last = content;
        CommitOnEnterOrLostFocus(box, () =>
        {
            if (box.Text == (last ?? ""))
                return;
            if (value is null && string.IsNullOrEmpty(box.Text))
                return;
            last = box.Text;
            if (canWrite && Data?.Strings is not null)
            {
                setter(Data.Strings.MakeString(box.Text));
            }
            else if (value is not null)
            {
                // Can't swap the reference, so edit the shared string directly
                value.Content = box.Text;
                ValueChanged?.Invoke();
                MarkDirty();
                mainWindow.RefreshTreeLabels();
            }
        });
        return box;
    }

    private Control ByteArrayEditor(byte[] value, bool canWrite, Action<object> setter, string label)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var info = new TextBlock { Text = value is null ? "(null)" : $"{value.Length:N0} bytes", VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(info);

        var export = new Button { Content = "Export...", IsEnabled = value is not null };
        export.Click += async (_, _) =>
        {
            string path = await DialogHelper.SaveFile(mainWindow, $"Export {label}", "All files|*", null, $"{label}.bin");
            if (path is null)
                return;
            try
            {
                File.WriteAllBytes(path, value);
            }
            catch (Exception e)
            {
                await mainWindow.ShowError($"Failed to export:\n{e.Message}");
            }
        };
        panel.Children.Add(export);

        if (canWrite)
        {
            var import = new Button { Content = "Import..." };
            import.Click += async (_, _) =>
            {
                string path = await DialogHelper.OpenFile(mainWindow, $"Import {label}", "All files|*");
                if (path is null)
                    return;
                try
                {
                    value = File.ReadAllBytes(path);
                    setter(value);
                    info.Text = $"{value.Length:N0} bytes";
                    export.IsEnabled = true;
                }
                catch (Exception e)
                {
                    await mainWindow.ShowError($"Failed to import:\n{e.Message}");
                }
            };
            panel.Children.Add(import);
        }
        return panel;
    }

    /// <summary>
    /// A link to another resource, with buttons to change or clear the reference.
    /// </summary>
    private Control ReferenceEditor(Type resourceType, object value, bool canWrite, Action<object> setter)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        var link = new Button
        {
            Classes = { "link" },
            Content = Plain(ResourceInfo.DisplayName(value)),
            IsEnabled = value is not null,
            MinWidth = 120,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        ToolTip.SetTip(link, "Click to open; middle-click or Ctrl+click to open in a new tab");
        link.Click += (_, _) => { if (value is not null) mainWindow.OpenInTab(value, false); };
        link.PointerReleased += (_, e) =>
        {
            if (value is not null && e.InitialPressMouseButton == MouseButton.Middle)
                mainWindow.OpenInTab(value, true);
        };
        link.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (value is not null && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.GetCurrentPoint(link).Properties.IsLeftButtonPressed)
            {
                mainWindow.OpenInTab(value, true);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        panel.Children.Add(link);

        ResourceCategory cat = ResourceInfo.CategoryFor(resourceType);
        if (canWrite && cat is not null)
        {
            var change = new Button { Content = "…", Padding = new Thickness(8, 1) };
            ToolTip.SetTip(change, $"Choose a different {ResourceInfo.TypeName(Activator.CreateInstance(resourceType))}");
            change.Click += async (_, _) =>
            {
                IList list = Data is null ? null : cat.GetList(Data);
                if (list is null)
                    return;
                object picked = await DialogHelper.PickFromList(mainWindow, $"Choose {cat.Name.ToLowerInvariant()}",
                                                                list.Cast<object>().Where(o => o is not null).ToList(),
                                                                ResourceInfo.DisplayName, allowNone: true);
                if (picked is null)
                    return;
                value = ReferenceEquals(picked, DialogHelper.PickNone) ? null : picked;
                setter(value);
                link.Content = Plain(ResourceInfo.DisplayName(value));
                link.IsEnabled = value is not null;
            };
            panel.Children.Add(change);
        }
        return panel;
    }

    #endregion

    #region Lists and nested objects

    private Control NestedObjectEditor(string label, object value)
    {
        string summary = ResourceInfo.DisplayName(value);
        string typeName = ResourceInfo.TypeName(value);
        var expander = new Expander
        {
            Header = summary == value.GetType().FullName || summary == typeName ? $"{label}  ({typeName})" : $"{label}: {summary}  ({typeName})",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        if (depth >= MaxDepth)
        {
            expander.Content = new TextBlock { Text = "(nesting too deep; open the object directly instead)", Classes = { "dim" } };
            return expander;
        }
        bool built = false;
        expander.Expanding += (_, _) =>
        {
            if (built)
                return;
            built = true;
            var nested = new PropertyGrid(mainWindow, value, depth + 1, affectsData);
            nested.ValueChanged += () => ValueChanged?.Invoke();
            expander.Content = nested;
        };
        return expander;
    }

    private Control ListEditor(string label, IList list)
    {
        Type elemType = list.GetType().IsGenericType
            ? list.GetType().GetGenericArguments()[0]
            : (list.GetType().GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>))?.GetGenericArguments()[0] ?? typeof(object));

        var expander = new Expander
        {
            Header = $"{label}  [{list.Count} item{(list.Count == 1 ? "" : "s")}]",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        if (depth >= MaxDepth)
        {
            expander.Content = new TextBlock { Text = "(nesting too deep)", Classes = { "dim" } };
            return expander;
        }

        void Populate()
        {
            var content = new StackPanel { Spacing = 2 };
            var items = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            content.Children.Add(items);
            int shown = 0;

            void ShowMore()
            {
                int end = Math.Min(list.Count, shown + ListPageSize);
                for (int i = shown; i < end; i++)
                    AddListItem(items, list, i, elemType, () =>
                    {
                        expander.Header = $"{label}  [{list.Count} item{(list.Count == 1 ? "" : "s")}]";
                        Populate();
                    });
                shown = end;
            }
            ShowMore();

            if (shown < list.Count)
            {
                var more = new Button { Content = $"Show more ({list.Count - shown} remaining)" };
                more.Click += (_, _) =>
                {
                    ShowMore();
                    if (shown >= list.Count)
                        content.Children.Remove(more);
                    else
                        more.Content = $"Show more ({list.Count - shown} remaining)";
                };
                content.Children.Add(more);
            }

            if (!list.IsReadOnly && !list.IsFixedSize && CanCreateElement(elemType))
            {
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
                var add = new Button { Content = "+ Add item" };
                add.Click += (_, _) =>
                {
                    try
                    {
                        list.Add(CreateElement(elemType));
                        expander.Header = $"{label}  [{list.Count} item{(list.Count == 1 ? "" : "s")}]";
                        ValueChanged?.Invoke();
                        MarkDirty();
                        Populate();
                    }
                    catch (Exception e)
                    {
                        _ = mainWindow.ShowError($"Could not add item:\n{e.Message}");
                    }
                };
                buttons.Children.Add(add);
                content.Children.Add(buttons);
            }

            expander.Content = content;
        }

        bool built = false;
        expander.Expanding += (_, _) =>
        {
            if (built)
                return;
            built = true;
            Populate();
        };
        return expander;
    }

    private bool CanCreateElement(Type t)
    {
        if (t == typeof(string) || IsNumeric(t) || t == typeof(bool) || t.IsEnum || t == typeof(UndertaleString))
            return true;
        if (ResourceInfo.CategoryFor(t) is not null)
            return true; // adds a null reference to fill in
        return !t.IsAbstract && !t.IsInterface && t.GetConstructor(Type.EmptyTypes) is not null;
    }

    private object CreateElement(Type t)
    {
        if (t == typeof(string))
            return "";
        if (t == typeof(UndertaleString))
            return Data.Strings.MakeString("");
        if (ResourceInfo.CategoryFor(t) is not null)
            return null;
        if (t.IsValueType)
            return Activator.CreateInstance(t);
        return Activator.CreateInstance(t);
    }

    private void AddListItem(Grid grid, IList list, int index, Type elemType, Action repopulate)
    {
        object item = list[index];
        Type itemType = item?.GetType() ?? elemType;

        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        // Index label with a context menu for removal/reordering
        var idx = new TextBlock
        {
            Text = $"[{index}]",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 8, 0),
            Classes = { "dim" },
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        if (!list.IsReadOnly && !list.IsFixedSize)
        {
            var menu = new ContextMenu();
            var remove = new MenuItem { Header = "Remove item" };
            remove.Click += (_, _) =>
            {
                list.RemoveAt(index);
                ValueChanged?.Invoke();
                MarkDirty();
                repopulate();
            };
            var up = new MenuItem { Header = "Move up", IsEnabled = index > 0 };
            up.Click += (_, _) => { Swap(list, index, index - 1); repopulate(); };
            var down = new MenuItem { Header = "Move down", IsEnabled = index < list.Count - 1 };
            down.Click += (_, _) => { Swap(list, index, index + 1); repopulate(); };
            menu.Items.Add(remove);
            menu.Items.Add(up);
            menu.Items.Add(down);
            idx.ContextMenu = menu;
            ToolTip.SetTip(idx, "Right-click for item actions");
        }
        SetRow(idx, row);
        SetColumn(idx, 0);
        grid.Children.Add(idx);

        Control editor;
        bool settable = !list.IsReadOnly;
        Action<object> setter = v =>
        {
            list[index] = v;
            ValueChanged?.Invoke();
            MarkDirty();
        };

        if (item is not null && ResourceInfo.CategoryFor(itemType) is null && !IsSimple(itemType) && item is not IList && item is not UndertaleResourceRef)
        {
            // Complex element: nested grid in an expander, titled by the element's name
            editor = NestedObjectEditor(ResourceInfo.DisplayName(item) == item.GetType().FullName ? ResourceInfo.TypeName(item) : ResourceInfo.DisplayName(item), item);
        }
        else
        {
            editor = CreateValueEditor(itemType, item, settable, setter, $"[{index}]", out _);
        }
        editor.Margin = new Thickness(0, 1);
        SetRow(editor, row);
        SetColumn(editor, 1);
        grid.Children.Add(editor);
    }

    private static bool IsSimple(Type t) =>
        t == typeof(string) || IsNumeric(t) || t == typeof(bool) || t.IsEnum || t == typeof(UndertaleString) || t == typeof(byte[]);

    private void Swap(IList list, int a, int b)
    {
        (list[a], list[b]) = (list[b], list[a]);
        ValueChanged?.Invoke();
        MarkDirty();
    }

    #endregion
}
