using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace UndertaleModToolAvalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppSettings.Load();
        ApplyTheme();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            // Open a data file passed on the command line (e.g. from a file manager)
            string file = desktop.Args?.FirstOrDefault(a => !a.StartsWith('-'));
            if (file is not null)
                window.Opened += async (_, _) => await window.LoadFile(file);
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme()
    {
        if (Current is null)
            return;
        Current.RequestedThemeVariant = AppSettings.Instance.Theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }
}
