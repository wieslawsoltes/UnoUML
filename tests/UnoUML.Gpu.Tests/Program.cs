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
            ("regenerated owners retain referenced images until the last removal", TextureOwnerReplacement),
            ("camera changes and numerical validation", CameraUpdates),
            ("camera-only transactions retain exact resource collections", CameraTransactions),
            ("conservative transformed bounds and culling", BoundsAndCulling),
            ("local bounds survive sparse patches and refresh with geometry", RetainedLocalBounds),
            ("image-to-vector replacements require fresh conservative geometry", ImageToVectorBounds),
            ("resource totals publish atomically across retained updates", ResourceTotals),
            ("viewport overlays retain pixel geometry across camera changes", ViewportOverlays),
            ("coordinate-space validation is atomic", CoordinateSpaceValidation),
            ("gradient and clipping protocol", PaintAndClip),
            ("overlay tail edits retain diagram uniform slots", OverlayUniformRetention),
            ("gradient repacking and painter reorders refresh affected uniform slots", UniformSlotRepacking),
            ("command changes and recreated arenas invalidate draw uniforms", UniformContentsAndLifetime),
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
            """{"version":3,"revision":2}""",
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



    private static void RetainedLocalBounds()
    {
        var model = Seed();
        Accept(model, """{"revision":2,"commands":[{"id":"triangle","bounds":[-1,-2,1,2]}]}""");
        var original = model.Commands["triangle"];
        Equal(original.Bounds, new GpuRect(-1, -2, 21, 12));
        Accept(model, """
            {"revision":3,"commands":[{"id":"triangle","matrix":[0,1,-1,0,42,51],
              "paint":{"kind":"solid","color":[0.2,0.4,0.8,1]},"clip":[0,0,100,100],"space":"viewport"}]}
            """);
        var moved = model.Commands["triangle"];
        Equal(moved.Bounds, original.Bounds);
        Check(ReferenceEquals(moved.Vertices, original.Vertices), "Sparse edits retain the geometry used to establish local bounds");
        Equal(moved.ViewportBounds(model.Camera), new GpuRect(32, 50, 12, 21));

        Accept(model, """{"revision":4,"commands":[{"id":"triangle","vertices":[-12,-14,3,0,0,4]}]}""");
        Equal(model.Commands["triangle"].Bounds, new GpuRect(-12, -14, 32, 24));
        Accept(model, """{"revision":5,"commands":[{"id":"triangle","bounds":[0,0,1,1]}]}""");
        Equal(model.Commands["triangle"].Bounds, new GpuRect(-12, -14, 15, 18));
        Accept(model, """{"revision":6,"commands":[{"id":"triangle","vertices":[1,1,2,1,1,2],"bounds":[1,1,1,1]}]}""");
        Equal(model.Commands["triangle"].Bounds, new GpuRect(1, 1, 1, 1));
        Reject(model, """{"revision":7,"commands":[{"id":"triangle","bounds":[0,0,-1,1]}]}""");
    }

    private static void ResourceTotals()
    {
        var model = Seed();
        Equal(model.GradientStopCount, 0L);
        Accept(model, """
            {"revision":2,"commands":[
              {"id":"triangle","vertices":[0,0,20,0,0,10,0,10,20,0,20,10],
               "paint":{"kind":"linear","stops":[
                 {"offset":0,"color":[1,0,0,1]},{"offset":0.5,"color":[0,1,0,1]},{"offset":1,"color":[0,0,1,1]}]}},
              {"id":"label2","kind":"image","image":"glyph","bounds":[2,0,1,1]}],
              "textures":[{"id":"unused","width":2,"height":1,"rgba":"AAAAAAAAAAA="}],
              "order":["triangle","label","label2"]}
            """);
        Equal(model.VertexBytes, 48L);
        Equal(model.GradientStopCount, 3L);
        Equal(model.TextureBytes, 4L);
        Equal(model.Images.Count, 1);
        Accept(model, """
            {"revision":3,"commands":[{"id":"aux","kind":"image","image":"auxiliary","bounds":[4,0,1,1]}],
              "textures":[
                {"id":"glyph","width":2,"height":1,"rgba":"AAAAAAAAAAA="},
                {"id":"auxiliary","width":1,"height":1,"rgba":"AAAAAA=="}],
              "order":["triangle","label","label2","aux"]}
            """);
        Equal(model.TextureBytes, 12L);
        Equal(model.VertexBytes, 48L);
        Equal(model.GradientStopCount, 3L);
        Accept(model, """
            {"revision":4,"remove":["triangle","label","label"],"commands":[
              {"id":"triangle","kind":"mesh","vertices":[0,0,1,0,0,1],"bounds":[0,0,1,1]}]}
            """);
        Equal(model.VertexBytes, 24L);
        Equal(model.GradientStopCount, 0L);
        Equal(model.TextureBytes, 12L);
        Check(model.Images.ContainsKey("glyph"), "A shared image survives removal of its first owner");
        Accept(model, """{"revision":5,"remove":["label2"]}""");
        Equal(model.TextureBytes, 4L);
        Check(!model.Images.ContainsKey("glyph"), "The last owner releases the image and its accounted bytes");

        // Geometry, stop and texture deltas are all staged before late viewport validation.
        Reject(model, """
            {"revision":6,"commands":[{"id":"triangle","vertices":[0,0,2,0,0,2,0,2,2,0,2,2],
              "paint":{"kind":"linear","stops":[{"offset":0,"color":[1,0,0,1]},{"offset":1,"color":[0,0,1,1]}]}}],
              "textures":[{"id":"auxiliary","width":2,"height":1,"rgba":"AAAAAAAAAAA="}],"height":0}
            """);
        Reject(model, """
            {"revision":6,"remove":["triangle"],"textures":[
              {"id":"auxiliary","width":2,"height":1,"rgba":"!!!!!!!!!!!!"}]}
            """);
        Accept(model, """
            {"revision":6,"commands":[{"id":"triangle","vertices":[0,0,2,0,0,2,0,2,2,0,2,2],
              "paint":{"kind":"linear","stops":[{"offset":0,"color":[1,0,0,1]},{"offset":1,"color":[0,0,1,1]}]}}],
              "textures":[{"id":"auxiliary","width":2,"height":1,"rgba":"AAAAAAAAAAA="}]}
            """);
        Equal(model.VertexBytes, 48L);
        Equal(model.GradientStopCount, 2L);
        Equal(model.TextureBytes, 8L);
        Accept(model, """{"revision":7,"camera":[2,0,0,2,10,20],"width":800,"height":600}""");
        Equal(model.VertexBytes, 48L);
        Equal(model.GradientStopCount, 2L);
        Equal(model.TextureBytes, 8L);
        Accept(model, """
            {"revision":8,"reset":true,"commands":[
              {"id":"replacement","kind":"mesh","vertices":[0,0,1,0,0,1],"bounds":[0,0,1,1]}]}
            """);
        Equal(model.VertexBytes, 24L);
        Equal(model.GradientStopCount, 0L);
        Equal(model.TextureBytes, 0L);
        Accept(model, """{"revision":9,"reset":true}""");
        Equal(model.VertexBytes, 0L);
        Equal(model.GradientStopCount, 0L);
        Equal(model.TextureBytes, 0L);
    }


    private static void TextureOwnerReplacement()
    {
        var model = Seed();
        GpuImage original = model.Images["glyph"];
        Accept(model, """
            {"revision":2,"remove":["label"],"commands":[
              {"id":"regenerated-label","kind":"image","image":"glyph","bounds":[4,5,1,1]}],
              "order":["triangle","regenerated-label"]}
            """);
        Check(!model.Commands.ContainsKey("label"), "The old source owner must be removed");
        Check(ReferenceEquals(model.Images["glyph"], original), "A replacement owner retains the exact native image without a texture payload");
        Check(ReferenceEquals(model.Images["glyph"].Rgba, original.Rgba), "Owner replacement retains decoded pixels");
        Equal(model.TextureBytes, 4L);
        Accept(model, """{"revision":3,"remove":["regenerated-label"]}""");
        Equal(model.Images.Count, 0);
        Equal(model.TextureBytes, 0L);
        Reject(model, """
            {"revision":4,"commands":[{"id":"returning-label","kind":"image","image":"glyph","bounds":[4,5,1,1]}],
              "order":["triangle","returning-label"]}
            """);
        Accept(model, """
            {"revision":4,"commands":[{"id":"returning-label","kind":"image","image":"glyph","bounds":[4,5,1,1]}],
              "textures":[{"id":"glyph","width":1,"height":1,"rgba":"/////w=="}],
              "order":["triangle","returning-label"]}
            """);
        Equal(model.TextureBytes, 4L);
        Check(!ReferenceEquals(model.Images["glyph"], original), "A pruned image must be supplied again when its owner returns");
    }

    private static void ImageToVectorBounds()
    {
        foreach (string kind in new[] { "mesh", "fill" })
        {
            var model = new GpuSceneModel();
            Accept(model, """
                {"revision":1,"commands":[{"id":"shape","kind":"image","image":"pixel","bounds":[0,0,1,1],
                  "matrix":[1,0,0,1,-15,-20],"vertices":[-5,-7,20,0,0,30]}],
                  "textures":[{"id":"pixel","width":1,"height":1,"rgba":"/////w=="}],"order":["shape"]}
                """);
            Equal(model.Commands["shape"].Bounds, new GpuRect(0, 0, 1, 1));
            Check(!model.Commands["shape"].ViewportBounds(model.Camera).Intersects(10, 10), "Image geometry does not expand its sampling quad");
            // A kind field is a full replacement, never a patch that can inherit
            // image-only bounds or an otherwise unused image vertex payload.
            Reject(model, "{\"revision\":2,\"commands\":[{\"id\":\"shape\",\"kind\":\"" + kind + "\"}]}");
            Reject(model, "{\"revision\":2,\"commands\":[{\"id\":\"shape\",\"kind\":\"" + kind +
                "\",\"vertices\":[-5,-7,20,0,0,30]}]}");
            Accept(model, "{\"revision\":2,\"commands\":[{\"id\":\"shape\",\"kind\":\"" + kind +
                "\",\"vertices\":[-5,-7,20,0,0,30],\"bounds\":[0,0,1,1],\"matrix\":[1,0,0,1,-15,-20]}]}");
            Equal(model.Commands["shape"].Bounds, new GpuRect(-5, -7, 25, 37));
            Check(model.Commands["shape"].ViewportBounds(model.Camera).Intersects(10, 10), "Complete vector replacement covers and culls every supplied vertex");
            Equal(model.TextureBytes, 0L);
        }
    }

    private static void ViewportOverlays()
    {
        var model = Seed();
        GpuCommand triangle = model.Commands["triangle"];
        GpuImage glyph = model.Images["glyph"];
        Equal(triangle.Space, GpuCoordinateSpace.World);
        Equal(triangle.ViewportBounds(model.Camera), new GpuRect(12, 14, 40, 20));
        Check(triangle.ViewportClip(model.Camera) is null, "An unclipped world command stays unclipped");
        Accept(model, """
            {"revision":2,"commands":[{"id":"handle","kind":"mesh","space":"viewport",
              "vertices":[0,0,8,0,0,8,0,8,8,0,8,8],"bounds":[0,0,8,8],
              "matrix":[1,0,0,1,40,50],"clip":[40,50,6,7]}],
              "order":["triangle","label","handle"]}
            """);
        GpuCommand handle = model.Commands["handle"];
        Equal(handle.Space, GpuCoordinateSpace.Viewport);
        Equal(handle.ViewportBounds(model.Camera), new GpuRect(40, 50, 8, 8));
        Equal(handle.ViewportClip(model.Camera), (GpuRect?)new GpuRect(40, 50, 6, 7));
        Check(ReferenceEquals(model.Commands["triangle"], triangle), "Adding an overlay retains the diagram command");
        Check(ReferenceEquals(model.Images["glyph"], glyph), "Adding an overlay retains the text texture");

        var commands = model.Commands;
        var images = model.Images;
        Accept(model, """{"revision":3,"camera":[0,4,-4,0,400,-100]}""");
        Check(ReferenceEquals(model.Commands, commands), "Camera updates retain the combined command collection");
        Check(ReferenceEquals(model.Images, images), "Camera updates retain the combined texture collection");
        Equal(handle.ViewportBounds(model.Camera), new GpuRect(40, 50, 8, 8));
        Equal(handle.ViewportClip(model.Camera), (GpuRect?)new GpuRect(40, 50, 6, 7));
        Equal(triangle.ViewportBounds(model.Camera), new GpuRect(360, -100, 40, 80));
        Check(handle.ViewportBounds(model.Camera).Intersects(640, 480), "Visible overlay ignores the offscreen diagram camera");
        Check(!triangle.ViewportBounds(model.Camera).Intersects(640, 480), "World geometry still culls after camera movement");

        Accept(model, """{"revision":4,"commands":[{"id":"handle","matrix":[0,1,-1,0,60,70]}]}""");
        GpuCommand moved = model.Commands["handle"];
        Equal(moved.Space, GpuCoordinateSpace.Viewport);
        Equal(moved.ViewportBounds(model.Camera), new GpuRect(52, 70, 8, 8));
        Equal(moved.ViewportClip(model.Camera), (GpuRect?)new GpuRect(40, 50, 6, 7));
        Check(ReferenceEquals(moved.Vertices, handle.Vertices), "Overlay position updates retain exact pixel geometry");
        Check(ReferenceEquals(moved.Paint, handle.Paint), "Overlay position updates retain paint");

        Accept(model, """{"revision":5,"commands":[{"id":"handle","space":"world"}]}""");
        Equal(model.Commands["handle"].Space, GpuCoordinateSpace.World);
        Equal(model.Commands["handle"].ViewportClip(model.Camera), (GpuRect?)new GpuRect(172, 60, 28, 24));
        Check(ReferenceEquals(model.Commands["handle"].Vertices, handle.Vertices), "Changing coordinate space preserves geometry");

        Accept(model, """{"revision":6,"commands":[{"id":"handle","space":"viewport"}]}""");
        Accept(model, """{"revision":7,"commands":[{"id":"handle","kind":"image","image":"glyph","bounds":[0,0,1,1]}]}""");
        GpuCommand replaced = model.Commands["handle"];
        Equal(replaced.Space, GpuCoordinateSpace.World);
        Equal(replaced.Matrix, Matrix3x2.Identity);
        Check(replaced.Clip is null, "Full replacement resets an omitted viewport clip");
        Accept(model, """{"revision":8,"remove":["handle"]}""");
        Equal(model.CommandCount, 2);
        Equal(model.VertexBytes, 24L);
        Check(ReferenceEquals(model.Commands["triangle"], triangle), "Removing overlay chrome retains diagram geometry");
        Check(ReferenceEquals(model.Images["glyph"], glyph), "Removing overlay chrome retains diagram textures");
    }

    private static void CoordinateSpaceValidation()
    {
        var model = Seed();
        foreach (string space in new[] { "\"screen\"", "\"\"", "\"VIEWPORT\"", "null", "42", "true", "{}", "[]" })
            Reject(model, """{"revision":2,"commands":[{"id":"triangle","matrix":[1,0,0,1,9,8],"space":""" + space + "}]}");
        Accept(model, """{"version":2,"revision":2,"commands":[{"id":"triangle","space":"world"},{"id":"label","space":"viewport"}]}""");
        Equal(model.Commands["triangle"].Space, GpuCoordinateSpace.World);
        Equal(model.Commands["label"].Space, GpuCoordinateSpace.Viewport);
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


    private static void OverlayUniformRetention()
    {
        var model = Seed();
        var upload = new UniformUploadProbe();
        Equal(upload.Upload(model), "triangle,label");
        var diagram = model.Commands["triangle"];
        var label = model.Commands["label"];
        Accept(model, """
            {"revision":2,"commands":[
              {"id":"overlay:selection","kind":"mesh","space":"viewport","vertices":[0,0,10,0,0,10],"bounds":[0,0,10,10]},
              {"id":"overlay:shadow","kind":"fill","space":"viewport","vertices":[0,0,10,0,0,10],"bounds":[0,0,10,10],
               "paint":{"kind":"linear","stops":[{"offset":0,"color":[0,0,0,0.3]},{"offset":1,"color":[0,0,0,0]}]}},
              {"id":"overlay:focus","kind":"mesh","space":"viewport","vertices":[0,0,10,0,0,10],"bounds":[0,0,10,10]}
            ],"order":["triangle","label","overlay:selection","overlay:shadow","overlay:focus"]}
            """);
        Equal(upload.Upload(model), "overlay:selection,overlay:shadow,overlay:focus");
        Check(ReferenceEquals(diagram, model.Commands["triangle"]), "Overlay insertion retains the diagram command");
        Check(ReferenceEquals(label, model.Commands["label"]), "Overlay insertion retains the text command");

        Accept(model, """{"revision":3,"commands":[{"id":"overlay:selection","matrix":[1,0,0,1,20,30]}]}""");
        Equal(upload.Upload(model), "overlay:selection");

        var focus = model.Commands["overlay:focus"];
        Accept(model, """
            {"revision":4,"remove":["overlay:selection","overlay:shadow"],
             "order":["triangle","label","overlay:focus"]}
            """);
        Check(ReferenceEquals(focus, model.Commands["overlay:focus"]), "The surviving focus command itself is unchanged");
        Equal(upload.Upload(model), "overlay:focus");
        Equal(upload.SlotCount, 3);
        Equal(upload.StopOffset(2), 0u);

        Accept(model, """{"revision":5,"remove":["overlay:focus"],"order":["triangle","label"]}""");
        Equal(upload.Upload(model), "");
        Equal(upload.SlotCount, 2);
        Accept(model, """{"revision":6,"camera":[1,0,0,1,37,19]}""");
        Equal(upload.Upload(model), "");

        Accept(model, """
            {"revision":7,"commands":[{"id":"overlay:focus","kind":"mesh","space":"viewport",
              "vertices":[0,0,10,0,0,10],"bounds":[0,0,10,10]}],"order":["triangle","label","overlay:focus"]}
            """);
        Equal(upload.Upload(model), "overlay:focus");
        Accept(model, """{"revision":8,"reset":true}""");
        Equal(upload.Upload(model), "");
        Equal(upload.SlotCount, 0);
    }

    private static void UniformSlotRepacking()
    {
        var model = Seed();
        Accept(model, """
            {"revision":2,"commands":[
              {"id":"triangle","paint":{"kind":"linear","stops":[
                {"offset":0,"color":[1,0,0,1]},{"offset":1,"color":[0,0,1,1]}]}},
              {"id":"tail","kind":"mesh","vertices":[0,0,10,0,0,10],"bounds":[0,0,10,10]}
            ],"order":["triangle","label","tail"]}
            """);
        var upload = new UniformUploadProbe();
        Equal(upload.Upload(model), "triangle,label,tail");
        Equal(upload.StopOffset(1), 2u);
        var label = model.Commands["label"];
        var tail = model.Commands["tail"];
        Accept(model, """
            {"revision":3,"commands":[{"id":"triangle","paint":{"kind":"linear","stops":[
              {"offset":0,"color":[1,0,0,1]},{"offset":0.5,"color":[0,1,0,1]},{"offset":1,"color":[0,0,1,1]}]}}]}
            """);
        Check(ReferenceEquals(label, model.Commands["label"]) && ReferenceEquals(tail, model.Commands["tail"]),
            "Gradient packing can move offsets of otherwise unchanged commands");
        Equal(upload.Upload(model), "triangle,label,tail");
        Equal(upload.StopOffset(1), 3u);
        Equal(upload.StopOffset(2), 3u);

        Accept(model, """
            {"revision":4,"commands":[{"id":"triangle","paint":{"kind":"linear","stops":[
              {"offset":0,"color":[0.5,0,0,1]},{"offset":0.5,"color":[0,0.5,0,1]},{"offset":1,"color":[0,0,0.5,1]}]}}]}
            """);
        Equal(upload.Upload(model), "triangle");

        Accept(model, """{"revision":5,"order":["tail","label","triangle"]}""");
        Equal(upload.Upload(model), "tail,label,triangle");
        Equal(upload.StopOffset(1), 0u);
        Accept(model, """{"revision":6,"order":["label","tail","triangle"]}""");
        Equal(upload.Upload(model), "label,tail");
        Equal(upload.Upload(model), "");
    }

    private static void UniformContentsAndLifetime()
    {
        var model = Seed();
        var upload = new UniformUploadProbe();
        Equal(upload.Upload(model), "triangle,label");
        string[] patches =
        [
            """{"matrix":[1,0,0,1,5,6]}""",
            """{"bounds":[-1,-2,24,15]}""",
            """{"paint":{"kind":"solid","color":[0.2,0.4,0.8,1]}}""",
            """{"paint":{"kind":"solid","color":[0.2,0.4,0.8,1],"opacity":0.5,"transform":[2,0,0,2,3,4]}}""",
            """{"clip":[0,0,8,9]}""",
            """{"space":"viewport"}""",
            """{"clip":null}""",
            """{"space":"world"}""",
            """{"rule":"evenodd"}""",
            """{"vertices":[0,0,30,0,0,15]}"""
        ];
        int revision = 2;
        foreach (string patch in patches)
        {
            Accept(model, "{\"revision\":" + revision++ + ",\"commands\":[{\"id\":\"triangle\"," + patch[1..^1] + "}]}");
            Equal(upload.Upload(model), "triangle");
            Equal(upload.Upload(model), "");
        }

        // Texture replacement changes a binding/resource, not the image draw's
        // uniform contents. The existing render-bundle binding checks own it.
        Accept(model, "{\"revision\":" + revision++ + ",\"textures\":[{\"id\":\"glyph\",\"width\":1,\"height\":1,\"rgba\":\"AAAAAA==\"}]}");
        Equal(upload.Upload(model), "");
        Equal(upload.Upload(model, arenaReplaced: true), "triangle,label");
        Equal(upload.Upload(model), "");
        // Device teardown discards render items; an identical retained scene must
        // populate its new arena even when every command reference is unchanged.
        upload = new UniformUploadProbe();
        Equal(upload.Upload(model), "triangle,label");
    }

    // Drive the production slot state using real accepted scene transactions.
    // Expected upload IDs above are fixed by visual ownership and packing changes;
    // this probe has no GPU, no duplicate dirty predicate, and no mocked renderer.
    private sealed class UniformUploadProbe
    {
        private GpuDrawUniformSlot[] slots = [];
        internal int SlotCount => slots.Length;
        internal uint StopOffset(int index) => slots[index].StopOffset;
        internal string Upload(GpuSceneModel model, bool arenaReplaced = false)
        {
            var next = new GpuDrawUniformSlot[model.Order.Length];
            var uploaded = new List<string>();
            uint stopOffset = 0;
            for (int index = 0; index < model.Order.Length; index++)
            {
                GpuCommand command = model.Commands[model.Order[index]];
                GpuDrawUniformSlot previous = index < slots.Length ? slots[index] : default;
                if (previous.NeedsUpload(command, stopOffset, arenaReplaced)) uploaded.Add(command.Id);
                next[index] = new(command, stopOffset);
                stopOffset = checked(stopOffset + (uint)command.Paint.Stops.Length);
            }
            slots = next;
            return string.Join(",", uploaded);
        }
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
        long VertexBytes, long TextureBytes, long GradientStopCount)
    {
        internal static Snapshot Take(GpuSceneModel model) =>
            new(model.Revision, model.Camera, model.Width, model.Height,
                model.Commands, model.Images, model.Order, model.VertexBytes, model.TextureBytes, model.GradientStopCount);

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
            Equal(model.GradientStopCount, GradientStopCount);
        }
    }
}
