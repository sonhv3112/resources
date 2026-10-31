using System.Text;
using System.Text.Json.Nodes;

namespace GpmLite.Core.Fingerprint;

/// <summary>
/// Realistic per-GPU WebGL parameter tables fed to the patched core via <c>--gl-webgl-params</c>.
/// Values are our own, modelled on PUBLIC GPU/ANGLE-backend specs (NOT copied from any vendor data):
/// getParameter numeric limits, getSupportedExtensions() and getShaderPrecisionFormat().
///
/// The payload matches the WAVE2 switch contract schema:
///   { "params": {"&lt;pnameDec&gt;": number|bool|[a,b], ...},
///     "exts":   ["EXT_...", ...],
///     "precisions": {"&lt;shaderTypeDec&gt;:&lt;precisionTypeDec&gt;": [min,max,precision], ...} }
/// Keys are GLenum decimal values.
/// </summary>
public static class WebGlPresets
{
    // ---- GLenum constants (decimal keys in the JSON) ----
    private const int MAX_TEXTURE_SIZE = 0x0D33;                 // 3379
    private const int MAX_VIEWPORT_DIMS = 0x0D3A;                // 3386
    private const int MAX_RENDERBUFFER_SIZE = 0x84E8;            // 34024
    private const int MAX_VERTEX_ATTRIBS = 0x8869;              // 34921
    private const int MAX_VARYING_VECTORS = 0x8DFC;            // 36348
    private const int MAX_VERTEX_UNIFORM_VECTORS = 0x8DFB;      // 36347
    private const int MAX_FRAGMENT_UNIFORM_VECTORS = 0x8DFD;    // 36349
    private const int MAX_TEXTURE_IMAGE_UNITS = 0x8872;        // 34930
    private const int MAX_COMBINED_TEXTURE_IMAGE_UNITS = 0x8B4D; // 35661
    private const int MAX_VERTEX_TEXTURE_IMAGE_UNITS = 0x8B4C;  // 35660
    private const int MAX_CUBE_MAP_TEXTURE_SIZE = 0x851C;      // 34076
    private const int ALIASED_LINE_WIDTH_RANGE = 0x846E;      // 33902
    private const int ALIASED_POINT_SIZE_RANGE = 0x846D;      // 33901
    private const int MAX_TEXTURE_MAX_ANISOTROPY_EXT = 0x84FF; // 34047
    private const int MAX_TEXTURE_LOD_BIAS = 0x84FD;          // 34045
    // WebGL2
    private const int MAX_3D_TEXTURE_SIZE = 0x8073;           // 32883
    private const int MAX_ARRAY_TEXTURE_LAYERS = 0x88FF;      // 35071
    private const int MAX_COLOR_ATTACHMENTS = 0x8CDF;        // 36063
    private const int MAX_DRAW_BUFFERS = 0x8824;             // 34852
    private const int MAX_SAMPLES = 0x8D57;                  // 36183
    private const int MAX_COMBINED_UNIFORM_BLOCKS = 0x8A2E;  // 35374
    private const int MAX_UNIFORM_BLOCK_SIZE = 0x8A30;       // 35376
    private const int MAX_UNIFORM_BUFFER_BINDINGS = 0x8A2F;  // 35375
    private const int MAX_VERTEX_UNIFORM_BLOCKS = 0x8A2B;    // 35371
    private const int MAX_FRAGMENT_UNIFORM_BLOCKS = 0x8A2D;  // 35373
    private const int MAX_VERTEX_UNIFORM_COMPONENTS = 0x8B4A; // 35658
    private const int MAX_FRAGMENT_UNIFORM_COMPONENTS = 0x8B49; // 35657
    private const int MAX_ELEMENT_INDEX = 0x8D6B;           // 36203
    private const int MAX_ELEMENTS_INDICES = 0x80E9;        // 33001
    private const int MAX_ELEMENTS_VERTICES = 0x80E8;       // 33000

    // precision enums
    private const int VERTEX_SHADER = 0x8B31;   // 35633
    private const int FRAGMENT_SHADER = 0x8B30; // 35632
    private const int LOW_FLOAT = 0x8DF0;       // 36336
    private const int MEDIUM_FLOAT = 0x8DF1;    // 36337
    private const int HIGH_FLOAT = 0x8DF2;      // 36338
    private const int LOW_INT = 0x8DF3;         // 36339
    private const int MEDIUM_INT = 0x8DF4;      // 36340
    private const int HIGH_INT = 0x8DF5;        // 36341

