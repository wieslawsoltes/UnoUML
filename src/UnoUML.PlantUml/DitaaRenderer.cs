using System.Numerics;

namespace UnoUML.PlantUml;

/// <summary>Reads Ditaa's orthogonal ASCII topology as a planar embedded graph.</summary>
internal static partial class DitaaRenderer
{
    private const int N = 1, E = 2, S = 4, W = 8;
    private readonly record struct Vertex(int X, int Y, bool Round);
    private sealed class Face(List<Vertex> corners, long area, bool dashed)
    {
        public readonly List<Vertex> Corners = corners;
        public readonly long Area = area;
        public readonly bool Dashed = dashed;
        public string Fill = "#ffffff", Tag = "";
        public int MinX = corners.Min(p => p.X), MaxX = corners.Max(p => p.X), MinY = corners.Min(p => p.Y), MaxY = corners.Max(p => p.Y);
    }
    private sealed class Grid(List<string> rows, int columns, int start)
    {
        internal readonly int Width = columns + 2, Height = rows.Count + 2, Columns = columns, Rows = rows.Count, SourceLine = start;
        internal readonly char[] Cells = new char[(columns + 2) * (rows.Count + 2)];
        internal readonly byte[] Adj = new byte[(columns + 2) * (rows.Count + 2)], Bridges = new byte[(columns + 2) * (rows.Count + 2)];
        internal readonly int[] Step = [-(columns + 2), 1, columns + 2, -1];
        internal readonly int[] Owner = new int[(columns + 2) * (rows.Count + 2)];
        internal readonly List<Face> Faces = [];
        internal int X(int i) => i % Width - 1;
        internal int Y(int i) => i / Width - 1;
        internal int Index(int x, int y) => (y + 1) * Width + x + 1;
        internal void Load()
        {
            Array.Fill(Owner, -1); Array.Fill(Cells, ' ');
            for (int y = 0; y < rows.Count; y++) rows[y].CopyTo(0, Cells, Index(0, y), rows[y].Length);
        }
    }

