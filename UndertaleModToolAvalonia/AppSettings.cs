using System;
using System.IO;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Underanalyzer.Decompiler;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Persistent settings, stored as JSON in the user's config directory
/// (e.g. <c>~/.config/UndertaleModTool/settings-avalonia.json</c> on Linux).
/// </summary>
public class AppSettings
{
    public static readonly string ConfigFolder = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UndertaleModTool");

    private static string SettingsPath => Path.Join(ConfigFolder, "settings-avalonia.json");

    public static AppSettings Instance { get; private set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>"Default" (follow system), "Light", or "Dark".</summary>
    public string Theme { get; set; } = "Default";
    public bool WarnOnClose { get; set; } = true;
    public bool ShowNullEntriesInResourceTree { get; set; } = false;
    public string InstanceIdPrefix { get; set; } = "inst_";
    public DecompilerSettings DecompilerSettings { get; set; } = new();
    public string LastOpenedDirectory { get; set; }
    public string LastScriptDirectory { get; set; }
    public string[] RecentFiles { get; set; } = [];

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                Instance = JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(SettingsPath), JsonOptions) ?? new();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Failed to load settings, using defaults: {e.Message}");
            Instance = new();
        }
        Instance.DecompilerSettings ??= new();
        Instance.RecentFiles ??= [];
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigFolder);
            File.WriteAllBytes(SettingsPath, JsonSerializer.SerializeToUtf8Bytes(Instance, JsonOptions));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Failed to save settings: {e.Message}");
        }
    }

    public void AddRecentFile(string path)
    {
        var list = new System.Collections.Generic.List<string>(RecentFiles);
        list.Remove(path);
        list.Insert(0, path);
        if (list.Count > 10)
            list.RemoveRange(10, list.Count - 10);
        RecentFiles = list.ToArray();
        Save();
    }
}

/// <summary>
/// GML decompiler settings instance used by the main UndertaleModTool tool.
/// </summary>
public class DecompilerSettings : IDecompileSettings
{
    /// <summary>
    /// Types of indents provided for in-tool decompilation.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<IndentStyleKind>))]
    public enum IndentStyleKind
    {
        FourSpaces,
        TwoSpaces,
        Tabs
    }

    // Inner settings used to store values that we don't have any business reimplementing
    [JsonIgnore]
    private DecompileSettings _innerSettings;

    /// <summary>
    /// Indentation style being used for decompilation.
    /// </summary>
    public IndentStyleKind IndentStyle { get; set; }

    /// <inheritdoc/>
    [JsonIgnore]
    public string IndentString
    {
        get => IndentStyle switch
        {
            IndentStyleKind.FourSpaces => "    ",
            IndentStyleKind.TwoSpaces => "  ",
            IndentStyleKind.Tabs => "\t",
            _ => throw new Exception("Unknown indent style")
        };
    }

