using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using SacredFix.Core;
using static SacredFix.Proxy.D9.Display;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Dibujo del motor Direct3D 11 con la semántica de Direct3D 7. FASE 3a: lo 2D, es decir, los vértices ya transformados
/// (XYZRHW). Lo 3D (XYZ, con matrices y luces) está en Backend11.Ffp.cs (fase 3b).
/// - Estados: el motor guarda los de render y de etapa de textura de D3D7. Al dibujar los traduce a objetos de D3D11
///   (mezcla, profundidad, rasterizado, muestreo), cada uno en una caché por su combinación de valores.
/// - Vértices: un shader de vértices por FVF, generado y compilado la primera vez, con su descripción de entrada.
///   Los XYZRHW se pasan de píxeles de la imagen a coordenadas de recorte, con el desplazamiento de medio píxel de D3D7
///   (allí el centro del píxel está en el entero) y w = 1/rhw para que la textura siga la perspectiva.
/// - Píxeles: un shader generado por la combinación de las etapas de textura (COLOROP/ALPHAOP con sus argumentos), la
///   prueba de alfa y el especular. Una etapa con textura pero sin textura puesta la lee como blanco.
/// - Recorte: D3D7 recorta lo 2D con el viewport. Aquí el viewport de D3D11 es la imagen entera y el del juego es el
///   rectángulo de recorte (scissor). En los menús, con el lienzo de la interfaz, no se recorta (como Backend9).
/// - Abanicos de triángulos (D3D11 no los tiene): se convierten en listas con índices.
/// </summary>
internal sealed unsafe partial class Backend11
{
    private const int DevCreateDSV = 10, DevCreateDepthState = 21;
    private const int CtxDrawIndexed = 12, CtxIASetIB = 19, CtxOMSetDepth = 36, CtxRSSetScissor = 45, CtxClearDSV = 53;

    // ---- estados de D3D7 ----
    private readonly uint[] _rs = new uint[256];
    private readonly uint[,] _tss = new uint[8, 32];
    private readonly Surface7?[] _tex = new Surface7?[8];
    private readonly int[] _vpSaved = new int[6];

    private void InitStates()
    {
        for (int s = 0; s < 256; s++) _rs[s] = DefaultRenderState(s);
        for (uint st = 0; st < 8; st++) for (int t = 1; t < 32; t++) _tss[st, t] = DefaultTextureStageState(st, t);
        _vpSaved[5] = BitConverter.SingleToInt32Bits(1f); // MaxZ
    }

    public override uint DefaultRenderState(int s) => s switch
    {
        4 => 1, 7 => 1, 8 => 3, 9 => 2, 14 => 1, 19 => 2, 20 => 1, 22 => 3, 23 => 4, 25 => 8, 60 => 0xFFFFFFFF,
        137 => 1, 141 => 1, 145 => 1, 146 => 2, _ => 0,
    };
    public override uint DefaultTextureStageState(uint stage, int t) => t switch
    {
        1 => stage == 0 ? 4u : 1u, 2 => 2, 3 => 1, 4 => stage == 0 ? 2u : 1u, 5 => 2, 6 => 1, 11 => stage, 12 or 13 or 14 => 1,
        16 or 17 => 1, 18 => 1, 21 => 1, _ => 0,
    };

    public override int SetRenderState(int s, uint v) { if ((uint)s < 256) { _rs[s] = v; if (s == 139) _vsDirty = true; } return 0; }
    public override int SetTextureStageState(uint stage, int t, uint v)
    {
        if (stage >= 8 || (uint)t >= 32) return 0;
        if (t == 12) { _tss[stage, 13] = v; _tss[stage, 14] = v; }
        _tss[stage, t] = v;
        return 0;
    }
    public override int SetTexture(uint stage, Surface7? s) { if (stage < 8) _tex[stage] = s; return 0; }
    public override int SetViewport(nint vp) { if (vp != 0) for (int i = 0; i < 6; i++) _vpSaved[i] = ((int*)vp)[i]; _vsDirty = true; return 0; }
    public override int GetViewport(nint vp) { if (vp != 0) for (int i = 0; i < 6; i++) ((int*)vp)[i] = _vpSaved[i]; return 0; }

    // ---- objetos de D3D11 ----
    private nint _depth, _dsv;
    private bool _bound;                       // imagen interna y su Z puestas como destino (Present lo cambia)
    private nint _curVS, _curPS, _curLayout, _curBlend, _curDepth, _curRaster;
    private readonly nint[] _curSamp = new nint[8], _curSrv = new nint[8];
    private nint _vsCb, _psCb;
    private readonly float[] _psCbData = new float[8], _psCbSent = new float[8];
    private nint _dvb, _dib; private int _dvbPos, _dibPos;
    private const int DynVB = 4 << 20, DynIB = 1 << 20;
    private nint _whiteSrv;

    private readonly Dictionary<VsKey, (nint vs, nint layout)> _vsCache = new();
    private readonly Dictionary<PsKey, nint> _psCache = new();
    private readonly Dictionary<uint, nint> _blendCache = new(), _rasterCache = new(), _sampCache = new();
    private readonly Dictionary<ulong, nint> _depthCache = new();
    private uint _curStencilRef;
    private readonly float[] _vpCur = new float[6];
    private static int _drawErrors;

