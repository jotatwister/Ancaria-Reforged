using System;
using SacredFix.Core;
using static SacredFix.Proxy.D9.Display;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Motor Direct3D 11, FASE 5: antialiasing (MSAA, [Display] MultiSample).
/// - El juego dibuja en una imagen con MSAA (_msTex) y su Z-buffer con MSAA. La imagen interna normal (_scene) queda
///   como imagen resuelta: la que se estira al presentar y la que leen y escriben Lock y GDI.
/// - Resolver (MSAA -> _scene): antes de presentar y antes de que la CPU lea la pantalla, solo si se ha dibujado.
/// - Al revés: si la CPU ha escrito en la pantalla (pantalla de carga, rellenos), al siguiente dibujo se copia _scene
///   a la imagen con MSAA (un triángulo de pantalla completa), para que el MSAA no la tape al resolver.
/// - A diferencia de D3D9, lo 2D también lleva MSAA: las coordenadas de textura se interpolan en el centroide (dentro
///   de la parte cubierta del píxel). Así, en el borde de las baldosas del suelo no se lee fuera de su textura, que era
///   lo que hacía las líneas.
/// </summary>
internal sealed unsafe partial class Backend11
{
    private const int DevCheckMsaa = 30, CtxResolve = 57;
    private int _samples = 1;
    private nint _msTex, _msRtv, _msDepth, _msDsv, _psCopy;
    private bool _msaaNewer;   // se ha dibujado en la imagen con MSAA desde la última vez que se resolvió
    private bool _sceneNewer;  // la CPU ha escrito en _scene y la imagen con MSAA no lo tiene

    private nint TargetRtv => _msRtv != 0 ? _msRtv : _sceneRtv;
    private nint TargetDsv => _msDsv != 0 ? _msDsv : _dsv;

    private const string ShCopy = @"
Texture2D t0 : register(t0);
float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return t0.Load(int3(pos.xy, 0)); }";

    private void CreateMsaa()
    {
        int want = Layer.MultiSample;
        if (want < 2) return;
        var check = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, int>)Fn(_dev, DevCheckMsaa);
        int n = 0;
        for (int c = Math.Min(want, 8); c >= 2; c /= 2)
        {
            uint q1 = 0, q2 = 0;
            check(_dev, 87, (uint)c, &q1);
            check(_dev, 45, (uint)c, &q2);
            if (q1 > 0 && q2 > 0) { n = c; break; }
        }
        if (n == 0) { Log.Warn($"D11: la tarjeta no admite MSAA {want}x: sin antialiasing"); return; }
        uint* t = stackalloc uint[11];
        t[0] = (uint)RW; t[1] = (uint)RH; t[2] = 1; t[3] = 1; t[4] = 87; t[5] = (uint)n; t[6] = 0; t[7] = 0; t[8] = 0x20; t[9] = 0; t[10] = 0;
        nint tex = 0, rtv = 0, dtex = 0, dsv = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Fn(_dev, DevCreateTexture2D))(_dev, t, 0, &tex);
        if (hr == 0) hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Fn(_dev, DevCreateRTV))(_dev, tex, 0, &rtv);
        t[4] = 45; t[8] = 0x40;
        if (hr == 0) hr = ((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Fn(_dev, DevCreateTexture2D))(_dev, t, 0, &dtex);
        if (hr == 0) hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Fn(_dev, DevCreateDSV))(_dev, dtex, 0, &dsv);
        nint ps = hr == 0 ? PS(ShCopy) : 0;
        if (hr != 0 || ps == 0)
        {
            Log.Warn($"D11: no se pudo preparar el MSAA {n}x (0x{hr:X8}): sin antialiasing");
            Layer.ReleaseCom(dsv); Layer.ReleaseCom(dtex); Layer.ReleaseCom(rtv); Layer.ReleaseCom(tex); Layer.ReleaseCom(ps);
            return;
        }
        _msTex = tex; _msRtv = rtv; _msDepth = dtex; _msDsv = dsv; _psCopy = ps; _samples = n;
        float* black = stackalloc float[4];
        black[0] = black[1] = black[2] = 0; black[3] = 1;
        ((delegate* unmanaged[Stdcall]<nint, nint, float*, void>)Fn(_ctx, CtxClearRTV))(_ctx, _msRtv, black);
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, float, byte, void>)Fn(_ctx, CtxClearDSV))(_ctx, _msDsv, 3, 1f, 0);
        Log.Info($"D11: antialiasing MSAA {n}x (también en lo 2D, con coordenadas de textura en el centroide)");
    }

    /// <summary>Lo dibujado: hay que resolverlo antes de presentarlo o leerlo.</summary>
    private void Drew() { _gpuNewer = true; _msaaNewer = true; }

    /// <summary>La imagen con MSAA a _scene (si ha cambiado).</summary>
    private void Resolve()
    {
        if (_msTex == 0 || !_msaaNewer) return;
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, uint, uint, void>)Fn(_ctx, CtxResolve))(_ctx, _scene, 0, _msTex, 0, 87);
        _msaaNewer = false;
    }

    /// <summary>Lo que la CPU escribió en _scene, a la imagen con MSAA (antes de seguir dibujando en ella).</summary>
    private void Unresolve()
    {
        if (!_sceneNewer) return;
        _sceneNewer = false;
        if (_msRtv == 0) return;
        NoGS();
        nint rtv = _msRtv;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)Fn(_ctx, CtxOMSetRT))(_ctx, 1, &rtv, 0);
        SetVp(0, 0, RW, RH, 0, 1);
        int* r = stackalloc int[4];
        r[0] = 0; r[1] = 0; r[2] = RW; r[3] = RH;
        ((delegate* unmanaged[Stdcall]<nint, uint, int*, void>)Fn(_ctx, CtxRSSetScissor))(_ctx, 1, r);
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)Fn(_ctx, CtxRSSetState))(_ctx, RasterState(1));
        ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)Fn(_ctx, CtxOMSetBlend))(_ctx, BlendOff(), null, 0xFFFFFFFF);
        SetDepth(DepthState(false, false, 8), 0);
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)Fn(_ctx, CtxIASetLayout))(_ctx, 0);
        ((delegate* unmanaged[Stdcall]<nint, int, void>)Fn(_ctx, CtxIASetTopology))(_ctx, 4);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Fn(_ctx, CtxVSSetShader))(_ctx, _vsFull, null, 0);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Fn(_ctx, CtxPSSetShader))(_ctx, _psCopy, null, 0);
        nint srv = _sceneSrv;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxPSSetSRV))(_ctx, 0, 1, &srv);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)Fn(_ctx, CtxDraw))(_ctx, 3, 0);
        srv = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxPSSetSRV))(_ctx, 0, 1, &srv);
        // lo cambiado, a poner de nuevo
        _curVS = _curPS = _curLayout = _curBlend = _curRaster = 0;
        _curSrv[0] = 0;
        _msaaNewer = false; // ahora las dos son iguales
    }
}
