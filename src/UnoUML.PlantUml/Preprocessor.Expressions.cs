using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace UnoUML.PlantUml;

internal sealed partial class Preprocessor
{
    private readonly record struct Value(string Text, int? Integer, JsonElement? Container = null)
    {
        public static Value Number(int value) => new(value.ToString(CultureInfo.InvariantCulture), value);
        public static Value String(string value) => value.Length <= MaxBytes ? new(value, null) : throw Unsupported();

        internal static Value Json(JsonElement element)
        {
            element = element.Clone();
            if (element.ValueKind == JsonValueKind.String) return new(element.GetString()!, null, element);
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                element.WriteTo(writer);
            return new(Encoding.UTF8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length)), null, element);
        }

        public string Scalar => Text;
        public bool True => Integer is { } number ? number != 0 : Text.Length != 0;
        public int Int => Integer ?? 0;
    }

    private sealed record CallArguments(List<Value> Positional, Dictionary<string, Value> Named)
    {
        internal bool Literal { get; set; }
    }

    // A bounded operator stack preserves TIM's eager evaluation and its legacy
    // handling of adjacent operands, including unquoted decimal defaults.
    private sealed class Expression(Preprocessor engine, string text, Dictionary<string, Value> locals, int depth)
    {
        private int pos;
        private int expressionLevel;
        private Dictionary<string, Value>? expressionVariables;
        internal int Position => pos;

        internal Value All()
        {
            var result = ReadExpression(0);
            Space();
            if (pos != text.Length) throw Unsupported("Unexpected expression token at " + pos + " in " + text);
            return result;
        }

        private Value ReadExpression(int nesting, Stack<Value>? parentValues = null)
        {
            if (nesting > 64 || depth + nesting > 96) throw Unsupported();
            // TIM resolves variable operands before executing the expression's
            // function calls. Capture only referenced names to avoid copying the
            // entire global scope for every library expression.
            if (expressionLevel++ == 0) expressionVariables = CaptureVariables();
            try { return ReadOperations(nesting, parentValues); }
            finally { if (--expressionLevel == 0) expressionVariables = null; }
        }

        private Dictionary<string, Value> CaptureVariables()
        {
            var variables = new Dictionary<string, Value>(StringComparer.Ordinal);
            int parentheses = 0, brackets = 0;
            char quote = '\0';
            for (int i = pos; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c is '\'' or '"') { quote = c; continue; }
                if (parentheses == 0 && brackets == 0 && c is ',' or ')' or ']') break;
                if (c == '(') parentheses++;
                else if (c == ')') parentheses--;
                else if (c == '[') brackets++;
                else if (c == ']') brackets--;
                if (!IsNameStart(c)) continue;
                int end = NameEnd(text, i);
                string name = text[i..end];
                if (engine.TryGetVariable(name, locals, out var value))
                {
                    variables[name] = value;
                    if (IsScalar(value) && end < text.Length && text[end] is '.' or '[')
                    {
                        end = PlainTokenEnd(i);
                        string token = text[i..end];
                        variables[token] = DisplayedValue(engine.Substitute(token, locals, depth));
                    }
                }
                i = end - 1;
            }
            return variables;
        }

        private Value ReadOperations(int nesting, Stack<Value>? parentValues)
        {
            var values = parentValues ?? new Stack<Value>();
            var operators = new Stack<string>();
            bool expectingValue = true;
            while (true)
            {
                Space();
                if (pos == text.Length || text[pos] is ',' or ')' or ']') break;
                string token = Operator();
                if (token.Length == 0 || token == "-" && expectingValue)
                {
                    values.Push(Atom(nesting, values));
                    expectingValue = false;
                    continue;
                }
                while (operators.TryPeek(out string? previous) && Precedence(previous) >= Precedence(token))
                    Apply(values, operators.Pop());
                operators.Push(token);
                pos += token.Length;
                expectingValue = true;
            }
            while (operators.Count > 0) Apply(values, operators.Pop());
            return values.TryPop(out var result) ? result : throw Unsupported();
        }

        private static int Precedence(string token) => token switch
        {
            "||" => 1, "&&" => 2, "==" or "!=" => 3, "<=" or ">=" or "<" or ">" => 4,
            "+" or "-" => 5, "*" or "/" => 6, _ => 0
        };

        private static void Apply(Stack<Value> values, string token)
        {
            if (!values.TryPop(out var other) || !values.TryPop(out var value)) throw Unsupported();
            bool numbers = value.Integer.HasValue && other.Integer.HasValue;
            int comparison = numbers ? value.Int.CompareTo(other.Int) : string.CompareOrdinal(value.Scalar, other.Scalar);
            values.Push(token switch
            {
                "+" => numbers ? Value.Number(unchecked(value.Int + other.Int)) : Value.String(value.Scalar + other.Scalar),
                "-" => numbers ? Value.Number(unchecked(value.Int - other.Int)) : Value.String(value.Scalar + other.Scalar),
                "*" => numbers ? Value.Number(unchecked(value.Int * other.Int)) : Value.String(value.Scalar + "*" + other.Scalar),
                "/" when !numbers => Value.String(value.Scalar + "/" + other.Scalar),
                "/" when other.Int != 0 => Value.Number(value.Int == int.MinValue && other.Int == -1 ? int.MinValue : value.Int / other.Int),
                "&&" => Bool(value.True && other.True), "||" => Bool(value.True || other.True),
                "==" => Bool(comparison == 0), "!=" => Bool(comparison != 0),
                "<" => Bool(comparison < 0), ">" => Bool(comparison > 0),
                "<=" => Bool(comparison <= 0), ">=" => Bool(comparison >= 0),
                _ => throw Unsupported()
            });
        }

        private string Operator()
        {
            if (pos == text.Length) return "";
            var remaining = text.AsSpan(pos);
            foreach (var op in DoubleOperators) if (remaining.StartsWith(op)) return op;
            return text[pos] switch { '<' => "<", '>' => ">", '+' => "+", '-' => "-", '*' => "*", '/' => "/", _ => "" };
        }

        private void Space() { while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++; }
        private bool Consume(char c) { Space(); if (pos >= text.Length || text[pos] != c) return false; pos++; return true; }

        private Value Atom(int nesting, Stack<Value> values)
        {
            engine.Charge();
            if (nesting > 64) throw Unsupported();
            Space();
            if (pos >= text.Length) throw Unsupported();
            char c = text[pos];
            if (c == '(') { pos++; var result = ReadExpression(nesting + 1, values); if (!Consume(')')) throw Unsupported(); return result; }
            if (c == '-')
            {
                pos++;
                int begin = pos;
                while (pos < text.Length && char.IsAsciiDigit(text[pos])) pos++;
                if (begin == pos || !int.TryParse("-" + text[begin..pos], CultureInfo.InvariantCulture, out int negative)) throw Unsupported();
                return Value.Number(negative);
            }
            if (c is '"' or '\'') return Value.String(Quoted());
            if (c is '{' or '[')
            {
                // JSON literals are recognized by whole-expression directives,
                // not inside function argument token streams in the reference.
                if (nesting != 0) throw Unsupported();
                int begin = pos, level = 0; bool quoted = false;
                for (; pos < text.Length; pos++)
                {
                    char ch = text[pos];
                    if (quoted && ch == '\\') { pos++; continue; }
                    if (ch == '"') { quoted = !quoted; continue; }
                    if (quoted) continue;
                    if (ch is '{' or '[') { if (++level > 64) throw Unsupported(); }
                    if (ch is '}' or ']') { if (--level == 0) { pos++; break; } }
                }
                if (quoted || level != 0) throw Unsupported();
                using var doc = JsonDocument.Parse(text[begin..pos], new JsonDocumentOptions { MaxDepth = 64 });
                return Value.Json(doc.RootElement);
            }
            if (char.IsAsciiDigit(c))
            {
                int begin = pos++;
                while (pos < text.Length && char.IsAsciiDigit(text[pos])) pos++;
                if (!int.TryParse(text.AsSpan(begin, pos - begin), NumberStyles.None, CultureInfo.InvariantCulture, out int integer)) throw Unsupported();
                return Value.Number(integer);
            }
            if (IsNameStart(c)) return Reference(nesting, true);
            int literal = pos;
            pos = PlainTokenEnd(pos);
            if (pos == literal) throw Unsupported();
            return Value.String(text[literal..pos]);
        }

        private int PlainTokenEnd(int index)
        {
            int brackets = 0;
            while (index < text.Length && !char.IsWhiteSpace(text[index]) && "(),=+-*/<>&|".IndexOf(text[index]) < 0)
            {
                if (text[index] == ']')
                {
                    if (brackets == 0) break;
                    brackets--;
                }
                if (text[index] == '[') brackets++;
                index++;
            }
            return index;
        }

        private static bool IsScalar(Value value) => value.Container == null || value.Container.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number;

        private string Quoted()
        {
            char quote = text[pos++];
            int begin = pos;
            while (pos < text.Length && text[pos] != quote) pos++;
            if (pos == text.Length) throw Unsupported();
            return text[begin..pos++];
        }

        internal Value Reference(int nesting = 0, bool expressionToken = false)
        {
            engine.Charge();
            int start = pos;
            pos = NameEnd(text, pos);
            int nameEnd = pos;
            string name = text[start..pos];
            if (expressionToken) Space();
            if (pos < text.Length && text[pos] == '(')
            {
                var args = Arguments(nesting, !expressionToken && engine.RoutineIsUnquoted(name));
                if (name[0] == '%' && !engine.routines.ContainsKey(name))
                {
                    if (args.Named.Count != 0) throw Unsupported();
                    return engine.InvokeBuiltin(name, args.Positional, locals, depth);
                }
                return engine.InvokeFunction(name, args, locals, depth + 1);
            }
            pos = nameEnd;
            bool found = expressionToken && expressionVariables != null ? expressionVariables.TryGetValue(name, out var value) :
                engine.TryGetVariable(name, locals, out value);
            if (!found)
            {
                if (expressionToken) pos = PlainTokenEnd(start);
                return Value.String(text[start..pos].TrimEnd());
            }
            if (expressionToken && IsScalar(value) && pos < text.Length && text[pos] is '.' or '[')
            {
                pos = PlainTokenEnd(start);
                return expressionVariables != null && expressionVariables.TryGetValue(text[start..pos], out var captured)
                    ? captured : DisplayedValue(engine.Substitute(text[start..pos], locals, depth));
            }
            return Members(value, nesting);
        }

        private Value Members(Value value, int nesting)
        {
            bool accessed = false;
            while (value.Container is { } element && pos < text.Length)
            {
                if (text[pos] == '.' && element.ValueKind == JsonValueKind.Object)
                {
                    int begin = ++pos;
                    while (pos < text.Length && IsNamePart(text[pos])) pos++;
                    if (begin == pos) throw Unsupported();
                    if (!element.TryGetProperty(text[begin..pos], out var member)) return Value.String("");
                    value = Value.Json(member);
                    accessed = true;
                }
                else if (text[pos] == '[')
                {
                    pos++;
                    var index = ReadExpression(nesting + 1);
                    if (!Consume(']')) throw Unsupported();
                    if (element.ValueKind == JsonValueKind.Array && int.TryParse(index.Scalar, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < element.GetArrayLength())
                        value = Value.Json(element[i]);
                    else if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(index.Scalar, out var member))
                        value = Value.Json(member);
                    else throw Unsupported();
                    accessed = true;
                }
                else break;
            }
            if (!accessed || value.Container?.ValueKind != JsonValueKind.String) return value;
            // TIM resolves a JSON member through its displayed value. Numeric
            // strings therefore remain JSON scalars when used in expressions.
            return DisplayedValue(value.Scalar);
        }

        private static Value DisplayedValue(string displayed)
        {
            try
            {
                using var parsed = JsonDocument.Parse(displayed, new JsonDocumentOptions { MaxDepth = 64 });
                return Value.Json(parsed.RootElement);
            }
            catch (JsonException) { return Value.String(displayed); }
        }

        internal CallArguments Arguments(int nesting = 0, bool unquoted = false)
        {
            if (!Consume('(')) throw Unsupported();
            var args = new CallArguments([], new(StringComparer.Ordinal)) { Literal = true };
            if (Consume(')')) return args;
            do
            {
                if (args.Positional.Count + args.Named.Count >= 64) throw Unsupported();
                Space();
                string? name = null;
                if (pos < text.Length && IsNameStart(text[pos]) && text[pos] != '%')
                {
                    int begin = pos, end = NameEnd(text, pos);
                    while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
                    if (end < text.Length && text[end] == '=' && (end + 1 == text.Length || text[end + 1] != '='))
                    {
                        name = text[begin..NameEnd(text, begin)];
                        pos = end + 1;
                    }
                }
                int valueStart = pos;
                Value value = unquoted ? UnquotedArgument() : ReadExpression(nesting + 1);
                string raw = text[valueStart..pos].Trim();
                bool literal = raw.Length >= 2 && raw[0] is '\'' or '"' && raw[^1] == raw[0]
                    ? raw[1..^1] == value.Scalar && !raw[1..^1].Contains(raw[0])
                    : raw == value.Scalar && (LibraryMacro.Alias(raw) || int.TryParse(raw, out _));
                args.Literal &= literal;
                if (name == null) args.Positional.Add(value);
                else args.Named[name] = value;
            } while (Consume(','));
            if (!Consume(')')) throw Unsupported();
            return args;
        }

        private Value UnquotedArgument()
        {
            Space();
            string raw;
            if (pos < text.Length && text[pos] is '"' or '\'') raw = Quoted();
            else
            {
                int begin = pos, level = 0;
                while (pos < text.Length)
                {
                    char c = text[pos];
                    if (level == 0 && c is ',' or ')') break;
                    if (c == '(') level++;
                    else if (c == ')') level--;
                    pos++;
                }
                if (level != 0 || pos == text.Length) throw Unsupported();
                raw = text[begin..pos].Trim();
            }
            return Value.String(engine.Substitute(raw, locals, depth + 1));
        }
    }

    private static readonly string[] DoubleOperators = ["&&", "||", "==", "!=", "<=", ">="];
    private static Value Bool(bool value) => Value.Number(value ? 1 : 0);
    private Value Evaluate(string expression, Dictionary<string, Value> locals, int depth) =>
        depth > 64 ? throw Unsupported() : new Expression(this, expression, locals, depth).All();
}
