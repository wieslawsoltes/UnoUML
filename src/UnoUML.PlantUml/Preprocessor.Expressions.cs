using System.Globalization;
using System.Text.Json;

namespace UnoUML.PlantUml;

internal sealed partial class Preprocessor
{
    private readonly record struct Value(string Text, int? Integer, JsonElement? Container = null)
    {
        public static Value Number(int value) => new(value.ToString(CultureInfo.InvariantCulture), value);
        public static Value String(string value) => value.Length <= MaxLine ? new(value, null) : throw Unsupported();
        internal static Value Json(JsonElement element) => element.ValueKind switch {
            JsonValueKind.Object or JsonValueKind.Array => new("",null,element),
            JsonValueKind.String => String(element.GetString()!),
            JsonValueKind.Number when element.TryGetInt32(out int number) => Number(number),
            _ => throw Unsupported()
        };
        public string Scalar => Container == null ? Text : throw Unsupported();
        public bool True
        {
            get
            {
                if (Container != null) throw Unsupported();
                return Integer is { } number ? number != 0 : Text.Length != 0;
            }
        }
        public int Int => Integer ?? throw Unsupported();
    }

    // Read only the scalar TIM grammar for which this engine can reproduce the result.
    // Unknown tokens, mixed-type comparisons and implementation-defined overflows defer to PlantUML.
    private sealed class Expression(Preprocessor engine, string text, Dictionary<string,Value> locals, int depth)
    {
        private int pos;
        internal int Position => pos;

        internal Value All()
        {
            var result = Binary(1, 0);
            Space();
            if (pos != text.Length) throw Unsupported();
            return result;
        }

        private Value Binary(int precedence, int nesting)
        {
            if (nesting > 32 || depth + nesting > 48) throw Unsupported();
            var value = Atom(nesting);
            while (true)
            {
                Space();
                string token = Operator();
                int rank = token switch {
                    "||" => 1, "&&" => 2, "==" or "!=" => 3, "<=" or ">=" or "<" or ">" => 4,
                    "+" or "-" => 5, "*" or "/" => 6, _ => 0
                };
                if (rank < precedence) return value;
                pos += token.Length;
                Value other = Binary(rank + 1, nesting + 1);
                value = token switch {
                    "+" when value.Integer == null || other.Integer == null => Value.String(value.Scalar + other.Scalar),
                    "+" => Value.Number(checked(value.Int + other.Int)),
                    "-" => Value.Number(checked(value.Int - other.Int)),
                    "*" => Value.Number(checked(value.Int * other.Int)),
                    "/" when other.Int != 0 => Value.Number(value.Int / other.Int),
                    "&&" => Bool(value.True && other.True), "||" => Bool(value.True || other.True),
                    "==" when value.Integer.HasValue == other.Integer.HasValue => Bool(value.Scalar == other.Scalar),
                    "!=" when value.Integer.HasValue == other.Integer.HasValue => Bool(value.Scalar != other.Scalar),
                    "<" => Bool(value.Int < other.Int), ">" => Bool(value.Int > other.Int),
                    "<=" => Bool(value.Int <= other.Int), ">=" => Bool(value.Int >= other.Int),
                    _ => throw Unsupported()
                };
            }
        }

        private string Operator()
        {
            if (pos == text.Length) return "";
            var remaining = text.AsSpan(pos);
            foreach (var op in DoubleOperators) if (remaining.StartsWith(op)) return op;
            return text[pos] switch { '<' => "<", '>' => ">", '+' => "+", '-' => "-", '*' => "*", '/' => "/", _ => "" };
        }

        private void Space() { while (pos < text.Length && text[pos] is ' ' or '\t') pos++; }
        private bool Consume(char c) { Space(); if (pos >= text.Length || text[pos] != c) return false; pos++; return true; }

