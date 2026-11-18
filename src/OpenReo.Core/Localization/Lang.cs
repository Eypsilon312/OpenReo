using System.Globalization;
using OpenReo.Core.Diagnostics;

namespace OpenReo.Core.Localization;

/// <summary>
/// UI language. German is the source language in the code; every user visible text goes
/// through <see cref="T"/> / <see cref="F"/> and is translated via <see cref="Translations.English"/>.
/// The language is chosen once at startup (setting "auto" = Windows display language).
/// </summary>
public static class Lang
{
    public const string Auto = "auto";
    public const string German = "de";
    public const string English = "en";

    private static readonly HashSet<string> ReportedMissing = new();
    private static readonly HashSet<string> EnglishValues = new(Translations.English.Values);

    /// <summary>Active language ("de" or "en").</summary>
    public static string Current { get; private set; } = Resolve(Auto);

    public static bool IsEnglish => Current == English;

    /// <summary>Resolves a setting value ("auto", "de", "en") to a concrete language.</summary>
    public static string Resolve(string? setting) => setting switch
    {
        German => German,
        English => English,
        // auto: German for German Windows (de-DE, de-AT, de-CH …), English for everything else
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? German : English,
    };

    public static void Initialize(string? setting)
    {
        Current = Resolve(setting);
        Log.Info($"UI language: {Current} (setting: {setting ?? Auto}, Windows: {CultureInfo.CurrentUICulture.Name})");
    }

    /// <summary>Translates a German UI text.</summary>
    public static string T(string german)
    {
        if (!IsEnglish || string.IsNullOrEmpty(german)) return german;
        if (Translations.English.TryGetValue(german, out var en)) return en;
        if (EnglishValues.Contains(german)) return german; // already translated
        lock (ReportedMissing)
            if (ReportedMissing.Add(german)) Log.Debug($"Missing translation: \"{german}\"");
        return german;
    }

    /// <summary>Translates a German format string ({0}, {1} …) and inserts the arguments.</summary>
    public static string F(string germanFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(germanFormat), args);
}
