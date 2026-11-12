using System.Numerics;
using Silk.NET.WebGPU;
using Uno.WinUI.Graphics3DWebGpu;
using GpuBuffer = Silk.NET.WebGPU.Buffer;

namespace UnoUML.App.Rendering.Gpu;

public sealed unsafe partial class DiagramGpuCanvas
{
    private DiagramGpuStatistics EncodeFrame(WebGpuCanvasContext context)
    {
        EnsureAttachments(context.PixelWidth, context.PixelHeight);
        SynchronizeResources();
        WriteGlobals(context);
        var api = context.Api;
        CommandEncoder* encoder = null;
        RenderPassEncoder* pass = null;
        CommandBuffer* commands = null;
        int visible = 0, draws = 1, viewportCommands = 0, visibleViewportCommands = 0;
        try
        {
            encoder = api.DeviceCreateCommandEncoder(context.Device, null);
            Ensure(encoder, "frame command encoder");
            var color = new RenderPassColorAttachment {
                View = _multisampleView, ResolveTarget = context.TargetView, DepthSlice = uint.MaxValue,
                LoadOp = LoadOp.Clear, StoreOp = StoreOp.Discard, ClearValue = new Color(247d / 255, 248d / 255, 251d / 255, 1)
            };
            var stencil = new RenderPassDepthStencilAttachment {
                View = _stencilView,
                DepthLoadOp = LoadOp.Clear, DepthStoreOp = StoreOp.Discard, DepthClearValue = 1,
                StencilLoadOp = LoadOp.Clear, StencilStoreOp = StoreOp.Discard, StencilClearValue = 0
            };
            var descriptor = new RenderPassDescriptor {
                ColorAttachmentCount = 1, ColorAttachments = &color, DepthStencilAttachment = &stencil
            };
            pass = api.CommandEncoderBeginRenderPass(encoder, &descriptor);
            Ensure(pass, "frame render pass");
            uint offset = 0;
            api.RenderPassEncoderSetBindGroup(pass, 0, GetBindings(_whiteTexture!), 1, &offset);
            api.RenderPassEncoderSetPipeline(pass, _backgroundPipeline);
            api.RenderPassEncoderDraw(pass, 3, 1, 0, 0);

            // Each bundle retains its own pipelines, binding offsets and geometry. Camera-only
            // frames submit all visible commands in one interop call, in exact painter order.
            for (int index = 0; index < _renderItems.Length; index++)
            {
                ref readonly RenderItem item = ref _renderItems[index];
                GpuCommand command = item.Command;
                bool viewport = command.Space == GpuCoordinateSpace.Viewport;
                if (viewport) viewportCommands++;
                if (!command.ViewportBounds(_scene.Camera).Intersects((float)_scene.Width, (float)_scene.Height))
                    continue;
                if (command.ViewportClip(_scene.Camera) is { } clip && !clip.Intersects((float)_scene.Width, (float)_scene.Height))
                    continue;
                if (command.Paint.Opacity <= 0) continue;
                _visibleBundles[visible++] = item.Bundle;
                if (viewport) visibleViewportCommands++;
                draws += item.Draws;
            }
            if (visible > 0)
            {
                // All pipelines use reference zero. Stroke union tests Equal before incrementing
                // from zero to one, so overlapping triangles cannot apply opacity repeatedly.
                api.RenderPassEncoderSetStencilReference(pass, 0);
                fixed (nint* handles = _visibleBundles)
                    api.RenderPassEncoderExecuteBundles(pass, (nuint)visible, (RenderBundle**)handles);
            }
            api.RenderPassEncoderEnd(pass);
            api.RenderPassEncoderRelease(pass); pass = null;
            commands = api.CommandEncoderFinish(encoder, null);
            Ensure(commands, "frame command buffer");
            api.QueueSubmit(context.Queue, 1, &commands);
            _submittedFrames++;
            return new(_scene.Revision, _presentedFrames, draws, _scene.CommandCount, visible,
                _scene.CommandCount - visible, _scene.VertexBytes, _scene.TextureBytes, 0,
                context.PixelWidth, context.PixelHeight, (int)Samples)
            {
                PipelineCount = 6, GeometryBuffers = _meshes.Count, ImageTextures = _textures.Count,
                RenderBundles = _commandBundles.Count, UniformAndStopBytes = (long)(_drawCapacity + _stopCapacity + GlobalUniformBytes),
                DrawUniformCapacityBytes = (long)_drawCapacity, GradientCapacityBytes = (long)_stopCapacity,
                GeometryUploadBytes = _geometryUploadBytes, TextureUploadBytes = _textureUploadBytes,
                CameraUploadBytes = _cameraUploadBytes, DrawUniformUploadBytes = _drawUniformUploadBytes,
                GradientUploadBytes = _gradientUploadBytes, BundleBuilds = _bundleBuilds,
                ViewportCommands = viewportCommands, VisibleViewportCommands = visibleViewportCommands
            };
        }
        finally
        {
            if (pass != null) { api.RenderPassEncoderEnd(pass); api.RenderPassEncoderRelease(pass); }
            if (commands != null) api.CommandBufferRelease(commands);
            if (encoder != null) api.CommandEncoderRelease(encoder);
        }
    }

