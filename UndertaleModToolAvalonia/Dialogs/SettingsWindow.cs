using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using UndertaleModToolAvalonia.Editors;

namespace UndertaleModToolAvalonia.Dialogs;

/// <summary>
/// Settings: appearance, behaviour, and decompiler options. Saved when closed.
/// </summary>
public class SettingsWindow : Window
{
    public SettingsWindow(MainWindow mainWindow)
    {
        Title = "Settings";
        Width = 640;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AppSettings s = AppSettings.Instance;

        var theme = new ComboBox { ItemsSource = new[] { "Default", "Light", "Dark" }, SelectedItem = s.Theme ?? "Default", MinWidth = 160 };
        theme.SelectionChanged += (_, _) =>
        {
            s.Theme = theme.SelectedItem as string ?? "Default";
            App.ApplyTheme();
        };

        var warnOnClose = new CheckBox { Content = "Warn about unsaved changes when closing", IsChecked = s.WarnOnClose };
        warnOnClose.IsCheckedChanged += (_, _) => s.WarnOnClose = warnOnClose.IsChecked == true;

        var showNull = new CheckBox { Content = "Show null entries in the resource tree", IsChecked = s.ShowNullEntriesInResourceTree };
        showNull.IsCheckedChanged += (_, _) => s.ShowNullEntriesInResourceTree = showNull.IsChecked == true;

        var prefix = new TextBox { Text = s.InstanceIdPrefix, MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
        prefix.TextChanged += (_, _) => s.InstanceIdPrefix = string.IsNullOrEmpty(prefix.Text) ? "inst_" : prefix.Text;
        ToolTip.SetTip(prefix, "Prefix used by the decompiler/compiler for room instance IDs");

        var general = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "General", Classes = { "header" } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Theme", VerticalAlignment = VerticalAlignment.Center, Width = 150 }, theme } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Instance ID prefix", VerticalAlignment = VerticalAlignment.Center, Width = 150 }, prefix } },
                warnOnClose,
                showNull,
                new TextBlock { Text = $"Settings file: {System.IO.Path.Join(AppSettings.ConfigFolder, "settings-avalonia.json")}", Classes = { "dim" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = "Decompiler", Classes = { "header" }, Margin = new Thickness(0, 12, 0, 0) },
                new PropertyGrid(mainWindow, s.DecompilerSettings, affectsData: false)
            }
        };

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(close, Dock.Bottom);
        close.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(close);
        root.Children.Add(new ScrollViewer { Content = general, Padding = new Thickness(0, 0, 12, 0) });
        Content = root;

        Closed += (_, _) => AppSettings.Save();
    }
}