        private Value Atom(int nesting)
        {
            engine.Charge();
            if (nesting > 32) throw Unsupported();
            Space();
            if (pos >= text.Length) throw Unsupported();
            char c = text[pos];
            if (c == '(') { pos++; var result = Binary(1, nesting+1); if (!Consume(')')) throw Unsupported(); return result; }
            if (c == '-') { pos++; return Value.Number(checked(-Atom(nesting+1).Int)); }
            if (c is '"' or '\'')
            {
                int begin = ++pos;
                while (pos < text.Length && text[pos] != c) { if (text[pos] == '\\') throw Unsupported(); pos++; }
                if (pos == text.Length) throw Unsupported();
                return Value.String(text[begin..pos++]);
            }
            if (c is '{' or '[')
            {
                if (nesting != 0) throw Unsupported();
                int begin = pos, level = 0; bool quoted = false;
                for (; pos < text.Length; pos++)
                {
                    char ch = text[pos];
                    if (quoted && ch == '\\') { pos++; continue; }
                    if (ch == '"') { quoted = !quoted; continue; }
                    if (quoted) continue;
                    if (ch is '{' or '[') { if (++level > 32) throw Unsupported(); }
                    if (ch is '}' or ']') { if (--level == 0) { pos++; break; } }
                }
                if (quoted || level != 0) throw Unsupported();
                using var doc = JsonDocument.Parse(text[begin..pos],new JsonDocumentOptions { MaxDepth = 32 });
                return Value.Json(doc.RootElement.Clone());
            }
            if (char.IsAsciiDigit(c))
            {
                int begin = pos++;
                while (pos < text.Length && char.IsAsciiDigit(text[pos])) pos++;
                if (!int.TryParse(text.AsSpan(begin,pos-begin), NumberStyles.None, CultureInfo.InvariantCulture, out var integer)) throw Unsupported();
                return Value.Number(integer);
            }
            return Reference(nesting);
        }

        internal Value Reference(int nesting = 0)
        {
            engine.Charge();
            int start = pos;
            pos = NameEnd(text, pos);
            string name = text[start..pos];
            if (pos < text.Length && text[pos] == '(')
            {
                var args = Arguments(nesting);
                if (name[0] == '%') return (name, args.Count) switch {
                    ("%true",0) => Bool(true), ("%false",0) => Bool(false), ("%not",1) => Bool(!args[0].True),
                    ("%strlen",1) => Value.Number(args[0].Scalar.Length), ("%string",1) => Value.String(args[0].Scalar),
                    ("%size",1) => Value.Number(args[0].Container is { } container ? container.ValueKind == JsonValueKind.Array ?
                        container.GetArrayLength() : container.EnumerateObject().Count() : args[0].Integer.HasValue ? 0 : args[0].Scalar.Length),
                    ("%upper",1) => ChangeCase(args[0], true), ("%lower",1) => ChangeCase(args[0], false),
                    ("%variable_exists",1) => Bool(locals.ContainsKey(args[0].Text) || engine.globals.ContainsKey(args[0].Text)),
                    _ => throw Unsupported()
                };
                if (!engine.routines.TryGetValue(name, out var routine) || routine.Return == null) throw Unsupported();
                return engine.Evaluate(routine.Return, engine.Bind(routine, args), depth+1);
            }
            if (name[0] != '$' || !(locals.TryGetValue(name,out var value) || engine.globals.TryGetValue(name,out value))) throw Unsupported();
            while (value.Container is { } element && pos < text.Length)
            {
                if (text[pos] == '.' && element.ValueKind == JsonValueKind.Object)
                {
                    int begin = ++pos;
                    while (pos < text.Length && (char.IsAsciiLetterOrDigit(text[pos]) || text[pos] == '_')) pos++;
                    if (begin == pos || !element.TryGetProperty(text[begin..pos],out var member)) throw Unsupported();
                    value = Value.Json(member);
                }
                else if (text[pos] == '[')
                {
                    pos++;
                    var index = Binary(1,nesting+1);
                    if (!Consume(']')) throw Unsupported();
                    if (element.ValueKind == JsonValueKind.Array && index.Integer is int i && i >= 0 && i < element.GetArrayLength())
                        value = Value.Json(element[i]);
                    else if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(index.Scalar,out var member))
                        value = Value.Json(member);
                    else throw Unsupported();
                }
                else break;
            }
            return value;
        }

        internal List<Value> Arguments(int nesting = 0)
        {
            if (!Consume('(')) throw Unsupported();
            var args = new List<Value>();
            if (Consume(')')) return args;
            do { if (args.Count == 24) throw Unsupported(); args.Add(Binary(1,nesting+1)); } while (Consume(','));
            if (!Consume(')')) throw Unsupported();
            return args;
        }
    }

    private static readonly string[] DoubleOperators = ["&&","||","==","!=","<=",">="];
    private static Value ChangeCase(Value value, bool upper)
    {
        // Java applies full Unicode casing, including multi-character expansions.
        // Defer those inputs until their exact rules are available in this engine.
        string text = value.Scalar;
        if (!System.Text.Ascii.IsValid(text)) throw Unsupported();
        return Value.String(upper ? text.ToUpperInvariant() : text.ToLowerInvariant());
    }
    private static Value Bool(bool value) => Value.Number(value ? 1 : 0);
    private Value Evaluate(string expression, Dictionary<string,Value> locals, int depth) =>
        depth > 16 ? throw Unsupported() : new Expression(this,expression,locals,depth).All();
}