    private void SynchronizeResources()
    {
        if (ReferenceEquals(_uploadedCommands, _scene.Commands) && ReferenceEquals(_uploadedOrder, _scene.Order))
            return; // Camera-only transaction: no graph allocation, buffer upload, or bind-group churn.
        bool orderChanged = !_uploadedOrder.SequenceEqual(_scene.Order, StringComparer.Ordinal);
        bool stopsChanged = orderChanged || _scene.Commands.Any(pair =>
            !_uploadedCommands.TryGetValue(pair.Key, out var previous) || !ReferenceEquals(pair.Value.Paint, previous.Paint));
        bool arenaReplaced = EnsureBufferCapacity(
            checked((ulong)Math.Max(1, _scene.Order.Length) * _drawStride),
            ref _drawBuffer, ref _drawCapacity, BufferUsage.Uniform | BufferUsage.CopyDst);

        foreach (string id in _meshes.Keys.Where(id => !_scene.Commands.TryGetValue(id, out var command) || command.Kind == GpuCommandKind.Image).ToArray())
        {
            ReleaseBuffer(ref _meshes[id].Buffer);
            _meshes.Remove(id);
        }
        foreach (var command in _scene.Commands.Values)
        {
            if (command.Kind == GpuCommandKind.Image) continue;
            if (_meshes.TryGetValue(command.Id, out var existing) && ReferenceEquals(existing.Vertices, command.Vertices)) continue;
            GpuBuffer* buffer = CreateBuffer((ulong)command.Vertices.Length * sizeof(float), BufferUsage.Vertex | BufferUsage.CopyDst);
            try { WriteFloats(buffer, 0, command.Vertices); }
            catch { ReleaseBuffer(ref buffer); throw; }
            if (existing is not null) ReleaseBuffer(ref existing.Buffer);
            _meshes[command.Id] = new(command.Vertices, buffer);
        }
        foreach (string id in _textures.Keys.Where(id => !_scene.Images.ContainsKey(id)).ToArray())
        {
            ReleaseTexture(_textures[id]); _textures.Remove(id);
        }
        foreach (var image in _scene.Images.Values)
        {
            if (_textures.TryGetValue(image.Id, out var existing) && ReferenceEquals(existing.Image, image)) continue;
            TextureResource resource = CreateTexture(image);
            if (existing is not null) ReleaseTexture(existing);
            _textures[image.Id] = resource;
        }

        if (stopsChanged)
        {
            int count = _scene.Commands.Values.Sum(command => command.Paint.Stops.Length);
            var stopData = new float[Math.Max(1, count) * 8];
            _stopOffsets.Clear();
            int position = 0;
            foreach (string id in _scene.Order)
            {
                _stopOffsets[id] = (uint)position;
                foreach (var stop in _scene.Commands[id].Paint.Stops)
                {
                    int start = position++ * 8;
                    stopData[start] = stop.Offset;
                    stop.Color.CopyTo(stopData, start + 4);
                }
            }
            arenaReplaced |= EnsureBufferCapacity((ulong)stopData.Length * sizeof(float), ref _stopBuffer, ref _stopCapacity,
                BufferUsage.Storage | BufferUsage.CopyDst);
            WriteFloats(_stopBuffer, 0, stopData);
        }

        for (int index = 0; index < _scene.Order.Length; index++)
        {
            GpuCommand command = _scene.Commands[_scene.Order[index]];
            uint stopOffset = _stopOffsets.GetValueOrDefault(command.Id);
            // Render items still describe the preceding uploaded frame here.
            // A moved command changes its slot occupant; gradient repacking can
            // change its stop offset without changing the command itself.
            GpuDrawUniformSlot previous = index < _renderItems.Length ? _renderItems[index].Uniform : default;
            if (previous.NeedsUpload(command, stopOffset, arenaReplaced))
                WriteDraw(command, checked((ulong)index * _drawStride), stopOffset);
        }
        SynchronizeBundles();
        _uploadedCommands = _scene.Commands;
        _uploadedOrder = _scene.Order;
    }

