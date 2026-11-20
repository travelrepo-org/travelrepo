using System.Text.Json;
using System.Text.Json.Nodes;
using TravelRepo.Core;

namespace TravelRepo.Native;

/// <summary>
/// The JSON arguments of one call, read by name and type. A missing optional argument takes the default the command
/// declares; a missing required one or a value of the wrong type fails the call with <c>argument.missing</c> or
/// <c>argument.invalid</c>. With <see cref="Recorder"/> set, nothing is read and every request is recorded instead.
/// </summary>
internal sealed class Arguments(JsonObject values)
{
    /// <summary>Records which arguments a command reads, with the type and default it expects. Used by tests.</summary>
    public List<(string Name, string Type, object? Default, bool Required)>? Recorder { get; init; }

    public static Arguments Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new([]);
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new DomainException("argument.invalid", "The arguments are not valid JSON: " + ex.Message); }
        return node is JsonObject values ? new(values) : throw new DomainException("argument.invalid", "The arguments must be a JSON object.");
    }

    public string Required(string name) => Record(name, "string", null, true) ? "" : Value(name, true, n => n.GetValue<string>(), "a string")!;
    public string? String(string name) => Record(name, "string", null, false) ? null : Value(name, false, n => n.GetValue<string>(), "a string");
    public string String(string name, string fallback) => Record(name, "string", fallback, false) ? fallback : Value(name, false, n => n.GetValue<string>(), "a string") ?? fallback;
    public int Int(string name, int fallback) => Record(name, "int", fallback, false) ? fallback : Value<int?>(name, false, n => n.GetValue<int>(), "a whole number") ?? fallback;
    public bool Bool(string name, bool fallback) => Record(name, "bool", fallback, false) ? fallback : Value<bool?>(name, false, n => n.GetValue<bool>(), "true or false") ?? fallback;
    public string[] Strings(string name) => Record(name, "string[]", null, true) ? [] : Value(name, true, StringArray, "an array of strings")!;
    public string[]? OptionalStrings(string name) => Record(name, "string[]", null, false) ? null : Value(name, false, StringArray, "an array of strings");
    public JsonElement Json(string name) => Record(name, "json", null, true) ? default : Value<JsonElement?>(name, true, n => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone(), "a JSON value")!.Value;

    private bool Record(string name, string type, object? fallback, bool required)
    {
        if (Recorder is null) return false;
        Recorder.Add((name, type, fallback, required)); return true;
    }

    private T? Value<T>(string name, bool required, Func<JsonNode, T> read, string expected)
    {
        if (!values.TryGetPropertyValue(name, out var node) || node is null)
            return required ? throw new DomainException("argument.missing", $"The argument {name} is missing.") : default;
        try { return read(node); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
        { throw new DomainException("argument.invalid", $"The argument {name} must be {expected}."); }
    }

    private static string[] StringArray(JsonNode node) => node.AsArray().Select(item => item?.GetValue<string>() ?? throw new FormatException()).ToArray();
}
