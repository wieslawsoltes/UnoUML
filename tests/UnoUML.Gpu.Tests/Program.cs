using System.Numerics;
using System.Text.Json;
using UnoUML.App.Rendering.Gpu;

namespace UnoUML.Gpu.Tests;

internal static class Program
{
    private static int assertions;

    private const string InitialScene = """
        {
          "version": 1, "revision": 1, "reset": true,
          "width": 640, "height": 480, "camera": [2,0,0,2,12,14],
          "commands": [
            {
              "id": "triangle", "kind": "fill",
              "vertices": [0,0,20,0,0,10], "bounds": [0,0,20,10],
              "paint": {"kind":"solid","color":[1,0,0,1]}
            },
            {"id":"label","kind":"image","image":"glyph","bounds":[0,0,1,1]}
          ],
          "textures": [{"id":"glyph","width":1,"height":1,"rgba":"/////w=="}],
          "order": ["triangle","label"]
        }
        """;

    public static int Main()
    {
        (string Name, Action Run)[] cases =
        [
            ("retained scene and painter order", InitialState),
            ("atomic rejection of malformed patches", AtomicFailures),
            ("stale revision suppression", StaleRevisions),
            ("transform updates retain geometry and images", RetainedResources),
            ("full replacements reset optional state and partial patches retain it", FullReplacementSemantics),
            ("texture replacement and removal", TextureLifetime),
            ("camera changes and numerical validation", CameraUpdates),
            ("camera-only transactions retain exact resource collections", CameraTransactions),
            ("conservative transformed bounds and culling", BoundsAndCulling),
            ("gradient and clipping protocol", PaintAndClip),
            ("bounded command and texture payloads", Budgets),
            ("reset replaces the retained scene", ResetScene)
        ];
        int failures = 0;
        foreach (var (name, run) in cases)
        {
            try { run(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
        }
        Console.WriteLine($"{assertions} GPU scene assertions; {cases.Length - failures}/{cases.Length} cases passed.");
        return failures == 0 ? 0 : 1;
    }

    private static GpuSceneModel Seed()
    {
        var model = new GpuSceneModel();
        Accept(model, InitialScene);
        return model;
    }

    private static bool Apply(GpuSceneModel model, string json, out string? error)
    {
        using var document = JsonDocument.Parse(json);
        return model.TryApply(document.RootElement, out error);
    }

    private static void Accept(GpuSceneModel model, string json)
    {
        bool applied = Apply(model, json, out string? error);
        Check(applied, "Expected scene acceptance: " + error);
        Check(error is null, "Successful update must clear its diagnostic");
    }

    private static void Reject(GpuSceneModel model, string json)
    {
        var before = Snapshot.Take(model);
        bool applied = Apply(model, json, out string? error);
        Check(!applied, "Malformed update was accepted: " + json[..Math.Min(json.Length, 160)]);
        Check(!string.IsNullOrWhiteSpace(error), "Malformed update must report a diagnostic");
        before.AssertUnchanged(model);
    }

    private static void InitialState()
    {
        var model = Seed();
        Equal(model.Revision, 1L);
        Equal(model.CommandCount, 2);
        Equal(string.Join(",", model.Order), "triangle,label");
        Equal(model.VertexBytes, 24L);
        Equal(model.TextureBytes, 4L);
        Equal(model.Width, 640d);
        Equal(model.Height, 480d);
        Equal(Vector2.Transform(new(1, 2), model.Camera), new Vector2(14, 18));
        Equal(model.Commands["triangle"].Kind, GpuCommandKind.Fill);
        Check(model.Images["glyph"].Rgba.AsSpan().SequenceEqual(new byte[] { 255, 255, 255, 255 }), "Decoded RGBA bytes");
    }

    private static void AtomicFailures()
    {
        var model = Seed();
        string[] failures =
        [
            """[]""",
            """{"version":2,"revision":2}""",
            """{"revision":-1}""",
            """{"revision":2,"width":0}""",
            """{"revision":2,"height":1e99}""",
            """{"revision":2,"camera":[1,0,0,1,0]}""",
            """{"revision":2,"commands":[{"id":"triangle","matrix":[1,0,0,1,1e999,0]}]}""",
            """{"revision":2,"commands":[{"id":"triangle","vertices":[0,0,1,1]}]}""",
            """{"revision":2,"commands":[{"id":"triangle","bounds":[0,0,-1,1]}]}""",
            """{"revision":2,"commands":[{"id":"triangle","kind":"unknown"}]}""",
            """{"revision":2,"commands":[{"id":"triangle","rule":"winding-ish"}]}""",
            """{"revision":2,"commands":[{"id":"triangle","paint":{"kind":"solid","color":[1,0,0,2]}}]}""",
            """{"revision":2,"commands":[{"id":"triangle","paint":{"kind":"solid","opacity":-0.1}}]}""",
            """{"revision":2,"commands":[{"id":"triangle"},{"id":"triangle"}]}""",
            """{"revision":2,"commands":[{"id":"triangle","matrix":[1,0,0,1,40,50]}],"order":["triangle","missing"]}""",
            """{"revision":2,"order":["triangle","triangle"]}""",
            """{"revision":2,"order":["triangle"]}""",
            """{"revision":2,"commands":[{"id":"new","kind":"mesh","vertices":[0,0,1,0,0,1],"bounds":[0,0,1,1]}]}""",
            """{"revision":2,"commands":[{"id":"label","image":"missing"}]}""",
            """{"revision":2,"textures":[{"id":"glyph","width":1,"height":1,"rgba":"AAAA"}]}""",
            """{"revision":2,"textures":[{"id":"glyph","width":1,"height":1,"rgba":"AAAAAAAA"}]}""",
            """{"revision":2,"textures":[{"id":"glyph","width":1,"height":1,"rgba":"??????=="}]}""",
            """{"revision":2,"textures":[{"id":"glyph","width":1,"height":1,"rgba":"/////w=="},{"id":"glyph","width":1,"height":1,"rgba":"/////w=="}]}""",
            """{"revision":2,"commands":[{"id":"bad\u0001id","kind":"mesh","vertices":[0,0,1,0,0,1],"bounds":[0,0,1,1]}]}"""
        ];
        foreach (string failure in failures) Reject(model, failure);
        // The valid part of an earlier rejected patch must not leak into a later update.
        Accept(model, """{"revision":2,"commands":[{"id":"triangle","matrix":[1,0,0,1,4,5]}]}""");
        Equal(model.Commands["triangle"].Matrix.Translation, new Vector2(4, 5));
    }

    private static void StaleRevisions()
    {
        var model = Seed();
        var before = Snapshot.Take(model);
        foreach (long revision in new[] { 0L, 1L })
        {
            // A stale command payload is never parsed or applied.
            bool applied = Apply(model, "{\"revision\":" + revision + ",\"commands\":\"obsolete\"}", out string? error);
            Check(!applied, "Stale revision was accepted");
            Check(error is null, "A stale revision is not a malformed update");
            before.AssertUnchanged(model);
        }
        Accept(model, """{"revision":3}""");
        Equal(model.Revision, 3L);
        Check(!Apply(model, """{"revision":2,"reset":true}""", out _), "Reset cannot roll the scene backwards");
        Equal(model.CommandCount, 2);
    }

    private static void RetainedResources()
    {
        var model = Seed();
        var triangle = model.Commands["triangle"];
        var label = model.Commands["label"];
        var glyph = model.Images["glyph"];
        Accept(model, """
            {"revision":2,"commands":[{"id":"triangle","matrix":[0,1,-1,0,30,40],
              "paint":{"kind":"solid","color":[0,1,0,1],"opacity":0.5}}]}
            """);
        var updated = model.Commands["triangle"];
        Check(!ReferenceEquals(updated, triangle), "Changed command descriptor must be replaced");
        Check(ReferenceEquals(updated.Vertices, triangle.Vertices), "Drag must retain the original vertex allocation");
        Check(ReferenceEquals(model.Commands["label"], label), "Unchanged draw commands must retain identity");
        Check(ReferenceEquals(model.Images["glyph"], glyph), "Transform updates must retain decoded texture identity");
        Check(ReferenceEquals(model.Images["glyph"].Rgba, glyph.Rgba), "Transform updates must not copy image pixels");
        Equal(updated.Paint.Opacity, 0.5f);
        Equal(updated.Matrix.Translation, new Vector2(30, 40));
        Equal(string.Join(",", model.Order), "triangle,label");
    }


    private static void FullReplacementSemantics()
    {
        var model = Seed();
        var pixels = model.Images["glyph"].Rgba;
        Accept(model, """
            {"revision":2,"commands":[{
              "id":"label","kind":"image","image":"glyph","bounds":[0,0,1,1],
              "matrix":[2,0,0,3,20,30],"clip":[1,2,3,4],
              "paint":{"kind":"solid","color":[0.2,0.4,0.6,0.8],"opacity":0.25}
            }]}
            """);
        Check(model.Commands["label"].Clip is not null, "Initial image owns a clip");
        Equal(model.Commands["label"].Paint.Opacity, .25f);

        // A kind-bearing command is a complete snapshot. The SVG compiler omits
        // absent image clips; inheriting one would keep a deleted clip active.
        Accept(model, """
            {"revision":3,"commands":[{
              "id":"label","kind":"image","image":"glyph","bounds":[4,5,2,2]
            }]}
            """);
        var replacement = model.Commands["label"];
        Check(replacement.Clip is null, "Full image replacement clears an omitted clip");
        Equal(replacement.Matrix, Matrix3x2.Identity);
        Equal(replacement.Paint.Opacity, 1f);
        Check(replacement.Paint.Color.AsSpan().SequenceEqual(new float[] { 1, 1, 1, 1 }),
            "Full image replacement restores neutral tint");
        Equal(replacement.Bounds, new GpuRect(4, 5, 2, 2));
        Check(ReferenceEquals(model.Images["glyph"].Rgba, pixels), "Unchanged image pixels stay retained");

        // A command without kind is explicitly a patch. Changing its matrix
        // must preserve established clip, paint, image and vertex ownership.
        Accept(model, """
            {"revision":4,"commands":[{
              "id":"label","clip":[10,20,30,40],
              "paint":{"kind":"solid","color":[0,1,0,1],"opacity":0.5}
            }]}
            """);
        var before = model.Commands["label"];
        Accept(model, """{"revision":5,"commands":[{"id":"label","matrix":[1,0,0,1,11,12]}]}""");
        var patched = model.Commands["label"];
        Equal(patched.Matrix, Matrix3x2.CreateTranslation(11, 12));
        Equal(patched.Clip, before.Clip);
        Check(ReferenceEquals(patched.Paint, before.Paint), "Matrix patch retains exact paint object");
        Check(ReferenceEquals(patched.Vertices, before.Vertices), "Matrix patch retains exact vertex array");
        Equal(patched.Image, before.Image);
        Check(ReferenceEquals(model.Images["glyph"].Rgba, pixels), "Matrix patch retains image bytes");

        // Full vectors obey the same defaulting contract instead of silently
        // inheriting the previous owner's red fill.
        Accept(model, """
            {"revision":6,"commands":[{
              "id":"triangle","kind":"mesh","vertices":[1,1,3,1,1,3],"bounds":[1,1,2,2]
            }]}
            """);
        Equal(model.Commands["triangle"].Kind, GpuCommandKind.Mesh);
        Equal(model.Commands["triangle"].Matrix, Matrix3x2.Identity);
        Check(model.Commands["triangle"].Paint.Color.AsSpan().SequenceEqual(new float[] { 1, 1, 1, 1 }),
            "Full vector replacement resets omitted paint");
        Reject(model, """{"revision":7,"commands":[{"id":"label","kind":"image","bounds":[0,0,1,1]}]}""");
        Reject(model, """{"revision":7,"commands":[{"id":"triangle","kind":"fill","bounds":[0,0,1,1]}]}""");
    }

    private static void TextureLifetime()
    {
        var model = Seed();
        var geometry = model.Commands["triangle"].Vertices;
        var previous = model.Images["glyph"];
        Accept(model, """{"revision":2,"textures":[{"id":"glyph","width":1,"height":1,"rgba":"AAAAAA=="}]}""");
        Check(!ReferenceEquals(previous, model.Images["glyph"]), "Texture update must replace its resource descriptor");
        Check(model.Images["glyph"].Rgba.All(value => value == 0), "Replacement pixel contents");
        Check(ReferenceEquals(geometry, model.Commands["triangle"].Vertices), "Texture updates retain geometry");
        Accept(model, """{"revision":3,"remove":["label"]}""");
        Equal(model.TextureBytes, 0L);
        Equal(model.Images.Count, 0);
        Equal(string.Join(",", model.Order), "triangle");
        Accept(model, """{"revision":4,"remove":["triangle"]}""");
        Equal(model.CommandCount, 0);
        Equal(model.VertexBytes, 0L);
        Accept(model, """{"revision":5,"textures":[{"id":"unused","width":1,"height":1,"rgba":"/////w=="}]}""");
        Equal(model.TextureBytes, 0L);
    }

    private static void CameraUpdates()
    {
        var model = Seed();
        var commands = model.Commands;
        var images = model.Images;
        Check(model.TrySetCamera([1.5, 0, 0, 1.5, -12, 7], 1024, 768), "Valid camera update");
        Equal(Vector2.Transform(new(10, 2), model.Camera), new Vector2(3, 10));
        Equal(model.Revision, 1L);
        Check(ReferenceEquals(commands, model.Commands), "Camera-only update must not replace commands");
        Check(ReferenceEquals(images, model.Images), "Camera-only update must not replace image resources");
        var before = Snapshot.Take(model);
        (double[] Matrix, double Width, double Height)[] invalid =
        [
            (null!, 10, 10),
            ([1, 0, 0, 1, 0], 10, 10),
            ([double.NaN, 0, 0, 1, 0, 0], 10, 10),
            ([1, 0, 0, 1, double.PositiveInfinity, 0], 10, 10),
            ([1, 0, 0, 1, 1e10, 0], 10, 10),
            ([1, 0, 0, 1, 0, 0], 0, 10),
            ([1, 0, 0, 1, 0, 0], 10, -1),
            ([1, 0, 0, 1, 0, 0], double.NaN, 10),
            ([1, 0, 0, 1, 0, 0], 10, double.PositiveInfinity),
            ([1, 0, 0, 1, 0, 0], 1e7, 10)
        ];
        foreach (var value in invalid)
        {
            Check(!model.TrySetCamera(value.Matrix, value.Width, value.Height), "Invalid camera was accepted");
            before.AssertUnchanged(model);
        }
    }

    private static void CameraTransactions()
    {
        var model = Seed();
        var commands = model.Commands;
        var images = model.Images;
        var order = model.Order;
        var vertices = model.Commands["triangle"].Vertices;
        var pixels = model.Images["glyph"].Rgba;
        Accept(model, """{"revision":2,"camera":[3,0,0,3,20,30],"width":800,"height":600}""");
        Equal(model.Revision, 2L);
        Equal(model.Camera, new Matrix3x2(3, 0, 0, 3, 20, 30));
        Equal(model.Width, 800d);
        Equal(model.Height, 600d);
        Check(ReferenceEquals(commands, model.Commands), "Camera transaction must retain command dictionary");
        Check(ReferenceEquals(images, model.Images), "Camera transaction must retain image dictionary");
        Check(ReferenceEquals(order, model.Order), "Camera transaction must retain painter order");
        Check(ReferenceEquals(vertices, model.Commands["triangle"].Vertices), "Camera transaction must retain vertices");
        Check(ReferenceEquals(pixels, model.Images["glyph"].Rgba), "Camera transaction must retain pixels");
        Accept(model, """{"revision":3,"commands":[],"remove":[],"textures":[],"camera":[1,0,0,1,0,0]}""");
        Check(ReferenceEquals(commands, model.Commands), "Empty command collection is still a camera-only update");
        Check(ReferenceEquals(images, model.Images), "Empty texture collection must retain images");
        Check(ReferenceEquals(order, model.Order), "Empty removal collection must retain painter order");
        Reject(model, """{"revision":4,"camera":[1,0,0,1,0,0],"width":0}""");
        Reject(model, """{"revision":4,"camera":[1,0,0,1,1e999,0]}""");
        Reject(model, """{"revision":4,"commands":{}}""");
        Reject(model, """{"revision":4,"textures":null}""");
        Reject(model, """{"revision":4,"remove":"none"}""");
    }

    private static void BoundsAndCulling()
    {
        var model = Seed();
        Accept(model, """{"revision":2,"commands":[{"id":"triangle","bounds":[-1,-2,1,2]}]}""");
        Equal(model.Commands["triangle"].Bounds, new GpuRect(-1, -2, 21, 12));
        Equal(new GpuRect(1, 2, 3, 4).Transform(new Matrix3x2(0, 1, -1, 0, 5, 10)), new GpuRect(-1, 11, 4, 3));
        var localThenCamera = Matrix3x2.CreateTranslation(30, 40) * Matrix3x2.CreateScale(2);
        Equal(new GpuRect(0, 0, 10, 20).Transform(localThenCamera), new GpuRect(60, 80, 20, 40));
        Equal(new GpuRect(0, 0, 10, 20).Transform(Matrix3x2.CreateScale(-1, 1)), new GpuRect(-10, 0, 10, 20));
        Check(new GpuRect(-5, -5, 10, 10).Intersects(100, 100), "Partial intersection must remain visible");
        Check(new GpuRect(-10, 0, 10, 10).Intersects(100, 100), "Conservative left-edge coverage");
        Check(!new GpuRect(100, 0, 10, 10).Intersects(100, 100), "Right-of-viewport command must cull");
        Check(!new GpuRect(0, 100, 10, 10).Intersects(100, 100), "Below-viewport command must cull");
        Check(!new GpuRect(-20, 0, 10, 10).Intersects(100, 100), "Left-of-viewport command must cull");
        Check(!new GpuRect(0, 0, 0, 10).Intersects(100, 100), "Degenerate command must cull");
    }

    private static void PaintAndClip()
    {
        var model = Seed();
        Accept(model, """
            {"revision":2,"commands":[{"id":"triangle","rule":"evenodd","clip":[1,2,3,4],
              "paint":{"kind":"linear","start":[0,0],"end":[1,0],"spread":"reflect",
                "transform":[2,0,0,3,4,5],
                "stops":[{"offset":0,"color":[1,0,0,1]},{"offset":1,"color":[0,0,1,0.5]}]}}]}
            """);
        var command = model.Commands["triangle"];
        Equal(command.Rule, "evenodd");
        Equal(command.Clip, (GpuRect?)new GpuRect(1, 2, 3, 4));
        Equal(command.Paint.Spread, "reflect");
        Equal(command.Paint.Stops.Length, 2);
        Equal(command.Paint.Transform, new Matrix3x2(2, 0, 0, 3, 4, 5));
        Reject(model, """{"revision":3,"commands":[{"id":"triangle","paint":{"kind":"linear","stops":[]}}]}""");
        Reject(model, """{"revision":3,"commands":[{"id":"triangle","paint":{"kind":"radial","radius":-1,"stops":[{"offset":0,"color":[1,1,1,1]}]}}]}""");
        Reject(model, """{"revision":3,"commands":[{"id":"triangle","paint":{"kind":"linear","stops":[{"offset":1,"color":[1,1,1,1]},{"offset":0,"color":[0,0,0,1]}]}}]}""");
        Accept(model, """{"revision":3,"commands":[{"id":"triangle","clip":null}]}""");
        Check(model.Commands["triangle"].Clip is null, "Explicit null must remove a retained clip");
        Check(ReferenceEquals(model.Commands["triangle"].Paint, command.Paint), "Removing a clip retains its paint");
    }

    private static void Budgets()
    {
        var model = Seed();
        Reject(model, "{\"revision\":2,\"remove\":[" + string.Join(",", Enumerable.Repeat("\"unused\"", GpuSceneModel.MaxCommands + 1)) + "]}");
        Reject(model, "{\"revision\":2,\"textures\":[{\"id\":\"glyph\",\"width\":" + (GpuSceneModel.MaxTextureSide + 1) + ",\"height\":1,\"rgba\":\"\"}]}");
        // Within the side limit, but 8192 x 4096 x 4 exceeds the decoded-byte limit.
        Reject(model, """{"revision":2,"textures":[{"id":"glyph","width":8192,"height":4096,"rgba":""}]}""");
        Reject(model, """{"revision":2,"textures":[{"id":"glyph","width":0,"height":1,"rgba":""}]}""");
        string stop = """{"offset":0,"color":[1,1,1,1]}""";
        Reject(model, "{\"revision\":2,\"commands\":[{\"id\":\"triangle\",\"paint\":{\"kind\":\"linear\",\"stops\":[" +
            string.Join(",", Enumerable.Repeat(stop, 65)) + "]}}]}");
        Reject(model, "{\"revision\":2,\"remove\":[\"" + new string('x', 513) + "\"]}");

        // Exercise the aggregate retained command limit through the real protocol.
        // Each draw owns one triangle, so this remains a small, bounded fixture.
        var commands = Enumerable.Range(0, GpuSceneModel.MaxCommands)
            .Select(i => "{\"id\":\"n" + i + "\",\"kind\":\"mesh\",\"bounds\":[0,0,1,1],\"vertices\":[0,0,1,0,0,1]}");
        var full = new GpuSceneModel();
        Accept(full, "{\"revision\":1,\"reset\":true,\"commands\":[" + string.Join(",", commands) + "]}");
        Equal(full.CommandCount, GpuSceneModel.MaxCommands);
        Reject(full, """{"revision":2,"commands":[{"id":"overflow","kind":"mesh","bounds":[0,0,1,1],"vertices":[0,0,1,0,0,1]}]}""");
    }

    private static void ResetScene()
    {
        var model = Seed();
        var oldGeometry = model.Commands["triangle"].Vertices;
        Accept(model, """
            {"revision":2,"reset":true,"commands":[
              {"id":"replacement","kind":"mesh","vertices":[1,1,2,1,1,2],"bounds":[1,1,1,1]}
            ]}
            """);
        Equal(model.CommandCount, 1);
        Equal(string.Join(",", model.Order), "replacement");
        Equal(model.Images.Count, 0);
        Check(!model.Commands.ContainsKey("triangle"), "Reset must drop old commands");
        Check(!ReferenceEquals(oldGeometry, model.Commands["replacement"].Vertices), "Reset uploads fresh geometry");
        Reject(model, """{"revision":3,"reset":true,"commands":[{"id":"label","kind":"image","image":"glyph","bounds":[0,0,1,1]}]}""");
        Accept(model, """{"revision":3,"reset":true}""");
        Equal(model.CommandCount, 0);
        Equal(model.TextureBytes, 0L);
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T actual, T expected)
    {
        assertions++;
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new InvalidOperationException($"Expected {expected}, received {actual}.");
    }

    private sealed record Snapshot(long Revision, Matrix3x2 Camera, double Width, double Height,
        Dictionary<string, GpuCommand> Commands, Dictionary<string, GpuImage> Images, string[] Order,
        long VertexBytes, long TextureBytes)
    {
        internal static Snapshot Take(GpuSceneModel model) =>
            new(model.Revision, model.Camera, model.Width, model.Height,
                model.Commands, model.Images, model.Order, model.VertexBytes, model.TextureBytes);

        internal void AssertUnchanged(GpuSceneModel model)
        {
            Equal(model.Revision, Revision);
            Equal(model.Camera, Camera);
            Equal(model.Width, Width);
            Equal(model.Height, Height);
            Check(ReferenceEquals(model.Commands, Commands), "Rejected update replaced command storage");
            Check(ReferenceEquals(model.Images, Images), "Rejected update replaced image storage");
            Check(ReferenceEquals(model.Order, Order), "Rejected update replaced painter order");
            Equal(model.VertexBytes, VertexBytes);
            Equal(model.TextureBytes, TextureBytes);
        }
    }
}
