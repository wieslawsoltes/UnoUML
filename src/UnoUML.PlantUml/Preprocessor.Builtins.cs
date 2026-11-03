using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnoUML.PlantUml;

internal sealed partial class Preprocessor
{
    private static readonly HashSet<string> BuiltinNames = new(("true false boolval not and or xor nand nor nxor " +
        "strlen string size upper lower substr strpos splitstr splitstr_regex intval hex2dec dec2hex mod chr ord " +
        "variable_exists function_exists get_variable_value set_variable_value call_user_func invoke_procedure retrieve_procedure " +
        "str2json load_json get_json_keys get_json_type json_key_exists json_add json_remove json_set json_merge " +
        "date now random getenv filename filename_no_extension dirpath filedate file_exists xargs version feature eval " +
        "get_all_theme get_current_theme get_all_stdlib get_stdlib darken lighten hsl_color is_dark is_light reverse_color reverse_hsluv_color " +
        "dollar percent backslash tab breakline newline n left_align right_align").Split(' ').Select(name => "%" + name), StringComparer.Ordinal);

    private static bool IsBuiltin(string name) => BuiltinNames.Contains(name);

    private Value InvokeBuiltin(string name, IReadOnlyList<Value> args, Dictionary<string, Value> locals, int depth)
    {
        Charge();
        switch (name, args.Count)
        {
            case ("%true", 0): return Bool(true);
            case ("%false", 0): return Bool(false);
            case ("%boolval", 1):
                return args[0].Scalar.ToLowerInvariant() switch
                {
                    "true" or "1" => Bool(true),
                    "false" or "0" => Bool(false),
                    _ => throw Unsupported()
                };
            case ("%not", 1): return Bool(!args[0].True);
            case ("%and", 2): return Bool(args[0].True && args[1].True);
            case ("%or", 2): return Bool(args[0].True || args[1].True);
            case ("%xor", 2): return Bool(args[0].True != args[1].True);
            case ("%nand", 2): return Bool(!(args[0].True && args[1].True));
            case ("%nor", 2): return Bool(!(args[0].True || args[1].True));
            case ("%nxor", 2): return Bool(args[0].True == args[1].True);
            case ("%strlen", 1): return Value.Number(args[0].Scalar.Length);
            case ("%string", 1): return Value.String(args[0].Scalar);
            case ("%size", 1):
                return Value.Number(args[0].Container is { } container ? container.ValueKind switch
                {
                    JsonValueKind.Array => container.GetArrayLength(),
                    JsonValueKind.Object => container.EnumerateObject().Count(),
                    _ => 0
                } : args[0].Integer.HasValue ? 0 : args[0].Text.Length);
            case ("%upper", 1): return Value.String(PreprocessorText.Upper(args[0].Scalar, options?.Culture ?? CultureInfo.InvariantCulture));
            case ("%lower", 1): return Value.String(PreprocessorText.Lower(args[0].Scalar, options?.Culture ?? CultureInfo.InvariantCulture));
            case ("%substr", 2 or 3):
                {
                    string text = args[0].Scalar;
                    int begin = args[1].Int;
                    if (begin < 0) throw Unsupported();
                    if (begin >= text.Length) return Value.String("");
                    int length = args.Count == 3 ? Math.Min(args[2].Int, text.Length - begin) : text.Length - begin;
                    if (length < 0) throw Unsupported();
                    return Value.String(text.Substring(begin, length));
                }
            case ("%strpos", 2): return Value.Number(args[0].Scalar.IndexOf(args[1].Scalar, StringComparison.Ordinal));
            case ("%splitstr", 2): return JsonStrings(SplitCharacters(args[0].Scalar, args[1].Scalar));
            case ("%splitstr_regex", 2): return JsonStrings(SplitRegex(args[0].Scalar, args[1].Scalar));
            case ("%intval", 1):
                return int.TryParse(args[0].Scalar, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number)
                ? Value.Number(number) : throw Unsupported();
            case ("%hex2dec", 1): return Value.Number(ParseHex(args[0].Scalar));
            case ("%dec2hex", 1): return Value.String(args[0].Int.ToString("x", CultureInfo.InvariantCulture));
            case ("%mod", 2):
                if (args[1].Int == 0) throw Unsupported();
                return Value.Number(args[0].Int == int.MinValue && args[1].Int == -1 ? 0 : args[0].Int % args[1].Int);
            case ("%chr", 1): return Value.String(Rune.IsValid(args[0].Int) ? char.ConvertFromUtf32(args[0].Int) : "\0");
            case ("%ord", 1):
                return Value.Number(args[0].Scalar.Length == 0 ? 0 :
                char.IsSurrogatePair(args[0].Scalar, 0) ? char.ConvertToUtf32(args[0].Scalar, 0) : args[0].Scalar[0]);
            case ("%variable_exists", 1): return Bool(TryGetVariable(args[0].Scalar, locals, out _));
            case ("%function_exists", 1): return Bool(FunctionExists(args[0].Scalar));
            case ("%get_variable_value", 1): return TryGetVariable(args[0].Scalar, locals, out var value) ? value : Value.String("");
            case ("%set_variable_value", 2): SetVariable(args[0].Scalar, args[1], locals, "global"); return Value.String("");
            case ("%call_user_func", >= 1): return CallFunction(args[0].Scalar, args.Skip(1).ToArray(), locals, depth + 1);
            case ("%invoke_procedure", >= 1): InvokeProcedure(args[0].Scalar, args.Skip(1).ToArray(), locals, depth + 1); return Value.String("");
            case ("%retrieve_procedure", >= 1): return RetrieveProcedure(args[0].Scalar, args.Skip(1).ToArray(), locals, depth + 1);
            case ("%str2json", 1):
                try { return ParseJson(args[0].Scalar); }
                catch (JsonException) { return Value.String(""); }
            case ("%load_json", >= 1 and <= 3):
                {
                    // Resources are already decoded by the host. Validate the optional
                    // charset, then preserve the default-on-missing behavior of TIM.
                    if (args.Count == 3 && !ValidEncoding(args[2].Scalar)) throw Unsupported();
                    string? data = ResolveResource(args[0].Scalar);
                    return ParseJson(string.IsNullOrEmpty(data) ? args.Count > 1 ? args[1].Scalar : "{}" : data);
                }
            case ("%get_json_keys", 1): return JsonKeys(args[0]);
            case ("%get_json_type", 1): return Value.String(JsonType(args[0]));
            case ("%json_key_exists", 2):
                return Bool(args[0].Container is { ValueKind: JsonValueKind.Object } json &&
                args[1].Integer == null && args[1].Container is null or { ValueKind: JsonValueKind.String } &&
                json.TryGetProperty(args[1].Scalar, out _));
            case ("%json_add", 2 or 3):
            case ("%json_remove", 2):
            case ("%json_set", 2 or 3):
            case ("%json_merge", 2): return TransformJson(name, args);
            case ("%getenv", 1): return Value.String(options?.ResolveEnvironment?.Invoke(args[0].Scalar) ?? "");
            case ("%filename", 0): return Value.String(FileName(CurrentFile));
            case ("%filename_no_extension", 0):
                {
                    string file = FileName(CurrentFile);
                    int extension = file.LastIndexOf('.');
                    return Value.String(extension < 0 ? file : file[..extension]);
                }
            case ("%dirpath", 0):
                {
                    string path = CurrentFile ?? "";
                    int last = path.LastIndexOf('/');
                    return Value.String(last < 0 ? "" : path[..last]);
                }
            case ("%filedate", 0):
                return Value.String(options?.SourceFileModifiedAt is { } modified
                ? PreprocessorDate.Format(modified, null, options?.TimeZone ?? TimeZoneInfo.Local, options?.Culture ?? CultureInfo.InvariantCulture) : "");
            case ("%file_exists", 1): return Bool(ResolveResource(args[0].Scalar) != null);
            case ("%xargs", 1): return Value.String(options?.Arguments ?? "");
            case ("%version", 0): return Value.String("1.2026.9");
            case ("%feature", 1):
                return Bool(args[0].Scalar.Equals("style", StringComparison.OrdinalIgnoreCase) ||
                args[0].Scalar.Equals("theme", StringComparison.OrdinalIgnoreCase));
            case ("%eval", 1): return Value.Number(Evaluate(args[0].Scalar, locals, depth + 1).Int);
            case ("%now", 0): return Value.Number(unchecked((int)(options?.UtcNow() ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()));
            case ("%date", >= 0 and <= 3):
                {
                    var instant = args.Count >= 2 ? DateTimeOffset.FromUnixTimeSeconds(args[1].Int) : options?.UtcNow() ?? DateTimeOffset.UtcNow;
                    var zone = args.Count == 3 ? PreprocessorDate.ResolveZone(args[2].Scalar) : options?.TimeZone ?? TimeZoneInfo.Local;
                    return Value.String(PreprocessorDate.Format(instant, args.Count == 0 ? null : args[0].Scalar, zone, options?.Culture ?? CultureInfo.InvariantCulture, args.Count == 3 ? args[2].Scalar : null));
                }
            case ("%random", >= 0 and <= 2):
                {
                    int minimum = args.Count == 2 ? args[0].Int : 0;
                    int maximum = args.Count == 0 ? 2 : args[^1].Int;
                    if (maximum <= minimum || (long)maximum - minimum > int.MaxValue) throw Unsupported();
                    return Value.Number(options?.RandomInteger(minimum, maximum) ?? Random.Shared.Next(minimum, maximum));
                }
            case ("%get_all_theme", 0): return JsonStrings(BuiltinResources.Themes.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            case ("%get_current_theme", 0): return GetCurrentTheme();
            case ("%get_all_stdlib", 0): return JsonStrings(BuiltinResources.Libraries.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            case ("%get_all_stdlib", 1): return LibraryInformation(legacy: true);
            case ("%get_stdlib", >= 0 and <= 2): return LibraryInformation(legacy: false, args);
            case ("%darken" or "%lighten", 2):
            case ("%hsl_color", 3 or 4):
            case ("%is_dark" or "%is_light" or "%reverse_color" or "%reverse_hsluv_color", 1): return ColorBuiltin(name, args);
            case ("%dollar", 0): return Value.String("$");
            case ("%percent", 0): return Value.String("%");
            case ("%backslash", 0): return Value.String("<U+005C>");
            case ("%tab", 0): return Value.String("\t");
            case ("%breakline", 0): return Value.String("\n");
            case ("%newline" or "%n", 0): return Value.String("\\n");
            case ("%left_align", 0): return Value.String("\\l");
            case ("%right_align", 0): return Value.String("\\r");
            default: throw Unsupported();
        }
    }

    private static string FileName(string? path) => path == null ? "" : path[(path.LastIndexOf('/') + 1)..];

    private static int ParseHex(string text)
    {
        bool negative = text.StartsWith('-');
        var digits = text.AsSpan(negative || text.StartsWith('+') ? 1 : 0);
        if (digits.IsEmpty || digits.Length > 8 || !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value)) return 0;
        if (negative) return value <= 0x80000000 ? unchecked(-(int)value) : 0;
        return value <= int.MaxValue ? (int)value : 0;
    }

    private static bool ValidEncoding(string name)
    {
        try { _ = Encoding.GetEncoding(name); return true; }
        catch (ArgumentException) { return false; }
    }

    private static Value ParseJson(string text)
    {
        if (text.Length > MaxBytes) throw Unsupported();
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        return Value.Json(document.RootElement.Clone());
    }

    private static Value WriteJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) write(writer);
        if (stream.Length > MaxBytes) throw Unsupported();
        using var document = JsonDocument.Parse(stream.GetBuffer().AsMemory(0, (int)stream.Length), new JsonDocumentOptions { MaxDepth = 32 });
        return Value.Json(document.RootElement.Clone());
    }

