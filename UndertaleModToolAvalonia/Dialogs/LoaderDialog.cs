using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace UndertaleModToolAvalonia.Dialogs;

/// <summary>
/// A non-closable progress window. All public members are thread-safe.
/// </summary>
public class LoaderDialog : Window
{
    private readonly TextBlock messageText;
    private readonly TextBlock statusText;
    private readonly ProgressBar progressBar;
    private bool allowClose;

    public string SavedStatusText { get; set; }

    public LoaderDialog(string title, string message)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        messageText = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        statusText = new TextBlock { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 3 };
        progressBar = new ProgressBar { IsIndeterminate = true, Height = 18, Minimum = 0, Maximum = 1, ShowProgressText = false };

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children = { messageText, progressBar, statusText }
        };

        Closing += (_, e) =>
        {
            if (!allowClose)
                e.Cancel = true;
        };
    }

    private static void OnUI(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess())
            a();
        else
            Dispatcher.UIThread.Post(a);
    }

    /// <summary>
    /// Updates all fields. A <paramref name="maxValue"/> of 0 or less shows an indeterminate bar.
    /// </summary>
    public void Update(string message, string status, double value, double maxValue)
    {
        OnUI(() =>
        {
            if (message is not null)
                messageText.Text = message;
            SetStatus(status, value, maxValue);
        });
    }

    private void SetStatus(string status, double value, double maxValue)
    {
        if (maxValue > 0)
        {
            progressBar.IsIndeterminate = false;
            progressBar.Maximum = maxValue;
            progressBar.Value = Math.Clamp(value, 0, maxValue);
            statusText.Text = $"{status} ({value:0}/{maxValue:0})";
        }
        else
        {
            progressBar.IsIndeterminate = true;
            statusText.Text = status;
        }
    }

    public void ReportStatus(string status)
    {
        OnUI(() =>
        {
            SavedStatusText = status;
            SetStatus(status, progressBar.Value, progressBar.IsIndeterminate ? 0 : progressBar.Maximum);
        });
    }

    public void ReportValue(double value)
    {
        OnUI(() => SetStatus(SavedStatusText, value, progressBar.IsIndeterminate ? 0 : progressBar.Maximum));
    }

    public void SetMaximum(double? max)
    {
        OnUI(() =>
        {
            if (max is double m && m > 0)
            {
                progressBar.IsIndeterminate = false;
                progressBar.Maximum = m;
                progressBar.Value = 0;
            }
            else
                progressBar.IsIndeterminate = true;
        });
    }

    /// <summary>
    /// Closes the dialog for good.
    /// </summary>
    public void ForceClose()
    {
        OnUI(() =>
        {
            allowClose = true;
            Close();
        });
    }
}