    internal static string? TryRender(string source)
    {
        if (source.Length > 150000) return null;
        var rows = new List<string>(); int line = -1, start = -1, columns = 0; bool closed = false;
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            line++;
            var trimmed = raw.Trim();
            if (start < 0) { if (trimmed.IsEmpty) continue; if (!trimmed.SequenceEqual("@startditaa")) return null; start = line + 1; continue; }
            if (closed) { if (!trimmed.IsEmpty) return null; continue; }
            if (trimmed.SequenceEqual("@endditaa")) { closed = true; continue; }
            if (trimmed.StartsWith("!", StringComparison.Ordinal) || trimmed.StartsWith("@", StringComparison.Ordinal) ||
                trimmed.StartsWith("< --", StringComparison.Ordinal) || trimmed.StartsWith("< -", StringComparison.Ordinal)) return null;
            string text = raw.TrimEnd(' ').ToString();
            if (text.Length > 700 || rows.Count >= 550 || text.Any(c => char.IsControl(c) || char.IsSurrogate(c))) return null;
            columns = Math.Max(columns, text.Length); rows.Add(text);
        }
        if (!closed || columns == 0 || columns * rows.Count > 110000) return null;
        var grid = new Grid(rows, columns, start); grid.Load();
        if (!BuildEdges(grid)) return null;
        FindBridges(grid);
        if (!FindFaces(grid) || !AssignInteriors(grid) || !ReadTags(grid)) return null;
        return Draw(grid);
    }

    private static int Ports(char c) => c switch
    {
        '-' or '=' => E | W,
        '|' or ':' => N | S,
        '+' or '/' or '\\' or '*' => N | E | S | W,
        '>' => W,
        '<' => E,
        '^' => S,
        'v' or 'V' => N,
        _ => 0
    };
    private static bool BuildEdges(Grid g)
    {
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Columns; x++)
            {
                int i = g.Index(x, y), ports = Ports(g.Cells[i]);
                for (int dir = 0; dir < 4; dir++)
                {
                    if ((ports & (1 << dir)) == 0) continue;
                    int j = i + g.Step[dir];
                    if ((Ports(g.Cells[j]) & (1 << ((dir + 2) % 4))) != 0) g.Adj[i] |= (byte)(1 << dir);
                }
            }
        for (int i = 0; i < g.Cells.Length; i++)
        {
            byte mask = g.Adj[i];
            if (mask == 0) continue;
            if (g.Cells[i] is '/' or '\\')
            {
                bool known = g.Cells[i] == '/' ? mask is (S | E) or (N | W) : mask is (W | S) or (N | E);
                if (!known) return false;
            }
            // Avoid treating punctuation within ordinary words as a cable glyph, e.g. "long--name".
            if (g.Cells[i] is '-' or '=' && (char.IsLetterOrDigit(g.Cells[i - 1]) || char.IsLetterOrDigit(g.Cells[i + 1]))) return false;
            if (g.Cells[i] is 'v' or 'V' && (char.IsLetterOrDigit(g.Cells[i - 1]) || char.IsLetterOrDigit(g.Cells[i + 1]))) return false;
        }
        return true;
    }

    // Tarjan bridges isolate open wires from face boundaries. An arrow between two boxes
    // must never turn both boxes into a single filled polygon.
    private static void FindBridges(Grid g)
    {
        int n = g.Cells.Length, tick = 0;
        var order = new int[n]; var low = new int[n]; var parent = new int[n]; var cursor = new byte[n]; var stack = new int[n];
        for (int root = 0; root < n; root++)
        {
            if (g.Adj[root] == 0 || order[root] != 0) continue;
            int size = 1; stack[0] = root; parent[root] = -1; low[root] = order[root] = ++tick;
            while (size > 0)
            {
                int i = stack[size - 1];
                if (cursor[i] < 4)
                {
                    int d = cursor[i]++, j = i + g.Step[d];
                    if ((g.Adj[i] & (1 << d)) == 0 || j == parent[i]) continue;
                    if (order[j] != 0) { low[i] = Math.Min(low[i], order[j]); continue; }
                    parent[j] = i; low[j] = order[j] = ++tick; stack[size++] = j;
                }
                else
                {
                    size--; int ancestor = parent[i]; if (ancestor < 0) continue;
                    low[ancestor] = Math.Min(low[ancestor], low[i]);
                    if (low[i] <= order[ancestor]) continue;
                    int outDir = Array.IndexOf(g.Step, i - ancestor);
                    g.Bridges[ancestor] |= (byte)(1 << outDir); g.Bridges[i] |= (byte)(1 << ((outDir + 2) % 4));
                }
            }
        }
    }

    private static bool FindFaces(Grid g)
    {
        var used = new byte[g.Cells.Length];
        for (int origin = 0; origin < used.Length; origin++)
            for (int initial = 0; initial < 4; initial++)
            {
                if ((g.Adj[origin] & ~g.Bridges[origin] & ~used[origin] & (1 << initial)) == 0) continue;
                var corners = new List<Vertex>();
                int i = origin, d = initial, steps = 0;
                long area = 0; bool dashed = false;
                do
                {
                    if (++steps > 4 * g.Cells.Length || (used[i] & (1 << d)) != 0) return false;
                    used[i] |= (byte)(1 << d);
                    int j = i + g.Step[d];
                    area += (long)g.X(i) * g.Y(j) - (long)g.X(j) * g.Y(i);
                    dashed |= g.Cells[j] is '=' or ':';
                    int mask = g.Adj[j] & ~g.Bridges[j];
                    // Right-hand walk visits each directed half-edge exactly once.
                    int next = (d + 1) % 4;
                    while ((mask & (1 << next)) == 0) next = (next + 3) % 4;
                    if (next != d) corners.Add(new(g.X(j), g.Y(j), g.Cells[j] is '/' or '\\'));
                    i = j; d = next;
                }
                while (i != origin || d != initial);
                if (area <= 0) continue; // exterior face
                if (corners.Count < 4 || corners.Count > 2500 || g.Faces.Count >= 700) return false;
                g.Faces.Add(new(corners, area, dashed));
            }
        g.Faces.Sort((a, b) => b.Area.CompareTo(a.Area));
        return true;
    }

    private static bool AssignInteriors(Grid g)
    {
        // Scan horizontal bands from large boundary loops to small ones. A nested node owns
        // its own tags, even when it sits inside another shape.
        long work = 0;
        for (int index = 0; index < g.Faces.Count; index++)
        {
            var face = g.Faces[index]; var v = face.Corners;
            work += (long)(face.MaxY - face.MinY) * (v.Count + face.MaxX - face.MinX);
            if (work > 8000000) return false;
            var cuts = new List<int>();
            for (int y = face.MinY; y < face.MaxY; y++)
            {
                cuts.Clear();
                for (int k = 0; k < v.Count; k++)
                {
                    var a = v[k]; var b = v[(k + 1) % v.Count];
                    if (a.X == b.X && Math.Min(a.Y, b.Y) <= y && y < Math.Max(a.Y, b.Y)) cuts.Add(a.X);
                }
                cuts.Sort();
                if (cuts.Count % 2 != 0) return false;
                for (int k = 0; k < cuts.Count; k += 2)
                    for (int x = cuts[k] + 1; x < cuts[k + 1]; x++)
                        if (g.Adj[g.Index(x, y)] == 0) g.Owner[g.Index(x, y)] = index;
            }
        }
        return true;
    }

    private static readonly Dictionary<string, string> Colors = new(StringComparer.Ordinal)
    {
        ["GRE"] = "#99dd99",
        ["BLU"] = "#5555bb",
        ["RED"] = "#ee3322",
        ["YEL"] = "#ffff33",
        ["PNK"] = "#ffaaaa",
        ["BLK"] = "#000000"
    };
    private static bool ReadTags(Grid g)
    {
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Columns; x++)
            {
                int i = g.Index(x, y); char c = g.Cells[i]; int owner = g.Owner[i];
                if (owner < 0) continue;
                var shape = g.Faces[owner];
                int length = 0;
                if (c == 'c' && x + 3 < g.Columns)
                {
                    string code = new(g.Cells, i + 1, 3);
                    if (Colors.TryGetValue(code, out var fill)) { shape.Fill = fill; length = 4; }
                    else if (code.All(Uri.IsHexDigit))
                    {
                        shape.Fill = "#" + string.Concat(code.Select(k => new string(k, 2))); length = 4;
                    }
                }
                if (c == '{')
                {
                    int end = x + 1;
                    while (end < g.Columns && char.IsAsciiLetter(g.Cells[g.Index(end, y)])) end++;
                    if (end < g.Columns && end > x + 1 && g.Cells[g.Index(end, y)] == '}')
                    {
                        string tag = new(g.Cells, i + 1, end - x - 1);
                        if (tag is not ("d" or "s" or "io" or "c" or "mo" or "o" or "tr") || shape.Tag.Length != 0 || shape.Corners.Count != 4) return false;
                        shape.Tag = tag; length = end - x + 1;
                    }
                }
                if (length == 0) continue;
                for (int k = 0; k < length; k++) if (g.Owner[i + k] != owner || g.Adj[i + k] != 0) return false;
                Array.Fill(g.Cells, ' ', i, length); x += length - 1;
            }
        return true;
    }
}
