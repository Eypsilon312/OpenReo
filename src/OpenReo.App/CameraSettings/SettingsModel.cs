using System.Globalization;
using System.Text.Json.Nodes;
using OpenReo.Core.Reolink;

namespace OpenReo.App.CameraSettings;

public enum FieldKind
{
    /// <summary>Integer 0/1 shown as check box.</summary>
    Bool01,
    /// <summary>String options (from the camera's "range" or a fixed list).</summary>
    Enum,
    /// <summary>Integer options (from "range" or fixed labels).</summary>
    IntEnum,
    /// <summary>Integer with min/max (slider if the range is known).</summary>
    Int,
    Text,
    Password,
    ReadOnly,
}

/// <summary>One editable value inside a settings section, addressed by a JSON path like "Isp.dayNight".</summary>
public sealed class FieldSpec
{
    public FieldSpec(string path, string label, FieldKind kind)
    {
        Path = path;
        Label = label;
        Kind = kind;
    }

    public string Path { get; }
    public string Label { get; }
    public FieldKind Kind { get; }
    public string? Hint { get; init; }
    public int? Min { get; init; }
    public int? Max { get; init; }

    /// <summary>Display labels for known option values (both string and int enums).</summary>
    public Dictionary<string, string>? Labels { get; init; }

    /// <summary>Fallback options if the camera does not report a range.</summary>
    public string[]? Options { get; init; }

    /// <summary>Custom option source (e.g. encoder resolutions depending on another value).</summary>
    public Func<SettingsSection, IEnumerable<string>?>? OptionsProvider { get; init; }

    /// <summary>Re-render the page after a change (dependent options).</summary>
    public bool RebuildOnChange { get; init; }
}

/// <summary>A user action shown as button on a page.</summary>
public sealed record SettingsAction(string Label, Func<SettingsContext, Task> Run, bool Destructive = false);

/// <summary>
/// One Get/Set command pair of the Reolink API (e.g. GetIsp / SetIsp).
/// The camera's answer is kept as <see cref="Original"/>; the UI edits <see cref="Edited"/>;
/// saving sends only the changed fields (plus keys such as "channel").
/// </summary>
public sealed class SettingsSection
{
    public required string Title { get; init; }
    public required string GetCmd { get; init; }
    public string? SetCmd { get; init; }
    public JsonObject? GetParam { get; init; }

    /// <summary>Alternative command pair for older firmware (tried if <see cref="GetCmd"/> fails).</summary>
    public (string Get, string Set)? Fallback { get; init; }

    public List<FieldSpec> Fields { get; init; } = new();

    /// <summary>Path to a weekly schedule (string of 168 chars, or object with one string per trigger).</summary>
    public string[] SchedulePaths { get; init; } = Array.Empty<string>();

    /// <summary>Keys always sent with a changed object (identify the target).</summary>
    public string[] KeepKeys { get; init; } = { "channel" };

    /// <summary>Additional top level parameters for the Set command (e.g. channel for SetAiAlarm).</summary>
    public JsonObject? SetExtras { get; init; }

    public string? Note { get; init; }

    // Runtime state -----------------------------------------------------------
    public string ActiveGetCmd { get; set; } = "";
    public string? ActiveSetCmd { get; set; }
    public JsonObject? Original { get; set; }
    public JsonObject? Edited { get; set; }
    public JsonObject? Range { get; set; }
    public bool Supported => Edited != null;

    public bool IsDirty => ActiveSetCmd != null && Original != null && Edited != null && JsonUtil.Diff(Original, Edited, KeepKeys) != null;

    /// <summary>Parameter object for the Set command, or null if nothing changed.</summary>
    public JsonObject? BuildSetParam()
    {
        if (Original == null || Edited == null) return null;
        var diff = JsonUtil.Diff(Original, Edited, KeepKeys);
        if (diff == null) return null;
        if (SetExtras != null)
            foreach (var (k, v) in SetExtras) diff[k] = v?.DeepClone();
        return diff;
    }