    private bool EnsureBufferCapacity(ulong required, ref GpuBuffer* buffer, ref ulong capacity, BufferUsage usage)
    {
        if (capacity >= required) return false;
        ulong next = Math.Max(32, capacity);
        while (next < required) next = checked(next * 2);
        GpuBuffer* replacement = CreateBuffer(next, usage);
        ReleaseBindings();
        ReleaseBuffer(ref buffer);
        buffer = replacement; capacity = next;
        return true;
    }

    private void WriteGlobals(WebGpuCanvasContext context)
    {
        Span<float> data = stackalloc float[16];
        data.Clear();
        PutMatrix(data, 0, _scene.Camera);
        data[4] = _scene.Camera.M31; data[5] = _scene.Camera.M32;
        data[6] = (float)_scene.Width; data[7] = (float)_scene.Height;
        data[8] = context.PixelWidth; data[9] = context.PixelHeight;
        data[10] = _targetFormat is TextureFormat.Bgra8UnormSrgb or TextureFormat.Rgba8UnormSrgb ? 1 : 0;
        WriteFloats(_globalBuffer, 0, data);
    }

    private void WriteDraw(GpuCommand command, ulong offset, uint stopOffset)
    {
        Span<float> data = stackalloc float[64];
        data.Clear();
        GpuPaint paint = command.Paint;
        PutMatrix(data, 0, command.Matrix);
        data[4] = command.Matrix.M31; data[5] = command.Matrix.M32;
        data[6] = paint.Kind == "linear" ? 1 : paint.Kind == "radial" ? 2 : 0;
        data[7] = paint.Spread == "repeat" ? 1 : paint.Spread == "reflect" ? 2 : 0;
        PutRect(data, 8, command.Bounds);
        paint.Color.AsSpan().CopyTo(data.Slice(12, 4));
        PutMatrix(data, 16, paint.Transform);
        data[20] = paint.Transform.M31; data[21] = paint.Transform.M32;
        data[22] = paint.Opacity; data[23] = stopOffset;
        data[24] = paint.Start.X; data[25] = paint.Start.Y; data[26] = paint.End.X; data[27] = paint.End.Y;
        data[28] = paint.Center.X; data[29] = paint.Center.Y; data[30] = paint.Focal.X; data[31] = paint.Focal.Y;
        data[32] = paint.Radius; data[33] = paint.FocalRadius; data[34] = paint.Stops.Length;
        if (command.Clip is { } clip) PutRect(data, 36, clip);
        else data[38] = -1;
        data[40] = command.Space == GpuCoordinateSpace.Viewport ? 1 : 0;
        WriteFloats(_drawBuffer, offset, data);
    }

    private void WriteFloats(GpuBuffer* buffer, ulong offset, ReadOnlySpan<float> data)
    {
        fixed (float* values = data)
            _api!.QueueWriteBuffer(_queue, buffer, offset, values, checked((nuint)data.Length * sizeof(float)));
        long bytes = (long)data.Length * sizeof(float);
        if (buffer == _globalBuffer) _cameraUploadBytes += bytes;
        else if (buffer == _drawBuffer) _drawUniformUploadBytes += bytes;
        else if (buffer == _stopBuffer) _gradientUploadBytes += bytes;
        else _geometryUploadBytes += bytes;
    }
    private static void PutMatrix(Span<float> data, int offset, Matrix3x2 matrix)
    {
        data[offset] = matrix.M11; data[offset + 1] = matrix.M12;
        data[offset + 2] = matrix.M21; data[offset + 3] = matrix.M22;
    }
    private static void PutRect(Span<float> data, int offset, GpuRect rect)
    {
        data[offset] = rect.X; data[offset + 1] = rect.Y; data[offset + 2] = rect.Width; data[offset + 3] = rect.Height;
    }
}