    private bool CreateDepth()
    {
        uint* t = stackalloc uint[11];
        t[0] = (uint)RW; t[1] = (uint)RH; t[2] = 1; t[3] = 1; t[4] = 45 /* D24_UNORM_S8_UINT */; t[5] = 1; t[6] = 0;
        t[7] = 0; t[8] = 0x40 /* DEPTH_STENCIL */; t[9] = 0; t[10] = 0;
        nint tex = 0, dsv = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Fn(_dev, DevCreateTexture2D))(_dev, t, 0, &tex);
        if (hr == 0) hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Fn(_dev, DevCreateDSV))(_dev, tex, 0, &dsv);
        if (hr != 0) { Log.Error($"D11: Z-buffer -> 0x{hr:X8}"); Layer.ReleaseCom(tex); return false; }
        _depth = tex; _dsv = dsv;
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, float, byte, void>)Fn(_ctx, CtxClearDSV))(_ctx, _dsv, 3, 1f, 0);
        // blanco de 1x1 para las etapas con textura pero sin textura puesta
        uint white = 0xFFFFFFFF;
        _whiteSrv = CreateRgbaTexture(1, 1, &white);
        // constantes de los shaders del juego
        uint* bd = stackalloc uint[6];
        bd[0] = VsCbFloats * 4; bd[1] = 0; bd[2] = 4; bd[3] = 0; bd[4] = 0; bd[5] = 0;
        nint cb = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Fn(_dev, DevCreateBuffer))(_dev, bd, 0, &cb);
        _vsCb = cb;
        bd[0] = 32; cb = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Fn(_dev, DevCreateBuffer))(_dev, bd, 0, &cb);
        _psCb = cb;
        // búferes dinámicos de vértices e índices
        bd[0] = DynVB; bd[1] = 2; bd[2] = 1; bd[3] = 0x10000;
        nint b = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Fn(_dev, DevCreateBuffer))(_dev, bd, 0, &b);
        _dvb = b;
        bd[0] = DynIB; bd[2] = 2 /* INDEX */; b = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Fn(_dev, DevCreateBuffer))(_dev, bd, 0, &b);
        _dib = b;
        if (_vsCb == 0 || _psCb == 0 || _dvb == 0 || _dib == 0) { Log.Error("D11: no se pudieron crear los búferes de dibujo"); return false; }
        for (int i = 0; i < 8; i++) _psCbSent[i] = float.NaN;
        return true;
    }

    /// <summary>Lo que cambia Present (o la superposición): al siguiente dibujo se vuelve a poner todo.</summary>
    private void Unbind()
    {
        _bound = false;
        _curVS = _curPS = _curLayout = _curBlend = _curDepth = _curRaster = 0;
        Array.Clear(_curSamp); Array.Clear(_curSrv);
        _vpCur[2] = -1;
        for (int i = 0; i < 8; i++) _psCbSent[i] = float.NaN;
    }

    private void BindScene()
    {
        if (_bound) return;
        Unresolve();
        nint rtv = TargetRtv;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)Fn(_ctx, CtxOMSetRT))(_ctx, 1, &rtv, TargetDsv);
        _vpCur[2] = -1;
        nint cb = _vsCb;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxVSSetCB))(_ctx, 0, 1, &cb);
        cb = _psCb;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxPSSetCB))(_ctx, 0, 1, &cb);
        cb = _vsCb;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxPSSetCB))(_ctx, 1, 1, &cb); // (luz por píxel)
        _bound = true;
    }

    // ---- shaders de vértices por FVF ----

    private static int TexCount(uint fvf) => (int)((fvf >> 8) & 0xF);
    private static int TexSize(uint fvf, int k) => ((fvf >> (16 + k * 2)) & 3) switch { 0 => 2, 1 => 3, 2 => 4, _ => 1 };
    private static readonly nint SemPos = Marshal.StringToHGlobalAnsi("POSITION"), SemCol = Marshal.StringToHGlobalAnsi("COLOR"),
        SemTex = Marshal.StringToHGlobalAnsi("TEXCOORD"), SemNrm = Marshal.StringToHGlobalAnsi("NORMAL"), SemPad = Marshal.StringToHGlobalAnsi("PADDING");

    // salida del shader de vértices y entrada del de píxeles: la misma, pero las coordenadas de textura se leen en el
    // centroide (con MSAA, dentro de la parte cubierta del píxel)
    private const string VsOut = "struct VO { float4 pos : SV_Position; float4 dif : COLOR0; float4 spe : COLOR1; float4 t0 : TEXCOORD0; float4 t1 : TEXCOORD1; float4 t2 : TEXCOORD2; float4 t3 : TEXCOORD3; float3 vp : TEXCOORD4; float3 vn : TEXCOORD5; };\n";
    private const string GsOut = "struct VO { float4 pos : SV_Position; float4 dif : COLOR0; float4 spe : COLOR1; centroid float4 t0 : TEXCOORD0; centroid float4 t1 : TEXCOORD1; centroid float4 t2 : TEXCOORD2; centroid float4 t3 : TEXCOORD3; float3 vp : TEXCOORD4; float3 vn : TEXCOORD5; nointerpolation float4 ub : TEXCOORD6; };\n";
    private const string PsInput = "struct VO { float4 pos : SV_Position; float4 dif : COLOR0; float4 spe : COLOR1; centroid float4 t0 : TEXCOORD0; centroid float4 t1 : TEXCOORD1; centroid float4 t2 : TEXCOORD2; centroid float4 t3 : TEXCOORD3; float3 vp : TEXCOORD4; float3 vn : TEXCOORD5; };\n";

    // ---- shaders de píxeles por combinación de etapas ----

    /// <summary>
    /// Lo 2D ampliado (RenderScale), con filtro de punto o lineal (a 1:1 los dos leían el texel exacto): el color se mezcla como un bilineal
    /// «nítido» (solo en una franja de un píxel de pantalla en el borde entre texels; misc.y = escala) y ponderado por el
    /// alfa, para que no entre el color de lo transparente; el alfa es el del texel más cercano, como con filtro de punto.
    /// Así el borde de las baldosas queda opaco (sin rendijas) y los dibujos, definidos y sin texels de ancho desigual.
    /// </summary>
    private const string ShSmooth2D = @"
// lo 2D ampliado con filtro lineal: si la pieza cae dentro de su textura, la coordenada no pasa de medio texel del
// borde (como el modo «sujetar»); si no, en el borde el filtro mezclaría con el lado contrario y saldría una línea
// ub: límites de coordenadas de la pieza (del geometry shader; ub.z < ub.x si no hay): no se lee fuera de sus texels
float2 Clamp2D(Texture2D t, float2 uv, float4 ub)
{
    float2 sz; t.GetDimensions(sz.x, sz.y);
    if (ub.z >= ub.x && ub.w >= ub.y)
    {
        float2 lo = (floor(ub.xy * sz + 0.5) + 0.5) / sz, hi = (ceil(ub.zw * sz - 0.5) - 0.5) / sz;
        return clamp(uv, lo, max(lo, hi));
    }
    return all(uv >= -1 / sz) && all(uv <= 1 + 1 / sz) ? clamp(uv, 0.5 / sz, 1 - 0.5 / sz) : uv;
}