    /// <summary>One GPU family's WebGL limits and extension/precision profile.</summary>
    private sealed record Preset(
        int MaxTextureSize,
        int ViewportDim,
        int MaxRenderbufferSize,
        int MaxVertexAttribs,
        int MaxVaryingVectors,
        int MaxVertexUniformVectors,
        int MaxFragmentUniformVectors,
        int MaxTextureImageUnits,
        int MaxCombinedTextureImageUnits,
        int MaxVertexTextureImageUnits,
        int MaxCubeMapTextureSize,
        int AliasedLineWidthMax,
        int AliasedPointSizeMax,
        int MaxTextureMaxAnisotropy,
        int MaxSamples,
        int MaxUniformBufferBindings,
        int MaxCombinedUniformBlocks,
        int MaxVertexUniformComponents,
        int MaxFragmentUniformComponents,
        string[] Exts,
        bool HighpEverywhere);

    // Standard Chrome WebGL2 extension list on a desktop ANGLE/D3D11 backend (NVIDIA/AMD/Intel on Windows).
    private static readonly string[] DesktopExts =
    [
        "EXT_clip_control", "EXT_color_buffer_float", "EXT_color_buffer_half_float",
        "EXT_conservative_depth", "EXT_depth_clamp", "EXT_disjoint_timer_query_webgl2",
        "EXT_float_blend", "EXT_frag_depth", "EXT_polygon_offset_clamp",
        "EXT_render_snorm", "EXT_texture_compression_bptc", "EXT_texture_compression_rgtc",
        "EXT_texture_filter_anisotropic", "EXT_texture_mirror_clamp_to_edge", "EXT_texture_norm16",
        "KHR_parallel_shader_compile", "OES_draw_buffers_indexed", "OES_sample_variables",
        "OES_shader_multisample_interpolation", "OES_texture_float_linear",
        "OVR_multiview2", "WEBGL_blend_func_extended", "WEBGL_clip_cull_distance",
        "WEBGL_compressed_texture_s3tc", "WEBGL_compressed_texture_s3tc_srgb",
        "WEBGL_debug_renderer_info", "WEBGL_debug_shaders", "WEBGL_lose_context",
        "WEBGL_multi_draw", "WEBGL_provoking_vertex", "WEBGL_stencil_texturing",
    ];

    // Apple-silicon ANGLE/Metal backend: no S3TC/BPTC desktop compressed formats, adds ASTC/ETC.
    private static readonly string[] AppleExts =
    [
        "EXT_clip_control", "EXT_color_buffer_float", "EXT_color_buffer_half_float",
        "EXT_depth_clamp", "EXT_disjoint_timer_query_webgl2", "EXT_float_blend",
        "EXT_polygon_offset_clamp", "EXT_render_snorm", "EXT_texture_compression_bptc",
        "EXT_texture_filter_anisotropic", "EXT_texture_mirror_clamp_to_edge", "EXT_texture_norm16",
        "KHR_parallel_shader_compile", "OES_draw_buffers_indexed", "OES_sample_variables",
        "OES_shader_multisample_interpolation", "OES_texture_float_linear",
        "OVR_multiview2", "WEBGL_blend_func_extended", "WEBGL_clip_cull_distance",
        "WEBGL_compressed_texture_astc", "WEBGL_compressed_texture_etc",
        "WEBGL_compressed_texture_etc1", "WEBGL_compressed_texture_s3tc",
        "WEBGL_compressed_texture_s3tc_srgb", "WEBGL_debug_renderer_info",
        "WEBGL_debug_shaders", "WEBGL_lose_context", "WEBGL_multi_draw",
        "WEBGL_provoking_vertex", "WEBGL_stencil_texturing",
    ];

    // Linux Mesa (Intel/AMD) OpenGL backend.
    private static readonly string[] MesaExts =
    [
        "EXT_clip_control", "EXT_color_buffer_float", "EXT_color_buffer_half_float",
        "EXT_conservative_depth", "EXT_depth_clamp", "EXT_disjoint_timer_query_webgl2",
        "EXT_float_blend", "EXT_frag_depth", "EXT_polygon_offset_clamp", "EXT_render_snorm",
        "EXT_texture_compression_bptc", "EXT_texture_compression_rgtc",
        "EXT_texture_filter_anisotropic", "EXT_texture_mirror_clamp_to_edge", "EXT_texture_norm16",
        "KHR_parallel_shader_compile", "OES_draw_buffers_indexed", "OES_sample_variables",
        "OES_shader_multisample_interpolation", "OES_texture_float_linear", "OVR_multiview2",
        "WEBGL_blend_func_extended", "WEBGL_clip_cull_distance",
        "WEBGL_compressed_texture_s3tc", "WEBGL_compressed_texture_s3tc_srgb",
        "WEBGL_debug_renderer_info", "WEBGL_debug_shaders", "WEBGL_lose_context",
        "WEBGL_multi_draw", "WEBGL_provoking_vertex", "WEBGL_stencil_texturing",
    ];