    private static void WriteValue(Utf8JsonWriter writer, Value value)
    {
        if (value.Container is { } json) json.WriteTo(writer);
        else if (value.Integer is int number) writer.WriteNumberValue(number);
        else writer.WriteStringValue(value.Text);
    }

    private static Value JsonStrings(IEnumerable<string> values) => WriteJson(writer =>
    {
        writer.WriteStartArray();
        foreach (string value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    });

    private static string JsonType(Value value) => value.Container is { } json ? json.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "json"
    } : value.Integer.HasValue ? "number" : "string";

    private static Value JsonKeys(Value value)
    {
        if (value.Container is not { } json) throw Unsupported();
        return json.ValueKind switch
        {
            JsonValueKind.Object => JsonStrings(json.EnumerateObject().Select(property => property.Name)),
            JsonValueKind.Array => JsonStrings(json.EnumerateArray().Where(element => element.ValueKind == JsonValueKind.Object)
                .SelectMany(element => element.EnumerateObject().Select(property => property.Name))),
            _ => throw Unsupported()
        };
    }

    private Value TransformJson(string operation, IReadOnlyList<Value> args)
    {
        if (args[0].Container is not { } original) throw Unsupported();
        if (original.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)) return args[0];
        if (operation == "%json_merge")
        {
            if (args[1].Container is not { } other) throw Unsupported();
            if (other.ValueKind != original.ValueKind) return args[0];
            if (original.ValueKind == JsonValueKind.Object) return MergeObjects(original, other, deep: false, 0);
        }
        if (operation == "%json_set" && args.Count == 2)
        {
            if (original.ValueKind != JsonValueKind.Object) throw Unsupported();
            return args[1].Container is { ValueKind: JsonValueKind.Object } other ? MergeObjects(original, other, deep: true, 0) : args[0];
        }
        return WriteJson(writer =>
        {
            if (original.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                int position = 0;
                foreach (var item in original.EnumerateArray())
                {
                    Charge();
                    if (args[1].Integer == position && operation == "%json_set") WriteValue(writer, args[2]);
                    else if (args[1].Integer != position || operation != "%json_remove") item.WriteTo(writer);
                    position++;
                }
                if (operation == "%json_add") WriteValue(writer, args[1]);
                if (operation == "%json_merge") foreach (var item in args[1].Container!.Value.EnumerateArray()) { Charge(); item.WriteTo(writer); }
                writer.WriteEndArray();
                return;
            }
            writer.WriteStartObject();
            string key = args[1].Scalar;
            var properties = original.EnumerateObject().ToArray();
            int replace = Array.FindLastIndex(properties, property => property.Name == key);
            for (int index = 0; index < properties.Length; index++)
            {
                Charge();
                var property = properties[index];
                if (index == replace && operation == "%json_remove") continue;
                writer.WritePropertyName(property.Name);
                if (index == replace && operation == "%json_set") WriteValue(writer, args[2]);
                else property.Value.WriteTo(writer);
            }
            if (operation == "%json_add" || operation == "%json_set" && replace < 0)
            {
                if (args.Count != 3) throw Unsupported();
                writer.WritePropertyName(key); WriteValue(writer, args[2]);
            }
            writer.WriteEndObject();
        });
    }

    private Value MergeObjects(JsonElement original, JsonElement changes, bool deep, int level)
    {
        if (level > 32) throw Unsupported();
        var fields = original.EnumerateObject().Select(property => new KeyValuePair<string, JsonElement>(property.Name, property.Value)).ToList();
        foreach (var change in changes.EnumerateObject())
        {
            Charge();
            int position = fields.FindLastIndex(property => property.Key == change.Name);
            var value = change.Value;
            if (deep && position >= 0 && fields[position].Value.ValueKind == JsonValueKind.Object && value.ValueKind == JsonValueKind.Object)
                value = MergeObjects(fields[position].Value, value, true, level + 1).Container!.Value;
            if (position < 0) fields.Add(new(change.Name, value));
            else fields[position] = new(change.Name, value);
        }
        return WriteJson(writer =>
        {
            writer.WriteStartObject();
            foreach (var field in fields) { writer.WritePropertyName(field.Key); field.Value.WriteTo(writer); }
            writer.WriteEndObject();
        });
    }

    private static IEnumerable<string> SplitCharacters(string text, string separators)
    {
        var delimiters = separators.EnumerateRunes().ToHashSet();
        int start = 0, offset = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (delimiters.Contains(rune))
            {
                if (offset > start) yield return text[start..offset];
                start = offset + rune.Utf16SequenceLength;
            }
            offset += rune.Utf16SequenceLength;
        }
        if (start < text.Length) yield return text[start..];
    }

    private IEnumerable<string> SplitRegex(string text, string pattern)
    {
        // Java's String.split suppresses capturing groups and a leading empty
        // zero-width match. Enumerating matches also keeps the operation bounded.
        pattern = Regex.Replace(pattern, @"\\Q(.*?)\\E", match => Regex.Escape(match.Groups[1].Value), RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));
        var regex = new Regex(pattern, RegexOptions.ECMAScript | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        var parts = new List<string>();
        int start = 0;
        foreach (Match match in regex.Matches(text))
        {
            Charge();
            if (match.Index == 0 && match.Length == 0) continue;
            parts.Add(text[start..match.Index]);
            start = match.Index + match.Length;
        }
        if (parts.Count == 0) return [text];
        parts.Add(text[start..]);
        while (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
        return parts;
    }

    private static Value LibraryInformation(bool legacy, IReadOnlyList<Value>? args = null)
    {
        var metadata = BuiltinResources.Libraries;
        if (args is { Count: > 0 })
        {
            if (!BuiltinResources.TryGetLibrary(args[0].Scalar, out var library)) throw Unsupported();
            if (args.Count == 2) return Value.String(library.TryGetProperty(args[1].Scalar.ToLowerInvariant(), out var field) ? field.GetString() ?? "" : "");
            return Value.Json(library);
        }
        return WriteJson(writer =>
        {
            writer.WriteStartObject();
            foreach (var library in metadata.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                if (!BuiltinResources.TryGetLibrary(library.Name, out var resolved)) throw Unsupported();
                writer.WritePropertyName(library.Name);
                if (!legacy) { resolved.WriteTo(writer); continue; }
                writer.WriteStartObject();
                writer.WriteString("name", library.Name);
                writer.WriteString("version", resolved.TryGetProperty("version", out var version) ? version.GetString() : "");
                writer.WriteString("source", resolved.TryGetProperty("source", out var source) ? source.GetString() : "");
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        });
    }
}
