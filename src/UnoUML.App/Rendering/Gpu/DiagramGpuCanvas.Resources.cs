using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Uno.WinUI.Graphics3DWebGpu;
using GpuBuffer = Silk.NET.WebGPU.Buffer;

namespace UnoUML.App.Rendering.Gpu;

public sealed unsafe partial class DiagramGpuCanvas
{
    private const uint Samples = 4;
    private const ulong DrawUniformBytes = 256;
    private const ulong GlobalUniformBytes = 64;
    private WebGPU? _api;
    private Device* _device;
    private Queue* _queue;
    private Limits _limits;
    private TextureFormat _targetFormat;
    private uint _drawStride = 256;
    private BindGroupLayout* _bindingLayout;
    private PipelineLayout* _pipelineLayout;
    private RenderPipeline* _backgroundPipeline;
    private RenderPipeline* _nonzeroPipeline;
    private RenderPipeline* _evenoddPipeline;
    private RenderPipeline* _unionPipeline;
    private RenderPipeline* _paintPipeline;
    private RenderPipeline* _imagePipeline;
    private GpuBuffer* _globalBuffer;
    private GpuBuffer* _drawBuffer;
    private GpuBuffer* _stopBuffer;
    private ulong _drawCapacity;
    private ulong _stopCapacity;
    private Sampler* _sampler;
    private TextureResource? _whiteTexture;
    private Texture* _multisampleColor;
    private TextureView* _multisampleView;
    private Texture* _stencilTexture;
    private TextureView* _stencilView;
    private int _attachmentWidth;
    private int _attachmentHeight;
    private readonly Dictionary<string, MeshResource> _meshes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextureResource> _textures = new(StringComparer.Ordinal);
    private Dictionary<string, GpuCommand> _uploadedCommands = new(StringComparer.Ordinal);
    private string[] _uploadedOrder = [];
    private readonly Dictionary<string, uint> _stopOffsets = new(StringComparer.Ordinal);