    // ANGLE / Direct3D11 on Windows (NVIDIA/AMD/Intel desktop). D3D11 reports the large 32767 viewport.
    private static readonly Preset NvidiaD3D11 = new(
        MaxTextureSize: 16384, ViewportDim: 32767, MaxRenderbufferSize: 16384, MaxVertexAttribs: 16,
        MaxVaryingVectors: 30, MaxVertexUniformVectors: 4096, MaxFragmentUniformVectors: 1024,
        MaxTextureImageUnits: 16, MaxCombinedTextureImageUnits: 32, MaxVertexTextureImageUnits: 16,
        MaxCubeMapTextureSize: 16384, AliasedLineWidthMax: 1, AliasedPointSizeMax: 1024,
        MaxTextureMaxAnisotropy: 16, MaxSamples: 8, MaxUniformBufferBindings: 72,
        MaxCombinedUniformBlocks: 24, MaxVertexUniformComponents: 16384, MaxFragmentUniformComponents: 4096,
        Exts: DesktopExts, HighpEverywhere: true);

    private static readonly Preset AmdD3D11 = NvidiaD3D11 with { MaxSamples = 8, MaxFragmentUniformComponents = 4096 };
    private static readonly Preset IntelD3D11 = NvidiaD3D11 with
    {
        MaxSamples = 8, MaxVertexUniformVectors = 4096, MaxFragmentUniformVectors = 1024,
    };

    // ANGLE / Metal on Apple silicon: smaller 16384 viewport, 31 varyings, point size 511, 4 samples.
    private static readonly Preset AppleMetal = new(
        MaxTextureSize: 16384, ViewportDim: 16384, MaxRenderbufferSize: 16384, MaxVertexAttribs: 16,
        MaxVaryingVectors: 31, MaxVertexUniformVectors: 1024, MaxFragmentUniformVectors: 1024,
        MaxTextureImageUnits: 16, MaxCombinedTextureImageUnits: 32, MaxVertexTextureImageUnits: 16,
        MaxCubeMapTextureSize: 16384, AliasedLineWidthMax: 1, AliasedPointSizeMax: 511,
        MaxTextureMaxAnisotropy: 16, MaxSamples: 4, MaxUniformBufferBindings: 72,
        MaxCombinedUniformBlocks: 24, MaxVertexUniformComponents: 4096, MaxFragmentUniformComponents: 4096,
        Exts: AppleExts, HighpEverywhere: true);

    // Mesa OpenGL on Linux: large uniform budgets, 32 varyings, 16384 viewport.
    private static readonly Preset Mesa = new(
        MaxTextureSize: 16384, ViewportDim: 16384, MaxRenderbufferSize: 16384, MaxVertexAttribs: 16,
        MaxVaryingVectors: 32, MaxVertexUniformVectors: 16384, MaxFragmentUniformVectors: 16384,
        MaxTextureImageUnits: 16, MaxCombinedTextureImageUnits: 32, MaxVertexTextureImageUnits: 16,
        MaxCubeMapTextureSize: 16384, AliasedLineWidthMax: 1, AliasedPointSizeMax: 2047,
        MaxTextureMaxAnisotropy: 16, MaxSamples: 16, MaxUniformBufferBindings: 84,
        MaxCombinedUniformBlocks: 84, MaxVertexUniformComponents: 16384, MaxFragmentUniformComponents: 16384,
        Exts: MesaExts, HighpEverywhere: true);

    // Generic safe fallback for an unrecognised renderer (conservative ANGLE/D3D11 shape).
    private static readonly Preset Generic = NvidiaD3D11;

