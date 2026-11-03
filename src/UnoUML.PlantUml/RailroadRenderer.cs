using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

/// <summary>EBNF productions and regular expressions share one measured railroad tree.</summary>
internal static partial class RailroadRenderer
{
    private enum Kind { Literal, Reference, Special, Set, Sequence, Choice, Option, Loop, Capture }
    private sealed class Rail(Kind kind, int line, string text, Rail[] children)
    {
        public readonly Kind Kind = kind;
        public readonly int Line = line;
        public readonly string Text = text;
        public readonly Rail[] Children = children;
        public int Width, Above, Below;
    }

    private sealed record Rule(string Name, int Line, Rail Root);

    internal static string? TryRender(string source, bool regex, IReadOnlyDictionary<string, string>? options = null)
    {
        if (source.Length > 65536) return null;
        var body = new StringBuilder();
        var rows = new List<Rule>();
        int line = -1, firstLine = 0, stage = 0;
        string title = "";
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            line++;
            var trimmed = raw.Trim();
            if (trimmed.Length > 8192) return null;
            if (stage != 1)
            {
                if (trimmed.IsEmpty) continue;
                if (stage != 0 || !trimmed.SequenceEqual(regex ? "@startregex" : "@startebnf")) return null;
                stage = 1; firstLine = line + 1; continue;
            }
            if (trimmed.SequenceEqual(regex ? "@endregex" : "@endebnf")) { stage = 2; continue; }
            if (trimmed.StartsWith("title ", StringComparison.Ordinal) && title.Length == 0 && rows.Count == 0 && body.Length == 0)
            {
                title = trimmed[6..].ToString();
                if (!SvgText.IsPlain(title) || title.Length > 300) return null;
                firstLine = line + 1; continue;
            }
            if (trimmed.StartsWith("@", StringComparison.Ordinal) || trimmed.StartsWith("!", StringComparison.Ordinal) ||
                trimmed.StartsWith("<style", StringComparison.Ordinal) || trimmed.StartsWith("skinparam ", StringComparison.Ordinal)) return null;
            if (!regex) { body.Append(raw).Append('\n'); continue; }
            if (trimmed.IsEmpty) continue;
            // Each regex production keeps the source line supplied by preprocessing.
            if (rows.Count != 0 || !raw.SequenceEqual(trimmed)) return null;
            try
            {
                var parser = new Reader(raw.ToString(), line, true, options);
                var root = parser.Pattern();
                if (!parser.AtEnd) return null;
                rows.Add(new("", line, root));
            }
            catch (InvalidRail) { return null; }
        }
        if (stage != 2) return null;
        if (!regex)
        {
            try
            {
                var parser = new Reader(body.ToString(), firstLine, false, options);
                while (!parser.AtEnd)
                {
                    if (rows.Count >= 256) return null;
                    rows.Add(parser.Production());
                }
            }
            catch (InvalidRail) { return null; }
        }
        return rows.Count == 0 ? null : Draw(rows, title);
    }

    // Internal parse failures only occur after recognizing the dialect; a partially parsed diagram is never displayed.
    private sealed class InvalidRail : Exception;

    private sealed class Reader(string source, int firstLine, bool regex, IReadOnlyDictionary<string, string>? options)
    {
        private int pos, line = firstLine, count, depth, capture;
        private bool insideCharacterSet;
        private readonly string[]? descriptions = DescriptiveNames(options);
        private char Current => pos < source.Length ? source[pos] : '\0';
        public bool AtEnd { get { Skip(); return pos == source.Length; } }
        private void Skip()
        {
            if (regex) return;
            while (pos < source.Length && char.IsWhiteSpace(source[pos]))
                if (source[pos++] == '\n') line++;
        }
        private bool Take(char c)
        {
            Skip();
            if (Current != c) return false;
            pos++; return true;
        }
        private void Need(char c) { if (!Take(c)) throw new InvalidRail(); }
        private Rail Make(Kind kind, int at, string text = "", params Rail[] children)
        {
            if (++count > 3600 || text.Length > 512) throw new InvalidRail();
            return new(kind, at, insideCharacterSet ? text : Describe(text, descriptions), children);
        }
        private Rail Many(Kind kind, int at, List<Rail> items)
        {
            if (items.Count == 0) throw new InvalidRail();
            return items.Count == 1 ? items[0] : Make(kind, at, "", items.ToArray());
        }
        public Rule Production()
        {
            Skip(); int begin = line;
            string name = Name(); Need('=');
            var root = Pattern(); Need(';');
            return new(name, begin, root);
        }
        private string Name()
        {
            Skip(); int start = pos;
            if (!char.IsLetter(Current) && Current != '_') throw new InvalidRail();
            do { pos++; } while (char.IsLetterOrDigit(Current) || Current == '_');
            if (pos - start > 180) throw new InvalidRail();
            return source[start..pos];
        }
        public Rail Pattern()
        {
            if (++depth > 48) throw new InvalidRail();
            Skip(); int begin = line;
            var choices = new List<Rail> { Serial() };
            while (Take('|')) choices.Add(Serial());
            depth--;
            return Many(Kind.Choice, begin, choices);
        }
        private Rail Serial()
        {
            Skip(); int begin = line;
            var terms = new List<Rail>();
            if (!regex)
            {
                terms.Add(GrammarAtom());
                while (Take(',')) terms.Add(GrammarAtom());
                return Many(Kind.Sequence, begin, terms);
            }
            // Fuse adjacent unquantified literals into one SVG box. Quantifiers bind to
            // individual atoms before fusion (abc+ is ab followed by one or more c).
            var literals = new StringBuilder();
            void Flush()
            {
                if (literals.Length == 0) return;
                terms.Add(Make(Kind.Literal, begin, literals.ToString())); literals.Clear();
            }
            while (Current is not ('\0' or '|' or ')'))
            {
                var node = Quantified();
                if (node.Kind == Kind.Literal && literals.Length + node.Text.Length < 512) literals.Append(node.Text);
                else { Flush(); terms.Add(node); }
            }
            Flush();
            return Many(Kind.Sequence, begin, terms);
        }
        private Rail GrammarAtom()
        {
            Skip(); int begin = line;
            if (Take('('))
            {
                var part = Pattern(); Need(')'); return part;
            }
            if (Take('['))
            {
                var part = Pattern(); Need(']'); return Make(Kind.Option, begin, "", part);
            }
            if (Take('{'))
            {
                var part = Pattern(); Need('}'); return Make(Kind.Option, begin, "", Make(Kind.Loop, begin, "", part));
            }
            if (Current is '\'' or '"')
            {
                char quote = source[pos++]; int start = pos;
                while (Current != quote && Current is not ('\0' or '\n' or '\\')) pos++;
                if (Current != quote || start == pos) throw new InvalidRail();
                string text = source[start..pos++];
                return Make(Kind.Literal, begin, text);
            }
            return Make(Kind.Reference, begin, Name());
        }
        private Rail Quantified()
        {
            int begin = line;
            var node = RegexAtom();
            if (Take('?')) return Make(Kind.Option, begin, "", node);
            if (Take('+')) return Make(Kind.Loop, begin, "", node);
            if (Take('*')) return Make(Kind.Option, begin, "", Make(Kind.Loop, begin, "", node));
            if (Current != '{') return node;
            int start = pos++;
            int min = Number(), max = min;
            if (Take(',')) max = Current == '}' ? int.MaxValue : Number();
            Need('}');
            if (min > max || max == 0) throw new InvalidRail();
            if (min == 1 && max == 1) return node;
            if (min == 0 && max == 1) return Make(Kind.Option, begin, "", node);
            var repeated = Make(Kind.Loop, begin, source[start..pos], node);
            return min == 0 ? Make(Kind.Option, begin, "", repeated) : repeated;
        }
        private int Number()
        {
            int start = pos;
            while (char.IsAsciiDigit(Current)) pos++;
            if (start == pos || pos - start > 6) throw new InvalidRail();
            return int.Parse(source.AsSpan(start, pos - start), CultureInfo.InvariantCulture);
        }
        private Rail RegexAtom()
        {
            int begin = line;
            if (Take('('))
            {
                int number = 0;
                if (Take('?')) Need(':'); else number = ++capture;
                var inner = Pattern(); Need(')');
                return number == 0 ? inner : Make(Kind.Capture, begin, number.ToString(CultureInfo.InvariantCulture), inner);
            }
            if (Take('[')) return CharacterSet();
            if (Take('\\'))
            {
                if (Current == 'Q')
                {
                    int end = source.IndexOf("\\E", ++pos, StringComparison.Ordinal);
                    if (end <= pos) throw new InvalidRail();
                    string literal = source[pos..end]; pos = end + 2;
                    if (literal.Length > 1 && Current is ('?' or '+' or '*' or '{')) throw new InvalidRail();
                    return Make(Kind.Literal, begin, literal);
                }
                return Escape();
            }
            if (Current is '.' or '^' or '$') return Make(Kind.Special, begin, source[pos++].ToString());
            if (Current is ']' or '}' or '{' or '*' or '+' or '?' or '\0' || char.IsControl(Current)) throw new InvalidRail();
            return Make(Kind.Literal, begin, RuneText());
        }
        private string RuneText()
        {
            int start = pos;
            if (char.IsHighSurrogate(Current))
            {
                if (++pos >= source.Length || !char.IsLowSurrogate(Current)) throw new InvalidRail();
            }
            else if (char.IsLowSurrogate(Current)) throw new InvalidRail();
            pos++;
            return source[start..pos];
        }
        private Rail Escape()
        {
            if (Current == '\0') throw new InvalidRail();
            char c = source[pos++];
            if ("dDsSwWbBtrnaefAZzG".Contains(c)) return Make(Kind.Special, line, "\\" + c);
            if (!char.IsAsciiLetterOrDigit(c) && !char.IsControl(c)) return Make(Kind.Literal, line, c.ToString());
            throw new InvalidRail(); // backreferences, Unicode properties and numeric escapes require the reference lexer.
        }
        private Rail CharacterSet()
        {
            int begin = line;
            insideCharacterSet = true;
            bool negate = Take('^');
            var tokens = new List<Rail>();
            while (Current is not ('\0' or ']'))
            {
                if (Current == '[' || source.AsSpan(pos).StartsWith("&&", StringComparison.Ordinal)) throw new InvalidRail();
                Rail first = Take('\\') ? Escape() : Make(Kind.Literal, line, RuneText());
                string label = first.Text;
                if (Current == '-' && pos + 1 < source.Length && source[pos + 1] != ']')
                {
                    pos++;
                    if (Current == '[') throw new InvalidRail();
                    var end = Take('\\') ? Escape() : Make(Kind.Literal, line, RuneText());
                    if (first.Kind != Kind.Literal || end.Kind != Kind.Literal || string.CompareOrdinal(label, end.Text) > 0) throw new InvalidRail();
                    label += "-" + end.Text;
                }
                tokens.Add(Make(Kind.Literal, line, label));
                if (tokens.Count > 128) throw new InvalidRail();
            }
            Need(']');
            if (tokens.Count == 0) throw new InvalidRail();
            insideCharacterSet = false;
            return Make(Kind.Set, begin, negate ? "^" : "", tokens.ToArray());
        }
    }
}
