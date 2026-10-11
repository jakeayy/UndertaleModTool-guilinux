using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Plays audio by delegating to a command-line player available on the system
/// (ffplay, mpv, paplay, pw-play, aplay, afplay), falling back to the default application.
/// </summary>
public static class AudioPlayer
{
    private static Process current;
    private static string tempFile;

    private static readonly (string Exe, string Args)[] Players =
    [
        ("ffplay", "-nodisp -autoexit -loglevel quiet \"{0}\""),
        ("mpv", "--no-video --really-quiet \"{0}\""),
        ("pw-play", "\"{0}\""),
        ("paplay", "\"{0}\""),
        ("afplay", "\"{0}\""),
        ("aplay", "-q \"{0}\"") // WAV only
    ];

    /// <summary>
    /// Detects the audio container from its header bytes.
    /// </summary>
    public static string DetectExtension(byte[] data)
    {
        if (data is null || data.Length < 4)
            return ".bin";
        if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F')
            return ".wav";
        if (data[0] == 'O' && data[1] == 'g' && data[2] == 'g' && data[3] == 'S')
            return ".ogg";
        if ((data[0] == 'I' && data[1] == 'D' && data[2] == '3') || (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0))
            return ".mp3";
        return ".bin";
    }

    public static void Play(byte[] data)
    {
        Stop();
        tempFile = Path.Join(Path.GetTempPath(), $"umt_audio_{Environment.ProcessId}{DetectExtension(data)}");
        File.WriteAllBytes(tempFile, data);
        PlayFile(tempFile);
    }

    public static void PlayFile(string path)
    {
        Stop();
        string ext = Path.GetExtension(path).ToLowerInvariant();
        foreach (var (exe, args) in Players)
        {
            if (exe == "aplay" && ext != ".wav")
                continue;
            if (FindOnPath(exe) is not string fullPath)
                continue;
            try
            {
                current = Process.Start(new ProcessStartInfo(fullPath, string.Format(args, path))
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                if (current is not null)
                    return;
            }
            catch
            {
                // try the next player
            }
        }
        // Nothing suitable found: let the desktop environment handle it
        OpenWithDefaultApp(path);
    }

    public static void Stop()
    {
        try
        {
            if (current is { HasExited: false })
                current.Kill();
        }
        catch
        {
            // already gone
        }
        current?.Dispose();
        current = null;
    }

    public static void OpenWithDefaultApp(string pathOrUrl)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true })?.Dispose();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start("open", [pathOrUrl])?.Dispose();
        else
            Process.Start("xdg-open", [pathOrUrl])?.Dispose();
    }

    private static string FindOnPath(string exe)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        string[] exts = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? [".exe", ""] : [""];
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                   .SelectMany(dir => exts.Select(e => Path.Join(dir, exe + e)))
                   .FirstOrDefault(File.Exists);
    }
}
