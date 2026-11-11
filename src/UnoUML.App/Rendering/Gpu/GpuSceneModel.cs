using System.Numerics;
using System.Text.Json;

namespace UnoUML.App.Rendering.Gpu;

/// <summary>Bounded, atomic retained display-list updates. This model has no UI or GPU dependency.</summary>
public sealed class GpuSceneModel
{
    public const int MaxCommands = 32768;
    public const int MaxVertexFloats = 8 * 1024 * 1024;
    public const int MaxTextureSide = 8192;
    public const int MaxGradientStops = 262144;
    public const long MaxTextureBytes = 96L * 1024 * 1024;
    public const int MaxJsonCharacters = 48 * 1024 * 1024;

    internal Dictionary<string, GpuCommand> Commands { get; private set; } = new(StringComparer.Ordinal);
    internal Dictionary<string, GpuImage> Images { get; private set; } = new(StringComparer.Ordinal);
    internal string[] Order { get; private set; } = [];
    public long Revision { get; private set; } = -1;
    public Matrix3x2 Camera { get; private set; } = Matrix3x2.Identity;
    public double Width { get; private set; } = 1;
    public double Height { get; private set; } = 1;
    public int CommandCount => Order.Length;
    public long TextureBytes => Images.Values.Sum(image => (long)image.Rgba.Length);
    public long VertexBytes => Commands.Values.Sum(command => (long)command.Vertices.Length * sizeof(float));

    public bool TryApply(JsonElement value, out string? error)
    {
        error = null;
        try
        {
            if (value.ValueKind != JsonValueKind.Object) throw Invalid("The scene update must be an object.");
            if (value.TryGetProperty("version", out var version) && version.GetInt32() != 1)
                throw Invalid("Unsupported GPU scene protocol.");
            long revision = value.GetProperty("revision").GetInt64();
            if (revision < 0) throw Invalid("Invalid scene revision.");
            if (revision <= Revision) return false;
            bool reset = value.TryGetProperty("reset", out var resetValue) && resetValue.GetBoolean();
            // Camera transactions are the interaction hot path. Validate first and publish without
            // cloning dictionaries, walking vertices, decoding textures, or allocating graph state.
            if (!reset && !value.TryGetProperty("order", out _) &&
                IsEmptyUpdateArray(value, "commands") && IsEmptyUpdateArray(value, "remove") && IsEmptyUpdateArray(value, "textures"))
            {
                Matrix3x2 nextCamera = value.TryGetProperty("camera", out var cameraOnly) ? Matrix(cameraOnly) : Camera;
                double nextWidth = value.TryGetProperty("width", out var widthOnly) ? Dimension(widthOnly) : Width;
                double nextHeight = value.TryGetProperty("height", out var heightOnly) ? Dimension(heightOnly) : Height;
                Camera = nextCamera; Width = nextWidth; Height = nextHeight; Revision = revision;
                return true;
            }
            var commands = reset ? new Dictionary<string, GpuCommand>(StringComparer.Ordinal) : new Dictionary<string, GpuCommand>(Commands, StringComparer.Ordinal);
            var images = reset ? new Dictionary<string, GpuImage>(StringComparer.Ordinal) : new Dictionary<string, GpuImage>(Images, StringComparer.Ordinal);

            if (value.TryGetProperty("remove", out var remove))
                foreach (var id in Array(remove, MaxCommands)) commands.Remove(Id(id));
            long vertexFloats = commands.Values.Sum(command => (long)command.Vertices.Length);
            long gradientStops = commands.Values.Sum(command => (long)command.Paint.Stops.Length);
            if (value.TryGetProperty("commands", out var additions))
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in Array(additions, MaxCommands))
                {
                    string id = Id(item.GetProperty("id"));
                    if (!seen.Add(id)) throw Invalid("Duplicate command update.");
                    commands.TryGetValue(id, out var existing);
                    var command = ReadCommand(item, existing);
                    vertexFloats += command.Vertices.Length - (existing?.Vertices.Length ?? 0);
                    gradientStops += command.Paint.Stops.Length - (existing?.Paint.Stops.Length ?? 0);
                    if (vertexFloats > MaxVertexFloats || gradientStops > MaxGradientStops)
                        throw Invalid("GPU geometry or gradient budget exceeded.");
                    commands[id] = command;
                }
            }
            if (commands.Count > MaxCommands) throw Invalid("GPU command budget exceeded.");
            string[] order = value.TryGetProperty("order", out var orderValue)
                ? Array(orderValue, MaxCommands).Select(Id).ToArray()
                : reset ? commands.Keys.ToArray() : Order.Where(commands.ContainsKey).ToArray();
            if (order.Distinct(StringComparer.Ordinal).Count() != order.Length || order.Any(id => !commands.ContainsKey(id)))
                throw Invalid("Invalid GPU painter order.");
            // Every retained command must have a defined painter-order position.
            if (order.Length != commands.Count) throw Invalid("The painter order omits retained commands.");
            if (commands.Values.Sum(command => (long)command.Vertices.Length) > MaxVertexFloats)
                throw Invalid("GPU geometry budget exceeded.");

