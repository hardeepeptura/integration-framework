using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IntegrationFramework.Core.Engine;

/// <summary>
/// Applies an entity mapping's field definitions to a source payload and validates
/// the mapped output against its rules. Pure/static so both the entity_mapping node
/// and the mappings validate API share identical semantics.
/// </summary>
public static class EntityMappingApplier
{
    private static readonly string[] KnownTransforms = ["trim", "upper", "lower"];

    /// <summary>
    /// Resolves every field's source and builds the mapped target object.
    /// Returns a list of errors (empty when mapping succeeded fully); missing optional
    /// fields fall back to "default" when configured. All-or-nothing is enforced by callers.
    /// </summary>
    public static List<string> ApplyFieldMappings(
        string? mappingJson, JsonNode? source, RunContext runContext, out JsonObject mapped)
    {
        mapped = new JsonObject();
        var errors = new List<string>();

        var fields = ParseFields(mappingJson, errors);
        foreach (var field in fields)
        {
            if (field.Target is null)
            {
                errors.Add("Each mapping field requires a 'target'.");
                continue;
            }
            if (field.Source is null)
            {
                errors.Add($"Field '{field.Target}': missing 'source'.");
                continue;
            }

            JsonNode? value = field.Source.StartsWith("$.")
                ? runContext.ResolvePath(field.Source)
                : ResolveRelative(source, field.Source);

            if (value is null)
            {
                if (field.Default is not null)
                {
                    mapped[field.Target] = field.Default.DeepClone();
                    continue;
                }
                if (field.Required)
                    errors.Add($"Field '{field.Target}': source '{field.Source}' was not found.");
                continue;
            }

            value = ApplyTransforms(field.Target, field.Transforms, value, errors);
            if (value is not null)
                mapped[field.Target] = value.DeepClone();
        }
        return errors;
    }

