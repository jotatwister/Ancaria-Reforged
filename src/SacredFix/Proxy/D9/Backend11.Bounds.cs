using System;
using SacredFix.Core;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Límites de cada pieza 2D (geometry shader). Con la resolución ampliada, el borde de una pieza de la interfaz
/// sacada de una textura con varias piezas (marcos, botones, letras) leía texels de la pieza de al lado: rayas en
/// los marcos. El GS pasa al PS, sin interpolar, el rectángulo de coordenadas de textura del triángulo (en un
/// rectángulo partido en dos triángulos, el de la pieza entera), y el PS (Smooth2D / Clamp2D) no lee fuera de él.
/// Solo en lo 2D ampliado dibujado con triángulos; en el resto de dibujos no hay GS (NoGS).
/// </summary>
internal sealed unsafe partial class Backend11
{
    private const int DevCreateGS = 13, CtxGSSetShader = 23;
    private nint _gs, _curGS;

    private const string ShBounds = @"
struct VI { float4 pos : SV_Position; float4 dif : COLOR0; float4 spe : COLOR1; float4 t0 : TEXCOORD0; float4 t1 : TEXCOORD1; float4 t2 : TEXCOORD2; float4 t3 : TEXCOORD3; float3 vp : TEXCOORD4; float3 vn : TEXCOORD5; };
struct GO { float4 pos : SV_Position; float4 dif : COLOR0; float4 spe : COLOR1; float4 t0 : TEXCOORD0; float4 t1 : TEXCOORD1; float4 t2 : TEXCOORD2; float4 t3 : TEXCOORD3; float3 vp : TEXCOORD4; float3 vn : TEXCOORD5; nointerpolation float4 ub : TEXCOORD6; };
[maxvertexcount(3)]
void main(triangle VI v[3], inout TriangleStream<GO> s)
{
    float2 mn = min(min(v[0].t0.xy, v[1].t0.xy), v[2].t0.xy), mx = max(max(v[0].t0.xy, v[1].t0.xy), v[2].t0.xy);
    [unroll] for (int k = 0; k < 3; k++)
    {
        GO o;
        o.pos = v[k].pos; o.dif = v[k].dif; o.spe = v[k].spe; o.t0 = v[k].t0; o.t1 = v[k].t1; o.t2 = v[k].t2; o.t3 = v[k].t3;
        o.vp = v[k].vp; o.vn = v[k].vn; o.ub = float4(mn, mx);
        s.Append(o);
    }
}";

    private void CreateBoundsGS()
    {
        nint blob = Compile(ShBounds, "gs_4_0");
        if (blob == 0) { Log.Warn("D11: sin geometry shader de límites (pueden verse rayas en marcos de la interfaz)"); return; }
        nint gs = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, nuint, nint, nint*, int>)Fn(_dev, DevCreateGS))(_dev, BlobPtr(blob), BlobSize(blob), 0, &gs);
        Layer.ReleaseCom(blob);
        if (hr != 0) { Log.Warn($"D11: geometry shader de límites -> 0x{hr:X8}"); return; }
        _gs = gs;
    }

    /// <summary>Antes de dibujar con otra tubería (presentar, superposición, copias): sin geometry shader.</summary>
    private void NoGS()
    {
        if (_curGS == 0) return;
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Fn(_ctx, CtxGSSetShader))(_ctx, 0, null, 0);
        _curGS = 0;
    }
}
