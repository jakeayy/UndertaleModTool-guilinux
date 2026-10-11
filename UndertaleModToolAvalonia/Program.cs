using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;

namespace UndertaleModToolAvalonia;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!CheckLinuxDependencies())
            return 1;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Avalonia's X11 backend unconditionally loads these libraries, and aborts the process
    /// (with an unreadable native crash) if they're missing, which happens on minimal installs and WSL.
    /// </summary>
    private static bool CheckLinuxDependencies()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
            return true;

        var missing = new List<string>();
        foreach (string lib in new[] { "libICE.so.6", "libSM.so.6", "libfontconfig.so.1" })
        {
            if (NativeLibrary.TryLoad(lib, out IntPtr handle))
                NativeLibrary.Free(handle);
            else
                missing.Add(lib);
        }
        if (missing.Count == 0)
            return true;

        Console.Error.WriteLine($"UndertaleModTool can't start: missing system libraries: {string.Join(", ", missing)}");
        Console.Error.WriteLine("Install them with your package manager, for example:");
        Console.Error.WriteLine("  Debian/Ubuntu: sudo apt install libice6 libsm6 libfontconfig1");
        Console.Error.WriteLine("  Fedora:        sudo dnf install libICE libSM fontconfig");
        Console.Error.WriteLine("  Arch:          sudo pacman -S libice libsm fontconfig");
        return false;
    }
}