    /// <summary>Validates a mapped object against ValidationRulesJson. Returns all violations.</summary>
    public static List<string> ValidateRules(JsonObject mapped, string? validationRulesJson)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(validationRulesJson)) return errors;

        JsonNode? rulesRoot;
        try
        {
            rulesRoot = JsonNode.Parse(validationRulesJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            errors.Add($"Invalid validation rules JSON: {ex.Message}");
            return errors;
        }

        var rules = rulesRoot is JsonObject o && o.TryGetPropertyValue("rules", out var r) && r is JsonArray ra
            ? ra
            : rulesRoot as JsonArray;
        if (rules is null)
        {
            errors.Add("Validation rules must be an array or an object with a 'rules' array.");
            return errors;
        }

        foreach (var ruleNode in rules)
        {
            if (ruleNode is not JsonObject rule) continue;
            var field = rule.TryGetPropertyValue("field", out var f) && f is JsonValue fv && fv.TryGetValue<string>(out var fs)
                ? fs
                : null;
            if (string.IsNullOrWhiteSpace(field))
            {
                errors.Add("Each validation rule requires a 'field'.");
                continue;
            }

            var present = mapped.TryGetPropertyValue(field, out var value) && value is not null;
            if (!present)
            {
                if (Bool(rule, "required"))
                    errors.Add($"Field '{field}' is required.");
                continue;
            }

            var type = String(rule, "type");
            switch (type)
            {
                case null:
                    break;
                case "string":
                    if (value is not JsonValue || value.GetValueKind() != System.Text.Json.JsonValueKind.String)
                        errors.Add($"Field '{field}' must be a string.");
                    else
                        ValidateStringRule(field, value.GetValue<string>(), rule, errors);
                    break;
                case "number":
                    if (value is not JsonValue ||
                        value!.GetValueKind() is not (System.Text.Json.JsonValueKind.Number))
                        errors.Add($"Field '{field}' must be a number.");
                    else
                        ValidateNumberRule(field, (JsonValue)value, rule, errors);
                    break;
                case "boolean":
                    if (value!.GetValueKind() is not (System.Text.Json.JsonValueKind.False or System.Text.Json.JsonValueKind.True))
                        errors.Add($"Field '{field}' must be a boolean.");
                    break;
                default:
                    errors.Add($"Field '{field}': unknown rule type '{type}'.");
                    break;
            }
        }
        return errors;
    }

    private static void ValidateStringRule(string field, string text, JsonObject rule, List<string> errors)
    {
        if (Int(rule, "minLength") is { } minLen && text.Length < minLen)
            errors.Add($"Field '{field}' must be at least {minLen} characters.");
        if (Int(rule, "maxLength") is { } maxLen && text.Length > maxLen)
            errors.Add($"Field '{field}' must be at most {maxLen} characters.");
        var pattern = String(rule, "pattern");
        if (pattern is not null)
        {
            try
            {
                if (!Regex.IsMatch(text, pattern))
                    errors.Add($"Field '{field}' does not match pattern '{pattern}'.");
            }
            catch (ArgumentException)
            {
                errors.Add($"Field '{field}': invalid regex pattern '{pattern}'.");
            }
        }
    }

    private static void ValidateNumberRule(string field, JsonValue value, JsonObject rule, List<string> errors)
    {
        // CLR-backed JsonValues only unbox to their exact stored type, so try the
        // common numeric representations before giving up.
        double? number = value.TryGetValue<double>(out var d) ? d
            : value.TryGetValue<float>(out var f) ? f
            : value.TryGetValue<decimal>(out var m) ? (double)m
            : value.TryGetValue<long>(out var l) ? l
            : value.TryGetValue<int>(out var i) ? i
            : null;
        if (number is null)
        {
            errors.Add($"Field '{field}' must be a number.");
            return;
        }
        if (Int(rule, "min") is { } min && number < min)
            errors.Add($"Field '{field}' must be at least {min}.");
        if (Int(rule, "max") is { } max && number > max)
            errors.Add($"Field '{field}' must be at most {max}.");
    }

    private static JsonNode? ApplyTransforms(string target, string[]? transforms, JsonNode value, List<string> errors)
    {
        if (transforms is null || transforms.Length == 0) return value;
        if (value is not JsonValue strValue || strValue.GetValueKind() != System.Text.Json.JsonValueKind.String ||
            !strValue.TryGetValue<string>(out var text))
        {
            errors.Add($"Field '{target}': transforms require a string value.");
            return value;
        }
        foreach (var raw in transforms)
        {
            var transform = raw?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(transform)) continue;
            if (!KnownTransforms.Contains(transform))
            {
                errors.Add($"Field '{target}': unknown transform '{raw}'.");
                continue;
            }
            text = transform switch
            {
                "trim" => text.Trim(),
                "upper" => text.ToUpperInvariant(),
                "lower" => text.ToLowerInvariant(),
                _ => text
            };
        }
        return JsonValue.Create(text);
    }

    /// <summary>Resolves a relative path ("user.email", "items[0].id") against the source payload.</summary>
    private static JsonNode? ResolveRelative(JsonNode? source, string path)
    {
        if (source is null) return null;
        // Reuse RunContext's path engine with the source payload as the input root.
        return new RunContext(source).ResolvePath("$.input." + path.TrimStart('.'));
    }

    private sealed record MappingField(string? Source, string? Target, bool Required, JsonNode? Default, string[]? Transforms);

    private static List<MappingField> ParseFields(string? mappingJson, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(mappingJson))
        {
            errors.Add("Entity mapping is missing its mapping JSON.");
            return [];
        }
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(mappingJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            errors.Add($"Invalid mapping JSON: {ex.Message}");
            return [];
        }

        var fieldsNode = root is JsonObject o && o.TryGetPropertyValue("fields", out var f) ? f : root;
        if (fieldsNode is not JsonArray fields)
        {
            errors.Add("Mapping JSON must be an object with a 'fields' array.");
            return [];
        }

        var result = new List<MappingField>();
        foreach (var item in fields)
        {
            if (item is not JsonObject field)
            {
                errors.Add("Each mapping field must be an object.");
                continue;
            }
            var source = String(field, "source");
            var target = String(field, "target");
            var transforms = field.TryGetPropertyValue("transforms", out var t) && t is JsonArray ta
                ? ta.Select(x => x?.ToJsonString().Trim('"') ?? string.Empty)
                    .Where(s => !string.IsNullOrEmpty(s)).ToArray()
                : null;
            result.Add(new MappingField(
                source, target,
                Required: Bool(field, "required"),
                Default: field.TryGetPropertyValue("default", out var d) && d is not null ? d : null,
                Transforms: transforms));
        }
        return result;
    }

    private static string? String(JsonObject obj, string field) =>
        obj.TryGetPropertyValue(field, out var v) && v is JsonValue val && val.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonObject obj, string field) =>
        obj.TryGetPropertyValue(field, out var v) && v is JsonValue val && val.TryGetValue<int>(out var i) ? i : null;

    private static bool Bool(JsonObject obj, string field) =>
        obj.TryGetPropertyValue(field, out var v) && v is JsonValue val && val.TryGetValue<bool>(out var b) && b;
}