            var usedImages = commands.Values.Where(command => command.Kind == GpuCommandKind.Image)
                .Select(command => command.Image!).ToHashSet(StringComparer.Ordinal);
            images = images.Where(pair => usedImages.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            long textureBytes = images.Values.Sum(image => (long)image.Rgba.Length);
            if (value.TryGetProperty("textures", out var textures))
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in Array(textures, MaxCommands))
                {
                    string id = Id(item.GetProperty("id"));
                    if (!seen.Add(id)) throw Invalid("Duplicate texture update.");
                    int width = item.GetProperty("width").GetInt32(), height = item.GetProperty("height").GetInt32();
                    long length = (long)width * height * 4;
                    if (width < 1 || height < 1 || width > MaxTextureSide || height > MaxTextureSide || length > MaxTextureBytes)
                        throw Invalid("Invalid or excessive texture dimensions.");
                    string encoded = item.GetProperty("rgba").GetString() ?? "";
                    if (encoded.Length != ((length + 2) / 3) * 4) throw Invalid("Invalid RGBA payload length.");
                    textureBytes += length - (images.TryGetValue(id, out var previousImage) ? previousImage.Rgba.LongLength : 0);
                    if (textureBytes > MaxTextureBytes) throw Invalid("GPU texture budget exceeded.");
                    var rgba = Convert.FromBase64String(encoded);
                    if (rgba.LongLength != length) throw Invalid("RGBA payload does not match its dimensions.");
                    images[id] = new(id, width, height, rgba);
                }
            }
            if (usedImages.Any(id => !images.ContainsKey(id))) throw Invalid("A command references an unavailable texture.");
            images = images.Where(pair => usedImages.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (images.Values.Sum(image => (long)image.Rgba.Length) > MaxTextureBytes) throw Invalid("GPU texture budget exceeded.");

            Matrix3x2 camera = value.TryGetProperty("camera", out var cameraValue) ? Matrix(cameraValue) : Camera;
            double widthCss = value.TryGetProperty("width", out var w) ? Dimension(w) : Width;
            double heightCss = value.TryGetProperty("height", out var h) ? Dimension(h) : Height;
            Commands = commands; Images = images; Order = order;
            Camera = camera; Width = widthCss; Height = heightCss; Revision = revision;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or FormatException or OverflowException or ArgumentException or KeyNotFoundException)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool TrySetCamera(double[] matrix, double width, double height)
    {
        if (matrix is null || matrix.Length != 6 || matrix.Any(number => !double.IsFinite(number) || Math.Abs(number) > 1e9) ||
            !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 || width > 1e6 || height > 1e6)
            return false;
        Camera = new((float)matrix[0], (float)matrix[1], (float)matrix[2], (float)matrix[3], (float)matrix[4], (float)matrix[5]);
        Width = width; Height = height;
        return true;
    }

    private static GpuCommand ReadCommand(JsonElement value, GpuCommand? old)
    {
        // Kind marks a complete replacement. Only kind-less incremental patches inherit fields.
        // In particular, an absent clip on a replacement must clear a previous clip.
        if (value.TryGetProperty("kind", out _)) old = null;
        string id = Id(value.GetProperty("id"));
        GpuCommandKind kind = value.TryGetProperty("kind", out var kindValue)
            ? kindValue.GetString() switch
            {
                "fill" => GpuCommandKind.Fill,
                "mesh" => GpuCommandKind.Mesh,
                "image" => GpuCommandKind.Image,
                _ => throw Invalid("Unsupported GPU command kind.")
            }
            : old?.Kind ?? throw Invalid("A new command requires a kind.");
        float[] vertices = value.TryGetProperty("vertices", out var vertexValue)
            ? Numbers(vertexValue, -1, MaxVertexFloats) : old?.Vertices ?? [];
        if (vertices.Length % 6 != 0 || kind != GpuCommandKind.Image && vertices.Length < 6)
            throw Invalid("Geometry must contain complete XY triangles.");
        GpuRect bounds = value.TryGetProperty("bounds", out var boundsValue)
            ? Rect(boundsValue) : old?.Bounds ?? throw Invalid("A new command requires bounds.");
        if (kind != GpuCommandKind.Image)
        {
            // The cover must clear every stencil sample written by the geometry.
            // Union with actual vertices also makes malformed declared bounds safe.
            float minX = bounds.X, minY = bounds.Y, maxX = bounds.Right, maxY = bounds.Bottom;
            for (int i = 0; i < vertices.Length; i += 2)
            {
                minX = MathF.Min(minX, vertices[i]); maxX = MathF.Max(maxX, vertices[i]);
                minY = MathF.Min(minY, vertices[i + 1]); maxY = MathF.Max(maxY, vertices[i + 1]);
            }
            bounds = new(minX, minY, maxX - minX, maxY - minY);
        }
        Matrix3x2 matrix = value.TryGetProperty("matrix", out var matrixValue) ? Matrix(matrixValue) : old?.Matrix ?? Matrix3x2.Identity;
        string rule = value.TryGetProperty("rule", out var ruleValue) ? ruleValue.GetString() ?? "" : old?.Rule ?? "nonzero";
        if (rule is not ("nonzero" or "evenodd")) throw Invalid("Unsupported fill rule.");
        GpuPaint paint = value.TryGetProperty("paint", out var paintValue) ? ReadPaint(paintValue) : old?.Paint ?? GpuPaint.White;
        GpuRect? clip = value.TryGetProperty("clip", out var clipValue)
            ? clipValue.ValueKind == JsonValueKind.Null ? null : Rect(clipValue) : old?.Clip;
        string? image = value.TryGetProperty("image", out var imageValue)
            ? imageValue.ValueKind == JsonValueKind.Null ? null : Id(imageValue) : old?.Image;
        if (kind == GpuCommandKind.Image && image is null) throw Invalid("An image command requires a texture.");
        return new(id, kind, vertices, matrix, bounds, rule, paint, clip, image);
    }

    private static GpuPaint ReadPaint(JsonElement value)
    {
        string kind = value.GetProperty("kind").GetString() ?? "";
        if (kind is not ("solid" or "linear" or "radial")) throw Invalid("Unsupported paint.");
        float[] color = value.TryGetProperty("color", out var c) ? Color(c) : [1, 1, 1, 1];
        float opacity = value.TryGetProperty("opacity", out var o) ? Unit(o) : 1;
        Matrix3x2 transform = value.TryGetProperty("transform", out var t) ? Matrix(t) : Matrix3x2.Identity;
        Vector2 start = Point(value, "start", Vector2.Zero), end = Point(value, "end", Vector2.UnitX);
        Vector2 center = Point(value, "center", new(.5f)), focal = Point(value, "focal", center);
        float radius = value.TryGetProperty("radius", out var r) ? Number(r) : .5f;
        float focalRadius = value.TryGetProperty("focalRadius", out var fr) ? Number(fr) : 0;
        if (radius < 0 || focalRadius < 0) throw Invalid("Gradient radii must be nonnegative.");
        string spread = value.TryGetProperty("spread", out var sp) ? sp.GetString() ?? "" : "pad";
        if (spread is not ("pad" or "repeat" or "reflect")) throw Invalid("Unsupported gradient spread.");
        GpuStop[] stops = value.TryGetProperty("stops", out var s)
            ? Array(s, 64).Select(stop => new GpuStop(Unit(stop.GetProperty("offset")), Color(stop.GetProperty("color")))).ToArray() : [];
        if (kind != "solid" && stops.Length == 0) throw Invalid("A gradient requires stops.");
        for (int i = 1; i < stops.Length; i++)
            if (stops[i].Offset < stops[i - 1].Offset) throw Invalid("Gradient stops must be ordered.");
        return new(kind, color, opacity, transform, start, end, center, focal, radius, focalRadius, spread, stops);
    }

    private static bool IsEmptyUpdateArray(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var array)) return true;
        if (array.ValueKind != JsonValueKind.Array) throw Invalid("Invalid update array.");
        return array.GetArrayLength() == 0;
    }

    private static JsonElement[] Array(JsonElement value, int max)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > max) throw Invalid("Invalid or excessive array.");
        return value.EnumerateArray().ToArray();
    }
    private static float[] Numbers(JsonElement value, int count, int max = 16)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > max || count >= 0 && value.GetArrayLength() != count)
            throw Invalid("Invalid numeric array.");
        var result = new float[value.GetArrayLength()]; int i = 0;
        foreach (var number in value.EnumerateArray()) result[i++] = Number(number);
        return result;
    }
    private static float Number(JsonElement value)
    {
        if (!value.TryGetSingle(out float number) || !float.IsFinite(number) || MathF.Abs(number) > 1e9f)
            throw Invalid("Invalid numeric value.");
        return number;
    }
    private static double Dimension(JsonElement value)
    {
        double number = value.GetDouble();
        if (!double.IsFinite(number) || number <= 0 || number > 1e6) throw Invalid("Invalid viewport dimension.");
        return number;
    }
    private static float Unit(JsonElement value)
    {
        float number = Number(value);
        if (number < 0 || number > 1) throw Invalid("Expected a value between zero and one.");
        return number;
    }
    private static float[] Color(JsonElement value)
    {
        float[] result = Numbers(value, 4);
        if (result.Any(number => number is < 0 or > 1)) throw Invalid("Invalid normalized color.");
        return result;
    }
    private static Vector2 Point(JsonElement value, string key, Vector2 fallback)
    {
        if (!value.TryGetProperty(key, out var element)) return fallback;
        float[] numbers = Numbers(element, 2);
        return new(numbers[0], numbers[1]);
    }
    private static Matrix3x2 Matrix(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 6)
            throw Invalid("Invalid affine matrix.");
        return new(Number(value[0]), Number(value[1]), Number(value[2]),
            Number(value[3]), Number(value[4]), Number(value[5]));
    }
    private static GpuRect Rect(JsonElement value)
    {
        float[] numbers = Numbers(value, 4);
        if (numbers[2] < 0 || numbers[3] < 0) throw Invalid("Negative bounds.");
        return new(numbers[0], numbers[1], numbers[2], numbers[3]);
    }
    private static string Id(JsonElement value)
    {
        string result = value.GetString() ?? "";
        if (result.Length is < 1 or > 512 || result.Any(char.IsControl)) throw Invalid("Invalid resource identity.");
        return result;
    }
    private static InvalidOperationException Invalid(string message) => new(message);
}

