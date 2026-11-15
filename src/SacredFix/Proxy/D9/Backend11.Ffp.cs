using System;
using System.Collections.Generic;
using System.Text;
using SacredFix.Core;
using static SacredFix.Proxy.D9.Display;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Motor Direct3D 11, FASE 3b: la tubería fija de Direct3D 7 para lo 3D (vértices XYZ).
/// - Matrices: el motor guarda las de D3D7 (WORLD 1, VIEW 2, PROJECTION 3, TEXTURE0-7 16-23). El juego las lee
///   mucho con GetTransform. Al dibujar se pasan al shader WORLD·VIEW·PROJECTION y WORLD·VIEW (filas, como D3D:
///   v' = v·M).
/// - Luz por vértice en el espacio de la cámara, como D3D: direccional, puntual y foco (alcance, atenuación
///   a0 + a1·d + a2·d², cono theta/phi con falloff). Incluye ambiente global (AMBIENT), material (SetMaterial) y
///   fuentes de color (COLORVERTEX, *MATERIALSOURCE). El especular usa el vector medio (LOCALVIEWER) y POWER.
///   Las luces se pasan en el espacio de la cámara con la vista del momento del dibujo.
/// - Matriz de textura por etapa (TEXTURETRANSFORMFLAGS = COUNTn) aplicada al juego de coordenadas de la etapa.
///   Con 2 coordenadas la entrada es (u, v, 1, 0): la traslación está en la tercera fila, como en D3D.
/// - Media píxel: D3D7 pone el centro del píxel en el entero, así que se desplaza +0,5 píxel (igual que en 2D).
/// - Viewport: para lo 3D el de D3D11 es el del juego (X, Y, W, H, MinZ, MaxZ).
/// </summary>
internal sealed unsafe partial class Backend11
{
    private readonly float[][] _xf = new float[24][];
    private readonly float[] _material = new float[17];
    private readonly Dictionary<uint, float[]> _lights = new();
    private readonly List<uint> _lightOn = new();
    private bool _vsDirty = true;
    private const int VsCbFloats = 320; // 1280 bytes
    private readonly float[] _vsData = new float[VsCbFloats];

    private float[] Xf(int s)
    {
        var m = _xf[s];
        if (m == null) { m = new float[16]; m[0] = m[5] = m[10] = m[15] = 1; _xf[s] = m; }
        return m;
    }

    public override int SetTransform(int s, float* m)
    {
        if ((uint)s >= 24 || m == null) return Layer.E_INVALIDARG;
        var d = Xf(s);
        for (int i = 0; i < 16; i++) d[i] = m[i];
        _vsDirty = true;
        return 0;
    }

    public override int GetTransform(int s, float* m)
    {
        if ((uint)s >= 24 || m == null) return Layer.E_INVALIDARG;
        var d = Xf(s);
        for (int i = 0; i < 16; i++) m[i] = d[i];
        return 0;
    }

    /// <summary>D3D: la nueva = la dada · la actual.</summary>
    public override int MultiplyTransform(int s, float* m)
    {
        if ((uint)s >= 24 || m == null) return Layer.E_INVALIDARG;
        var cur = Xf(s);
        float* r = stackalloc float[16];
        fixed (float* c = cur) Mul(m, c, r);
        for (int i = 0; i < 16; i++) cur[i] = r[i];
        _vsDirty = true;
        return 0;
    }

