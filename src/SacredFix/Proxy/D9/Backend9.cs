using System;
using SacredFix.Core;
using static SacredFix.Proxy.D9.Display;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Motor Direct3D 9 de la capa. El dispositivo, los destinos de dibujo, el MSAA, el escalado y la recuperación tras
/// perderlo están en Device9; aquí está la traducción de la semántica de DirectX 7 a Direct3D 9:
///   - matrices: WORLD 1 -> 256 (WORLD1-3: 4-6 -> 257-259);
///   - etapas de textura: ADDRESS*, BORDERCOLOR, *FILTER, MIPMAPLODBIAS, MAXMIPLEVEL y MAXANISOTROPY pasan a
///     SetSamplerState (con los valores de filtro de D3D7 convertidos);
///   - estados de render que D3D9 no tiene (TEXTUREPERSPECTIVE, COLORKEYENABLE...) no se envían;
///   - dibujo con punteros a vértices (DrawPrimitiveUP / DrawIndexedPrimitiveUP) con el FVF del juego (en D3D9 el
///     bit 0x20 de D3DLVERTEX es PSIZE, del mismo tamaño que el RESERVED1 de D3D7);
///   - con MSAA, lo 2D sin antialiasing (D3DRS_MULTISAMPLEANTIALIAS), para que no se vean las juntas de las baldosas.
/// </summary>
internal sealed unsafe class Backend9 : Backend
{
    private static nint D => Device9.Dev;
    private uint _fvf = 0xFFFFFFFF;

    public Backend9() { Device9.AfterReset += () => { _fvf = 0xFFFFFFFF; RaiseAfterReset(); }; }

    // ---- dispositivo ----

    public override bool Create() => Device9.Create();
    public override bool Ready => Device9.Dev != 0;
    public override void Evict()
    {
        if (D != 0) ((delegate* unmanaged[Stdcall]<nint, int>)Layer.Fn(D, Device9.EvictManagedResources))(D);
    }

    // ---- superficies ----