    /// <summary>Maps a <c>--gl-webgl-renderer</c> string to a GPU family preset.</summary>
    private static Preset Match(string renderer)
    {
        var r = renderer ?? "";
        if (r.Contains("Apple", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("Metal", StringComparison.OrdinalIgnoreCase)) return AppleMetal;
        if (r.Contains("Mesa", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("radeonsi", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("OpenGL", StringComparison.OrdinalIgnoreCase)) return Mesa;
        if (r.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return NvidiaD3D11;
        if (r.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return IntelD3D11;
        if (r.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) return AmdD3D11;
        return Generic;
    }

    /// <summary>Builds the {params, exts, precisions} JSON for the given renderer, per the switch contract.</summary>
    public static string BuildParamsJson(string rendererString)
    {
        var p = Match(rendererString);

        var @params = new JsonObject
        {
            [Key(MAX_TEXTURE_SIZE)] = p.MaxTextureSize,
            [Key(MAX_VIEWPORT_DIMS)] = new JsonArray(p.ViewportDim, p.ViewportDim),
            [Key(MAX_RENDERBUFFER_SIZE)] = p.MaxRenderbufferSize,
            [Key(MAX_VERTEX_ATTRIBS)] = p.MaxVertexAttribs,
            [Key(MAX_VARYING_VECTORS)] = p.MaxVaryingVectors,
            [Key(MAX_VERTEX_UNIFORM_VECTORS)] = p.MaxVertexUniformVectors,
            [Key(MAX_FRAGMENT_UNIFORM_VECTORS)] = p.MaxFragmentUniformVectors,
            [Key(MAX_TEXTURE_IMAGE_UNITS)] = p.MaxTextureImageUnits,
            [Key(MAX_COMBINED_TEXTURE_IMAGE_UNITS)] = p.MaxCombinedTextureImageUnits,
            [Key(MAX_VERTEX_TEXTURE_IMAGE_UNITS)] = p.MaxVertexTextureImageUnits,
            [Key(MAX_CUBE_MAP_TEXTURE_SIZE)] = p.MaxCubeMapTextureSize,
            [Key(ALIASED_LINE_WIDTH_RANGE)] = new JsonArray(1, p.AliasedLineWidthMax),
            [Key(ALIASED_POINT_SIZE_RANGE)] = new JsonArray(1, p.AliasedPointSizeMax),
            [Key(MAX_TEXTURE_MAX_ANISOTROPY_EXT)] = p.MaxTextureMaxAnisotropy,
            [Key(MAX_TEXTURE_LOD_BIAS)] = 16,
            // WebGL2
            [Key(MAX_3D_TEXTURE_SIZE)] = 2048,
            [Key(MAX_ARRAY_TEXTURE_LAYERS)] = 2048,
            [Key(MAX_COLOR_ATTACHMENTS)] = 8,
            [Key(MAX_DRAW_BUFFERS)] = 8,
            [Key(MAX_SAMPLES)] = p.MaxSamples,
            [Key(MAX_COMBINED_UNIFORM_BLOCKS)] = p.MaxCombinedUniformBlocks,
            [Key(MAX_UNIFORM_BLOCK_SIZE)] = 65536,
            [Key(MAX_UNIFORM_BUFFER_BINDINGS)] = p.MaxUniformBufferBindings,
            [Key(MAX_VERTEX_UNIFORM_BLOCKS)] = 14,
            [Key(MAX_FRAGMENT_UNIFORM_BLOCKS)] = 14,
            [Key(MAX_VERTEX_UNIFORM_COMPONENTS)] = p.MaxVertexUniformComponents,
            [Key(MAX_FRAGMENT_UNIFORM_COMPONENTS)] = p.MaxFragmentUniformComponents,
            [Key(MAX_ELEMENT_INDEX)] = 4294967294L,
            [Key(MAX_ELEMENTS_INDICES)] = 4194303,
            [Key(MAX_ELEMENTS_VERTICES)] = 1048575,
        };

        var exts = new JsonArray();
        foreach (var e in p.Exts) exts.Add(e);

        // Desktop/Apple GPUs promote every precision to highp: float {127,127,23}, int {31,30,0}.
        var precisions = new JsonObject();
        foreach (var shader in new[] { VERTEX_SHADER, FRAGMENT_SHADER })
        {
            precisions[$"{shader}:{LOW_FLOAT}"] = Fmt(127, 127, 23);
            precisions[$"{shader}:{MEDIUM_FLOAT}"] = Fmt(127, 127, 23);
            precisions[$"{shader}:{HIGH_FLOAT}"] = Fmt(127, 127, 23);
            precisions[$"{shader}:{LOW_INT}"] = Fmt(31, 30, 0);
            precisions[$"{shader}:{MEDIUM_INT}"] = Fmt(31, 30, 0);
            precisions[$"{shader}:{HIGH_INT}"] = Fmt(31, 30, 0);
        }

        var root = new JsonObject
        {
            ["params"] = @params,
            ["exts"] = exts,
            ["precisions"] = precisions,
        };
        return root.ToJsonString();

        static string Key(int glEnum) => glEnum.ToString(System.Globalization.CultureInfo.InvariantCulture);
        static JsonArray Fmt(int a, int b, int c) => new(a, b, c);
    }

    /// <summary>Base64 of a UTF-8 string, for the <c>--gl-webgl-params</c> switch value.</summary>
    public static string ToBase64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    /// <summary>Convenience: the ready-to-use <c>--gl-webgl-params</c> value for a renderer.</summary>
    public static string BuildParamsBase64(string rendererString) => ToBase64(BuildParamsJson(rendererString));
}
