using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace UndertaleModToolAvalonia.Dialogs;

/// <summary>
/// Shows code search results, where each code entry and line can be clicked to open it in the main window.
/// </summary>
public class SearchResultsWindow : Window
{
    private readonly MainWindow mainWindow;
    private readonly bool openDecompiled;

    public SearchResultsWindow(MainWindow mainWindow, string title, string query, int resultsCount,
                               IEnumerable<KeyValuePair<string, List<(int lineNum, string codeLine)>>> results,
                               bool openDecompiled, IEnumerable<string> failedList)
    {
        this.mainWindow = mainWindow;
        this.openDecompiled = openDecompiled;
        Title = title;
        Width = 900;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var resultList = results.ToList();
        var failed = failedList?.ToList() ?? [];

        var header = new TextBlock
        {
            Text = $"{resultsCount} result{(resultsCount == 1 ? "" : "s")} in {resultList.Count} code entr{(resultList.Count == 1 ? "y" : "ies")} for \"{query}\"." +
                   (failed.Count > 0 ? $"  {failed.Count} entr{(failed.Count == 1 ? "y" : "ies")} failed to process." : "") +
                   "  Click an entry or line to open it.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };

        // Each code entry is a separate expander-like block; keep it light by using one SelectableTextBlock per entry.
        var panel = new StackPanel { Spacing = 6 };
        foreach (var (codeName, lines) in resultList)
        {
            var nameButton = new Button
            {
                Content = new TextBlock { Text = $"{codeName}  ({lines.Count})" },
                Classes = { "link" },
                FontWeight = FontWeight.SemiBold,
                Padding = new Thickness(2, 0)
            };
            nameButton.Click += (_, _) => Open(codeName, -1);
            panel.Children.Add(nameButton);

            foreach (var (lineNum, codeLine) in lines.Take(200))
            {
                var row = new TextBlock
                {
                    FontFamily = new FontFamily("monospace"),
                    Margin = new Thickness(18, 0, 0, 0),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                row.Inlines = new InlineCollection
                {
                    new Run($"{lineNum,6}: ") { Foreground = Brushes.Gray },
                    new Run(codeLine.Trim())
                };
                int ln = lineNum;
                row.PointerPressed += (_, _) => Open(codeName, ln);
                panel.Children.Add(row);
            }
            if (lines.Count > 200)
                panel.Children.Add(new TextBlock { Text = $"   ... and {lines.Count - 200} more", Margin = new Thickness(18, 0, 0, 0), Classes = { "dim" } });
        }

        if (failed.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Failed to process:", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
            foreach (string f in failed)
                panel.Children.Add(new TextBlock { Text = f, Margin = new Thickness(18, 0, 0, 0) });
        }

        var copy = new Button { Content = "Copy all as text" };
        copy.Click += async (_, _) =>
        {
            var sb = new StringBuilder();
            foreach (var (codeName, lines) in resultList)
            {
                sb.AppendLine(codeName);
                foreach (var (lineNum, codeLine) in lines)
                    sb.AppendLine($"  {lineNum}: {codeLine}");
            }
            if (Clipboard is { } cb)
                await cb.SetTextAsync(sb.ToString());
        };

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Children = { copy } };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = panel });
        Content = root;
    }

    private void Open(string codeName, int lineNum)
    {
        mainWindow.OpenCodeEntry(codeName, lineNum, openDecompiled);
        mainWindow.Activate();
    }
}
