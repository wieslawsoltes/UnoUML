using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Silk.NET.WebGPU;
using Uno.WinUI.Graphics3DWebGpu;

namespace UnoUML.App.Rendering.Gpu;

/// <summary>
/// Retained, demand-driven 2D rendering on Uno's own WebGPU device. The SVG editor remains
/// the semantic and export model; only acknowledged display-list revisions replace its paint.
/// </summary>
public sealed unsafe partial class DiagramGpuCanvas : WebGpuCanvasElement
{
    private readonly object _gate = new();
    private readonly GpuSceneModel _scene = new();
    private readonly HashSet<nint> _scopeTokens = [];
    private static readonly ConcurrentDictionary<nint, ValidationBatch> PendingScopes = new();
    private static int _nextScopeToken;
    private long _generation;
    private long _presentationVersion;
    private int _observedPixelWidth;
    private int _observedPixelHeight;
    private bool _available;
    private bool _faulted;
    private bool _frameValidationPending;
    private bool _renderAgain;
    private long _presentedFrames;
    private DiagramGpuStatistics _statistics = new(-1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4);

    public event Action? Available;
    public event Action<long>? FramePresented;
    public event Action<string>? Unavailable;
    public event Action<string>? SceneRejected;

    public bool IsAvailable { get { lock (_gate) return _available && !_faulted; } }
    public long SceneRevision { get { lock (_gate) return _scene.Revision; } }
    public DiagramGpuStatistics Statistics { get { lock (_gate) return _statistics; } }