    private void CreateDeviceResources(WebGpuCanvasContext context)
    {
        _api = context.Api; _device = context.Device; _queue = context.Queue; _targetFormat = context.TargetFormat;
        SupportedLimits supported = default;
        if (!_api.DeviceGetLimits(_device, &supported))
            throw new InvalidOperationException("WebGPU device limits are unavailable.");
        _limits = supported.Limits;
        _drawStride = checked((uint)Align(DrawUniformBytes, Math.Max(256u, _limits.MinUniformBufferOffsetAlignment)));
        if (_limits.MaxUniformBufferBindingSize < DrawUniformBytes || _limits.MaxStorageBuffersPerShaderStage < 1)
            throw new InvalidOperationException("The WebGPU adapter cannot support the retained 2D binding layout.");

        var entries = stackalloc BindGroupLayoutEntry[5];
        entries[0] = new() {
            Binding = 0, Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
            Buffer = new() { Type = BufferBindingType.Uniform, MinBindingSize = GlobalUniformBytes }
        };
        entries[1] = new() {
            Binding = 1, Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
            Buffer = new() { Type = BufferBindingType.Uniform, HasDynamicOffset = true, MinBindingSize = DrawUniformBytes }
        };
        entries[2] = new() {
            Binding = 2, Visibility = ShaderStage.Fragment,
            Buffer = new() { Type = BufferBindingType.ReadOnlyStorage, MinBindingSize = 32 }
        };
        entries[3] = new() {
            Binding = 3, Visibility = ShaderStage.Fragment,
            Texture = new() { SampleType = TextureSampleType.Float, ViewDimension = TextureViewDimension.Dimension2D }
        };
        entries[4] = new() {
            Binding = 4, Visibility = ShaderStage.Fragment,
            Sampler = new() { Type = SamplerBindingType.Filtering }
        };
        var bindings = new BindGroupLayoutDescriptor { EntryCount = 5, Entries = entries };
        _bindingLayout = _api.DeviceCreateBindGroupLayout(_device, &bindings);
        Ensure(_bindingLayout, "bind-group layout");
        var layout = _bindingLayout;
        var pipelineLayout = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &layout };
        _pipelineLayout = _api.DeviceCreatePipelineLayout(_device, &pipelineLayout);
        Ensure(_pipelineLayout, "pipeline layout");
        _globalBuffer = CreateBuffer(GlobalUniformBytes, BufferUsage.Uniform | BufferUsage.CopyDst);
        _drawCapacity = _drawStride;
        _drawBuffer = CreateBuffer(_drawCapacity, BufferUsage.Uniform | BufferUsage.CopyDst);
        _stopCapacity = 32;
        _stopBuffer = CreateBuffer(_stopCapacity, BufferUsage.Storage | BufferUsage.CopyDst);
        var sampler = new SamplerDescriptor {
            AddressModeU = AddressMode.ClampToEdge, AddressModeV = AddressMode.ClampToEdge, AddressModeW = AddressMode.ClampToEdge,
            MagFilter = FilterMode.Linear, MinFilter = FilterMode.Linear, MipmapFilter = MipmapFilterMode.Nearest,
            LodMinClamp = 0, LodMaxClamp = 0, MaxAnisotropy = 1
        };
        _sampler = _api.DeviceCreateSampler(_device, &sampler);
        Ensure(_sampler, "sampler");
        _whiteTexture = CreateTexture(new("__white", 1, 1, [255, 255, 255, 255]));
        BuildPipelines();
    }

    private void BuildPipelines()
    {
        byte* code = (byte*)SilkMarshal.StringToPtr(DiagramGpuShaders.Source);
        ShaderModule* shader = null;
        try
        {
            var wgsl = new ShaderModuleWGSLDescriptor {
                Chain = new ChainedStruct { SType = SType.ShaderModuleWgslDescriptor }, Code = code
            };
            var descriptor = new ShaderModuleDescriptor { NextInChain = &wgsl.Chain };
            shader = _api!.DeviceCreateShaderModule(_device, &descriptor);
            Ensure(shader, "2D WGSL module");
            _backgroundPipeline = CreatePipeline(shader, "background_vertex", "background_fragment", false, 0);
            _nonzeroPipeline = CreatePipeline(shader, "mesh_vertex", "stencil_fragment", true, 1);
            _evenoddPipeline = CreatePipeline(shader, "mesh_vertex", "stencil_fragment", true, 2);
            _unionPipeline = CreatePipeline(shader, "mesh_vertex", "stencil_fragment", true, 3);
            _paintPipeline = CreatePipeline(shader, "cover_vertex", "paint_fragment", false, 4);
            _imagePipeline = CreatePipeline(shader, "cover_vertex", "image_fragment", false, 0);
        }
        finally
        {
            if (shader != null) _api!.ShaderModuleRelease(shader);
            SilkMarshal.Free((nint)code);
        }
    }

    private RenderPipeline* CreatePipeline(ShaderModule* shader, string vertexEntry, string fragmentEntry, bool mesh, int stencilMode)
    {
        byte* vertexName = (byte*)SilkMarshal.StringToPtr(vertexEntry);
        byte* fragmentName = (byte*)SilkMarshal.StringToPtr(fragmentEntry);
        try
        {
            var attribute = new VertexAttribute { Format = VertexFormat.Float32x2, ShaderLocation = 0, Offset = 0 };
            var vertexLayout = new VertexBufferLayout { ArrayStride = 8, StepMode = VertexStepMode.Vertex, AttributeCount = 1, Attributes = &attribute };
            var blendComponent = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha };
            var blend = new BlendState { Color = blendComponent, Alpha = blendComponent };
            bool stencilOnly = stencilMode is >= 1 and <= 3;
            var color = new ColorTargetState {
                Format = _targetFormat, WriteMask = stencilOnly ? (ColorWriteMask)0 : ColorWriteMask.All,
                Blend = stencilOnly ? null : &blend
            };
            var fragment = new FragmentState { Module = shader, EntryPoint = fragmentName, TargetCount = 1, Targets = &color };
            var front = new StencilFaceState {
                Compare = stencilMode == 4 ? CompareFunction.NotEqual : CompareFunction.Always,
                FailOp = StencilOperation.Keep, DepthFailOp = StencilOperation.Keep,
                PassOp = stencilMode switch {
                    1 => StencilOperation.IncrementWrap, 2 => StencilOperation.Invert,
                    3 => StencilOperation.Replace, 4 => StencilOperation.Zero, _ => StencilOperation.Keep
                }
            };
            var back = front;
            if (stencilMode == 1) back.PassOp = StencilOperation.DecrementWrap;
            var depthStencil = new DepthStencilState {
                Format = TextureFormat.Depth24PlusStencil8, DepthCompare = CompareFunction.Always, DepthWriteEnabled = false,
                StencilFront = front, StencilBack = back, StencilReadMask = 0xff, StencilWriteMask = stencilMode == 0 ? 0u : 0xffu
            };
            var pipeline = new RenderPipelineDescriptor {
                Layout = _pipelineLayout,
                Vertex = new VertexState {
                    Module = shader, EntryPoint = vertexName, BufferCount = mesh ? 1u : 0u, Buffers = mesh ? &vertexLayout : null
                },
                Fragment = &fragment,
                Primitive = new PrimitiveState { Topology = PrimitiveTopology.TriangleList, FrontFace = FrontFace.Ccw, CullMode = CullMode.None },
                DepthStencil = &depthStencil,
                Multisample = new MultisampleState { Count = Samples, Mask = uint.MaxValue, AlphaToCoverageEnabled = false }
            };
            RenderPipeline* result = _api!.DeviceCreateRenderPipeline(_device, &pipeline);
            Ensure(result, vertexEntry + "/" + fragmentEntry + " pipeline");
            return result;
        }
        finally
        {
            SilkMarshal.Free((nint)vertexName); SilkMarshal.Free((nint)fragmentName);
        }
    }

    private GpuBuffer* CreateBuffer(ulong size, BufferUsage usage)
    {
        if (size == 0 || size > _limits.MaxBufferSize ||
            (usage & BufferUsage.Storage) != 0 && size > _limits.MaxStorageBufferBindingSize)
            throw new InvalidOperationException("The retained GPU buffer exceeds adapter limits.");
        var descriptor = new BufferDescriptor { Size = size, Usage = usage };
        GpuBuffer* buffer = _api!.DeviceCreateBuffer(_device, &descriptor);
        Ensure(buffer, "buffer");
        return buffer;
    }

    private TextureResource CreateTexture(GpuImage image)
    {
        if (image.Width > _limits.MaxTextureDimension2D || image.Height > _limits.MaxTextureDimension2D)
            throw new InvalidOperationException("The image exceeds the adapter's 2D texture limit.");
        var descriptor = new TextureDescriptor {
            Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst, Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D((uint)image.Width, (uint)image.Height, 1),
            Format = TextureFormat.Rgba8Unorm, MipLevelCount = 1, SampleCount = 1
        };
        var resource = new TextureResource(image);
        try
        {
            resource.Texture = _api!.DeviceCreateTexture(_device, &descriptor);
            Ensure(resource.Texture, "RGBA texture");
            resource.View = _api.TextureCreateView(resource.Texture, null);
            Ensure(resource.View, "RGBA texture view");
            var destination = new ImageCopyTexture { Texture = resource.Texture, Aspect = TextureAspect.All };
            var layout = new TextureDataLayout { BytesPerRow = (uint)image.Width * 4, RowsPerImage = (uint)image.Height };
            var extent = descriptor.Size;
            fixed (byte* pixels = image.Rgba)
                _api.QueueWriteTexture(_queue, &destination, pixels, (nuint)image.Rgba.Length, &layout, &extent);
            return resource;
        }
        catch { ReleaseTexture(resource); throw; }
    }

    private BindGroup* GetBindings(TextureResource texture)
    {
        if (texture.Bindings != null) return texture.Bindings;
        var entries = stackalloc BindGroupEntry[5];
        entries[0] = new() { Binding = 0, Buffer = _globalBuffer, Size = GlobalUniformBytes };
        entries[1] = new() { Binding = 1, Buffer = _drawBuffer, Size = DrawUniformBytes };
        entries[2] = new() { Binding = 2, Buffer = _stopBuffer, Size = _stopCapacity };
        entries[3] = new() { Binding = 3, TextureView = texture.View };
        entries[4] = new() { Binding = 4, Sampler = _sampler };
        var descriptor = new BindGroupDescriptor { Layout = _bindingLayout, EntryCount = 5, Entries = entries };
        texture.Bindings = _api!.DeviceCreateBindGroup(_device, &descriptor);
        Ensure(texture.Bindings, "2D bind group");
        return texture.Bindings;
    }

    private void EnsureAttachments(int width, int height)
    {
        if (_attachmentWidth == width && _attachmentHeight == height) return;
        if (width < 1 || height < 1 || width > _limits.MaxTextureDimension2D || height > _limits.MaxTextureDimension2D ||
            (long)width * height * 32 > 512L * 1024 * 1024)
            throw new InvalidOperationException("The four-sample render target exceeds the attachment budget.");
        ReleaseAttachments();
        var descriptor = new TextureDescriptor {
            Usage = TextureUsage.RenderAttachment, Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D((uint)width, (uint)height, 1),
            Format = _targetFormat, MipLevelCount = 1, SampleCount = Samples
        };
        _multisampleColor = _api!.DeviceCreateTexture(_device, &descriptor);
        Ensure(_multisampleColor, "four-sample color target");
        _multisampleView = _api.TextureCreateView(_multisampleColor, null);
        Ensure(_multisampleView, "four-sample color view");
        descriptor.Format = TextureFormat.Depth24PlusStencil8;
        _stencilTexture = _api.DeviceCreateTexture(_device, &descriptor);
        Ensure(_stencilTexture, "four-sample stencil target");
        _stencilView = _api.TextureCreateView(_stencilTexture, null);
        Ensure(_stencilView, "four-sample stencil view");
        _attachmentWidth = width; _attachmentHeight = height;
    }

    private void ReleaseBindings()
    {
        if (_whiteTexture is { } white) ReleaseBinding(white);
        foreach (var texture in _textures.Values) ReleaseBinding(texture);
    }
    private void ReleaseBinding(TextureResource resource)
    {
        if (resource.Bindings != null) { _api!.BindGroupRelease(resource.Bindings); resource.Bindings = null; }
    }
    private void ReleaseTexture(TextureResource resource)
    {
        ReleaseBinding(resource);
        if (resource.View != null) { _api!.TextureViewRelease(resource.View); resource.View = null; }
        if (resource.Texture != null) {
            _api!.TextureDestroy(resource.Texture); _api.TextureRelease(resource.Texture); resource.Texture = null;
        }
    }
    private void ReleaseBuffer(ref GpuBuffer* buffer)
    {
        if (buffer != null) { _api!.BufferDestroy(buffer); _api.BufferRelease(buffer); buffer = null; }
    }
    private void ReleasePipeline(ref RenderPipeline* pipeline)
    {
        if (pipeline != null) { _api!.RenderPipelineRelease(pipeline); pipeline = null; }
    }
    private void ReleaseAttachments()
    {
        if (_multisampleView != null) { _api!.TextureViewRelease(_multisampleView); _multisampleView = null; }
        if (_multisampleColor != null) {
            _api!.TextureDestroy(_multisampleColor); _api.TextureRelease(_multisampleColor); _multisampleColor = null;
        }
        if (_stencilView != null) { _api!.TextureViewRelease(_stencilView); _stencilView = null; }
        if (_stencilTexture != null) {
            _api!.TextureDestroy(_stencilTexture); _api.TextureRelease(_stencilTexture); _stencilTexture = null;
        }
        _attachmentWidth = _attachmentHeight = 0;
    }
    private void DestroyDeviceResources(WebGpuCanvasContext context)
    {
        // Only release resources owned by this renderer. Device, Queue, Target and TargetView belong to Uno.
        _api = context.Api;
        ReleaseAttachments(); ReleaseBindings();
        foreach (var mesh in _meshes.Values) ReleaseBuffer(ref mesh.Buffer);
        foreach (var texture in _textures.Values) ReleaseTexture(texture);
        _meshes.Clear(); _textures.Clear();
        if (_whiteTexture is { } white) ReleaseTexture(white);
        _whiteTexture = null;
        ReleaseBuffer(ref _globalBuffer); ReleaseBuffer(ref _drawBuffer); ReleaseBuffer(ref _stopBuffer);
        ReleasePipeline(ref _backgroundPipeline); ReleasePipeline(ref _nonzeroPipeline);
        ReleasePipeline(ref _evenoddPipeline); ReleasePipeline(ref _unionPipeline);
        ReleasePipeline(ref _paintPipeline); ReleasePipeline(ref _imagePipeline);
        if (_sampler != null) { _api.SamplerRelease(_sampler); _sampler = null; }
        if (_pipelineLayout != null) { _api.PipelineLayoutRelease(_pipelineLayout); _pipelineLayout = null; }
        if (_bindingLayout != null) { _api.BindGroupLayoutRelease(_bindingLayout); _bindingLayout = null; }
        _uploadedCommands = new(StringComparer.Ordinal); _uploadedOrder = []; _stopOffsets.Clear();
        _drawCapacity = _stopCapacity = 0;
        _device = null; _queue = null; _api = null;
    }

    private static ulong Align(ulong value, ulong alignment) => checked((value + alignment - 1) / alignment * alignment);
    private static void Ensure(void* resource, string name)
    {
        if (resource == null) throw new InvalidOperationException("WebGPU failed to create " + name + ".");
    }
    private sealed class MeshResource(float[] vertices, GpuBuffer* buffer)
    {
        internal readonly float[] Vertices = vertices;
        internal GpuBuffer* Buffer = buffer;
    }
    private sealed class TextureResource(GpuImage image)
    {
        internal readonly GpuImage Image = image;
        internal Texture* Texture;
        internal TextureView* View;
        internal BindGroup* Bindings;
    }
}