float4 Smooth2D(Texture2D t, SamplerState s, float2 uv, float4 ub)
{
    float2 sz; t.GetDimensions(sz.x, sz.y);
    float2 p = uv * sz - 0.5;
    float2 b = floor(p);
    float2 f = saturate((p - b - 0.5) * misc.y + 0.5);
    float2 i0 = b, i1 = b + 1;
    // un dibujo suelto (coordenadas dentro de la textura): los vecinos, dentro de ella; si no, en el borde se mezclaría
    // con el lado contrario (modo de repetición) y saldría una línea fina en la junta entre trozos
    // (con un texel de margen: el juego lleva las coordenadas hasta 1 + medio texel, costumbre de D3D9)
    bool inside = all(uv >= -1 / sz) && all(uv <= 1 + 1 / sz);
    float2 imin = 0, imax = sz - 1;
    if (ub.z >= ub.x && ub.w >= ub.y) { inside = true; imin = floor(ub.xy * sz + 0.5); imax = max(imin, ceil(ub.zw * sz - 0.5) - 1); }
    if (inside) { i0 = clamp(i0, imin, imax); i1 = clamp(i1, imin, imax); }
    float2 c0 = (i0 + 0.5) / sz, c1 = (i1 + 0.5) / sz;
    float4 t00 = t.Sample(s, c0), t10 = t.Sample(s, float2(c1.x, c0.y)), t01 = t.Sample(s, float2(c0.x, c1.y)), t11 = t.Sample(s, c1);
    float w00 = (1 - f.x) * (1 - f.y) * t00.a, w10 = f.x * (1 - f.y) * t10.a, w01 = (1 - f.x) * f.y * t01.a, w11 = f.x * f.y * t11.a;
    float ws = w00 + w10 + w01 + w11;
    float2 inr = floor(uv * sz);
    if (inside) inr = clamp(inr, imin, imax);
    float4 tn = t.Sample(s, (inr + 0.5) / sz); // el texel más cercano (en su centro: igual con punto o lineal)
    float3 rgb = ws > 1e-5 ? (t00.rgb * w00 + t10.rgb * w10 + t01.rgb * w01 + t11.rgb * w11) / ws : tn.rgb;
    return float4(rgb, tn.a);
}
";

    /// <summary>
    /// Letras del juego ampliadas: el juego las pinta a su tamaño (1366x768) en un atlas y se amplían x1,4. Con el
    /// bilineal normal el borde de cada trazo se reparte en varios píxeles (borrosas); aquí se usa un bilineal «nítido»
    /// en color y alfa: la mezcla entre dos texels ocupa misc.z píxeles de pantalla ([Display] FontSharp: 1 = un píxel,
    /// el más nítido sin dientes; menos de 1, más suave). Necesita el muestreo lineal (se fuerza para estas texturas).
    /// </summary>
    private const string ShFont2D = @"
