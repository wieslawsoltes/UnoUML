namespace UnoUML.App.Rendering.Gpu;

/// <summary>Shared 2D programs. Layouts are explicit and identical across every pipeline.</summary>
internal static class DiagramGpuShaders
{
    internal const string Source = """
struct Globals {
    camera0: vec4<f32>,
    camera1: vec4<f32>,
    viewport: vec4<f32>,
    padding: vec4<f32>,
};
struct Draw {
    matrix0: vec4<f32>,
    matrix1: vec4<f32>,
    bounds: vec4<f32>,
    color: vec4<f32>,
    paint0: vec4<f32>,
    paint1: vec4<f32>,
    gradient0: vec4<f32>,
    gradient1: vec4<f32>,
    gradient2: vec4<f32>,
    clip: vec4<f32>,
    options: vec4<f32>,
    padding0: vec4<f32>,
    padding1: vec4<f32>,
    padding2: vec4<f32>,
    padding3: vec4<f32>,
    padding4: vec4<f32>,
};
struct Stop { offset: vec4<f32>, color: vec4<f32> };
@group(0) @binding(0) var<uniform> globals: Globals;
@group(0) @binding(1) var<uniform> draw: Draw;
@group(0) @binding(2) var<storage, read> stops: array<Stop>;
@group(0) @binding(3) var image: texture_2d<f32>;
@group(0) @binding(4) var imageSampler: sampler;

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) @interpolate(linear, centroid) local: vec2<f32>,
    @location(1) @interpolate(linear, centroid) world: vec2<f32>,
    @location(2) @interpolate(linear, centroid) uv: vec2<f32>,
};

fn affine(p: vec2<f32>, m0: vec4<f32>, offset: vec2<f32>) -> vec2<f32> {
    return vec2<f32>(m0.x * p.x + m0.z * p.y, m0.y * p.x + m0.w * p.y) + offset;
}
fn project(local: vec2<f32>, uv: vec2<f32>) -> VertexOutput {
    let world = affine(local, draw.matrix0, draw.matrix1.xy);
    let pixel = affine(world, globals.camera0, globals.camera1.xy);
    var result: VertexOutput;
    result.position = vec4<f32>(pixel.x / globals.camera1.z * 2.0 - 1.0,
        1.0 - pixel.y / globals.camera1.w * 2.0, 0.0, 1.0);
    result.local = local;
    result.world = world;
    result.uv = uv;
    return result;
}
@vertex fn mesh_vertex(@location(0) position: vec2<f32>) -> VertexOutput {
    return project(position, vec2<f32>(0.0));
}
fn quad_corner(index: u32) -> vec2<f32> {
    let corners = array<vec2<f32>, 6>(vec2<f32>(0.0, 0.0), vec2<f32>(1.0, 0.0),
        vec2<f32>(0.0, 1.0), vec2<f32>(0.0, 1.0), vec2<f32>(1.0, 0.0), vec2<f32>(1.0, 1.0));
    return corners[index];
}
@vertex fn cover_vertex(@builtin(vertex_index) index: u32) -> VertexOutput {
    let uv = quad_corner(index);
    return project(draw.bounds.xy + uv * draw.bounds.zw, uv);
}
@vertex fn background_vertex(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    let points = array<vec2<f32>, 3>(vec2<f32>(-1.0, -1.0), vec2<f32>(3.0, -1.0), vec2<f32>(-1.0, 3.0));
    return vec4<f32>(points[index], 0.0, 1.0);
}
fn srgb_to_linear(rgb: vec3<f32>) -> vec3<f32> {
    return select(rgb / 12.92, pow((rgb + vec3<f32>(0.055)) / 1.055, vec3<f32>(2.4)),
        rgb > vec3<f32>(0.04045));
}
fn output_color(straight: vec4<f32>) -> vec4<f32> {
    let alpha = clamp(straight.a, 0.0, 1.0);
    var rgb = clamp(straight.rgb, vec3<f32>(0.0), vec3<f32>(1.0));
    if globals.viewport.z > 0.5 { rgb = srgb_to_linear(rgb); }
    return vec4<f32>(rgb * alpha, alpha);
}
@fragment fn background_fragment(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let css = position.xy * globals.camera1.zw / globals.viewport.xy;
    let cell = fract(css / 16.0) * 16.0;
    let delta = cell - vec2<f32>(8.0);
    let distanceToDot = length(delta);
    let pixelWidth = max(globals.camera1.z / globals.viewport.x, globals.camera1.w / globals.viewport.y);
    let coverage = 1.0 - smoothstep(1.0 - pixelWidth * 0.5, 1.0 + pixelWidth * 0.5, distanceToDot);
    let background = vec3<f32>(247.0, 248.0, 251.0) / 255.0;
    let dot = vec3<f32>(215.0, 219.0, 229.0) / 255.0;
    return output_color(vec4<f32>(mix(background, dot, coverage), 1.0));
}
@fragment fn stencil_fragment() -> @location(0) vec4<f32> {
    return vec4<f32>(0.0);
}
fn spread_parameter(value: f32) -> f32 {
    if draw.matrix1.w > 1.5 {
        let period = value - 2.0 * floor(value / 2.0);
        return 1.0 - abs(period - 1.0);
    }
    if draw.matrix1.w > 0.5 { return fract(value); }
    return clamp(value, 0.0, 1.0);
}
fn stop_color(parameter: f32) -> vec4<f32> {
    let start = u32(draw.paint1.w);
    let count = u32(draw.gradient2.z);
    var previous = stops[start];
    if count < 2u || parameter < previous.offset.x { return previous.color; }
    for (var index = 1u; index < count; index = index + 1u) {
        let next = stops[start + index];
        if parameter < next.offset.x {
            let weight = clamp((parameter - previous.offset.x) / (next.offset.x - previous.offset.x), 0.0, 1.0);
            // SVG interpolates stop color and alpha separately (SVG 2, paint servers).
            return mix(previous.color, next.color, weight);
        }
        previous = next;
    }
    return previous.color;
}
fn radial_parameter(p: vec2<f32>) -> f32 {
    // Solve the SVG two-circle gradient, including off-center focus and focal radius.
    let point = p - draw.gradient1.zw;
    let direction = draw.gradient1.xy - draw.gradient1.zw;
    let focalRadius = draw.gradient2.y;
    let radiusDelta = draw.gradient2.x - focalRadius;
    let a = dot(direction, direction) - radiusDelta * radiusDelta;
    let b = -2.0 * (dot(point, direction) + focalRadius * radiusDelta);
    let c = dot(point, point) - focalRadius * focalRadius;
    let geometryScale = max(dot(direction, direction), radiusDelta * radiusDelta);
    if abs(a) <= geometryScale * 1e-7 {
        let linearScale = max(abs(dot(point, direction)), abs(focalRadius * radiusDelta));
        if abs(b) <= linearScale * 1e-7 { return 1.0; }
        return -c / b;
    }
    let discriminant = b * b - 4.0 * a * c;
    if discriminant < 0.0 { return 1.0; }
    let root = sqrt(max(discriminant, 0.0));
    let t0 = (-b - root) / (2.0 * a);
    let t1 = (-b + root) / (2.0 * a);
    let valid0 = focalRadius + t0 * radiusDelta >= 0.0;
    let valid1 = focalRadius + t1 * radiusDelta >= 0.0;
    if valid0 && valid1 { return max(t0, t1); }
    if valid0 { return t0; }
    if valid1 { return t1; }
    return 1.0;
}
fn clip_coverage(world: vec2<f32>) -> f32 {
    let footprint = max(fwidth(world), vec2<f32>(1e-20));
    if draw.clip.z < 0.0 { return 1.0; }
    let distances = min(world - draw.clip.xy, draw.clip.xy + draw.clip.zw - world);
    return clamp(min(distances.x / footprint.x, distances.y / footprint.y) + 0.5, 0.0, 1.0);
}
@fragment fn paint_fragment(input: VertexOutput) -> @location(0) vec4<f32> {
    var color = draw.color;
    if draw.matrix1.z > 0.5 {
        let p = affine(input.local, draw.paint0, draw.paint1.xy);
        var parameter = 1.0;
        if draw.matrix1.z < 1.5 {
            let direction = draw.gradient0.zw - draw.gradient0.xy;
            let lengthSquared = dot(direction, direction);
            if lengthSquared > 0.0 { parameter = dot(p - draw.gradient0.xy, direction) / lengthSquared; }
        } else {
            parameter = radial_parameter(p);
        }
        color = stop_color(spread_parameter(parameter));
    }
    color.a *= draw.paint1.z * clip_coverage(input.world);
    // Never discard: every covered stencil sample must be reset, including clipped samples.
    return output_color(color);
}
fn premultiplied_texel(pixel: vec2<i32>, dimensions: vec2<i32>) -> vec4<f32> {
    let color = textureLoad(image, clamp(pixel, vec2<i32>(0), dimensions - vec2<i32>(1)), 0);
    return vec4<f32>(color.rgb * color.a, color.a);
}
@fragment fn image_fragment(input: VertexOutput) -> @location(0) vec4<f32> {
    // Interpolate premultiplied texels even though protocol resources are straight RGBA.
    // This prevents dark fringes around colored glyphs and translucent image boundaries.
    let dimensions = vec2<i32>(textureDimensions(image));
    let position = input.uv * vec2<f32>(dimensions) - vec2<f32>(0.5);
    let origin = vec2<i32>(floor(position));
    let weight = fract(position);
    let a = premultiplied_texel(origin, dimensions);
    let b = premultiplied_texel(origin + vec2<i32>(1, 0), dimensions);
    let c = premultiplied_texel(origin + vec2<i32>(0, 1), dimensions);
    let d = premultiplied_texel(origin + vec2<i32>(1, 1), dimensions);
    let sampled = mix(mix(a, b, weight.x), mix(c, d, weight.x), weight.y);
    let straight = vec4<f32>(sampled.rgb / max(sampled.a, 1e-12), sampled.a * draw.paint1.z * clip_coverage(input.world));
    return output_color(straight);
}
""";
}
