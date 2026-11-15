using System;
using SacredFix.Core;
using static SacredFix.Proxy.D9.Display;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Motor Direct3D 11: resolución interna mayor que la del juego ([Display] RenderScale).
/// El juego sigue trabajando a Width x Height (1366x768: misma vista, mismos tamaños), pero el motor dibuja a
/// RW x RH (con «auto», a la altura de la pantalla: 1921x1080 en un monitor de 1080). Los modelos 3D, los efectos y
/// los textos salen nítidos; los dibujos 2D del juego se amplían al dibujarlos.
/// - Lo 2D (XYZRHW) ya se pasa a coordenadas proporcionales a la imagen, así que se adapta solo.
/// - Viewport, recorte y borrados vienen en píxeles del juego: se multiplican por Sx, Sy.
/// - Lo 2D con filtro de punto se dibuja con filtro lineal: ampliado x1,41 con punto saldrían píxeles de ancho
///   desigual.
/// - La CPU sigue viendo una pantalla de Width x Height (Lock, GDI, rellenos): al leerla, la imagen grande se reduce a
///   _small (lineal) y se copia de ahí; al escribirla, se sube a _small y se amplía a la imagen grande, solo en la
///   zona tocada.
/// </summary>
internal sealed unsafe partial class Backend11
{
    private int RW, RH;
    private float Sx = 1, Sy = 1;
    private bool Scaled => RW != Width || RH != Height;
    private nint _small, _smallRtv, _smallSrv, _psSample;

    private const string ShSample = @"
Texture2D t0 : register(t0);
SamplerState s0 : register(s0);
float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return t0.SampleLevel(s0, uv, 0); }";

    /// <summary>Tamaño de la imagen interna, según [Display] RenderScale y el área de la ventana al crear el dispositivo.</summary>
    private void ChooseRenderSize(int outW, int outH)
    {
        string m = Layer.RenderScale;
        float s;
        if (m == "auto") s = outH / (float)Height;
        else if (!float.TryParse(m, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out s)) s = 1;
        s = Math.Clamp(s, 1f, 4f);
        RW = (int)MathF.Round(Width * s); RH = (int)MathF.Round(Height * s);
        if (RW - Width < 2 && RH - Height < 2) { RW = Width; RH = Height; }
        Sx = RW / (float)Width; Sy = RH / (float)Height;
        if (Scaled) Log.Info($"D11: el juego trabaja a {Width}x{Height} y se dibuja a {RW}x{RH} (escala {Sx:F3}; [Display] RenderScale={m})");
    }

    private bool CreateSmall()
    {
        if (!Scaled) return true;
        uint* t = stackalloc uint[11];
        t[0] = (uint)Width; t[1] = (uint)Height; t[2] = 1; t[3] = 1; t[4] = 87; t[5] = 1; t[6] = 0; t[7] = 0; t[8] = 0x20 | 0x8; t[9] = 0; t[10] = 0;
        nint tex = 0, rtv = 0, srv = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Fn(_dev, DevCreateTexture2D))(_dev, t, 0, &tex);
        if (hr == 0) hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Fn(_dev, DevCreateRTV))(_dev, tex, 0, &rtv);
        if (hr == 0) hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Fn(_dev, DevCreateSRV))(_dev, tex, 0, &srv);
        nint ps = hr == 0 ? PS(ShSample) : 0;
        if (hr != 0 || ps == 0) { Log.Error($"D11: imagen auxiliar de {Width}x{Height} -> 0x{hr:X8}"); return false; }
        _small = tex; _smallRtv = rtv; _smallSrv = srv; _psSample = ps;
        return true;
    }

    /// <summary>Un rectángulo en píxeles del juego, a píxeles de la imagen interna (hacia fuera).</summary>
    private void ScaleRect(int* r)
    {
        if (!Scaled) return;
        r[0] = (int)MathF.Floor(r[0] * Sx); r[1] = (int)MathF.Floor(r[1] * Sy);
        r[2] = Math.Min(RW, (int)MathF.Ceiling(r[2] * Sx)); r[3] = Math.Min(RH, (int)MathF.Ceiling(r[3] * Sy));
    }

    /// <summary>Copia una imagen a otra estirándola (lineal), dentro del recorte dado (píxeles del destino).</summary>
    private void Blit(nint dstRtv, nint srcSrv, int dw, int dh, int x0, int y0, int x1, int y1)
    {
        NoGS();
        nint rtv = dstRtv;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)Fn(_ctx, CtxOMSetRT))(_ctx, 1, &rtv, 0);
        SetVp(0, 0, dw, dh, 0, 1);
        int* r = stackalloc int[4];
        r[0] = x0; r[1] = y0; r[2] = x1; r[3] = y1;
        ((delegate* unmanaged[Stdcall]<nint, uint, int*, void>)Fn(_ctx, CtxRSSetScissor))(_ctx, 1, r);
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)Fn(_ctx, CtxRSSetState))(_ctx, RasterState(1));
        ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)Fn(_ctx, CtxOMSetBlend))(_ctx, BlendOff(), null, 0xFFFFFFFF);
        SetDepth(DepthState(false, false, 8), 0);
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)Fn(_ctx, CtxIASetLayout))(_ctx, 0);
        ((delegate* unmanaged[Stdcall]<nint, int, void>)Fn(_ctx, CtxIASetTopology))(_ctx, 4);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Fn(_ctx, CtxVSSetShader))(_ctx, _vsFull, null, 0);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Fn(_ctx, CtxPSSetShader))(_ctx, _psSample, null, 0);
        nint srv = srcSrv, samp = _samp;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxPSSetSRV))(_ctx, 0, 1, &srv);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxPSSetSamplers))(_ctx, 0, 1, &samp);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)Fn(_ctx, CtxDraw))(_ctx, 3, 0);
        srv = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxPSSetSRV))(_ctx, 0, 1, &srv);
        // lo cambiado, a poner de nuevo en el siguiente dibujo
        _curVS = _curPS = _curLayout = _curBlend = _curRaster = 0;
        _curSrv[0] = 0; _curSamp[0] = 0;
        _bound = false;
    }

    /// <summary>La imagen interna (ya resuelta), reducida a _small.</summary>
    private void Downsample() => Blit(_smallRtv, _sceneSrv, Width, Height, 0, 0, Width, Height);

    /// <summary>Una zona de _small (píxeles del juego), ampliada a la imagen interna.</summary>
    private void Upsample(int x0, int y0, int x1, int y1)
    {
        int* r = stackalloc int[4];
        r[0] = x0; r[1] = y0; r[2] = x1; r[3] = y1;
        ScaleRect(r);
        Blit(_sceneRtv, _smallSrv, RW, RH, r[0], r[1], r[2], r[3]);
    }
}