float4 Font2D(Texture2D t, SamplerState s, float2 uv, float4 ub)
{
    float2 sz; t.GetDimensions(sz.x, sz.y);
    // la letra no lee fuera de su recuadro del atlas (si no, salen trozos de las vecinas)
    float2 lo = 0.5 / sz, hi = 1 - 0.5 / sz;
    if (ub.z >= ub.x && ub.w >= ub.y) { lo = (floor(ub.xy * sz + 0.5) + 0.5) / sz; hi = max(lo, (ceil(ub.zw * sz - 0.5) - 0.5) / sz); }
    // ampliación real (píxeles de pantalla por texel): x1,4 en la partida; en los menús el juego amplía más sus letras
    float2 tpp = fwidth(uv * sz);
    float m = 1 / max(max(tpp.x, tpp.y), 1e-4);
    if (m < 1.8)
    {
        float2 p = uv * sz - 0.5;
        float2 b = floor(p);
        float2 f = saturate((p - b - 0.5) * max(m * misc.z, 1) + 0.5);
        return t.SampleLevel(s, clamp((b + f + 0.5) / sz, lo, hi), 0);
    }
    // muy ampliadas: bilineal suave (bordes curvos, sin escalones) y el borde del alfa afinado a ~1 píxel de pantalla
    float4 c = t.SampleLevel(s, clamp(uv, lo, hi), 0);
    float k = max(m * misc.z * 0.6, 1);
    c.a = saturate((c.a - 0.45) * k + 0.5) * saturate(c.a * 4);
    return c;
}
";

    private record struct PsKey(ulong S0, ulong S1, ulong S2, ulong S3, uint Flags, uint Lit);

    private ulong StageKey(int s, bool smooth2d)
    {
        uint cop = _tss[s, 1];
        if (cop == 1) return 0;
        bool tex = _tex[s] != null && _tex[s]!.Surf != 0;
        return cop | (ulong)(_tss[s, 2] & 0x3F) << 5 | (ulong)(_tss[s, 3] & 0x3F) << 11 | (ulong)(_tss[s, 4] & 0x1F) << 17
            | (ulong)(_tss[s, 5] & 0x3F) << 22 | (ulong)(_tss[s, 6] & 0x3F) << 28 | (ulong)(Math.Min(_tss[s, 11] & 0xFFFF, 3)) << 34
            | (tex ? 1UL : 0) << 36 | (smooth2d && _tss[s, 16] == 1 ? 1UL : 0) << 37 | (smooth2d && _tss[s, 16] == 2 ? 1UL : 0) << 38
            | (smooth2d && tex && _tex[s]!.Font && Layer.FontSharp > 0 ? 1UL : 0) << 39;
    }

    private PsKey CurrentPsKey(bool is3d, uint vsFlags, uint fvf, bool tri)
    {
        bool sm = !is3d && Scaled;
        ulong s0 = StageKey(0, sm), s1 = s0 != 0 ? StageKey(1, sm) : 0, s2 = s1 != 0 ? StageKey(2, sm) : 0, s3 = s2 != 0 ? StageKey(3, sm) : 0;
        uint f = (_rs[15] != 0 ? (_rs[25] & 0xF) : 8u) | (_rs[29] != 0 ? 0x10u : 0);
        // alpha-to-coverage: prueba de alfa «mayor que», sin mezcla y con MSAA
        if (_rs[15] != 0 && (_rs[25] == 5 || _rs[25] == 7) && _rs[27] == 0 && _msRtv != 0 && Layer.AlphaToCoverage && is3d) f |= 0x20; // (en 2D abría rendijas en las juntas)
        // luz por píxel: las opciones de la luz y si el vértice trae color difuso / especular
        uint lit = (vsFlags & PixelLightFlag) != 0 ? (vsFlags & 0x1FFF) | ((fvf & 0x40) != 0 ? 1u << 14 : 0) | ((fvf & 0x80) != 0 ? 1u << 15 : 0) | 1u << 16 : 0;
        // lo 2D ampliado en triángulos: límites de la pieza (geometry shader) para no leer fuera de ella
        if (!is3d && Scaled && tri && ((s0 | s1 | s2 | s3) & (7UL << 37)) != 0 && _gs != 0) f |= 0x40;
        return new PsKey(s0, s1, s2, s3, f, lit);
    }

    private static string Arg(ulong a)
    {
        string b = (a & 0xF) switch { 0 => "dif", 2 => "tex", 3 => "tf", 4 => "spe", _ => "cur" };
        if ((a & 0x20) != 0) b = $"{b}.aaaa";
        if ((a & 0x10) != 0) b = $"(1 - {b})";
        return b;
    }

    private static string Op(ulong op, string a1, string a2, bool alpha)
    {
        string sw = alpha ? ".a" : ".rgb";
        string x = $"({a1}){sw}", y = $"({a2}){sw}";
        return op switch
        {
            2 => x, 3 => y, 4 => $"{x} * {y}", 5 => $"{x} * {y} * 2", 6 => $"{x} * {y} * 4", 7 => $"{x} + {y}",
            8 => $"{x} + {y} - 0.5", 9 => $"({x} + {y} - 0.5) * 2", 10 => $"{x} - {y}", 11 => $"{x} + {y} - {x} * {y}",
            12 => $"lerp({y}, {x}, dif.a)", 13 => $"lerp({y}, {x}, tex.a)", 14 => $"lerp({y}, {x}, tf.a)",
            15 => $"{x} + {y} * (1 - tex.a)", 16 => $"lerp({y}, {x}, cur.a)",
            18 => alpha ? x : $"({a1}).rgb + ({a1}).a * {y}",
            19 => alpha ? x : $"({a1}).rgb * {y} + ({a1}).a",
            20 => alpha ? x : $"(1 - ({a1}).a) * {y} + ({a1}).rgb",
            21 => alpha ? x : $"(1 - ({a1}).rgb) * {y} + ({a1}).a",
            24 => $"saturate(dot(({a1}).rgb - 0.5, ({a2}).rgb - 0.5) * 4)" + (alpha ? "" : ".xxx"),
            _ => x,
        };
    }

    private static string Ub(bool ub, ulong k) => ub && ((k >> 34) & 3) == 0 ? "i.ub" : "float4(0, 0, -1, -1)";

    private static bool UsesTexture(ulong k)
    {
        ulong cop = k & 0x1F, aop = (k >> 17) & 0x1F;
        if (cop is 13 or 15 || aop is 13 or 15) return true;
        foreach (int sh in new[] { 5, 11, 22, 28 }) if (((k >> sh) & 0xF) == 2) return true;
        return false;
    }

    private nint PixelShader(PsKey key)
    {
        if (_psCache.TryGetValue(key, out nint ps)) return ps;
        var src = new StringBuilder();
        for (int s = 0; s < 4; s++) src.Append($"Texture2D tx{s} : register(t{s}); SamplerState sm{s} : register(s{s});\n");
        bool ub = (key.Flags & 0x40) != 0;
        src.Append("cbuffer P : register(b0) { float4 tf; float4 misc; };\n").Append(ub ? GsOut : PsInput).Append(ShSmooth2D).Append(ShFont2D);
        // (las constantes de la luz, en b1; su «misc» pasa a «lmisc», que el PS ya tiene el suyo)
        if (key.Lit != 0) src.Append(VsCb3D.Replace("register(b0)", "register(b1)").Replace("float4 misc;", "float4 lmisc;"));
        src.Append("float4 main(VO i) : SV_Target\n{\n");
        if (key.Lit != 0)
        {
            src.Append("    float3 P = i.vp;\n    float3 N = i.vn * rsqrt(max(dot(i.vn, i.vn), 1e-12));\n    float4 vd = i.dif, vs = i.spe;\n");
            src.Append(LightCode(key.Lit & 0x1FFF, (key.Lit & (1u << 14)) != 0, (key.Lit & (1u << 15)) != 0).Replace("misc.", "lmisc."));
            src.Append("    float4 dif = ldif, spe = lspe;\n");
        }
        else src.Append("    float4 dif = i.dif, spe = i.spe;\n");
        src.Append("    float4 cur = dif;\n    float4 tex = 1;\n");
        ulong[] st = { key.S0, key.S1, key.S2, key.S3 };
        for (int s = 0; s < 4 && st[s] != 0; s++)
        {
            ulong k = st[s];
            ulong cop = k & 0x1F, aop = (k >> 17) & 0x1F;
            src.Append("    {\n");
            if (UsesTexture(k)) src.Append(((k >> 39) & 1) != 0
                ? $"        tex = Font2D(tx{s}, sm{s}, i.t{(k >> 34) & 3}.xy, {Ub(ub, k)});\n"
                : ((k >> 37) & 1) != 0
                ? $"        tex = Smooth2D(tx{s}, sm{s}, i.t{(k >> 34) & 3}.xy, {Ub(ub, k)});\n"
                : ((k >> 38) & 1) != 0 ? $"        tex = tx{s}.Sample(sm{s}, Clamp2D(tx{s}, i.t{(k >> 34) & 3}.xy, {Ub(ub, k)}));\n"
                : $"        tex = tx{s}.Sample(sm{s}, i.t{(k >> 34) & 3}.xy);\n");
            src.Append($"        float3 c = saturate({Op(cop, Arg(k >> 5), Arg(k >> 11), false)});\n");
            src.Append(aop == 1 ? "        float a = cur.a;\n" : $"        float a = saturate({Op(aop, Arg(k >> 22), Arg(k >> 28), true)});\n");
            src.Append("        cur = float4(c, a);\n    }\n");
        }
        if ((key.Flags & 0x10) != 0) src.Append("    cur.rgb = saturate(cur.rgb + spe.rgb);\n");
        uint fn = key.Flags & 0xF;
        if ((key.Flags & 0x20) != 0)
        {
            // el alfa pasa a cobertura del MSAA, afinado alrededor del umbral del juego (borde de un píxel de ancho)
            src.Append("    float aa = saturate((cur.a - misc.x / 255) / max(fwidth(cur.a), 1e-4) + 0.5);\n");
            src.Append("    if (aa <= 0) discard;\n    cur.a = aa;\n");
        }
        else if (fn != 8)
        {
            string cmp = fn switch { 1 => "false", 2 => "a8 < misc.x", 3 => "a8 == misc.x", 4 => "a8 <= misc.x", 5 => "a8 > misc.x", 6 => "a8 != misc.x", 7 => "a8 >= misc.x", _ => "true" };
            src.Append($"    float a8 = round(cur.a * 255);\n    if (!({cmp})) discard;\n");
        }
        src.Append("    return cur;\n}\n");
        ps = PS(src.ToString());
        if (ps == 0) Log.Error($"D11: shader de píxeles de las etapas {key} no compila");
        _psCache[key] = ps;
        return ps;
    }

    // ---- estados de D3D11 ----

    private static uint BlendFactor(uint f, bool alpha)
    {
        if (f < 1 || f > 11) f = f == 12 ? 5u : f == 13 ? 6u : 2u;
        if (alpha) f = f switch { 3 => 5u, 4 => 6u, 9 => 7u, 10 => 8u, _ => f };
        return f;
    }

    private nint BlendState(bool a2c = false)
    {
        bool on = _rs[27] != 0 && !a2c;
        uint src = _rs[19], dst = _rs[20];
        if (src == 12) { src = 5; dst = 6; } else if (src == 13) { src = 6; dst = 5; } // BOTHSRCALPHA / BOTHINVSRCALPHA
        uint key = on ? 1u | (src & 0xF) << 1 | (dst & 0xF) << 5 : a2c ? 0x10000u : 0u;
        if (_blendCache.TryGetValue(key, out nint b)) return b;
        uint* d = stackalloc uint[66];
        new Span<uint>(d, 66).Clear();
        d[0] = a2c ? 1u : 0; // AlphaToCoverageEnable
        d[2] = on ? 1u : 0; d[3] = BlendFactor(src, false); d[4] = BlendFactor(dst, false); d[5] = 1;
        d[6] = BlendFactor(src, true); d[7] = BlendFactor(dst, true); d[8] = 1; d[9] = 0xF;
        ((delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)Fn(_dev, DevCreateBlend))(_dev, d, &b);
        _blendCache[key] = b;
        return b;
    }

    private nint DepthState(bool enable, bool write, uint func) => DepthStencil(enable, write, func, false, 1, 1, 1, 8, 0xFF, 0xFF);

    /// <summary>Profundidad y plantilla (en D3D7 la plantilla es la misma por las dos caras).</summary>
    private nint DepthStencil(bool enable, bool write, uint func, bool st, uint fail, uint zfail, uint pass, uint sfunc, uint mask, uint wmask)
    {
        static uint Op(uint o) => o < 1 || o > 8 ? 1u : o;
        if (func < 1 || func > 8) func = 4;
        if (!st) { fail = zfail = pass = 1; sfunc = 8; mask = wmask = 0xFF; }
        sfunc = sfunc < 1 || sfunc > 8 ? 8u : sfunc;
        fail = Op(fail); zfail = Op(zfail); pass = Op(pass);
        mask &= 0xFF; wmask &= 0xFF;
        ulong key = (enable ? 1UL : 0) | (write ? 2UL : 0) | (ulong)func << 2 | (st ? 1UL : 0) << 6 | (ulong)fail << 7 | (ulong)zfail << 11
            | (ulong)pass << 15 | (ulong)sfunc << 19 | (ulong)mask << 23 | (ulong)wmask << 31;
        if (_depthCache.TryGetValue(key, out nint s)) return s;
        uint* d = stackalloc uint[13]; // D3D11_DEPTH_STENCIL_DESC
        d[0] = enable ? 1u : 0; d[1] = write ? 1u : 0; d[2] = func; d[3] = st ? 1u : 0; d[4] = mask | wmask << 8;
        d[5] = fail; d[6] = zfail; d[7] = pass; d[8] = sfunc;  // caras de delante
        d[9] = fail; d[10] = zfail; d[11] = pass; d[12] = sfunc; // y de detrás
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)Fn(_dev, DevCreateDepthState))(_dev, d, &s);
        if (hr != 0) Log.Error($"D11: estado de profundidad 0x{key:X} -> 0x{hr:X8}");
        _depthCache[key] = s;
        return s;
    }

    private void SetDepth(nint ds, uint stencilRef)
    {
        if (ds == _curDepth && stencilRef == _curStencilRef) return;
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Fn(_ctx, CtxOMSetDepth))(_ctx, ds, stencilRef);
        _curDepth = ds; _curStencilRef = stencilRef;
    }

    /// <summary>Viewport de D3D11 (solo si cambia).</summary>
    private void SetVp(float x, float y, float w, float h, float mn, float mx)
    {
        if (_vpCur[0] == x && _vpCur[1] == y && _vpCur[2] == w && _vpCur[3] == h && _vpCur[4] == mn && _vpCur[5] == mx) return;
        _vpCur[0] = x; _vpCur[1] = y; _vpCur[2] = w; _vpCur[3] = h; _vpCur[4] = mn; _vpCur[5] = mx;
        float* vp = stackalloc float[6];
        vp[0] = x; vp[1] = y; vp[2] = w; vp[3] = h; vp[4] = mn; vp[5] = mx;
        ((delegate* unmanaged[Stdcall]<nint, uint, float*, void>)Fn(_ctx, CtxRSSetViewports))(_ctx, 1, vp);
    }

    private nint RasterState(uint cull)
    {
        uint mode = cull switch { 1 => 1u, 2 => 2u, _ => 3u }; // NONE; CW -> las de delante (en D3D11, horario = delante); CCW -> las de detrás
        if (_rasterCache.TryGetValue(mode, out nint r)) return r;
        uint* d = stackalloc uint[10];
        new Span<uint>(d, 10).Clear();
        d[0] = 3; d[1] = mode; d[6] = 1 /* DepthClip */; d[7] = 1 /* Scissor */;
        ((delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)Fn(_dev, DevCreateRaster))(_dev, d, &r);
        _rasterCache[mode] = r;
        return r;
    }

    private nint SamplerState(int s, bool is3d, bool font = false)
    {
        uint u = _tss[s, 13], v = _tss[s, 14], mag = _tss[s, 16], min = _tss[s, 17], mip = _tss[s, 18], an = Math.Clamp(_tss[s, 21], 1, 16);
        if (font) { mag = 2; min = 2; mip = 1; } // (Font2D necesita el lineal)
        if (u < 1 || u > 4) u = 1;
        if (v < 1 || v > 4) v = 1;
        bool aniso = is3d && Mips;
        uint key = u | v << 3 | (mag & 7) << 6 | (min & 7) << 9 | (mip & 3) << 12 | an << 14 | (aniso ? 1u << 20 : 0) | (is3d ? 1u << 21 : 0);
        if (_sampCache.TryGetValue(key, out nint smp)) return smp;
        uint filter = aniso || mag == 5 || min == 3 ? 0x55u : (min == 2 ? 0x10u : 0) | (mag >= 2 ? 4u : 0) | (mip == 3 ? 1u : 0);
        if (aniso) an = (uint)Layer.Anisotropy;
        uint* d = stackalloc uint[13];
        new Span<uint>(d, 13).Clear();
        d[0] = filter; d[1] = u; d[2] = v; d[3] = 1; d[5] = an; d[6] = 1;
        ((float*)d)[12] = aniso || (is3d && mip >= 2) ? float.MaxValue : 0; // MaxLOD: lo 2D nunca usa mipmaps (en los bordes, con MSAA, salían líneas); lo 3D, si los pide
        ((delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)Fn(_dev, DevCreateSampler))(_dev, d, &smp);
        _sampCache[key] = smp;
        return smp;
    }

    // ---- recorte con el viewport del juego ----

    private static bool InMenus()
    {
        nint mgr = *(nint*)0x17EEBBC;
        if (mgr == 0) return false;
        uint f = *(uint*)(mgr + 8);
        return (f & 0x01) != 0 && (f & 0x04) == 0;
    }

    private void Scissor(bool is3d)
    {
        int* r = stackalloc int[4];
        int x = _vpSaved[0], y = _vpSaved[1], w = _vpSaved[2], h = _vpSaved[3];
        bool unclip = !is3d && InMenus() && x == UiCanvas.Left && y == 0 && w == 1024 && h == Height;
        if (unclip || w <= 0 || h <= 0) { r[0] = 0; r[1] = 0; r[2] = Width; r[3] = Height; }
        else { r[0] = Math.Max(0, x); r[1] = Math.Max(0, y); r[2] = Math.Min(Width, x + w); r[3] = Math.Min(Height, y + h); }
        ScaleRect(r);
        ((delegate* unmanaged[Stdcall]<nint, uint, int*, void>)Fn(_ctx, CtxRSSetScissor))(_ctx, 1, r);
    }

    // ---- preparar y dibujar ----

    private bool Apply(uint fvf, int type)
    {
        if (_dev == 0 || _dsv == 0) return false;
        uint pt = fvf & 0xE;
        if (pt != 0x4 && pt != 0x2) return false; // (sin mezcla de vértices XYZB1-5)
        bool is3d = pt == 0x2;
        uint vflags = is3d ? VsFlags() : 0;
        if (is3d && (vflags & 1) != 0 && (fvf & 0x10) != 0 && Layer.PixelLighting) vflags |= PixelLightFlag; // (con normales)
        var (vs, layout) = VertexShader(new VsKey(fvf, vflags));
        if (vs == 0 || layout == 0) return false;
        var pk = CurrentPsKey(is3d, vflags, fvf, type >= 4);
        nint ps = PixelShader(pk);
        if (ps == 0) return false;
        BindScene();
        if (vs != _curVS) { ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Fn(_ctx, CtxVSSetShader))(_ctx, vs, null, 0); _curVS = vs; }
        if (layout != _curLayout) { ((delegate* unmanaged[Stdcall]<nint, nint, void>)Fn(_ctx, CtxIASetLayout))(_ctx, layout); _curLayout = layout; }
        nint gs = (pk.Flags & 0x40) != 0 ? _gs : 0;
        if (gs != _curGS) { ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Fn(_ctx, CtxGSSetShader))(_ctx, gs, null, 0); _curGS = gs; }
        if (ps != _curPS) { ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Fn(_ctx, CtxPSSetShader))(_ctx, ps, null, 0); _curPS = ps; }
        nint b = BlendState((pk.Flags & 0x20) != 0);
        if (b != _curBlend) { ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)Fn(_ctx, CtxOMSetBlend))(_ctx, b, null, 0xFFFFFFFF); _curBlend = b; }
        SetDepth(DepthStencil(_rs[7] != 0, _rs[14] != 0, _rs[23], _rs[52] != 0, _rs[53], _rs[54], _rs[55], _rs[56], _rs[58], _rs[59]), _rs[57] & 0xFF);
        nint ra = RasterState(_rs[22]);
        if (ra != _curRaster) { ((delegate* unmanaged[Stdcall]<nint, nint, void>)Fn(_ctx, CtxRSSetState))(_ctx, ra); _curRaster = ra; }
        if (is3d)
        {
            float vw = _vpSaved[2] > 0 ? _vpSaved[2] : Width, vh = _vpSaved[3] > 0 ? _vpSaved[3] : Height;
            float mn = BitConverter.Int32BitsToSingle(_vpSaved[4]), mx = BitConverter.Int32BitsToSingle(_vpSaved[5]);
            if (!(mx > mn)) { mn = 0; mx = 1; }
            SetVp(_vpSaved[0] * Sx, _vpSaved[1] * Sy, vw * Sx, vh * Sy, mn, mx);
        }
        else SetVp(0, 0, RW, RH, 0, 1);
        Scissor(is3d);
        // texturas y muestreo de las etapas activas
        for (int s = 0; s < 4; s++)
        {
            if (_tss[s, 1] == 1) break;
            nint srv = _tex[s] is { Surf: not 0 } t ? (t.ShadowSrv != 0 ? t.ShadowSrv : t.Surf) : _whiteSrv;
            if (srv != _curSrv[s]) { ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxPSSetSRV))(_ctx, (uint)s, 1, &srv); _curSrv[s] = srv; }
            nint smp = SamplerState(s, is3d, ((s switch { 0 => pk.S0, 1 => pk.S1, 2 => pk.S2, _ => pk.S3 }) >> 39 & 1) != 0);
            if (smp != _curSamp[s]) { ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxPSSetSamplers))(_ctx, (uint)s, 1, &smp); _curSamp[s] = smp; }
        }
        // constantes
        UpdateVsConstants();
        uint tf = _rs[60];
        _psCbData[0] = ((tf >> 16) & 0xFF) / 255f; _psCbData[1] = ((tf >> 8) & 0xFF) / 255f; _psCbData[2] = (tf & 0xFF) / 255f; _psCbData[3] = (tf >> 24) / 255f;
        _psCbData[4] = _rs[24] & 0xFF; _psCbData[5] = Math.Max(Sx, 1f); _psCbData[6] = Layer.FontSharp;
        bool same = true;
        for (int i = 0; i < 8; i++) if (_psCbData[i] != _psCbSent[i]) { same = false; break; }
        if (!same)
        {
            fixed (float* p = _psCbData) ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, float*, uint, uint, void>)Fn(_ctx, CtxUpdateSub))(_ctx, _psCb, 0, 0, p, 0, 0);
            Array.Copy(_psCbData, _psCbSent, 8);
        }
        return true;
    }

    /// <summary>Copia datos a un búfer dinámico (añadiendo mientras quepa; si no, desde el principio). Devuelve el desplazamiento.</summary>
    private int Push(nint buf, ref int pos, int cap, void* data, int bytes)
    {
        if (bytes > cap) return -1;
        int mode = 5; // WRITE_NO_OVERWRITE
        if (pos + bytes > cap) { pos = 0; mode = 4; } // WRITE_DISCARD
        nint* m = stackalloc nint[3];
        if (((delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, nint*, int>)Fn(_ctx, CtxMap))(_ctx, buf, 0, pos == 0 ? 4 : mode, 0, m) != 0) return -1;
        Buffer.MemoryCopy(data, (byte*)m[0] + pos, bytes, bytes);
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Fn(_ctx, CtxUnmap))(_ctx, buf, 0);
        int at = pos;
        pos += (bytes + 15) & ~15;
        return at;
    }

    private ushort[] _fan = new ushort[1024];

    private int DrawCore(int type, uint fvf, nint verts, uint vcount, ushort* idx, uint icount, int stride)
    {
        if (verts == 0 || vcount == 0 || type < 1 || type > 6) return 0;
        TraceDraw(type, fvf, verts, vcount, idx, icount, stride);
        if (!Apply(fvf, type)) return 0;
        if (WantCloseGaps(fvf, vcount)) verts = CloseGaps(verts, vcount, stride, fvf);
        else if (type == 5 && WantCloseGapsStrip(fvf, vcount)) verts = CloseGapsStrip(verts, vcount, stride);
        int vo = Push(_dvb, ref _dvbPos, DynVB, (void*)verts, (int)vcount * stride);
        if (vo < 0) { if (_drawErrors++ < 5) Log.Warn($"D11: {vcount} vértices no caben en el búfer"); return 0; }
        nint vb = _dvb; uint st = (uint)stride, off = (uint)vo;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, uint*, void>)Fn(_ctx, CtxIASetVB))(_ctx, 0, 1, &vb, &st, &off);
        // índices: los del juego, o los del abanico convertido a lista
        uint n = idx != null ? icount : vcount;
        ushort* ind = idx;
        if (type == 6)
        {
            if (n < 3) return 0;
            uint tri = n - 2;
            if (_fan.Length < tri * 3) _fan = new ushort[tri * 3 * 2];
            fixed (ushort* f = _fan)
            {
                for (uint i = 0; i < tri; i++)
                {
                    f[i * 3] = idx != null ? idx[0] : (ushort)0;
                    f[i * 3 + 1] = idx != null ? idx[i + 1] : (ushort)(i + 1);
                    f[i * 3 + 2] = idx != null ? idx[i + 2] : (ushort)(i + 2);
                }
                return DrawList(4, f, tri * 3);
            }
        }
        if (ind == null)
        {
            ((delegate* unmanaged[Stdcall]<nint, int, void>)Fn(_ctx, CtxIASetTopology))(_ctx, type);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)Fn(_ctx, CtxDraw))(_ctx, n, 0);
            Drew();
            return 0;
        }
        return DrawList(type, ind, n);
    }

    private int DrawList(int topo, ushort* ind, uint n)
    {
        int io = Push(_dib, ref _dibPos, DynIB, ind, (int)n * 2);
        if (io < 0) { if (_drawErrors++ < 5) Log.Warn($"D11: {n} índices no caben en el búfer"); return 0; }
        ((delegate* unmanaged[Stdcall]<nint, nint, int, uint, void>)Fn(_ctx, CtxIASetIB))(_ctx, _dib, 57 /* R16_UINT */, (uint)io);
        ((delegate* unmanaged[Stdcall]<nint, int, void>)Fn(_ctx, CtxIASetTopology))(_ctx, topo);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, int, void>)Fn(_ctx, CtxDrawIndexed))(_ctx, n, 0, 0);
        Drew();
        return 0;
    }

    public override int Draw(int type, uint fvf, nint verts, uint vcount, int stride)
    {
        long t = T0();
        int r = DrawCore(type, fvf, verts, vcount, null, 0, stride);
        T1(Pf.Dibujo, t);
        return r;
    }
    public override int DrawIndexed(int type, uint fvf, nint verts, uint vcount, ushort* idx, uint icount, int stride)
    {
        if (idx == null) return 0;
        long t = T0();
        int r = DrawCore(type, fvf, verts, vcount, idx, icount, stride);
        T1(Pf.Dibujo, t);
        return r;
    }

    // ---- borrado ----

    private const string ShClear = @"
