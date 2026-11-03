namespace UnoUML.PlantUml;

internal static partial class SaltRenderer
{
    private static List<Token>? Tokenize(string source)
    {
        var tokens = new List<Token>();
        int phase = 0, lineNumber = -1, nextOffset = 0;
        foreach (var physical in source.AsSpan().EnumerateLines())
        {
            lineNumber++;
            int offset = nextOffset;
            nextOffset += physical.Length;
            if (nextOffset < source.Length && source[nextOffset] == '\r') nextOffset++;
            if (nextOffset < source.Length && source[nextOffset] == '\n') nextOffset++;
            var line = physical.Trim();
            if (line.IsEmpty || line[0] == '\'') continue;
            if (phase == 0)
            {
                if (!line.SequenceEqual("@startsalt")) return null;
                phase = 1;
                continue;
            }
            if (phase != 1) return null;
            if (line.SequenceEqual("@endsalt")) { phase = 2; continue; }
            if (line.Length > 8192 || line[0] is '!' or '@') return null;
            foreach (string reserved in Reserved)
                if (line.StartsWith(reserved, StringComparison.OrdinalIgnoreCase)) return null;
            string raw = physical.ToString();
            int begin = 0;
            for (int cursor = 0; cursor < raw.Length; cursor++)
            {
                char value = raw[cursor];
                if (value == '\\' && cursor + 1 < raw.Length && raw[cursor + 1] is '|' or '{' or '}') { cursor++; continue; }
                if (value is not ('|' or '{' or '}')) continue;
                Add(begin, cursor, false);
                if (value == '{')
                {
                    int end = cursor + 1;
                    if (end < raw.Length && "#!-+^/*".Contains(raw[end])) end++;
                    else if (end < raw.Length && raw[end] == 'S')
                    {
                        end++;
                        if (end < raw.Length && raw[end] is 'I' or '-') end++;
                    }
                    else if (end < raw.Length && raw[end] == 'T')
                    {
                        end++;
                        if (end < raw.Length && "#!-+".Contains(raw[end])) end++;
                    }
                    Add(cursor, end, raw.AsSpan(end).Trim().IsEmpty);
                    cursor = end - 1;
                }
                else if (value == '}') Add(cursor, cursor + 1, raw.AsSpan(cursor + 1).Trim().IsEmpty);
                begin = cursor + 1;
            }
            Add(begin, raw.Length, true);
            if (tokens.Count > 16384) return null;

            void Add(int start, int end, bool newRow)
            {
                while (start < end && char.IsWhiteSpace(raw[start])) start++;
                while (end > start && char.IsWhiteSpace(raw[end - 1])) end--;
                if (end > start) tokens.Add(new(raw[start..end], offset + start, lineNumber, start, newRow));
            }
        }
        return phase == 2 && tokens.Count > 0 ? tokens : null;
    }

    private static readonly string[] Reserved = ["title ", "header ", "footer ", "caption ", "legend", "skinparam", "sprite ", "scale ", "hide ", "show "];