    private static void Mul(float* a, float* b, float* r)
    {
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                r[i * 4 + j] = a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j] + a[i * 4 + 2] * b[8 + j] + a[i * 4 + 3] * b[12 + j];
    }

    // D3DMATERIAL7: diffuse, ambient, specular, emissive (r, g, b, a), power
    public override int SetMaterial(nint m)
    {
        if (m == 0) return Layer.E_INVALIDARG;
        for (int i = 0; i < 17; i++) _material[i] = ((float*)m)[i];
        _vsDirty = true;
        return 0;
    }
    public override int GetMaterial(nint m)
    {
        if (m == 0) return Layer.E_INVALIDARG;
        for (int i = 0; i < 17; i++) ((float*)m)[i] = _material[i];
        return 0;
    }

    // D3DLIGHT7 (104 bytes): tipo, difusa, especular, ambiente, posición, dirección, alcance, falloff, a0, a1, a2, theta, phi
    public override int SetLight(uint i, nint l)
    {
        if (l == 0) return Layer.E_INVALIDARG;
        var d = new float[26];
        for (int k = 0; k < 26; k++) d[k] = ((float*)l)[k];
        _lights[i] = d;
        _vsDirty = true;
        return 0;
    }
    public override int GetLight(uint i, nint l)
    {
        if (l == 0 || !_lights.TryGetValue(i, out var d)) return Layer.E_INVALIDARG;
        for (int k = 0; k < 26; k++) ((float*)l)[k] = d[k];
        return 0;
    }
    public override int LightEnable(uint i, int on)
    {
        if (!_lights.ContainsKey(i))
        {
            // la luz por defecto de D3D: direccional blanca hacia +z
            var d = new float[26];
            d[0] = BitConverter.Int32BitsToSingle(3); d[1] = d[2] = d[3] = 1; d[18] = 1; // (dirección z = 1)
            _lights[i] = d;
        }
        _lightOn.Remove(i);
        if (on != 0) _lightOn.Add(i);
        _vsDirty = true;
        return 0;
    }
    public override int GetLightEnable(uint i, int* on)
    {
        if (on == null) return Layer.E_INVALIDARG;
        if (!_lights.ContainsKey(i)) return Layer.E_INVALIDARG;
        *on = _lightOn.Contains(i) ? 1 : 0;
        return 0;
    }

    // ---- constantes del shader de vértices ----

    private void UpdateVsConstants()
    {
        if (!_vsDirty) return;
        _vsDirty = false;
        fixed (float* c = _vsData)
        {
            c[0] = 2f / Width; c[1] = 2f / Height; c[2] = Sx; c[3] = Sy;
            float* wv = c + 20;
            fixed (float* w = Xf(1)) fixed (float* v = Xf(2)) Mul(w, v, wv);
            fixed (float* p = Xf(3)) Mul(wv, p, c + 4);
            fixed (float* t0 = Xf(16)) for (int i = 0; i < 16; i++) c[36 + i] = t0[i];
            fixed (float* t1 = Xf(17)) for (int i = 0; i < 16; i++) c[52 + i] = t1[i];
            int vw = _vpSaved[2] > 0 ? _vpSaved[2] : Width, vh = _vpSaved[3] > 0 ? _vpSaved[3] : Height;
            c[68] = 1f / vw; c[69] = 1f / vh; c[70] = 0; c[71] = 0;
            for (int i = 0; i < 16; i++) c[72 + i] = _material[i];
            uint am = _rs[139];
            c[88] = ((am >> 16) & 0xFF) / 255f; c[89] = ((am >> 8) & 0xFF) / 255f; c[90] = (am & 0xFF) / 255f; c[91] = (am >> 24) / 255f;
            c[92] = _material[16]; c[93] = Math.Min(_lightOn.Count, 8); c[94] = 0; c[95] = 0;
            float* view = stackalloc float[16];
            fixed (float* v = Xf(2)) for (int i = 0; i < 16; i++) view[i] = v[i];
            for (int k = 0; k < 8 && k < _lightOn.Count; k++)
            {
                var l = _lights[_lightOn[k]];
                float* o = c + 96 + k * 28;
                for (int i = 0; i < 12; i++) o[i] = l[1 + i]; // difusa, especular, ambiente
                float px = l[13], py = l[14], pz = l[15], dx = l[16], dy = l[17], dz = l[18];
                o[12] = px * view[0] + py * view[4] + pz * view[8] + view[12];
                o[13] = px * view[1] + py * view[5] + pz * view[9] + view[13];
                o[14] = px * view[2] + py * view[6] + pz * view[10] + view[14];
                o[15] = l[19]; // alcance
                float ex = dx * view[0] + dy * view[4] + dz * view[8], ey = dx * view[1] + dy * view[5] + dz * view[9], ez = dx * view[2] + dy * view[6] + dz * view[10];
                float len = MathF.Sqrt(ex * ex + ey * ey + ez * ez);
                if (len > 0) { ex /= len; ey /= len; ez /= len; }
                o[16] = ex; o[17] = ey; o[18] = ez; o[19] = BitConverter.SingleToInt32Bits(l[0]); // tipo (1 punto, 2 foco, 3 direccional)
                o[20] = l[21]; o[21] = l[22]; o[22] = l[23]; o[23] = l[20]; // a0, a1, a2, falloff
                o[24] = MathF.Cos(l[24] / 2); o[25] = MathF.Cos(l[25] / 2); o[26] = 0; o[27] = 0;
            }
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, float*, uint, uint, void>)Fn(_ctx, CtxUpdateSub))(_ctx, _vsCb, 0, 0, c, 0, 0);
        }
    }

    // ---- shader de vértices ----

    private record struct VsKey(uint Fvf, uint Flags);

    /// <summary>Lo que cambia el shader de vértices de lo 3D (en 2D, 0).</summary>
    private uint VsFlags()
    {
        uint f = 0;
        if (_rs[137] != 0)
        {
            f |= 1;
            if (_rs[29] != 0) f |= 2;
            if (_rs[142] != 0) f |= 4;
            if (_rs[143] != 0) f |= 8;
            if (_rs[141] != 0) f |= 16 | (_rs[145] & 3) << 5 | (_rs[146] & 3) << 7 | (_rs[147] & 3) << 9 | (_rs[148] & 3) << 11;
        }
        for (int s = 0; s < 2; s++)
        {
            if (_tss[s, 1] == 1) break;
            uint ttf = _tss[s, 24] & 7, tci = Math.Min(_tss[s, 11] & 0xFFFF, 3);
            f |= ttf << (13 + s * 5) | tci << (16 + s * 5);
        }
        return f;
    }

    /// <summary>Luz por píxel ([Display] PixelLighting): bit de VsKey.Flags.</summary>
    private const uint PixelLightFlag = 1u << 31;

    /// <summary>
    /// Cálculo de la luz de D3D7 (en el espacio de la cámara), común al shader de vértices (por vértice) y al de
    /// píxeles (por píxel). Necesita P, N, vd, vs y las constantes de VsCb3D; deja el resultado en ldif y lspe.
    /// f: opciones de VsFlags; dif/spe: si el vértice trae color difuso / especular.
    /// </summary>
    private static string LightCode(uint f, bool dif, bool spe)
    {
        string Src(int sh, string mat)
        {
            uint s = (f >> sh) & 3;
            if ((f & 16) == 0) return mat;
            return s == 1 && dif ? "vd" : s == 2 && spe ? "vs" : mat;
        }
        var c = new StringBuilder();
        c.Append($"    float4 md = {Src(5, "mDif")}, ms = {Src(7, "mSpe")}, ma = {Src(9, "mAmb")}, me = {Src(11, "mEmi")};\n");
        c.Append((f & 4) != 0 ? "    float3 V = normalize(-P);\n" : "    float3 V = float3(0, 0, -1);\n");
        c.Append(@"    float3 ad = 0, dd = 0, sd = 0;
    [loop] for (int k = 0; k < (int)misc.y; k++)
    {
        float4 ld = L[k * 7], ls = L[k * 7 + 1], la = L[k * 7 + 2], lp = L[k * 7 + 3], lr = L[k * 7 + 4], lat = L[k * 7 + 5], lco = L[k * 7 + 6];
        int type = (int)lr.w;
        float3 Lv; float att = 1;
        if (type == 3) Lv = -lr.xyz;
        else
        {
            float3 dv = lp.xyz - P;
            float dist = length(dv);
            Lv = dv / max(dist, 1e-6);
            att = dist > lp.w ? 0 : 1 / max(lat.x + lat.y * dist + lat.z * dist * dist, 1e-6);
            if (type == 2)
            {
                float rho = dot(-Lv, lr.xyz);
                att *= rho > lco.x ? 1 : rho <= lco.y ? 0 : pow(saturate((rho - lco.y) / max(lco.x - lco.y, 1e-6)), lat.w);
            }
        }
        ad += la.rgb * att;
        float nl = dot(N, Lv);
        if (nl > 0)
        {
            dd += ld.rgb * nl * att;
");
        if ((f & 2) != 0) c.Append("            sd += ls.rgb * pow(max(dot(N, normalize(Lv + V)), 0), misc.x) * att;\n");
        c.Append(@"        }
    }
    float4 ldif = float4(saturate(me.rgb + ma.rgb * (gAmb.rgb + ad) + md.rgb * dd), md.a);
    float4 lspe = float4(saturate(ms.rgb * sd), vs.a);
");
        return c.ToString();
    }

    private const string VsCb3D = @"cbuffer C : register(b0)
{
    float4 sc;
    row_major float4x4 wvp;
    row_major float4x4 wv;
    row_major float4x4 tm0;
    row_major float4x4 tm1;
    float4 vpo;
    float4 mDif; float4 mAmb; float4 mSpe; float4 mEmi;
    float4 gAmb;
    float4 misc;   // x = power, y = luces
    float4 L[56];  // 8 luces x 7: difusa, especular, ambiente, posición+alcance, dirección+tipo, atenuación+falloff, cono
};
";

    private (nint vs, nint layout) VertexShader(VsKey key)
    {
        if (_vsCache.TryGetValue(key, out var c)) return c;
        c = (0, 0);
        uint fvf = key.Fvf, f = key.Flags;
        bool d3 = (fvf & 0xE) == 0x2;
        var src = new StringBuilder();
        src.Append(d3 ? VsCb3D : "cbuffer C : register(b0) { float4 sc; };\n");
        src.Append(d3 ? "struct VI { float3 pos : POSITION;" : "struct VI { float4 pos : POSITION;");
        uint* el = stackalloc uint[7 * 16];
        int n = 0, off = 0;
        void Add(nint sem, uint idx, uint fmt, int size)
        {
            uint* e = el + n * 7;
            e[0] = (uint)sem; e[1] = idx; e[2] = fmt; e[3] = 0; e[4] = (uint)off; e[5] = 0; e[6] = 0;
            n++; off += size;
        }
        if (d3) Add(SemPos, 0, 6 /* R32G32B32_FLOAT */, 12); else Add(SemPos, 0, 2 /* R32G32B32A32_FLOAT */, 16);
        bool nrm = (fvf & 0x10) != 0, dif = (fvf & 0x40) != 0, spe = (fvf & 0x80) != 0;
        if (nrm) { if (d3) { Add(SemNrm, 0, 6, 12); src.Append(" float3 nrm : NORMAL;"); } else off += 12; }
        if ((fvf & 0x20) != 0) off += 4; // RESERVED1
        if (dif) { Add(SemCol, 0, 87, 4); src.Append(" float4 dif : COLOR0;"); }
        if (spe) { Add(SemCol, 1, 87, 4); src.Append(" float4 spe : COLOR1;"); }
        int nt = TexCount(fvf);
        for (int k = 0; k < nt; k++)
        {
            int sz = TexSize(fvf, k);
            uint fmt = sz switch { 1 => 41u, 2 => 16u, 3 => 6u, _ => 2u };
            if (k < 4) { Add(SemTex, (uint)k, fmt, sz * 4); src.Append($" float{(sz == 1 ? "" : sz.ToString())} t{k} : TEXCOORD{k};"); }
            else off += sz * 4;
        }
        src.Append(" };\n").Append(VsOut);
        src.Append("VO main(VI i)\n{\n    VO o;\n");
        if (d3)
        {
            src.Append("    float4 p = float4(i.pos, 1);\n    o.pos = mul(p, wvp);\n");
            src.Append("    o.pos.x += o.pos.w * vpo.x; o.pos.y -= o.pos.w * vpo.y;\n");
        }
        else
        {
            src.Append("    float w = i.pos.w != 0 ? 1.0 / i.pos.w : 1.0;\n");
            src.Append("    o.pos = float4(((i.pos.x + 0.5) * sc.x - 1) * w, (1 - (i.pos.y + 0.5) * sc.y) * w, i.pos.z * w, w);\n");
        }
        src.Append(dif ? "    float4 vd = i.dif;\n" : "    float4 vd = 1;\n");
        src.Append(spe ? "    float4 vs = i.spe;\n" : "    float4 vs = 0;\n");
        bool pix = (f & PixelLightFlag) != 0;
        if (d3 && (f & 1) != 0)
        {
            src.Append("    float3 P = mul(p, wv).xyz;\n");
            src.Append(nrm ? "    float3 N = mul(i.nrm, (float3x3)wv);\n" : "    float3 N = 0;\n");
            if (pix)
            {
                // luz por píxel: el color del vértice tal cual y la posición y la normal para el shader de píxeles
                src.Append("    o.dif = vd;\n    o.spe = vs;\n    o.vp = P;\n    o.vn = N;\n");
            }
            else
            {
                if ((f & 8) != 0 && nrm) src.Append("    N = N * rsqrt(max(dot(N, N), 1e-12));\n");
                src.Append(LightCode(f, dif, spe));
                src.Append("    o.dif = ldif;\n    o.spe = lspe;\n    o.vp = 0;\n    o.vn = 0;\n");
            }
        }
        else src.Append("    o.dif = vd;\n    o.spe = vs;\n    o.vp = 0;\n    o.vn = 0;\n");
        // coordenadas de textura (con la matriz de la etapa que las usa, si la tiene)
        for (int k = 0; k < 4; k++)
        {
            if (k >= nt) { src.Append($"    o.t{k} = float4(0, 0, 0, 1);\n"); continue; }
            int sz = TexSize(fvf, k);
            src.Append(sz switch
            {
                1 => $"    o.t{k} = float4(i.t{k}, 0, 0, 1);\n",
                2 => $"    o.t{k} = float4(i.t{k}, 0, 1);\n",
                3 => $"    o.t{k} = float4(i.t{k}, 1);\n",
                _ => $"    o.t{k} = i.t{k};\n",
            });
        }
        if (d3)
            for (int s = 0; s < 2; s++)
            {
                uint ttf = (f >> (13 + s * 5)) & 7, tci = (f >> (16 + s * 5)) & 3;
                if (ttf == 0 || tci >= nt) continue;
                int sz = TexSize(fvf, (int)tci);
                string inp = sz switch
                {
                    1 => $"float4(i.t{tci}, 1, 0, 0)", 2 => $"float4(i.t{tci}, 1, 0)", 3 => $"float4(i.t{tci}, 1)", _ => $"i.t{tci}",
                };
                string r = $"mul({inp}, tm{s})";
                src.Append(ttf switch
                {
                    1 => $"    o.t{tci} = float4({r}.x, 0, 0, 1);\n",
                    2 => $"    o.t{tci} = float4({r}.xy, 0, 1);\n",
                    3 => $"    o.t{tci} = float4({r}.xyz, 1);\n",
                    _ => $"    o.t{tci} = {r};\n",
                });
            }
        src.Append("    return o;\n}\n");
        nint vs = VS(src.ToString(), out nint blob);
        if (vs == 0) { Log.Error($"D11: shader de vértices del FVF 0x{fvf:X} (0x{f:X}) no compila"); _vsCache[key] = c; return c; }
        nint layout = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, uint, nint, nuint, nint*, int>)Fn(_dev, DevCreateInputLayout))(_dev, el, (uint)n, BlobPtr(blob), BlobSize(blob), &layout);
        Layer.ReleaseCom(blob);
        if (hr != 0) Log.Error($"D11: descripción de vértices del FVF 0x{fvf:X} -> 0x{hr:X8}");
        else Log.Info($"D11: shader de vértices para el FVF 0x{fvf:X} (opciones 0x{f:X}, {off} bytes)");
        c = (vs, layout);
        _vsCache[key] = c;
        return c;
    }
}