cbuffer K : register(b0) { float4 col; float4 z; };
float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0, out float d : SV_Depth) : SV_Target { d = z.x; return col; }";
    private nint _psClear, _clearCb, _blendNoColor;

    /// <summary>Clear de D3D7: sin rectángulos borra el viewport; con ellos, cada uno (recortado al viewport).</summary>
    public override int Clear(uint count, nint rects, uint flags, uint color, float z, uint stencil)
    {
        if (_dev == 0 || _dsv == 0) return 0;
        int vx = Math.Max(0, _vpSaved[0]), vy = Math.Max(0, _vpSaved[1]);
        int vx1 = Math.Min(Width, _vpSaved[0] + _vpSaved[2]), vy1 = Math.Min(Height, _vpSaved[1] + _vpSaved[3]);
        bool fullVp = vx == 0 && vy == 0 && vx1 >= Width && vy1 >= Height;
        if (count == 0 || rects == 0)
        {
            if (fullVp) { ClearFull(flags, color, z, stencil); return 0; }
            ClearRect(vx, vy, vx1, vy1, flags, color, z, stencil);
            return 0;
        }
        int* r = (int*)rects;
        for (uint i = 0; i < count; i++, r += 4)
        {
            int x0 = Math.Max(r[0], vx), y0 = Math.Max(r[1], vy), x1 = Math.Min(r[2], vx1), y1 = Math.Min(r[3], vy1);
            if (x0 <= 0 && y0 <= 0 && x1 >= Width && y1 >= Height) ClearFull(flags, color, z, stencil);
            else if (x1 > x0 && y1 > y0) ClearRect(x0, y0, x1, y1, flags, color, z, stencil);
        }
        return 0;
    }

    private void ClearFull(uint flags, uint color, float z, uint stencil)
    {
        if ((flags & 1) != 0) ClearScene(color);
        uint df = ((flags & 2) != 0 ? 1u : 0) | ((flags & 4) != 0 ? 2u : 0);
        if (df != 0) ((delegate* unmanaged[Stdcall]<nint, nint, uint, float, byte, void>)Fn(_ctx, CtxClearDSV))(_ctx, TargetDsv, df, z, (byte)stencil);
    }

    private void ClearRect(int x0, int y0, int x1, int y1, uint flags, uint color, float z, uint stencil)
    {
        if ((flags & 7) == 0) return;
        if (_psClear == 0)
        {
            _psClear = PS(ShClear);
            uint* bd = stackalloc uint[6];
            bd[0] = 32; bd[1] = 0; bd[2] = 4; bd[3] = 0; bd[4] = 0; bd[5] = 0;
            nint cb = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Fn(_dev, DevCreateBuffer))(_dev, bd, 0, &cb);
            _clearCb = cb;
            uint* d = stackalloc uint[66];
            new Span<uint>(d, 66).Clear();
            d[3] = 2; d[4] = 1; d[5] = 1; d[6] = 2; d[7] = 1; d[8] = 1; d[9] = 0;
            nint b = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)Fn(_dev, DevCreateBlend))(_dev, d, &b);
            _blendNoColor = b;
            if (_psClear == 0 || _clearCb == 0 || _blendNoColor == 0) return;
        }
        BindScene();
        NoGS();
        float* k = stackalloc float[8];
        k[0] = ((color >> 16) & 0xFF) / 255f; k[1] = ((color >> 8) & 0xFF) / 255f; k[2] = (color & 0xFF) / 255f; k[3] = (color >> 24) / 255f;
        k[4] = z; k[5] = k[6] = k[7] = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, float*, uint, uint, void>)Fn(_ctx, CtxUpdateSub))(_ctx, _clearCb, 0, 0, k, 0, 0);
        nint cbp = _clearCb;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxPSSetCB))(_ctx, 0, 1, &cbp);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Fn(_ctx, CtxVSSetShader))(_ctx, _vsFull, null, 0);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Fn(_ctx, CtxPSSetShader))(_ctx, _psClear, null, 0);
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)Fn(_ctx, CtxIASetLayout))(_ctx, 0);
        ((delegate* unmanaged[Stdcall]<nint, int, void>)Fn(_ctx, CtxIASetTopology))(_ctx, 4);
        ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)Fn(_ctx, CtxOMSetBlend))(_ctx, (flags & 1) != 0 ? BlendOff() : _blendNoColor, null, 0xFFFFFFFF);
        SetDepth(DepthStencil((flags & 2) != 0, (flags & 2) != 0, 8, (flags & 4) != 0, 3, 3, 3, 8, 0xFF, 0xFF), stencil & 0xFF);
        SetVp(0, 0, RW, RH, 0, 1);
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)Fn(_ctx, CtxRSSetState))(_ctx, RasterState(1));
        int* r = stackalloc int[4];
        r[0] = x0; r[1] = y0; r[2] = x1; r[3] = y1;
        ScaleRect(r);
        ((delegate* unmanaged[Stdcall]<nint, uint, int*, void>)Fn(_ctx, CtxRSSetScissor))(_ctx, 1, r);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)Fn(_ctx, CtxDraw))(_ctx, 3, 0);
        if ((flags & 1) != 0) Drew();
        // lo que se ha cambiado, a poner de nuevo en el siguiente dibujo
        _curVS = _curPS = _curLayout = _curBlend = _curRaster = 0;
        cbp = _psCb;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxPSSetCB))(_ctx, 0, 1, &cbp);
    }

    private nint BlendOff()
    {
        if (_blendCache.TryGetValue(0, out nint b)) return b;
        uint s = _rs[27]; _rs[27] = 0;
        b = BlendState();
        _rs[27] = s;
        return b;
    }
}
