using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Underanalyzer.Decompiler;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModLib.Scripting;
using UndertaleModLib.Util;
using UndertaleModToolAvalonia.Dialogs;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Implementation of <see cref="IScriptInterface"/>, so that UndertaleModTool C# scripts (.csx) can run in this GUI.
/// </summary>
/// <remarks>
/// Scripts run on a background thread. Every method that touches the UI marshals to the UI thread and,
/// where the interface is synchronous, blocks the script thread until the user responds.
/// </remarks>
public partial class MainWindow : IScriptInterface
{
    private Task<ScriptOptions> scriptOptionsTask;
    private LoaderDialog scriptDialog;
    private bool finishedMessageEnabled = true;
    private int progressValue;
    private CancellationTokenSource progressCts;
    private Task progressUpdater;

    public string ScriptPath { get; private set; }
    public bool ScriptExecutionSuccess { get; private set; } = true;
    public string ScriptErrorMessage { get; private set; } = "";
    public string ScriptErrorType { get; private set; } = "";
    public bool IsAppClosed { get; private set; }
    public Action<Action> MainThreadAction => f => RunOnUI(f);

    UndertaleData IScriptInterface.Data => Data;
    object IScriptInterface.Highlighted => Highlighted;
    object IScriptInterface.Selected => Selected;

    private void InitializeScripting()
    {
        Closed += (_, _) => IsAppClosed = true;

        // Building script options loads Roslyn, which is slow; do it in the background
        scriptOptionsTask = Task.Run(() =>
            ScriptingUtil.CreateDefaultScriptOptions()
                         .AddImports("UndertaleModToolAvalonia", "System.Linq", "System.Threading.Tasks")
                         .AddReferences(typeof(MainWindow).Assembly,
                                        typeof(Enumerable).Assembly,
                                        typeof(Avalonia.Controls.Window).Assembly));
    }

    #region Thread helpers

    private static void RunOnUI(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Invoke(action);
    }

    /// <summary>
    /// Runs an async UI operation and blocks until it completes, from any thread.
    /// On the UI thread, a nested dispatcher frame keeps the UI responsive meanwhile.
    /// </summary>
    private static T RunOnUISync<T>(Func<Task<T>> func)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.InvokeAsync(func).GetAwaiter().GetResult();