    public void AcceptChanges() => Original = (JsonObject?)Edited?.DeepClone();
    public void RevertChanges() => Edited = (JsonObject?)Original?.DeepClone();
}

/// <summary>A page in the camera settings window (group of sections + actions).</summary>
public sealed class SettingsPage
{
    public required string Title { get; init; }
    public required string Icon { get; init; }
    public List<SettingsSection> Sections { get; init; } = new();
    public List<SettingsAction> Actions { get; init; } = new();

    /// <summary>Optional custom content (e.g. PTZ control) – returns null if not supported.</summary>
    public Func<SettingsContext, System.Windows.FrameworkElement?>? CustomContent { get; init; }
    public bool CustomSupported { get; set; }

    public bool Visible => Sections.Any(s => s.Supported) || CustomSupported;
}

/// <summary>Everything an action needs.</summary>
public sealed class SettingsContext
{
    public required ReolinkSession Session { get; init; }
    public required int Channel { get; init; } // 0-based API channel
    public required Func<string, Task> Report { get; init; }
    public required Action Rebuild { get; init; }
    public required System.Windows.Window Owner { get; init; }
    public Func<string, SettingsSection?> FindSection { get; init; } = _ => null;
}

/// <summary>JSON helpers: path access and diffs.</summary>
public static class JsonUtil
{
    private static IEnumerable<string> Split(string path) => path.Split('.', StringSplitOptions.RemoveEmptyEntries);

    public static JsonNode? Get(JsonNode? root, string path)
    {
        var node = root;
        foreach (var part in Split(path))
        {
            if (node is JsonObject o) node = o.TryGetPropertyValue(part, out var n) ? n : null;
            else if (node is JsonArray a && int.TryParse(part, out var i)) node = i >= 0 && i < a.Count ? a[i] : null;
            else return null;
            if (node == null) return null;
        }
        return node;
    }

    /// <summary>Sets an existing value (the path must exist).</summary>
    public static bool Set(JsonNode? root, string path, JsonNode? value)
    {
        var parts = Split(path).ToArray();
        var parent = parts.Length == 1 ? root : Get(root, string.Join('.', parts[..^1]));
        var last = parts[^1];
        if (parent is JsonObject o) { o[last] = value; return true; }
        if (parent is JsonArray a && int.TryParse(last, out var i) && i < a.Count) { a[i] = value; return true; }
        return false;
    }

    /// <summary>Range information uses the same structure but without array indices.</summary>
    public static JsonNode? GetRange(JsonObject? range, string path)
    {
        if (range == null) return null;
        var withoutIndices = string.Join('.', Split(path).Where(p => !int.TryParse(p, out _)));
        return Get(range, withoutIndices);
    }

    public static string? AsString(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<int>(out var i) => i.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "1" : "0",
        _ => n.ToJsonString(),
    };

    public static int? AsInt(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<long>(out var l) => (int)l,
        JsonValue v when v.TryGetValue<double>(out var d) => (int)d,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? 1 : 0,
        JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, out var p) => p,
        _ => null,
    };

    /// <summary>Recursive diff: only changed keys (objects are diffed per key, arrays/values as a whole).</summary>
    public static JsonObject? Diff(JsonObject original, JsonObject edited, string[] keepKeys)
    {
        var result = new JsonObject();
        bool changed = false;
        foreach (var (key, value) in edited)
        {
            original.TryGetPropertyValue(key, out var orig);
            if (value is JsonObject vo && orig is JsonObject oo)
            {
                var d = Diff(oo, vo, keepKeys);
                if (d != null) { result[key] = d; changed = true; }
            }
            else if (!JsonNode.DeepEquals(orig, value))
            {
                result[key] = value?.DeepClone();
                changed = true;
            }
        }
        if (!changed) return null;
        foreach (var k in keepKeys)
            if (!result.ContainsKey(k) && edited.TryGetPropertyValue(k, out var kv))
                result[k] = kv?.DeepClone();
        return result;
    }
}
