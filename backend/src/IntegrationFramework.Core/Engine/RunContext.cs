using System.Text.Json.Nodes;

namespace IntegrationFramework.Core.Engine;

/// <summary>
/// Holds per-run state and resolves "$.steps.<node>.<path>", "$.env.<var>" and
/// "$.input.<path>" references. Supports array indexing like "$.steps.a.items[0].id"
/// and brace interpolation "{$.steps.a.id}" inside strings.
/// </summary>
public class RunContext
{
    public const string StepsRoot = "steps";
    public const string EnvRoot = "env";
    public const string InputRoot = "input";

    private readonly Dictionary<string, JsonNode?> _steps = new();
    public JsonNode? Input { get; }
    public IReadOnlyDictionary<string, string> Env { get; }

    public RunContext(JsonNode? input, IReadOnlyDictionary<string, string>? env = null)
    {
        Input = input?.DeepClone();
        Env = env ?? new Dictionary<string, string>();
    }

    public void SetStepOutput(string nodeId, JsonNode? output) => _steps[nodeId] = output?.DeepClone();

    public JsonNode? GetStepOutput(string nodeId) => _steps.TryGetValue(nodeId, out var v) ? v?.DeepClone() : null;

    public IReadOnlyDictionary<string, JsonNode?> Steps => _steps;

    /// <summary>Resolves any "$...." reference or interpolation in the given node.</summary>
    public JsonNode? Resolve(JsonNode? value)
    {
        if (value is null) return null;
        if (value is JsonValue v && v.TryGetValue<string>(out var s)) return ResolveString(s);
        if (value is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var (k, child) in obj) result[k] = Resolve(child);
            return result;
        }
        if (value is JsonArray arr)
        {
            var result = new JsonArray();
            foreach (var child in arr) result.Add(Resolve(child)?.DeepClone());
            return result;
        }
        return value.DeepClone();
    }

    /// <summary>
    /// Resolves a string: full references ("$.steps.a.b") keep the referenced JSON type;
    /// strings containing "{$....}" segments are interpolated as text.
    /// </summary>
    public JsonNode? ResolveString(string text)
    {
        if (text.StartsWith("$."))
        {
            var node = ResolvePath(text);
            return node?.DeepClone();
        }
        if (text.Contains("{$."))
        {
            var interpolated = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"\{\$\.([^}]+)\}",
                m => ResolvePath("$." + m.Groups[1].Value) is { } n
                    ? (n is JsonValue jv && jv.TryGetValue<string>(out var sv) ? sv : n.ToJsonString().Trim('"'))
                    : string.Empty);
            return JsonValue.Create(interpolated);
        }
        return JsonValue.Create(text);
    }

    /// <summary>Resolves a "$.root.rest.of.path" expression to a node, or null when missing.</summary>
    public JsonNode? ResolvePath(string path)
    {
        if (!path.StartsWith("$."))
            return JsonValue.Create(path);

        var segments = path[2..].Split('.');
        if (segments.Length == 0 || string.IsNullOrWhiteSpace(segments[0]))
            return null;

        JsonNode? current = segments[0] switch
        {
            StepsRoot => new JsonObject { [StepsRoot] = StepsToJson() }[StepsRoot], // placeholder, replaced below
            EnvRoot => EnvToJson(),
            InputRoot => Input?.DeepClone(),
            _ => null
        };

        if (segments[0] == StepsRoot)
        {
            if (segments.Length < 2) return null;
            if (!_steps.TryGetValue(segments[1], out var stepOutput)) return null;
            current = stepOutput?.DeepClone();
            segments = segments[2..];
        }
        else if (segments[0] == EnvRoot)
        {
            if (segments.Length != 2) return null;
            return Env.TryGetValue(segments[1], out var val) ? JsonValue.Create(val) : null;
        }
        else if (segments[0] == InputRoot)
        {
            segments = segments[1..];
        }

        foreach (var rawSegment in segments)
        {
            if (current is null) return null;
            // Support indexed accessors appended to a segment: "items[0]"
            var segment = rawSegment;
            while (true)
            {
                var open = segment.IndexOf('[');
                if (open < 0) break;
                var close = segment.IndexOf(']', open);
                if (close < 0) return null;
                var tail = segment[(close + 1)..];
                var head = segment[..open];

                if (head.Length > 0)
                {
                    if (current is not JsonObject headObj || !headObj.TryGetPropertyValue(head, out current))
                        return null;
                }
                if (!int.TryParse(segment[(open + 1)..close], out var index)) return null;
                if (current is not JsonArray arr || index < 0 || index >= arr.Count) return null;
                current = arr[index];
                segment = tail;
                if (segment.Length == 0) break;
            }
            if (segment.Length == 0) continue;
            if (current is JsonObject obj && obj.TryGetPropertyValue(segment, out var next))
                current = next;
            else
                return null;
        }
        return current?.DeepClone();
    }

    private JsonNode StepsToJson()
    {
        var obj = new JsonObject();
        foreach (var (id, output) in _steps) obj[id] = output?.DeepClone();
        return obj;
    }

    private JsonNode EnvToJson()
    {
        var obj = new JsonObject();
        foreach (var (k, v) in Env) obj[k] = v;
        return obj;
    }
}