    private static Form? ParseForm(List<Token> tokens, ref int position, int depth)
    {
        if (depth >= 32 || position >= tokens.Count) return null;
        var opener = tokens[position++];
        if (!opener.Text.StartsWith('{')) return null;
        var form = new Form();
        switch (opener.Text)
        {
            case "{": break;
            case "{#": case "{!": case "{-": case "{+": form.Grid = opener.Text[1]; break;
            case "{^":
                form.Grid = '+';
                if (!opener.NewRow && position < tokens.Count)
                {
                    var title = tokens[position++];
                    if (title.Text.StartsWith('"') && title.Text.EndsWith('"') && title.Text.Length >= 2)
                        title = title with { Text = title.Text[1..^1], Start = title.Start + 1, Column = title.Column + 1 };
                    form.Title = ParseCell(title, Widget.Label);
                    if (form.Title == null) return null;
                    form.Header = form.Title.Height + 2;
                }
                break;
            case "{S": case "{SI": case "{S-":
                form.Kind = "scroll";
                form.Grid = '+';
                form.Right = opener.Text == "{S-" ? 0 : 13;
                form.Bottom = opener.Text == "{SI" ? 0 : 13;
                break;
            case "{/": form.Kind = "tabs"; break;
            case "{*": form.Kind = "menu"; break;
            case "{T": case "{T#": case "{T!": case "{T-": case "{T+":
                form.Kind = "tree";
                if (opener.Text.Length == 3) form.Grid = opener.Text[2];
                break;
            default: return null;
        }
        if (opener.Text == "{" && position < tokens.Count && tokens[position].Text is "T" or "T#" or "T!" or "T-" or "T+")
        {
            string marker = tokens[position++].Text;
            form.Kind = "tree";
            if (marker.Length == 2) form.Grid = marker[1];
        }
        var row = new List<Cell>();
        bool verticalTabs = false, closed = false;
        int columns = 0;
        while (position < tokens.Count)
        {
            var token = tokens[position];
            if (token.Text == "}") { position++; closed = true; break; }
            bool newRow;
            if (token.Text == "*")
            {
                if (row.Count == 0 || form.Kind is "tree" or "tabs" or "menu") return null;
                row[^1].Span++;
                position++;
                newRow = token.NewRow;
            }
            else
            {
                Cell? cell;
                if (token.Text.StartsWith('{'))
                {
                    if (form.Kind is "tabs" or "menu" || form.Kind == "tree" && row.Count == 0) return null;
                    var nested = ParseForm(tokens, ref position, depth + 1);
                    if (nested == null) return null;
                    cell = new Cell { Type = Widget.Group, Form = nested, Width = nested.Width + 4, Height = nested.Height + 4 };
                    newRow = tokens[position - 1].NewRow;
                }
                else
                {
                    position++;
                    if (token.Text is "N=" or "S=" or "E=" or "W=") return null;
                    Widget? forced = form.Kind switch { "tabs" => Widget.Tab, "menu" => Widget.Menu, "tree" when row.Count == 0 => Widget.Tree, _ => null };
                    cell = ParseCell(token, forced);
                    if (cell == null || cell.Depth > 128) return null;
                    newRow = token.NewRow;
                    if (form.Kind == "tabs" && newRow) verticalTabs = true;
                }
                cell.ColumnIndex = columns;
                row.Add(cell);
            }
            if (++columns > 128) return null;
            if (newRow)
            {
                form.Rows.Add(row);
                row = [];
                columns = 0;
                if (form.Rows.Count > 1024) return null;
            }
        }
        if (row.Count > 0) form.Rows.Add(row);
        if (!closed || form.Rows.Count is 0 or > 1024) return null;
        if (form.Kind == "tabs" && verticalTabs)
        {
            var cells = form.Rows.SelectMany(value => value).ToArray();
            form.Rows.Clear();
            foreach (var cell in cells) { cell.ColumnIndex = 0; form.Rows.Add([cell]); }
            form.Kind = "vertical-tabs";
        }
        if (form.Kind == "menu" && form.Rows.Count > 1)
        {
            form.Popups = [];
            foreach (var popup in form.Rows.Skip(1))
            {
                if (popup.Count < 2) return null;
                var targets = form.Rows[0].Where(cell => cell.Text == popup[0].Text).ToArray();
                if (targets.Length != 1 || form.Popups.Any(p => p.Target == targets[0])) return null;
                form.Popups.Add((targets[0], popup.Skip(1).ToList()));
            }
            form.Rows.RemoveRange(1, form.Rows.Count - 1);
        }
        return Measure(form) ? form : null;
    }

    private static bool Measure(Form form)
    {
        int columns = form.Rows.Max(row => row.Sum(cell => cell.Span));
        if (columns > 128) return false;
        form.Widths = new int[columns];
        form.Heights = new int[form.Rows.Count];
        for (int row = 0; row < form.Rows.Count; row++)
        {
            int column = 0;
            foreach (var cell in form.Rows[row])
            {
                if (cell.Span == 1) form.Widths[column] = Math.Max(form.Widths[column], cell.Width);
                column += cell.Span;
                form.Heights[row] = Math.Max(form.Heights[row], cell.Height);
            }
        }
        foreach (var row in form.Rows)
        {
            int column = 0;
            foreach (var cell in row)
            {
                int available = 0;
                for (int span = 0; span < cell.Span; span++) available += form.Widths[column + span];
                if (available < cell.Width) form.Widths[column + cell.Span - 1] += cell.Width - available;
                column += cell.Span;
            }
        }
        int required = (form.Title?.Width ?? 0) + 8;
        int total = form.Widths.Sum();
        if (form.Title != null && total < required) form.Widths[^1] += required - total;
        form.Width = form.Widths.Sum() + form.Right;
        form.Height = form.Heights.Sum() + form.Header + form.Bottom;
        return form.Width <= 32760 && form.Height <= 32760;
    }
}