    public override bool CreateTexture(Surface7 s)
    {
        if (!Device9.Create()) return false;
        nint tex = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, int, int, nint*, nint, int>)Layer.Fn(D, Device9.CreateTexture))(D, (uint)s.W, (uint)s.H, 1, 0, s.Fmt, 1 /* MANAGED */, &tex, 0);
        if (hr != 0) { Log.Error($"D9: CreateTexture {s.W}x{s.H} formato {s.Fmt} -> 0x{hr:X8}"); return false; }
        nint lvl = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Layer.Fn(tex, 18))(tex, 0, &lvl);
        s.Tex = tex; s.Surf = lvl;
        return true;
    }

    public override bool CreateSysMem(Surface7 s)
    {
        if (!Device9.Create()) return false;
        nint surf = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, int, int, nint*, nint, int>)Layer.Fn(D, Device9.CreateOffscreenPlainSurface))(D, (uint)s.W, (uint)s.H, s.Fmt, 2 /* SYSTEMMEM */, &surf, 0);
        if (hr != 0) { Log.Error($"D9: CreateOffscreenPlainSurface {s.W}x{s.H} formato {s.Fmt} -> 0x{hr:X8}"); return false; }
        s.Surf = surf;
        return true;
    }

    public override void Release(Surface7 s)
    {
        Layer.ReleaseCom(s.Surf); s.Surf = 0;
        Layer.ReleaseCom(s.Tex); s.Tex = 0;
    }

    /// <summary>La superficie D3D9 de una de las nuestras (la pantalla: el espejo o el back buffer, vía BeginCpuAccess).</summary>
    private static nint Target(Surface7 s) => s.Type switch
    {
        Surface7.Kind.Primary or Surface7.Kind.Back => Device9.Canvas,
        Surface7.Kind.Z => Device9.DepthStencil,
        _ => s.Surf,
    };
    private static nint ScreenCpu => Device9.SceneSurf != 0 ? Device9.Mirror : Device9.BackBuffer;
    private static int _lockErrors, _dcErrors, _presentErrors;

    public override bool Lock(Surface7 s, int* rect, uint flags, out byte* bits, out int pitch)
    {
        bits = null; pitch = 0;
        int* lr = stackalloc int[2]; // D3DLOCKED_RECT { Pitch, pBits }
        uint f = flags & 0x810;      // READONLY / NOSYSLOCK
        int hr;
        if (s.Type == Surface7.Kind.Texture) hr = ((delegate* unmanaged[Stdcall]<nint, uint, int*, int*, uint, int>)Layer.Fn(s.Tex, 19))(s.Tex, 0, lr, rect, f);
        else
        {
            nint t = s.Screen ? Device9.BeginCpuAccess() : Target(s);
            if (t == 0) return false;
            hr = ((delegate* unmanaged[Stdcall]<nint, int*, int*, uint, int>)Layer.Fn(t, 13))(t, lr, rect, f);
        }
        if (hr != 0) { if (_lockErrors++ < 8) Log.Warn($"D9: Lock ({s.Type} {s.W}x{s.H}) -> 0x{hr:X8}"); return false; }
        pitch = lr[0]; bits = (byte*)*(nint*)(lr + 1);
        return true;
    }

    public override void Unlock(Surface7 s)
    {
        if (s.Type == Surface7.Kind.Texture) { ((delegate* unmanaged[Stdcall]<nint, uint, int>)Layer.Fn(s.Tex, 20))(s.Tex, 0); return; }
        nint t = s.Screen ? ScreenCpu : Target(s);
        if (t != 0) ((delegate* unmanaged[Stdcall]<nint, int>)Layer.Fn(t, 14))(t);
        if (s.Screen) Device9.EndCpuAccess();
    }

    public override int GetDC(Surface7 s, nint* hdc)
    {
        nint t = s.Screen ? Device9.BeginCpuAccess() : Target(s);
        if (t == 0) return Layer.E_FAIL;
        nint dc = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Layer.Fn(t, 15))(t, &dc);
        *hdc = dc;
        if (hr != 0 && _dcErrors++ < 5) Log.Warn($"D9: GetDC ({s.Type}) -> 0x{hr:X8}");
        return hr;
    }

    public override int ReleaseDC(Surface7 s, nint hdc)
    {
        nint t = s.Screen ? ScreenCpu : Target(s);
        if (t == 0) return Layer.E_FAIL;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, int>)Layer.Fn(t, 16))(t, hdc);
        if (s.Screen) Device9.EndCpuAccess();
        return hr;
    }

    public override int FillScreen(int* rect, uint color)
    {
        var fill = (delegate* unmanaged[Stdcall]<nint, nint, int*, uint, int>)Layer.Fn(D, Device9.ColorFill);
        if (Device9.MsaaTarget != 0) fill(D, Device9.MsaaTarget, rect, color | 0xFF000000);
        return fill(D, Device9.Canvas, rect, color | 0xFF000000);
    }

    public override void Present(bool gameFrame)
    {
        if (D == 0) return;
        if (!Device9.Recover()) { System.Threading.Thread.Sleep(10); return; } // perdido: se espera a volver
        Device9.CheckResize();
        if (gameFrame) Device9.Resolve();
        Device9.Upscale();
        if (gameFrame) Overlay.Frame();
        if (Overlay.Any && gameFrame)
        {
            bool sc = Device9.SceneSurf != 0 && OutW > 0;
            Overlay.Draw(sc ? OutW / (float)Width : 1f, sc ? OutH / (float)Height : 1f);
        }
        OptionsMenu.Draw();
        int* src = stackalloc int[4];
        src[0] = 0; src[1] = 0; src[2] = OutW; src[3] = OutH;
        bool part = !Fullscreen && Device9.SceneSurf != 0 && OutW > 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, nint, int>)Layer.Fn(D, Device9.Present))(D, part ? (nint)src : 0, 0, 0, 0);
        Device9.OnPresentResult(hr);
        if (hr != 0 && hr != Device9.D3DERR_DEVICELOST && _presentErrors++ < 5) Log.Warn($"D9: Present -> 0x{hr:X8}");
    }

    // ---- traducción de estados ----

    /// <summary>Estados de render de D3D7 que existen en D3D9 con el mismo número.</summary>
    private static bool Supported(int s) =>
        s >= 7 && s < 256 && !(s == 10 || s == 11 || s == 12 || s == 13 || s == 17 || s == 18 || s == 21 || (s >= 30 && s <= 33)
            || (s >= 39 && s <= 47) || (s >= 49 && s <= 51) || (s >= 64 && s <= 95) || s == 138 || s == 144);

    /// <summary>Etapa de textura de D3D7 que en D3D9 es de muestreo (SetSamplerState).</summary>
    private static int SamplerOf(int t) => t switch { 13 => 1, 14 => 2, 15 => 4, 16 => 5, 17 => 6, 18 => 7, 19 => 8, 20 => 9, 21 => 10, _ => 0 };

    private static uint ToSamplerValue(int t, uint v) => t switch
    {
        16 => v switch { 1 => 1u, 5 => 3u, _ => 2u },        // MAGFILTER: POINT, ANISOTROPIC, el resto LINEAR
        17 => v switch { 1 => 1u, 3 => 3u, _ => 2u },        // MINFILTER: POINT, ANISOTROPIC, LINEAR
        18 => v switch { 1 => 0u, 2 => 1u, 3 => 2u, _ => 0u }, // MIPFILTER: NONE, POINT, LINEAR
        _ => v,
    };
    private static uint FromSamplerValue(int t, uint v) => t switch
    {
        16 => v switch { 1 => 1u, 3 => 5u, _ => 2u },
        17 => v switch { 1 => 1u, 3 => 3u, _ => 2u },
        18 => v switch { 0 => 1u, 1 => 2u, _ => 3u },
        _ => v,
    };

    private static int Ts(int s) => s == 1 ? 256 : s >= 4 && s <= 6 ? 257 + (s - 4) : s;
    private static int Sampler(uint stage, int s, uint v) => ((delegate* unmanaged[Stdcall]<nint, uint, int, uint, int>)Layer.Fn(D, Device9.SetSamplerState))(D, stage, s, v);

    public override uint DefaultRenderState(int s)
    {
        if (s == 4) return 1; // TEXTUREPERSPECTIVE
        if (!Supported(s)) return 0;
        uint v = 0;
        ((delegate* unmanaged[Stdcall]<nint, int, uint*, int>)Layer.Fn(D, 58))(D, s, &v);
        return v;
    }

    public override uint DefaultTextureStageState(uint stage, int t)
    {
        uint v = 0;
        int s9 = SamplerOf(t);
        if (s9 > 0) { ((delegate* unmanaged[Stdcall]<nint, uint, int, uint*, int>)Layer.Fn(D, 68))(D, stage, s9, &v); return FromSamplerValue(t, v); }
        if (t <= 28) ((delegate* unmanaged[Stdcall]<nint, uint, int, uint*, int>)Layer.Fn(D, 66))(D, stage, t, &v);
        return v;
    }

    public override int SetRenderState(int s, uint v) => Supported(s) ? Device9.RS(s, v) : 0;

    public override int SetTextureStageState(uint stage, int t, uint v)
    {
        if (t == 12) { Sampler(stage, 1, v); return Sampler(stage, 2, v); } // ADDRESS: U y V
        int s9 = SamplerOf(t);
        if (s9 > 0) return Sampler(stage, s9, ToSamplerValue(t, v));
        if (t > 28) return 0;
        return ((delegate* unmanaged[Stdcall]<nint, uint, int, uint, int>)Layer.Fn(D, Device9.SetTextureStageState))(D, stage, t, v);
    }

    // ---- matrices, viewport, material, luces ----

    public override int SetTransform(int s, float* m) => ((delegate* unmanaged[Stdcall]<nint, int, float*, int>)Layer.Fn(D, Device9.SetTransform))(D, Ts(s), m);
    public override int GetTransform(int s, float* m) => ((delegate* unmanaged[Stdcall]<nint, int, float*, int>)Layer.Fn(D, Device9.GetTransform))(D, Ts(s), m);
    public override int MultiplyTransform(int s, float* m) => ((delegate* unmanaged[Stdcall]<nint, int, float*, int>)Layer.Fn(D, Device9.MultiplyTransform))(D, Ts(s), m);
    public override int SetViewport(nint vp)
    {
        if (vp != 0) for (int i = 0; i < 6; i++) _vp[i] = ((int*)vp)[i];
        return ((delegate* unmanaged[Stdcall]<nint, nint, int>)Layer.Fn(D, Device9.SetViewport))(D, vp);
    }
    public override int GetViewport(nint vp) => ((delegate* unmanaged[Stdcall]<nint, nint, int>)Layer.Fn(D, Device9.GetViewport))(D, vp);
    public override int SetMaterial(nint m) => ((delegate* unmanaged[Stdcall]<nint, nint, int>)Layer.Fn(D, Device9.SetMaterial))(D, m);
    public override int GetMaterial(nint m) => ((delegate* unmanaged[Stdcall]<nint, nint, int>)Layer.Fn(D, Device9.GetMaterial))(D, m);
    public override int SetLight(uint i, nint l) => ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Layer.Fn(D, Device9.SetLight))(D, i, l);
    public override int GetLight(uint i, nint l) => ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Layer.Fn(D, Device9.GetLight))(D, i, l);
    public override int LightEnable(uint i, int on) => ((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Layer.Fn(D, Device9.LightEnable))(D, i, on);
    public override int GetLightEnable(uint i, int* on) => ((delegate* unmanaged[Stdcall]<nint, uint, int*, int>)Layer.Fn(D, Device9.GetLightEnable))(D, i, on);

    // ---- escena y dibujo ----

    public override bool BeginFrame() => Device9.Recover();
    public override int BeginScene() => ((delegate* unmanaged[Stdcall]<nint, int>)Layer.Fn(D, Device9.BeginScene))(D);
    public override int EndScene() => ((delegate* unmanaged[Stdcall]<nint, int>)Layer.Fn(D, Device9.EndScene))(D);
    public override int Clear(uint count, nint rects, uint flags, uint color, float z, uint stencil)
        => ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, uint, float, uint, int>)Layer.Fn(D, Device9.Clear))(D, count, rects, flags, color, z, stencil);

    public override int SetTexture(uint stage, Surface7? s)
        => ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Layer.Fn(D, Device9.SetTexture))(D, stage, s?.Tex ?? 0);

    private static uint Prims(int type, uint n) => type switch
    {
        1 => n, 2 => n / 2, 3 => n > 0 ? n - 1 : 0, 4 => n / 3, 5 or 6 => n > 1 ? n - 2 : 0, _ => 0,
    };

    private void Fvf(uint fvf)
    {
        if (fvf == _fvf) return;
        _fvf = fvf;
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Layer.Fn(D, Device9.SetFVF))(D, fvf);
    }

    // Último viewport del juego. Lo que el panorámico saca fuera del lienzo de la interfaz en la selección de personaje
    // (los carteles de los personajes de los lados) se cortaba en su borde. Para lo 2D se pone un momento el viewport de toda la imagen (en D3D9 la posición de lo 2D
    // no depende del viewport: solo cambia el recorte) y luego el del juego.
    private readonly int[] _vp = new int[6];
    private readonly int[] _full = new int[6];

    private bool Unclip2D(uint fvf)
    {
        // Solo en los menús y con el viewport del lienzo entero (el que pone UiCanvas, no el juego): el juego sí usa el
        // recorte con sus propios viewports (listas, mapas) y en la partida (mundo, mapa del tabulador).
        if ((fvf & 0xE) != 0x4 || !InMenus()) return false;
        if (_vp[0] != UiCanvas.Left || _vp[1] != 0 || _vp[2] != 1024 || _vp[3] != Height) return false;
        if (_vp[0] <= 0 && _vp[1] <= 0 && _vp[0] + _vp[2] >= Width && _vp[1] + _vp[3] >= Height) return false;
        _full[0] = 0; _full[1] = 0; _full[2] = Width; _full[3] = Height; _full[4] = _vp[4]; _full[5] = _vp[5];
        fixed (int* f = _full) ((delegate* unmanaged[Stdcall]<nint, int*, int>)Layer.Fn(D, Device9.SetViewport))(D, f);
        return true;
    }
    private static bool InMenus()
    {
        nint mgr = *(nint*)0x17EEBBC;
        if (mgr == 0) return false;
        uint f = *(uint*)(mgr + 8);
        return (f & 0x01) != 0 && (f & 0x04) == 0;
    }
    private void Reclip() { fixed (int* v = _vp) ((delegate* unmanaged[Stdcall]<nint, int*, int>)Layer.Fn(D, Device9.SetViewport))(D, v); }

    /// <summary>Con MSAA, lo 2D (vértices ya transformados) sin antialiasing: cada píxel entero o nada.</summary>
    private static bool No2DAntialias(uint fvf)
    {
        if (Device9.MsaaTarget == 0 || (fvf & 0xE) != 0x4) return false;
        Device9.RS(161, 0);
        return true;
    }

    public override int Draw(int type, uint fvf, nint verts, uint vcount, int stride)
    {
        uint p = Prims(type, vcount);
        if (p == 0 || verts == 0) return 0;
        Fvf(fvf);
        bool c = No2DAntialias(fvf), u = Unclip2D(fvf);
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, uint, nint, uint, int>)Layer.Fn(D, Device9.DrawPrimitiveUP))(D, type, p, verts, (uint)stride);
        if (c) Device9.RS(161, 1);
        if (u) Reclip();
        return hr;
    }

    public override int DrawIndexed(int type, uint fvf, nint verts, uint vcount, ushort* idx, uint icount, int stride)
    {
        uint p = Prims(type, icount);
        if (p == 0 || verts == 0 || idx == null) return 0;
        Fvf(fvf);
        bool c = No2DAntialias(fvf), u = Unclip2D(fvf);
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, uint, uint, uint, ushort*, int, nint, uint, int>)Layer.Fn(D, Device9.DrawIndexedPrimitiveUP))(D, type, 0, vcount, p, idx, 101 /* INDEX16 */, verts, (uint)stride);
        if (c) Device9.RS(161, 1);
        if (u) Reclip();
        return hr;
    }

    // ---- superposición ----

    public override nint CreateRgbaTexture(int w, int h, uint* pixels)
    {
        nint tex = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, int, int, nint*, nint, int>)Layer.Fn(D, Device9.CreateTexture))(D, (uint)w, (uint)h, 1, 0, 21, 1, &tex, 0);
        if (hr != 0) { Log.Warn($"D9: textura de la superposición -> 0x{hr:X8}"); return 0; }
        int* lr = stackalloc int[2];
        if (((delegate* unmanaged[Stdcall]<nint, uint, int*, int*, uint, int>)Layer.Fn(tex, 19))(tex, 0, lr, null, 0) != 0) { Layer.ReleaseCom(tex); return 0; }
        byte* dst = (byte*)*(nint*)(lr + 1);
        for (int y = 0; y < h; y++) Buffer.MemoryCopy(pixels + y * w, dst + y * lr[0], w * 4, w * 4);
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Layer.Fn(tex, 20))(tex, 0);
        return tex;
    }

    public override void DrawOverlay(nint tex, float* verts, int quads)
    {
        if (D == 0 || Device9.StateBlock == 0 || quads <= 0) return;
        nint dev = D, sb = Device9.StateBlock;
        ((delegate* unmanaged[Stdcall]<nint, int>)Layer.Fn(sb, 4))(sb); // Capture
        Device9.SetTarget(Device9.BackBuffer, 0);
        Device9.RS(7, 0); Device9.RS(15, 0); Device9.RS(27, 1); Device9.RS(19, 5); Device9.RS(20, 6); Device9.RS(22, 1);
        Device9.RS(137, 0); Device9.RS(28, 0); Device9.RS(52, 0); Device9.RS(174, 0); Device9.RS(168, 0xF); Device9.RS(161, 0);
        var tss = (delegate* unmanaged[Stdcall]<nint, uint, int, uint, int>)Layer.Fn(dev, Device9.SetTextureStageState);
        tss(dev, 0, 1, 4); tss(dev, 0, 2, 2); tss(dev, 0, 3, 0); tss(dev, 0, 4, 4); tss(dev, 0, 5, 2); tss(dev, 0, 6, 0);
        tss(dev, 0, 11, 0); tss(dev, 0, 24, 0); tss(dev, 1, 1, 1); tss(dev, 1, 4, 1);
        var ss = (delegate* unmanaged[Stdcall]<nint, uint, int, uint, int>)Layer.Fn(dev, Device9.SetSamplerState);
        ss(dev, 0, 5, 2); ss(dev, 0, 6, 2); ss(dev, 0, 7, 0); ss(dev, 0, 1, 3); ss(dev, 0, 2, 3);
        ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Layer.Fn(dev, Device9.SetTexture))(dev, 0, tex);
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Layer.Fn(dev, 92))(dev, 0);
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Layer.Fn(dev, 107))(dev, 0);
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Layer.Fn(dev, Device9.SetFVF))(dev, 0x144); // XYZRHW | DIFFUSE | TEX1
        ((delegate* unmanaged[Stdcall]<nint, int>)Layer.Fn(dev, Device9.BeginScene))(dev);
        var draw = (delegate* unmanaged[Stdcall]<nint, int, uint, float*, uint, int>)Layer.Fn(dev, Device9.DrawPrimitiveUP);
        for (int q = 0; q < quads; q++) draw(dev, 5, 2, verts + q * 28, 28);
        ((delegate* unmanaged[Stdcall]<nint, int>)Layer.Fn(dev, Device9.EndScene))(dev);
        Device9.RestoreTarget();
        ((delegate* unmanaged[Stdcall]<nint, int>)Layer.Fn(sb, 5))(sb); // Apply
        _fvf = 0xFFFFFFFF;
    }
}
