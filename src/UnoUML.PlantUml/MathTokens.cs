using System.Text;

namespace UnoUML.PlantUml;

internal static partial class MathRenderer
{
    private readonly record struct Symbol(Lex Kind, string Text, Atom Class);
    private static readonly Dictionary<string, Symbol> Tex = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Symbol> Ascii = new(StringComparer.Ordinal);
    private static readonly Dictionary<char, string[]> Initial;

    static MathRenderer()
    {
        void Add(string name, string value, Atom a = Atom.Ordinary)
        {
            Tex[name] = Ascii[name] = new(Lex.Glyph, value, a);
        }
        void Symbols(string mappings, Atom a = Atom.Ordinary)
        {
            foreach (var p in mappings.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int sep = p.IndexOf(':'); Add(p[..sep], p[(sep + 1)..], a);
            }
        }
        Symbols("alpha:α beta:β gamma:γ delta:δ epsilon:ϵ varepsilon:ε zeta:ζ eta:η theta:θ vartheta:ϑ iota:ι kappa:κ lambda:λ mu:μ nu:ν xi:ξ pi:π varpi:ϖ rho:ρ varrho:ϱ sigma:σ varsigma:ς tau:τ upsilon:υ phi:ϕ varphi:φ chi:χ psi:ψ omega:ω", Atom.Variable);
        Symbols("Gamma:Γ Delta:Δ Theta:Θ Lambda:Λ Xi:Ξ Pi:Π Sigma:Σ Upsilon:Υ Phi:Φ Psi:Ψ Omega:Ω");
        Symbols("infty:∞ partial:∂ nabla:∇ ell:ℓ aleph:ℵ hbar:ℏ emptyset:∅ varnothing:∅ forall:∀ exists:∃ neg:¬ therefore:∴ because:∵ angle:∠ perp:⊥ top:⊤ bot:⊥ dots:… ldots:… cdots:⋯ vdots:⋮ ddots:⋱");
        Symbols("pm:± mp:∓ times:× div:÷ cdot:⋅ ast:∗ star:⋆ circ:∘ oplus:⊕ otimes:⊗ odot:⊙ cap:∩ cup:∪ land:∧ lor:∨ wedge:∧ vee:∨ setminus:∖", Atom.Binary);
        Symbols("le:≤ leq:≤ ge:≥ geq:≥ ne:≠ neq:≠ approx:≈ sim:∼ simeq:≃ equiv:≡ cong:≅ propto:∝ ll:≪ gg:≫ in:∈ notin:∉ ni:∋ subset:⊂ subseteq:⊆ supset:⊃ supseteq:⊇ prec:≺ preceq:⪯ succ:≻ succeq:⪰ parallel:∥ mid:∣ to:→ gets:← rightarrow:→ leftarrow:← leftrightarrow:↔ Rightarrow:⇒ Leftarrow:⇐ Leftrightarrow:⇔ implies:⇒ iff:⇔ mapsto:↦ longrightarrow:⟶ uparrow:↑ downarrow:↓", Atom.Relation);
        Symbols("sum:∑ prod:∏ coprod:∐ bigcup:⋃ bigcap:⋂ bigvee:⋁ bigwedge:⋀", Atom.Large);
        Symbols("int:∫ iint:∬ iiint:∭ oint:∮", Atom.Integral);
        foreach (var s in "sin cos tan cot sec csc arcsin arccos arctan sinh cosh tanh coth exp ln log lg det dim gcd lcm mod ker deg arg".Split(' ')) Add(s, s, Atom.Function);
        foreach (var s in "lim min max inf sup limsup liminf".Split(' ')) Add(s, s, Atom.Large);
        foreach (var s in "frac dfrac tfrac binom sqrt hat tilde vec bar underline dot ddot quad qquad".Split(' ')) Tex[s] = Ascii[s] = new(Lex.Cmd, s, Atom.Ordinary);
        foreach (var s in "left right limits nolimits".Split(' ')) Tex[s] = new(Lex.Cmd, s, Atom.Ordinary);
        foreach (var s in "root abs norm floor ceil".Split(' ')) Ascii[s] = new(Lex.Cmd, s, Atom.Ordinary);
        foreach (var pair in "mathbf:bold mathrm:roman mathit:italic mathsf:sans mathtt:mono mathbb:double overline:bar widehat:hat widetilde:tilde enspace:medium".Split(' '))
        {
            var p = pair.Split(':'); Tex[p[0]] = new(Lex.Cmd, p[1], Atom.Ordinary);
        }
        foreach (var pair in "bb:bold bbb:double sf:sans tt:mono ul:underline overline:bar".Split(' '))
        {
            var p = pair.Split(':'); Ascii[p[0]] = new(Lex.Cmd, p[1], Atom.Ordinary);
        }
        void A(string key, string glyph, Atom atom = Atom.Ordinary) => Ascii[key] = new(Lex.Glyph, glyph, atom);
        foreach (var p in new (string Name, string TexName)[] { ("oo", "infty"), ("del", "partial"), ("grad", "nabla"), ("+-", "pm"), ("-+", "mp"), ("xx", "times"), ("-:", "div"), ("**", "ast"), ("***", "star"), ("o+", "oplus"), ("ox", "otimes"), ("o.", "odot"), ("nn", "cap"), ("nnn", "bigcap"), ("uu", "cup"), ("uuu", "bigcup"), ("^^", "wedge"), ("^^^", "bigwedge"), ("vv", "vee"), ("vvv", "bigvee"), ("!=", "neq"), ("<=", "le"), (">=", "ge"), ("-=", "equiv"), ("~=", "cong"), ("~~", "approx"), ("prop", "propto"), ("!in", "notin"), ("sub", "subset"), ("sube", "subseteq"), ("supe", "supseteq"), ("->", "to"), ("<-", "leftarrow"), ("<->", "leftrightarrow"), ("=>", "implies"), ("<=>", "iff"), ("|->", "mapsto"), ("AA", "forall"), ("EE", "exists"), ("not", "neg"), ("O/", "emptyset"), ("...", "ldots"), ("_|_", "bot") })
            Ascii[p.Name] = Tex[p.TexName];
        A("*", "⋅", Atom.Binary); A("//", "/"); A("@", "∘", Atom.Binary); A("~", "∼", Atom.Relation);
        A("and", "and", Atom.Function); A("or", "or", Atom.Function); A("if", "if", Atom.Function);
        foreach (var (name, symbol) in new[] { ("RR", "ℝ"), ("NN", "ℕ"), ("ZZ", "ℤ"), ("QQ", "ℚ"), ("CC", "ℂ") }) A(name, symbol);
        foreach (var (name, value) in new[] { ("langle", "⟨"), ("rangle", "⟩"), ("lfloor", "⌊"), ("rfloor", "⌋"), ("lceil", "⌈"), ("rceil", "⌉"), ("vert", "|"), ("Vert", "‖"), ("lvert", "|"), ("rvert", "|"), ("lVert", "‖"), ("rVert", "‖") })
            Tex[name] = new(Lex.Glyph, value, Atom.Ordinary);
        Ascii["(:"] = Ascii["<<"] = new(Lex.Left, "⟨", Atom.Open); Ascii[":)"] = Ascii[">>"] = new(Lex.Right, "⟩", Atom.Close);
        Ascii["{:"] = new(Lex.Left, ".", Atom.Open); Ascii[":}"] = new(Lex.Right, ".", Atom.Close);
        foreach (var s in "cancel color stackrel overset underset obrace ubrace overarc overparen mathcal mathfrak fr cc".Split(' ')) Ascii[s] = new(Lex.Cmd, "unsupported", Atom.Ordinary);
        Ascii["text"] = new(Lex.Text, "", Atom.Ordinary);
        Ascii["sup"] = Tex["supset"]; Ascii["epsi"] = Tex["epsilon"]; Ascii["Sqrt"] = Ascii["sqrt"];
        Ascii["lamda"] = Tex["lambda"]; Ascii["Lamda"] = Tex["Lambda"];
        foreach (var differential in new[] { "dx", "dy", "dz", "dt" }) A(differential, differential, Atom.Variable);
        foreach (var p in new (string Name, string Target)[] { ("divide", "div"), ("lt=", "le"), ("gt=", "ge"), ("mlt", "ll"), ("mgt", "gg"), ("-<", "prec"), ("-lt", "prec"), (">-", "succ"), ("-<=", "preceq"), (">-=", "succeq"), ("TT", "top"), ("uarr", "uparrow"), ("darr", "downarrow"), ("rarr", "rightarrow"), ("larr", "leftarrow"), ("harr", "leftrightarrow"), ("rArr", "Rightarrow"), ("lArr", "Leftarrow"), ("hArr", "Leftrightarrow"), ("Lim", "lim") })
            Ascii[p.Name] = Tex[p.Target];
        foreach (var p in new (string Name, string Value)[] { ("lt", "<"), ("gt", ">"), (":=", ":="), ("|--", "⊢"), ("|==", "⊨"), (">->", "↣"), ("->>", "↠"), (">->>", "⤖"), ("uArr", "⇑"), ("dArr", "⇓") }) A(p.Name, p.Value, Atom.Relation);
        foreach (var p in new (string Name, string Value)[] { ("prime", "′"), ("frown", "⌢"), ("diamond", "◇"), ("square", "□"), (":.", "∴"), (":'", "∵"), ("/_", "∠"), ("/_\\", "△") }) A(p.Name, p.Value);
        foreach (var name in "sech csch lub glb".Split(' ')) A(name, name, Atom.Function);
        foreach (var name in "Sin Cos Tan Arcsin Arccos Arctan Sinh Cosh Tanh Cot Sec Csc Log Ln".Split(' ')) A(name, name, Atom.Function);
        foreach (var p in new[] { "mathbf", "mathbb", "mathsf", "mathtt" }) Ascii[p] = Tex[p];
        Ascii["mbox"] = Ascii["text"];
        Ascii["left("] = new(Lex.Left, "(", Atom.Open); Ascii["right)"] = new(Lex.Right, ")", Atom.Close);
        Ascii["left["] = new(Lex.Left, "[", Atom.Open); Ascii["right]"] = new(Lex.Right, "]", Atom.Close);
        // Reserve upstream tokens whose extended semantics are not implemented. They
        // must never be silently split into ordinary variables or smaller operators.
        foreach (var name in new[] { "|><", "><|", "|><|", "|:", " :|".Trim(), ":|:", "|__", "__|", "|~", "~|", "lceiling", "rceiling", "newline", "%", "\\\\" })
            Ascii[name] = new(Lex.Cmd, "unsupported", Atom.Ordinary);
        Initial = Ascii.Keys.GroupBy(s => s[0]).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Length).ToArray());
    }

    private static List<Token> Tokenize(string source, bool latex)
    {
        var tokens = new List<Token>();
        int pos = 0;
        while (pos < source.Length)
        {
            if (tokens.Count >= 4096) throw new InvalidFormula();
            int at = pos;
            char c = source[pos++];
            void Put(Lex k, string v, Atom a = Atom.Ordinary) => tokens.Add(new(k, v, at, pos - at, a));
            if (char.IsWhiteSpace(c)) continue;
            if (c == '\\')
            {
                if (pos >= source.Length) throw new InvalidFormula();
                if (!latex)
                {
                    if (source[pos++] != ' ') throw new InvalidFormula();
                    Put(Lex.Cmd, "space"); continue;
                }
                if (!char.IsAsciiLetter(source[pos]))
                {
                    char special = source[pos++];
                    if (special == '\\') Put(Lex.Break, "");
                    else if (special is ',' or ':' or ';' or '!' or ' ') Put(Lex.Cmd, special switch { ',' => "thin", ':' => "medium", ';' => "thick", '!' => "negative", _ => "space" });
                    else if (special is '{' or '}' or '%' or '#' or '&' or '_' or '$' or '|') Put(Lex.Glyph, special == '|' ? "‖" : special.ToString());
                    else throw new InvalidFormula();
                    continue;
                }
                int start = pos;
                while (pos < source.Length && char.IsAsciiLetter(source[pos])) pos++;
                string cmd = source[start..pos];
                if (cmd is "text" or "mbox" or "operatorname" or "begin" or "end")
                {
                    string raw = TextArgument(source, ref pos, true);
                    Put(cmd switch { "begin" => Lex.BeginEnv, "end" => Lex.EndEnv, _ => Lex.Text }, raw, cmd == "operatorname" ? Atom.Function : Atom.Ordinary);
                    continue;
                }
                if (!Tex.TryGetValue(cmd, out var sym)) throw new InvalidFormula();
                Put(sym.Kind, sym.Text, sym.Class); continue;
            }
            if (!latex && Initial.TryGetValue(c, out var names))
            {
                string? match = null;
                foreach (var name in names) if (source.AsSpan(at).StartsWith(name, StringComparison.Ordinal)) { match = name; break; }
                if (match != null)
                {
                    var sym = Ascii[match]; pos = at + match.Length;
                    if (match is "text" or "mbox") Put(Lex.Text, TextArgument(source, ref pos, false));
                    else Put(sym.Kind, sym.Text, sym.Class);
                    continue;
                }
            }
            if (!latex && c == '"')
            {
                int next = source.IndexOf('"', pos);
                if (next < 0) throw new InvalidFormula();
                string val = source[pos..next]; pos = next + 1; Put(Lex.Text, val); continue;
            }
            if (char.IsAsciiDigit(c))
            {
                if (!latex)
                {
                    while (pos < source.Length && char.IsAsciiDigit(source[pos])) pos++;
                    if (pos + 1 < source.Length && source[pos] == '.' && char.IsAsciiDigit(source[pos + 1]))
                    { pos++; while (pos < source.Length && char.IsAsciiDigit(source[pos])) pos++; }
                }
                Put(Lex.Glyph, source[at..pos]); continue;
            }
            if (char.IsLetter(c)) { Put(Lex.Glyph, c.ToString(), Atom.Variable); continue; }
            switch (c)
            {
                case '{': case '(': case '[': Put(Lex.Left, c.ToString(), Atom.Open); break;
                case '}': case ')': case ']': Put(Lex.Right, c.ToString(), Atom.Close); break;
                case '_': Put(Lex.Sub, "_"); break;
                case '^': Put(Lex.Sup, "^"); break;
                case '/': Put(latex ? Lex.Glyph : Lex.Divide, "/"); break;
                case ',': Put(Lex.Comma, ",", Atom.Punctuation); break;
                case '|': Put(Lex.Bar, "|"); break;
                case '&' when latex: Put(Lex.Align, ""); break;
                case '=': case '<': case '>': case '≤': case '≥': case '≠': case '≈': case '→': case '∈': Put(Lex.Glyph, c.ToString(), Atom.Relation); break;
                case '+': case '-': case '−': case '*': case '×': case '±': Put(Lex.Glyph, c == '-' ? "−" : c.ToString(), Atom.Binary); break;
                case '.': case ':': case ';': case '!': case '\'': case '∞': Put(Lex.Glyph, c == '\'' ? "′" : c.ToString()); break;
                case '~' when latex: Put(Lex.Cmd, "space"); break;
                default: throw new InvalidFormula();
            }
        }
        tokens.Add(new(Lex.End, "", pos, 0)); return tokens;
    }

    private static string TextArgument(string source, ref int pos, bool tex)
    {
        while (pos < source.Length && char.IsWhiteSpace(source[pos])) pos++;
        if (pos >= source.Length) throw new InvalidFormula();
        char open = source[pos++], close = open switch { '{' => '}', '(' when !tex => ')', '[' when !tex => ']', _ => throw new InvalidFormula() };
        var text = new StringBuilder();
        while (pos < source.Length)
        {
            char c = source[pos++];
            if (c == close) return text.ToString();
            if (c == open || c == '$' && tex) throw new InvalidFormula();
            if (c == '\\')
            {
                if (!tex || pos == source.Length || !"{}_&#%$ ".Contains(source[pos])) throw new InvalidFormula();
                c = source[pos++];
            }
            text.Append(c);
        }
        throw new InvalidFormula();
    }
}
