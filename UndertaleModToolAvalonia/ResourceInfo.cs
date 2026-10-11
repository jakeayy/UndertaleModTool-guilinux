using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Wrapper shown for the "General info" tree entry, grouping the general info, options and language chunks.
/// </summary>
public sealed class GeneralInfoView
{
    public UndertaleGeneralInfo GeneralInfo { get; init; }
    public UndertaleOptions Options { get; init; }
    public UndertaleLanguage Language { get; init; }

    public override string ToString() => "General info";
}

/// <summary>
/// Wrapper shown for a category whose single tree entry opens a whole list (e.g. "Global init").
/// </summary>
public sealed class StandaloneListView
{
    public string Title { get; init; }
    public IList Entries { get; init; }

    public override string ToString() => Title;
}

/// <summary>
/// Describes one category of the resource tree.
/// </summary>
public sealed class ResourceCategory
{
    public string Name { get; init; }

    /// <summary>Element type of the list (for reference pickers and creating new entries).</summary>
    public Type ItemType { get; init; }

    /// <summary>Gets the list from the data file, or <see langword="null"/> if the chunk does not exist.</summary>
    public Func<UndertaleData, IList> GetList { get; init; }

    /// <summary>If set, the category is a single leaf that opens this object instead of listing entries.</summary>
    public Func<UndertaleData, object> GetStandalone { get; init; }

    /// <summary>Whether the user can add new entries through the tree.</summary>
    public bool CanAdd { get; init; } = true;
}

public static class ResourceInfo
{
    public static readonly IReadOnlyList<ResourceCategory> Categories =
    [
        new() { Name = "General info", GetStandalone = d => d.GeneralInfo is null ? null : new GeneralInfoView { GeneralInfo = d.GeneralInfo, Options = d.Options, Language = d.Language } },
        new() { Name = "Global init", GetStandalone = d => d.GlobalInitScripts is null ? null : new StandaloneListView { Title = "Global init scripts", Entries = (IList)d.GlobalInitScripts } },
        new() { Name = "Game End scripts", GetStandalone = d => d.GameEndScripts is null ? null : new StandaloneListView { Title = "Game End scripts", Entries = (IList)d.GameEndScripts } },
        Cat<UndertaleAudioGroup>("Audio groups", d => d.AudioGroups),
        Cat<UndertaleSound>("Sounds", d => d.Sounds),
        Cat<UndertaleSprite>("Sprites", d => d.Sprites),
        Cat<UndertaleBackground>("Backgrounds & Tile sets", d => d.Backgrounds),
        Cat<UndertalePath>("Paths", d => d.Paths),
        Cat<UndertaleScript>("Scripts", d => d.Scripts),
        Cat<UndertaleShader>("Shaders", d => d.Shaders),
        Cat<UndertaleFont>("Fonts", d => d.Fonts),
        Cat<UndertaleTimeline>("Timelines", d => d.Timelines),
        Cat<UndertaleGameObject>("Game objects", d => d.GameObjects),
        Cat<UndertaleRoom>("Rooms", d => d.Rooms),
        Cat<UndertaleSequence>("Sequences", d => d.Sequences),
        Cat<UndertaleAnimationCurve>("Animation curves", d => d.AnimationCurves),
        Cat<UndertaleExtension>("Extensions", d => d.Extensions),
        Cat<UndertaleTexturePageItem>("Texture page items", d => d.TexturePageItems),
        Cat<UndertaleCode>("Code", d => d.Code),
        Cat<UndertaleVariable>("Variables", d => d.Variables),
        Cat<UndertaleFunction>("Functions", d => d.Functions),
        Cat<UndertaleCodeLocals>("Code locals", d => d.CodeLocals),
        Cat<UndertaleString>("Strings", d => d.Strings),
        Cat<UndertaleEmbeddedTexture>("Embedded textures", d => d.EmbeddedTextures),
        Cat<UndertaleEmbeddedAudio>("Embedded audio", d => d.EmbeddedAudio),
        Cat<UndertaleTextureGroupInfo>("Texture group information", d => d.TextureGroupInfo),
        Cat<UndertaleEmbeddedImage>("Embedded images", d => d.EmbeddedImages),
        Cat<UndertaleFilterEffect>("Filter effects", d => d.FilterEffects),
        Cat<UndertaleParticleSystem>("Particle systems", d => d.ParticleSystems),
        Cat<UndertaleParticleSystemEmitter>("Particle system emitters", d => d.ParticleSystemEmitters),
    ];

    private static ResourceCategory Cat<T>(string name, Func<UndertaleData, IList<T>> getter)
    {
        return new ResourceCategory
        {
            Name = name,
            ItemType = typeof(T),
            GetList = d => getter(d) as IList
        };
    }

    /// <summary>
    /// Finds the category whose list holds items of the given type, if any.
    /// </summary>
    public static ResourceCategory CategoryFor(Type type)
    {
        foreach (var cat in Categories)
        {
            if (cat.ItemType is not null && cat.ItemType == type)
                return cat;
        }
        return null;
    }

    /// <summary>
    /// Human-readable name of any object from the data file.
    /// </summary>
    public static string DisplayName(object obj)
    {
        try
        {
            return obj switch
            {
                null => "(null)",
                UndertaleString s => s.Content ?? "(null)",
                UndertaleNamedResource { Name: not null } r => r.Name.Content ?? "(null)",
                UndertaleResourceRef rr => rr.Resource is null ? "(null)" : DisplayName(rr.Resource),
                UndertaleVariable v => v.Name?.Content ?? "(unnamed variable)",
                UndertaleFunction f => f.Name?.Content ?? "(unnamed function)",
                UndertaleCodeLocals cl => cl.Name?.Content ?? "(unnamed locals)",
                _ => obj.ToString()
            };
        }
        catch (Exception e)
        {
            return $"<error: {e.Message}>";
        }
    }

    /// <summary>
    /// Short type name used in tab headers and status text.
    /// </summary>
    public static string TypeName(object obj)
    {
        if (obj is null)
            return "";
        string n = obj.GetType().Name;
        if (n.StartsWith("Undertale") && n.Length > 9)
            n = n[9..];
        int tick = n.IndexOf('`');
        return tick >= 0 ? n[..tick] : n;
    }

    /// <summary>
    /// Whether a list element matches a tree filter string.
    /// </summary>
    public static bool Matches(object obj, string filter)
    {
        if (string.IsNullOrEmpty(filter))
            return true;
        if (obj is ISearchable s)
        {
            try
            {
                return s.SearchMatches(filter);
            }
            catch
            {
                // fall through to name matching
            }
        }
        return DisplayName(obj).Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Generates a name that doesn't collide with existing names in the list.
    /// </summary>
    public static string UniqueName(IList list, string baseName)
    {
        var existing = new HashSet<string>(list.Cast<object>().Select(DisplayName));
        for (int i = 0; ; i++)
        {
            string candidate = $"{baseName}{i}";
            if (!existing.Contains(candidate))
                return candidate;
        }
    }
}