    // Interface implementation (passes through to inner settings instance)
    public bool UseSemicolon { get => _innerSettings.UseSemicolon; set => _innerSettings.UseSemicolon = value; }
    public bool UseCSSColors { get => _innerSettings.UseCSSColors; set => _innerSettings.UseCSSColors = value; }
    public bool PrintWarnings { get => _innerSettings.PrintWarnings; set => _innerSettings.PrintWarnings = value; }
    public bool MacroDeclarationsAtTop { get => _innerSettings.MacroDeclarationsAtTop; set => _innerSettings.MacroDeclarationsAtTop = value; }
    public bool EmptyLineAfterBlockLocals { get => _innerSettings.EmptyLineAfterBlockLocals; set => _innerSettings.EmptyLineAfterBlockLocals = value; }
    public bool EmptyLineAroundEnums { get => _innerSettings.EmptyLineAroundEnums; set => _innerSettings.EmptyLineAroundEnums = value; }
    public bool EmptyLineAroundBranchStatements { get => _innerSettings.EmptyLineAroundBranchStatements; set => _innerSettings.EmptyLineAroundBranchStatements = value; }
    public bool EmptyLineBeforeSwitchCases { get => _innerSettings.EmptyLineBeforeSwitchCases; set => _innerSettings.EmptyLineBeforeSwitchCases = value; }
    public bool EmptyLineAfterSwitchCases { get => _innerSettings.EmptyLineAfterSwitchCases; set => _innerSettings.EmptyLineAfterSwitchCases = value; }
    public bool EmptyLineAroundFunctionDeclarations { get => _innerSettings.EmptyLineAroundFunctionDeclarations; set => _innerSettings.EmptyLineAroundFunctionDeclarations = value; }
    public bool EmptyLineAroundStaticInitialization { get => _innerSettings.EmptyLineAroundStaticInitialization; set => _innerSettings.EmptyLineAroundStaticInitialization = value; }
    public bool OpenBlockBraceOnSameLine { get => _innerSettings.OpenBlockBraceOnSameLine; set => _innerSettings.OpenBlockBraceOnSameLine = value; }
    public bool RemoveSingleLineBlockBraces { get => _innerSettings.RemoveSingleLineBlockBraces; set => _innerSettings.RemoveSingleLineBlockBraces = value; }
    public bool CleanupTry { get => _innerSettings.CleanupTry; set => _innerSettings.CleanupTry = value; }
    public bool CleanupElseToContinue { get => _innerSettings.CleanupElseToContinue; set => _innerSettings.CleanupElseToContinue = value; }
    public bool CleanupDefaultArgumentValues { get => _innerSettings.CleanupDefaultArgumentValues; set => _innerSettings.CleanupDefaultArgumentValues = value; }
    public bool CleanupBuiltinArrayVariables { get => _innerSettings.CleanupBuiltinArrayVariables; set => _innerSettings.CleanupBuiltinArrayVariables = value; }
    public bool CleanupLocalVarDeclarations { get => _innerSettings.CleanupLocalVarDeclarations; set => _innerSettings.CleanupLocalVarDeclarations = value; }
    public bool CleanupTemplateStrings { get => _innerSettings.CleanupTemplateStrings; set => _innerSettings.CleanupTemplateStrings = value; }
    public bool CreateEnumDeclarations { get => _innerSettings.CreateEnumDeclarations; set => _innerSettings.CreateEnumDeclarations = value; }
    public string UnknownEnumName { get => _innerSettings.UnknownEnumName; set => _innerSettings.UnknownEnumName = value; }
    public string UnknownEnumValuePattern { get => _innerSettings.UnknownEnumValuePattern; set => _innerSettings.UnknownEnumValuePattern = value; }
    public string UnknownArgumentNamePattern { get => _innerSettings.UnknownArgumentNamePattern; set => _innerSettings.UnknownArgumentNamePattern = value; }
    public bool AllowLeftoverDataOnStack { get => _innerSettings.AllowLeftoverDataOnStack; set => _innerSettings.AllowLeftoverDataOnStack = value; }

    public DecompilerSettings()
    {
        RestoreDefaults();
    }

    /// <summary>
    /// Restores default values for all decompiler settings.
    /// </summary>
    public void RestoreDefaults()
    {
        _innerSettings = new DecompileSettings()
        {
            UnknownArgumentNamePattern = "arg{0}",
            RemoveSingleLineBlockBraces = true,
            EmptyLineAroundBranchStatements = true,
            EmptyLineBeforeSwitchCases = true
        };
        IndentStyle = IndentStyleKind.FourSpaces;
    }

    /// <inheritdoc/>
    public bool TryGetPredefinedDouble(double value, [MaybeNullWhen(false)] out string result, out bool isResultMultiPart)
    {
        // Pass through to inner settings instance, which has some predefined values already
        return _innerSettings.TryGetPredefinedDouble(value, out result, out isResultMultiPart);
    }
}
