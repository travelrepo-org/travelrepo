using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Json.Schema;
using TravelRepo.Core;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace TravelRepo.Serialization;

/// <summary>Safe YAML 1.2-compatible JSON values. Unknown mapping entries are retained.</summary>
public static class YamlCodec
{
    public static Entity Read(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        while (parser.MoveNext())
        {
            if (parser.Current is AnchorAlias || parser.Current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty)) throw new DomainException("yaml.unsafe", "YAML anchors, aliases and explicit tags are not supported.");
        }
        var stream = new YamlStream(); stream.Load(new StringReader(yaml));
        if (stream.Documents.Count != 1) throw new DomainException("yaml.documents", "Exactly one YAML document is required.");
        return new Entity(Convert(stream.Documents[0].RootNode) as JsonObject ?? throw new DomainException("yaml.root", "The document must be a mapping."));
    }
    private static JsonNode? Convert(YamlNode node)
    {
        if (node is YamlMappingNode map)
        {
            var result = new JsonObject();
            foreach (var (key, value) in map.Children)
            {
                if (key is not YamlScalarNode s || s.Value is null) throw new DomainException("yaml.key", "Mapping keys must be strings.");
                if (result.ContainsKey(s.Value)) throw new DomainException("yaml.duplicate", "Duplicate mapping key.");
                result[s.Value] = Convert(value);
            }
            return result;
        }
        if (node is YamlSequenceNode seq) return new JsonArray(seq.Children.Select(Convert).ToArray());
        var scalar = (YamlScalarNode)node; var text = scalar.Value ?? "";
        if (scalar.Style == ScalarStyle.Plain)
        {
            if (text is "null" or "~" or "") return null;
            if (text is "true" or "false") return JsonValue.Create(text == "true");
            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)) return JsonValue.Create(integer);
            // Keep JSON numeric lexemes at arbitrary precision instead of rounding unknown extension data.
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$")) return JsonNode.Parse(text);
        }
        return JsonValue.Create(text);
    }
    public static string Write(Entity entity)
    {
        var stream = new YamlStream(new YamlDocument(ToYaml(entity.Data)));
        using var writer = new StringWriter(CultureInfo.InvariantCulture); stream.Save(writer, false);
        return writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
    private static YamlNode ToYaml(JsonNode? node)
    {
        if (node is JsonObject obj) return new YamlMappingNode(obj.Select(p => new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode(p.Key), ToYaml(p.Value))));
        if (node is JsonArray array) return new YamlSequenceNode(array.Select(ToYaml));
        if (node is null) return new YamlScalarNode("null") { Style = ScalarStyle.Plain };
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return new YamlScalarNode(s) { Style = s.Contains('\n') ? ScalarStyle.Literal : ScalarStyle.DoubleQuoted };
        return new YamlScalarNode(node.ToJsonString()) { Style = ScalarStyle.Plain };
    }
}

public static class SchemaValidation
{
    private static readonly Dictionary<string, JsonSchema> Schemas = Load();
    private static Dictionary<string, JsonSchema> Load()
    {
        var assembly = typeof(SchemaValidation).Assembly;
        return assembly.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.Ordinal)).ToDictionary(n => n.Split('.')[^2], n =>
        { using var stream = assembly.GetManifestResourceStream(n)!; using var reader = new StreamReader(stream); return JsonSchema.FromText(reader.ReadToEnd()); });
    }
    public static IReadOnlyList<Diagnostic> Validate(Entity entity)
    {
        var schema = Schemas.GetValueOrDefault(entity.Type) ?? Schemas["custom"];
        var result = schema.Evaluate(entity.Data, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return [];
        return [new("schema.invalid", Severity.Error, entity.Data["id"]?.ToString() ?? "travel.yaml", System.Text.Json.JsonSerializer.Serialize(result))];
    }
}
