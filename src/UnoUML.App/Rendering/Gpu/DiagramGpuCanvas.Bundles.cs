using Silk.NET.WebGPU;
using GpuBuffer = Silk.NET.WebGPU.Buffer;

namespace UnoUML.App.Rendering.Gpu;

public sealed unsafe partial class DiagramGpuCanvas
{
    private readonly Dictionary<string, CommandBundle> _commandBundles = new(StringComparer.Ordinal);
    private RenderItem[] _renderItems = [];
    private nint[] _visibleBundles = [];
    private long _bindingGeneration;

    private void SynchronizeBundles()
    {
        foreach (string id in _commandBundles.Keys.Where(id => !_scene.Commands.ContainsKey(id)).ToArray())
        {
            _api!.RenderBundleRelease(_commandBundles[id].Bundle);
            _commandBundles.Remove(id);
        }
        if (_renderItems.Length != _scene.Order.Length) _renderItems = new RenderItem[_scene.Order.Length];
        if (_visibleBundles.Length < _scene.Order.Length)
        {
            int capacity = Math.Max(16, _visibleBundles.Length);
            while (capacity < _scene.Order.Length) capacity *= 2;
            _visibleBundles = new nint[capacity];
        }
        for (int index = 0; index < _scene.Order.Length; index++)
        {
            var command = _scene.Commands[_scene.Order[index]];
            uint offset = checked((uint)index * _drawStride);
            bool directPaint = command.CanPaintMeshDirectly;
            TextureResource texture = command.Kind == GpuCommandKind.Image ? _textures[command.Image!] : _whiteTexture!;
            BindGroup* bindings = GetBindings(texture);
            GpuBuffer* vertices = command.Kind == GpuCommandKind.Image ? null : _meshes[command.Id].Buffer;
            if (!_commandBundles.TryGetValue(command.Id, out var existing) ||
                existing.BindingGeneration != _bindingGeneration || existing.Bindings != bindings ||
                existing.Vertices != vertices || existing.Offset != offset ||
                existing.Kind != command.Kind || existing.Rule != command.Rule || existing.DirectPaint != directPaint)
            {
                RenderBundle* replacement = RecordCommandBundle(command, vertices, bindings, offset, directPaint);
                if (existing is not null) _api!.RenderBundleRelease(existing.Bundle);
                existing = new(replacement, bindings, vertices, offset, command.Kind, command.Rule, directPaint, _bindingGeneration);
                _commandBundles[command.Id] = existing;
            }
            _renderItems[index] = new(command, (nint)existing.Bundle,
                command.Kind == GpuCommandKind.Image || directPaint ? 1 : 2, _stopOffsets.GetValueOrDefault(command.Id));
        }
    }

    private RenderBundle* RecordCommandBundle(GpuCommand command, GpuBuffer* vertices, BindGroup* bindings, uint offset, bool directPaint)
    {
        TextureFormat format = _targetFormat;
        var descriptor = new RenderBundleEncoderDescriptor {
            ColorFormatCount = 1, ColorFormats = &format, DepthStencilFormat = TextureFormat.Depth24PlusStencil8,
            SampleCount = Samples, DepthReadOnly = false, StencilReadOnly = false
        };
        RenderBundleEncoder* encoder = _api!.DeviceCreateRenderBundleEncoder(_device, &descriptor);
        Ensure(encoder, "retained render-bundle encoder");
        try
        {
            _api.RenderBundleEncoderSetBindGroup(encoder, 0, bindings, 1, &offset);
            if (command.Kind == GpuCommandKind.Image)
            {
                _api.RenderBundleEncoderSetPipeline(encoder, _imagePipeline);
                _api.RenderBundleEncoderDraw(encoder, 6, 1, 0, 0);
            }
            else
            {
                _api.RenderBundleEncoderSetVertexBuffer(encoder, 0, vertices, 0, (ulong)command.Vertices.Length * sizeof(float));
                RenderPipeline* pipeline = directPaint ? _directMeshPipeline : command.Kind == GpuCommandKind.Mesh ? _unionPipeline
                    : command.Rule == "evenodd" ? _evenoddPipeline : _nonzeroPipeline;
                _api.RenderBundleEncoderSetPipeline(encoder, pipeline);
                _api.RenderBundleEncoderDraw(encoder, (uint)command.Vertices.Length / 2, 1, 0, 0);
                if (!directPaint)
                {
                    _api.RenderBundleEncoderSetPipeline(encoder, _paintPipeline);
                    _api.RenderBundleEncoderDraw(encoder, 6, 1, 0, 0);
                }
            }
            RenderBundle* bundle = _api.RenderBundleEncoderFinish(encoder, null);
            Ensure(bundle, "retained command bundle");
            _bundleBuilds++;
            return bundle;
        }
        finally { _api.RenderBundleEncoderRelease(encoder); }
    }

    private void ReleaseBundles()
    {
        foreach (var bundle in _commandBundles.Values) _api!.RenderBundleRelease(bundle.Bundle);
        _commandBundles.Clear();
        _bindingGeneration++;
    }

    private sealed class CommandBundle(RenderBundle* bundle, BindGroup* bindings, GpuBuffer* vertices,
        uint offset, GpuCommandKind kind, string rule, bool directPaint, long bindingGeneration)
    {
        internal readonly RenderBundle* Bundle = bundle;
        internal readonly BindGroup* Bindings = bindings;
        internal readonly GpuBuffer* Vertices = vertices;
        internal readonly uint Offset = offset;
        internal readonly GpuCommandKind Kind = kind;
        internal readonly string Rule = rule;
        internal readonly bool DirectPaint = directPaint;
        internal readonly long BindingGeneration = bindingGeneration;
    }
    private readonly record struct RenderItem(GpuCommand Command, nint Bundle, int Draws, uint StopOffset)
    {
        internal GpuDrawUniformSlot Uniform => new(Command, StopOffset);
    }
}
