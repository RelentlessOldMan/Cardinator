using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cardinator.Services;

/// <summary>
/// The single set of JSON options for everything Cardinator persists to disk — projects (.cardinator),
/// cards, and templates (template.json). It encodes the project's backward-compatibility promise:
/// <b>a new version must always be able to read files written by any older version.</b>
///
/// How that promise is kept here:
///  • <see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/> — tolerate casing drift.
///  • Unknown JSON properties are ignored (System.Text.Json default) — so a field removed or renamed in
///    a newer version never makes an older file fail to load.
///  • Missing properties fall back to the C# default — so a field ADDED in a newer version is simply
///    absent (and defaulted) when reading an older file.
///  • Comments and trailing commas are allowed, and quoted numbers are accepted — tolerant of
///    hand-edited or third-party-written files.
///
/// Rules for changing a persisted type (so this promise holds):
///  1. Only ADD new optional properties, with a safe default. Never rename or change the type of an
///     existing serialized property. If a rename is truly needed, keep the old name readable.
///  2. Null-guard on load (default non-null objects/collections).
///  3. Add a golden "old format" load test when the shape changes.
/// </summary>
public static class JsonCompat
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