    public bool ApplyUpdate(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > GpuSceneModel.MaxJsonCharacters)
        {
            RejectScene("The GPU scene exceeds the bounded JSON transport budget.");
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            return ApplyUpdate(document.RootElement);
        }
        catch (JsonException exception)
        {
            RejectScene("Invalid GPU scene JSON: " + exception.Message);
            return false;
        }
    }

    public bool ApplyUpdate(JsonElement update)
    {
        string? error;
        bool changed;
        lock (_gate)
        {
            changed = _scene.TryApply(update, out error);
            if (changed) _presentationVersion++;
        }
        if (error is not null) RejectScene("GPU scene rejected: " + error);
        else if (changed) Invalidate();
        return changed;
    }

    public bool SetCamera(double[] matrix, double width, double height)
    {
        bool changed;
        lock (_gate)
        {
            changed = _scene.TrySetCamera(matrix, width, height);
            if (changed) _presentationVersion++;
        }
        if (changed) Invalidate();
        return changed;
    }

    protected override void Init(WebGpuCanvasContext context)
    {
        lock (_gate)
        {
            _generation++;
            _presentationVersion++;
            _observedPixelWidth = _observedPixelHeight = 0;
            _available = false; _faulted = false;
            _frameValidationPending = false; _renderAgain = false;
            var validation = BeginValidation(context, initialization: true, revision: -1);
            try
            {
                CreateDeviceResources(context);
            }
            catch (Exception exception)
            {
                validation.Failure = exception.Message;
                DestroyDeviceResources(context);
                ReportFailure("WebGPU initialization failed: " + exception.Message);
                throw;
            }
            finally
            {
                EndValidation(context, validation);
            }
        }
    }

    protected override void RenderOverride(WebGpuCanvasContext context)
    {
        lock (_gate)
        {
            if (_faulted) return;
            // Uno can replace the output target while an older frame's validation is pending.
            // A matching scene revision alone is insufficient after a DPR/physical-size change.
            if (_observedPixelWidth != context.PixelWidth || _observedPixelHeight != context.PixelHeight)
            {
                _observedPixelWidth = context.PixelWidth; _observedPixelHeight = context.PixelHeight;
                _presentationVersion++;
            }
            if (!_available || _frameValidationPending)
            {
                _renderAgain = true;
                return;
            }
            _frameValidationPending = true;
            _renderAgain = false;
            var validation = BeginValidation(context, initialization: false, _scene.Revision);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                validation.Statistics = EncodeFrame(context);
                stopwatch.Stop();
                validation.Statistics = validation.Statistics with { CpuEncodeMilliseconds = stopwatch.Elapsed.TotalMilliseconds };
            }
            catch (Exception exception)
            {
                validation.Failure = exception.Message;
                ReportFailure("WebGPU frame failed: " + exception.Message);
            }
            finally
            {
                EndValidation(context, validation);
            }
        }
    }

    protected override void OnDestroy(WebGpuCanvasContext context)
    {
        lock (_gate)
        {
            _generation++;
            _available = false;
            _frameValidationPending = false;
            foreach (nint token in _scopeTokens) PendingScopes.TryRemove(token, out _);
            _scopeTokens.Clear();
            DestroyDeviceResources(context);
            // CPU commands and decoded resources intentionally survive unload/reinitialization.
        }
        Dispatch(() =>
        {
            lock (_gate) if (_available) return;
            Unavailable?.Invoke("The WebGPU canvas is reinitializing; the SVG renderer remains active.");
        });
    }

    protected override void OnWebGpuUnavailable()
        => ReportFailure("Uno selected a backend without WebGPU canvas interop. The SVG renderer remains active.");

    private void RejectScene(string reason)
    {
        long generation, revision;
        lock (_gate)
        {
            if (_faulted) return;
            generation = _generation; revision = _scene.Revision;
        }
        Dispatch(() =>
        {
            lock (_gate)
                if (generation != _generation || revision != _scene.Revision || _faulted) return;
            // A source/transport problem leaves the device and last valid display list usable.
            // The producer can recover with a complete reset transaction after the next edit.
            SceneRejected?.Invoke(reason);
        });
    }

    private void ReportFailure(string reason)
    {
        long generation;
        lock (_gate)
        {
            if (_faulted) return;
            _faulted = true; _available = false;
            generation = _generation;
        }
        Dispatch(() =>
        {
            lock (_gate) if (generation != _generation) return;
            Unavailable?.Invoke(reason);
        });
    }

    private void Dispatch(Action action) => DispatcherQueue?.TryEnqueue(() => action());

    private ValidationBatch BeginValidation(WebGpuCanvasContext context, bool initialization, long revision)
    {
        // Scopes belong to these operations only; never replace Uno's shared device callbacks.
        context.Api.DevicePushErrorScope(context.Device, ErrorFilter.OutOfMemory);
        context.Api.DevicePushErrorScope(context.Device, ErrorFilter.Validation);
        return new(this, _generation, revision, initialization, _presentationVersion);
    }

    private void EndValidation(WebGpuCanvasContext context, ValidationBatch batch)
    {
        for (int i = 0; i < 2; i++)
        {
            nint token;
            do { token = (nint)Interlocked.Increment(ref _nextScopeToken); }
            while (token == 0 || !PendingScopes.TryAdd(token, batch));
            _scopeTokens.Add(token);
            context.Api.DevicePopErrorScope(context.Device, new PfnErrorCallback(&OnValidation), (void*)token);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnValidation(ErrorType error, byte* message, void* userdata)
    {
        // No managed exception may cross a native callback boundary.
        try
        {
            nint token = (nint)userdata;
            if (!PendingScopes.TryRemove(token, out var batch)) return;
            if (error != ErrorType.NoError)
            {
                string detail = message == null ? error.ToString() : Marshal.PtrToStringUTF8((nint)message) ?? error.ToString();
                Interlocked.CompareExchange(ref batch.Failure, error + ": " + detail, null);
            }
            if (!batch.Owner.TryGetTarget(out var owner)) return;
            lock (owner._gate) owner._scopeTokens.Remove(token);
            if (Interlocked.Decrement(ref batch.Remaining) == 0)
                owner.CompleteValidation(batch);
        }
        catch { /* Context teardown can race late device callbacks. */ }
    }

    private void CompleteValidation(ValidationBatch batch)
    {
        Dispatch(() =>
        {
            bool available = false, presented = false, invalidate = false;
            lock (_gate)
            {
                if (batch.Generation != _generation || _faulted) return;
                if (batch.Failure is not null)
                {
                    ReportFailure("WebGPU validation failed: " + batch.Failure);
                    return;
                }
                if (batch.Initialization)
                {
                    _available = true;
                    available = true;
                    invalidate = true;
                }
                else
                {
                    _frameValidationPending = false;
                    bool current = batch.Revision == _scene.Revision && batch.PresentationVersion == _presentationVersion;
                    if (current)
                    {
                        _presentedFrames++;
                        if (batch.Statistics is { } statistics)
                            _statistics = statistics with { PresentedFrames = _presentedFrames };
                    }
                    presented = current && batch.Revision >= 0;
                    invalidate = _renderAgain || !current;
                }
            }
            if (available) Available?.Invoke();
            if (presented) FramePresented?.Invoke(batch.Revision);
            if (invalidate) Invalidate();
        });
    }

    private sealed class ValidationBatch(DiagramGpuCanvas owner, long generation, long revision, bool initialization, long presentationVersion)
    {
        internal readonly WeakReference<DiagramGpuCanvas> Owner = new(owner);
        internal readonly long Generation = generation;
        internal readonly long Revision = revision;
        internal readonly long PresentationVersion = presentationVersion;
        internal readonly bool Initialization = initialization;
        internal int Remaining = 2;
        internal string? Failure;
        internal DiagramGpuStatistics? Statistics;
    }
}