internal enum GpuCommandKind { Fill, Mesh, Image }
internal sealed record GpuCommand(string Id, GpuCommandKind Kind, float[] Vertices, Matrix3x2 Matrix,
    GpuRect Bounds, string Rule, GpuPaint Paint, GpuRect? Clip, string? Image);
internal sealed record GpuImage(string Id, int Width, int Height, byte[] Rgba);
internal sealed record GpuStop(float Offset, float[] Color);
internal sealed record GpuPaint(string Kind, float[] Color, float Opacity, Matrix3x2 Transform,
    Vector2 Start, Vector2 End, Vector2 Center, Vector2 Focal, float Radius, float FocalRadius, string Spread, GpuStop[] Stops)
{
    internal static GpuPaint White { get; } = new("solid", [1, 1, 1, 1], 1, Matrix3x2.Identity,
        Vector2.Zero, Vector2.UnitX, new(.5f), new(.5f), .5f, 0, "pad", []);
}
internal readonly record struct GpuRect(float X, float Y, float Width, float Height)
{
    internal float Right => X + Width;
    internal float Bottom => Y + Height;
    internal GpuRect Transform(Matrix3x2 matrix)
    {
        var a = Vector2.Transform(new(X, Y), matrix);
        var b = Vector2.Transform(new(Right, Y), matrix);
        var c = Vector2.Transform(new(X, Bottom), matrix);
        var d = Vector2.Transform(new(Right, Bottom), matrix);
        float x = MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X));
        float y = MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y));
        return new(x, y, MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X)) - x,
            MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y)) - y);
    }
    internal bool Intersects(float width, float height) => Right >= 0 && Bottom >= 0 && X < width && Y < height && Width > 0 && Height > 0;
}

public sealed record DiagramGpuStatistics(long Revision, long PresentedFrames, int DrawCalls, int Commands,
    int VisibleCommands, int CulledCommands, long VertexBytes, long TextureBytes, double CpuEncodeMilliseconds,
    int PixelWidth, int PixelHeight, int SampleCount)
{
    public string Backend { get; init; } = "Uno shared WebGPU";
    public int PipelineCount { get; init; }
    public int GeometryBuffers { get; init; }
    public int ImageTextures { get; init; }
    public int RenderBundles { get; init; }
    public long UniformAndStopBytes { get; init; }
    public long GeometryUploadBytes { get; init; }
    public long TextureUploadBytes { get; init; }
    public long CameraUploadBytes { get; init; }
    public long DrawUniformUploadBytes { get; init; }
    public long GradientUploadBytes { get; init; }
    public long BundleBuilds { get; init; }
}