        Task<T> task = func();
        if (!task.IsCompleted)
        {
            using var cts = new CancellationTokenSource();
            task.ContinueWith(_ => cts.Cancel(), TaskScheduler.Default);
            Dispatcher.UIThread.MainLoop(cts.Token);
        }
        return task.GetAwaiter().GetResult();
    }

    private static void RunOnUISync(Func<Task> func) => RunOnUISync(async () => { await func(); return true; });

    #endregion

    #region Running scripts

    public async Task RunScript(string path)
    {
        if (!File.Exists(path))
        {
            await ShowError($"The script file doesn't exist:\n{path}");
            return;
        }
        AppSettings.Instance.LastScriptDirectory = Path.GetDirectoryName(path);

        ScriptExecutionSuccess = true;
        ScriptErrorMessage = "";
        ScriptErrorType = "";
        finishedMessageEnabled = true;
        ScriptPath = path;

        // Make sure in-progress code edits are part of the data the script sees
        if (!await CommitAllEditors())
            return;

        CommandBox.Text = $"Running {Path.GetFileName(path)} ...";
        IsEnabled = false;
        scriptDialog = null;

        string scriptText = $"#line 1 \"{path}\"\n" + await File.ReadAllTextAsync(path, Encoding.UTF8);
        object result = null;
        Exception error = null;
        try
        {
            ScriptOptions options = await scriptOptionsTask;
            result = await Task.Run(async () =>
                await CSharpScript.EvaluateAsync(scriptText, options.WithFilePath(path).WithFileEncoding(Encoding.UTF8),
                                                 this, typeof(IScriptInterface)));
        }
        catch (Exception e)
        {
            error = e;
        }

        await StopProgressBarUpdater();
        HideProgressBar();
        IsEnabled = true;

        if (error is CompilationErrorException compileError)
        {
            Console.Error.WriteLine(compileError);
            ScriptExecutionSuccess = false;
            ScriptErrorMessage = compileError.Message;
            ScriptErrorType = "CompilationErrorException";
            CommandBox.Text = compileError.Message;
            await ShowError(compileError.Message, "Script compile error");
        }
        else if (error is not null)
        {
            Console.Error.WriteLine(error);
            ScriptExecutionSuccess = false;
            ScriptErrorMessage = error.Message;
            ScriptErrorType = "Exception";
            CommandBox.Text = error.Message;
            await ShowError(ScriptingUtil.PrettifyException(in error), "Script error");
        }
        else if (finishedMessageEnabled)
        {
            CommandBox.Text = result?.ToString() ?? $"{Path.GetFileName(path)} finished!";
        }

        // Scripts commonly add/remove/rename resources
        MarkDirty();
        RebuildTree();
        RefreshCurrentTab();
        GC.Collect();
    }

    public bool RunUMTScript(string path)
    {
        // Called from within a running script (on the script thread)
        try
        {
            string text = $"#line 1 \"{path}\"\n" + File.ReadAllText(path, Encoding.UTF8);
            ScriptOptions options = scriptOptionsTask.GetAwaiter().GetResult();
            CSharpScript.EvaluateAsync(text, options.WithFilePath(path).WithFileEncoding(Encoding.UTF8), this, typeof(IScriptInterface))
                        .GetAwaiter().GetResult();
            return true;
        }
        catch (Exception e)
        {
            ScriptError(ScriptingUtil.PrettifyException(in e), "Script error");
            return false;
        }
    }

    public bool LintUMTScript(string path)
    {
        try
        {
            ScriptOptions options = scriptOptionsTask.GetAwaiter().GetResult();
            var script = CSharpScript.Create(File.ReadAllText(path, Encoding.UTF8), options.WithFilePath(path), typeof(IScriptInterface));
            var diagnostics = script.Compile().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
            if (diagnostics.Count > 0)
            {
                ScriptError(string.Join("\n", diagnostics.Select(d => d.ToString())), "Script lint error");
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            ScriptError(e.Message, "Script lint error");
            return false;
        }
    }

    private async void RunCommand_Click(object sender, RoutedEventArgs e) => await RunCommand();

    private async void CommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await RunCommand();
        }
    }

    /// <summary>
    /// Evaluates the command box contents as C#, like the Windows GUI's command box.
    /// </summary>
    private async Task RunCommand()
    {
        string code = CommandBox.Text;
        if (string.IsNullOrWhiteSpace(code))
            return;
        IsEnabled = false;
        try
        {
            ScriptOptions options = await scriptOptionsTask;
            object result = await Task.Run(async () => await CSharpScript.EvaluateAsync(code, options, this, typeof(IScriptInterface)));
            CommandBox.Text = result is null ? "" : Convert.ToString(result);
        }
        catch (CompilationErrorException ex)
        {
            CommandBox.Text = ex.Message;
        }
        catch (Exception ex)
        {
            CommandBox.Text = ScriptingUtil.PrettifyException(in ex);
        }
        finally
        {
            IsEnabled = true;
        }
        RefreshTreeLabels();
    }

    private void BuildScriptsMenu()
    {
        ScriptsMenu.Items.Clear();
        string scriptsDir = Path.Join(ExePath, "Scripts");
        if (Directory.Exists(scriptsDir))
            AddScriptItems(ScriptsMenu, new DirectoryInfo(scriptsDir));
        else
            ScriptsMenu.Items.Add(new MenuItem { Header = "(no Scripts folder found)", IsEnabled = false });

        ScriptsMenu.Items.Add(new Separator());
        var other = new MenuItem { Header = "Run _other script..." };
        other.Click += async (_, _) =>
        {
            string path = await DialogHelper.OpenFile(this, "Run script", "Scripts (*.csx)|*.csx|All files|*", AppSettings.Instance.LastScriptDirectory);
            if (path is not null)
                await RunScript(path);
        };
        ScriptsMenu.Items.Add(other);
        var refresh = new MenuItem { Header = "Refresh script list" };
        refresh.Click += (_, _) => BuildScriptsMenu();
        ScriptsMenu.Items.Add(refresh);
    }

    private void AddScriptItems(MenuItem parent, DirectoryInfo dir)
    {
        foreach (var sub in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var item = new MenuItem { Header = sub.Name.Replace("_", "__") };
            AddScriptItems(item, sub);
            if (item.Items.Count > 0)
                parent.Items.Add(item);
        }
        foreach (var file in dir.EnumerateFiles("*.csx").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var item = new MenuItem { Header = Path.GetFileNameWithoutExtension(file.Name).Replace("_", "__") };
            string path = file.FullName;
            item.Click += async (_, _) => await RunScript(path);
            parent.Items.Add(item);
        }
    }

    #endregion

    #region IScriptInterface: messages and dialogs

    public void EnsureDataLoaded()
    {
        if (Data is null)
            throw new ScriptException("Please load data.win first!");
    }

    public bool MakeNewDataFile()
    {
        RunOnUI(MakeNewDataFileInternal);
        return true;
    }

    public void ScriptMessage(string message) => RunOnUISync(() => ShowMessage(message, "Script message"));

    public void ScriptWarning(string message) => RunOnUISync(() => ShowWarning(message, "Script warning"));

    public bool ScriptQuestion(string message) => RunOnUISync(() => ShowQuestion(message, "Script question"));

    public void ScriptError(string error, string title = "Error", bool SetConsoleText = true)
    {
        RunOnUISync(() => ShowError(error, title));
        if (SetConsoleText)
        {
            SetUMTConsoleText(error);
            SetFinishedMessage(false);
        }
    }

    public void SetUMTConsoleText(string message) => RunOnUI(() => CommandBox.Text = message);

    public void SetFinishedMessage(bool isFinishedMessageEnabled) => finishedMessageEnabled = isFinishedMessageEnabled;

    public void ScriptOpenURL(string url)
    {
        try
        {
            AudioPlayer.OpenWithDefaultApp(url);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Failed to open URL {url}: {e.Message}");
        }
    }

    public string ScriptInputDialog(string title, string label, string defaultInput, string cancelText, string submitText, bool isMultiline, bool preventClose)
    {
        return RunOnUISync(() => DialogHelper.ShowTextInput(this, title, label, defaultInput, isMultiline, submitText, cancelText, preventClose));
    }

    public string SimpleTextInput(string title, string label, string defaultValue, bool allowMultiline, bool showDialog = true)
    {
        if (!showDialog)
        {
            RunOnUI(() => _ = DialogHelper.ShowTextOutput(this, title, label, defaultValue, modal: false));
            return null;
        }
        return RunOnUISync(() => DialogHelper.ShowTextInput(this, title, label, defaultValue, allowMultiline));
    }

    public void SimpleTextOutput(string title, string label, string message, bool allowMultiline)
    {
        if (!allowMultiline && message is not null && message.Contains('\n'))
            message = message[..message.IndexOf('\n')];
        RunOnUI(() => _ = DialogHelper.ShowTextOutput(this, title, label, message, modal: false));
    }

    public Task ClickableSearchOutput(string title, string query, int resultsCount,
                                      IOrderedEnumerable<KeyValuePair<string, List<(int lineNum, string codeLine)>>> resultsDict,
                                      bool showInDecompiledView, IOrderedEnumerable<string> failedList = null)
    {
        return ShowSearchResults(title, query, resultsCount, resultsDict, showInDecompiledView, failedList);
    }

    public Task ClickableSearchOutput(string title, string query, int resultsCount,
                                      IDictionary<string, List<(int lineNum, string codeLine)>> resultsDict,
                                      bool showInDecompiledView, IEnumerable<string> failedList = null)
    {
        return ShowSearchResults(title, query, resultsCount, resultsDict, showInDecompiledView, failedList);
    }

    private Task ShowSearchResults(string title, string query, int resultsCount,
                                   IEnumerable<KeyValuePair<string, List<(int lineNum, string codeLine)>>> results,
                                   bool decompiled, IEnumerable<string> failed)
    {
        // Materialize on the calling thread, then show on the UI thread
        var list = results.ToList();
        var failedList = failed?.ToList();
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            new SearchResultsWindow(this, title, query, resultsCount, list, decompiled, failedList).Show(this);
        }).GetTask();
    }

    public void ChangeSelection(object newSelection, bool inNewTab = false)
    {
        RunOnUI(() => OpenInTab(newSelection, inNewTab));
    }

    public string PromptChooseDirectory()
    {
        return RunOnUISync(() => DialogHelper.OpenFolder(this, "Choose a folder", Data is null ? null : Path.GetDirectoryName(FilePath)));
    }

    public string PromptLoadFile(string defaultExt, string filter)
    {
        return RunOnUISync(() => DialogHelper.OpenFile(this, "Open file", filter ?? "All files|*"));
    }

    public string PromptSaveFile(string defaultExt, string filter)
    {
        return RunOnUISync(() => DialogHelper.SaveFile(this, "Save file", filter ?? "All files|*", defaultExt));
    }

    #endregion

    #region IScriptInterface: progress

    public void InitializeScriptDialog()
    {
        RunOnUI(() => scriptDialog ??= new LoaderDialog("Script in progress...", "Please wait..."));
    }

    public void SetProgressBar(string message, string status, double progressValue, double maxValue)
    {
        InitializeScriptDialog();
        this.progressValue = (int)progressValue;
        scriptDialog.SavedStatusText = status;
        UpdateProgressBar(message, status, progressValue, maxValue);
        SetProgressBar();
    }

    public void SetProgressBar()
    {
        InitializeScriptDialog();
        RunOnUI(() =>
        {
            if (scriptDialog is { IsVisible: false })
                scriptDialog.Show(this);
        });
    }

    public void UpdateProgressBar(string message, string status, double progressValue, double maxValue)
    {
        scriptDialog?.Update(message, status, progressValue, maxValue);
    }

    public void UpdateProgressValue(double progressValue) => scriptDialog?.ReportValue(progressValue);

    public void UpdateProgressStatus(string status) => scriptDialog?.ReportStatus(status);

    public void HideProgressBar()
    {
        LoaderDialog dialog = scriptDialog;
        scriptDialog = null;
        dialog?.ForceClose();
    }

    public void AddProgress(int amount) => progressValue += amount;
    public void IncrementProgress() => progressValue++;
    public void AddProgressParallel(int amount) => Interlocked.Add(ref progressValue, amount);
    public void IncrementProgressParallel() => Interlocked.Increment(ref progressValue);
    public int GetProgress() => progressValue;
    public void SetProgress(int value) => progressValue = value;

    public void EnableUI() => RunOnUI(() => IsEnabled = true);

    public void StartProgressBarUpdater()
    {
        if (progressCts is not null)
            Console.Error.WriteLine("Warning: another progress bar updater task is already running.");

        progressCts = new CancellationTokenSource();
        CancellationToken token = progressCts.Token;
        progressUpdater = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                UpdateProgressValue(progressValue);
                try
                {
                    await Task.Delay(33, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
            UpdateProgressValue(progressValue);
        });
    }

    public async Task StopProgressBarUpdater()
    {
        if (progressCts is null)
            return;
        progressCts.Cancel();
        try
        {
            await progressUpdater.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("Stopping the progress bar updater task failed.");
        }
        progressCts.Dispose();
        progressCts = null;
    }

    #endregion

    #region IScriptInterface: code

    public string GetDecompiledText(string codeName, GlobalDecompileContext context = null, IDecompileSettings settings = null)
    {
        return GetDecompiledText(Data.Code.ByName(codeName), context, settings);
    }

    public string GetDecompiledText(UndertaleCode code, GlobalDecompileContext context = null, IDecompileSettings settings = null)
    {
        if (code is null)
            return "";
        if (code.ParentEntry is not null)
            return $"// This code entry is a reference to an anonymous function within \"{code.ParentEntry.Name.Content}\", decompile that instead.";

        // Prefer source code from an open project, if any
        if (Project is not null && Project.TryGetCodeSource(code, out string source))
            return source;

        try
        {
            return new DecompileContext(context ?? new GlobalDecompileContext(Data), code, settings ?? Data.ToolInfo.DecompilerSettings)
                .DecompileToString();
        }
        catch (Exception e)
        {
            return "/*\nDECOMPILER FAILED!\n\n" + e + "\n*/";
        }
    }

    public string GetDisassemblyText(string codeName) => GetDisassemblyText(Data.Code.ByName(codeName));

    public string GetDisassemblyText(UndertaleCode code)
    {
        if (code is null)
            return "";
        if (code.ParentEntry is not null)
            return $"; This code entry is a reference to an anonymous function within \"{code.ParentEntry.Name.Content}\", disassemble that instead.";
        try
        {
            return code.Disassemble(Data.Variables, Data.CodeLocals?.For(code));
        }
        catch (Exception e)
        {
            return "/*\nDISASSEMBLY FAILED!\n\n" + e + "\n*/";
        }
    }

    #endregion

    #region Search in code

    private async void SearchInCode_Click(object sender, RoutedEventArgs e)
    {
        if (Data?.Code is null)
            return;

        // Build a small search options dialog
        var dialog = new Window
        {
            Title = "Search in code",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var query = new TextBox { Watermark = "Text to find" };
        var nameFilter = new TextBox { Watermark = "Only in code entries whose name contains... (optional)" };
        var caseSensitive = new CheckBox { Content = "Case sensitive" };
        var regex = new CheckBox { Content = "Regular expression" };
        var assembly = new CheckBox { Content = "Search disassembly instead of decompiled code" };
        var ok = new Button { Content = "Search", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        bool go = false;
        ok.Click += (_, _) => { go = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children =
            {
                query, nameFilter, caseSensitive, regex, assembly,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } }
            }
        };
        dialog.Opened += (_, _) => query.Focus();
        await dialog.ShowDialog(this);
        if (!go || string.IsNullOrEmpty(query.Text))
            return;

        await SearchInCode(query.Text, nameFilter.Text, caseSensitive.IsChecked == true, regex.IsChecked == true, assembly.IsChecked == true);
    }

    private async Task SearchInCode(string text, string nameFilter, bool caseSensitive, bool isRegex, bool inAssembly)
    {
        if (!await CommitAllEditors())
            return;

        Regex keywordRegex = null;
        if (isRegex)
        {
            try
            {
                keywordRegex = new Regex(text, caseSensitive ? RegexOptions.Compiled : RegexOptions.Compiled | RegexOptions.IgnoreCase);
            }
            catch (ArgumentException e)
            {
                await ShowError($"Invalid regular expression: {e.Message}");
                return;
            }
        }

        var entries = Data.Code.Where(c => c is not null && c.ParentEntry is null &&
                                           (string.IsNullOrEmpty(nameFilter) || (c.Name?.Content?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false)))
                               .ToList();
        if (entries.Count == 0)
        {
            await ShowMessage("There are no code entries that match the name filter.");
            return;
        }

        var results = new ConcurrentDictionary<string, List<(int, string)>>();
        var failed = new ConcurrentBag<string>();
        int resultCount = 0, progress = 0;
        var comparison = caseSensitive ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;
        GlobalDecompileContext context = inAssembly ? null : new GlobalDecompileContext(Data);

        var loader = new LoaderDialog("Searching...", "Searching in code entries...");
        loader.Update(null, "Code entries", 0, entries.Count);
        Task search = Task.Run(() =>
        {
            Parallel.ForEach(entries, code =>
            {
                try
                {
                    string codeText = inAssembly
                        ? code.Disassemble(Data.Variables, Data.CodeLocals?.For(code), Data.CodeLocals is null)
                        : (Project is not null && Project.TryGetCodeSource(code, out string src)
                            ? src
                            : new DecompileContext(context, code, Data.ToolInfo.DecompilerSettings).DecompileToString());

                    var matches = new List<int>();
                    if (keywordRegex is not null)
                    {
                        foreach (Match m in keywordRegex.Matches(codeText))
                            matches.Add(m.Index);
                    }
                    else
                    {
                        int index = 0;
                        while ((index = codeText.IndexOf(text, index, comparison)) != -1)
                        {
                            matches.Add(index);
                            index += Math.Max(1, text.Length);
                        }
                    }

                    if (matches.Count > 0)
                    {
                        var lines = new List<(int, string)>();
                        int lineNumber = 0, lineStart = 0, scanned = 0;
                        foreach (int index in matches)
                        {
                            for (; scanned < index; scanned++)
                            {
                                if (codeText[scanned] == '\n')
                                {
                                    lineNumber++;
                                    lineStart = scanned + 1;
                                }
                            }
                            int lineEnd = codeText.IndexOf('\n', index);
                            if (lineEnd == -1)
                                lineEnd = codeText.Length;
                            string line = lineEnd - lineStart > 128 ? codeText[lineStart..(lineStart + 128)] + "..." : codeText[lineStart..lineEnd];
                            lines.Add((lineNumber + 1, line.TrimEnd('\r')));
                            Interlocked.Increment(ref resultCount);
                        }
                        results[code.Name.Content] = lines;
                    }
                }
                catch
                {
                    failed.Add(code.Name?.Content ?? "(unnamed)");
                }
                loader.ReportValue(Interlocked.Increment(ref progress));
            });
        });
        _ = search.ContinueWith(_ => loader.ForceClose(), TaskScheduler.Default);
        await loader.ShowDialog(this);
        await search;

        // Keep the data file's order
        var order = new Dictionary<string, int>();
        for (int i = 0; i < Data.Code.Count; i++)
        {
            if (Data.Code[i]?.Name?.Content is string n)
                order.TryAdd(n, i);
        }
        var sorted = results.OrderBy(r => order.GetValueOrDefault(r.Key, int.MaxValue)).ToList();
        var failedSorted = failed.OrderBy(f => order.GetValueOrDefault(f, int.MaxValue)).ToList();

        if (sorted.Count == 0 && failedSorted.Count == 0)
        {
            await ShowMessage($"No results found for \"{text}\".", "Search in code");
            return;
        }
        new SearchResultsWindow(this, $"Search results for \"{text}\"", text, resultCount, sorted, !inAssembly, failedSorted).Show(this);
    }

    #endregion
}
