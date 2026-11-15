using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Proxy.D9;

/// <summary>
/// IDirectDraw7 propio. Lleva dentro un IDirectDraw7 real de Windows (+0xC) solo para las consultas (modos de
/// pantalla, capacidades, memoria, identificador): esas casillas reenvían a él. Lo demás (nivel cooperativo, modo,
/// superficies) es nuestro; el dispositivo Direct3D 9 se crea al crear la superficie primaria.
/// </summary>
internal sealed unsafe partial class DirectDraw7
{
    public nint Native, Real;
    public nint D3D7;      // nuestro IDirect3D7 (se crea al pedirlo)
    public nint Primary;

    private static readonly string[] Names =
    {
        "QueryInterface", "AddRef", "Release", "Compact", "CreateClipper", "CreatePalette", "CreateSurface",
        "DuplicateSurface", "EnumDisplayModes", "EnumSurfaces", "FlipToGDISurface", "GetCaps", "GetDisplayMode",
        "GetFourCCCodes", "GetGDISurface", "GetMonitorFrequency", "GetScanLine", "GetVerticalBlankStatus",
        "Initialize", "RestoreDisplayMode", "SetCooperativeLevel", "SetDisplayMode", "WaitForVerticalBlank",
        "GetAvailableVidMem", "GetSurfaceFromDC", "RestoreAllSurfaces", "TestCooperativeLevel", "GetDeviceIdentifier",
        "StartModeTest", "EvaluateMode",
    };
    private static readonly int[] Args = { 2, 0, 0, 0, 3, 4, 3, 2, 4, 4, 0, 2, 1, 2, 1, 1, 1, 1, 1, 0, 2, 5, 2, 3, 2, 0, 0, 2, 3, 2 };

    public static readonly Layer.VTable VT = Build();

    private static Layer.VTable Build()
    {
        var v = new Layer.VTable("DirectDraw7", Names, Args);
        v.Set(0, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&QueryInterface);
        v.Set(3, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Ok0);           // Compact
        v.Set(6, (nint)(delegate* unmanaged[Stdcall]<nint, byte*, nint*, nint, int>)&CreateSurface);
        foreach (int i in new[] { 4, 11, 13, 15, 16, 23, 27 }) v.Forward(i);  // CreateClipper, caps, ...
        v.Set(8, (nint)(delegate* unmanaged[Stdcall]<nint, uint, byte*, nint, nint, int>)&EnumDisplayModes);
        v.Set(10, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Ok0);          // FlipToGDISurface
        v.Set(12, (nint)(delegate* unmanaged[Stdcall]<nint, byte*, int>)&GetDisplayMode);
        v.Set(14, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetGDISurface);
        v.Set(17, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&GetVerticalBlankStatus);
        v.Set(19, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Ok0);          // RestoreDisplayMode
        v.Set(20, (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, int>)&SetCooperativeLevel);
        v.Set(21, (nint)(delegate* unmanaged[Stdcall]<nint, int, int, int, int, uint, int>)&SetDisplayMode);
        v.Set(22, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, int>)&WaitForVerticalBlank);
        v.Set(25, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Ok0);          // RestoreAllSurfaces
        v.Set(26, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Ok0);          // TestCooperativeLevel
        return v;
    }

    public static nint Create(nint real)
    {
        var o = new DirectDraw7 { Real = real };
        o.Native = Layer.NewObject(VT.Ptr, o, real);
        return o.Native;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterface(nint self, nint riid, nint* ppv)
    {
        if (ppv == null) return Layer.E_INVALIDARG;
        var dd = Layer.Get<DirectDraw7>(self);
        if (Layer.GuidIs(riid, Layer.IID_IDirect3D7))
        {
            if (dd.D3D7 == 0) dd.D3D7 = Direct3D7.Create(self);
            if (dd.D3D7 == 0) { *ppv = 0; return Layer.E_NOINTERFACE; }
            Layer.AddRefCom(dd.D3D7);
            *ppv = dd.D3D7;
            return 0;
        }
        if (Layer.GuidIs(riid, Layer.IID_IDirectDraw7) || Layer.GuidIs(riid, Layer.IID_IUnknown) || Layer.GuidIs(riid, IID_DD4))
        {
            Layer.AddRefCom(self); *ppv = self; return 0;
        }
        // interfaces antiguas (las pide DirectShow para los vídeos): la cara v1
        if (Layer.GuidIs(riid, IID_DD1) || Layer.GuidIs(riid, IID_DD2))
        {
            nint f = dd.V1();
            Layer.AddRefCom(f); *ppv = f; return 0;
        }
        Log.Warn($"D9: DirectDraw7::QueryInterface({*(Guid*)riid}) no soportado");
        *ppv = 0;
        return Layer.E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetCooperativeLevel(nint self, nint hwnd, uint flags)
    {
        if (hwnd != 0) Display.Hwnd = hwnd;
        Display.Fullscreen = (flags & 1) != 0 && Layer.WindowStyle == 0;
        Log.Info($"D9: SetCooperativeLevel(ventana 0x{hwnd:X}, 0x{flags:X})");
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetDisplayMode(nint self, int w, int h, int bpp, int hz, uint flags)
    {
        Display.Width = w; Display.Height = h;
        Log.Info($"D9: SetDisplayMode({w}x{h}x{bpp})");
        return 0;
    }

    // ---- modos de vídeo ----
    // La capa no cambia el modo de vídeo de verdad (sin bordes / ventana / exclusiva al tamaño del escritorio), así que el
    // juego puede pedir cualquier tamaño. Pero find_mode solo encuentra los que le da EnumDisplayModes, y con
    // Width=auto (por ejemplo 1229x768 en un monitor 16:10) ese tamaño no existe en la tarjeta: se añade a la lista
    // de los reales, a 32 y 16 bits, si no está ya.
    private static delegate* unmanaged[Stdcall]<byte*, nint, int> _enumCb;
    private static nint _enumCtx;
    private static bool _seen32, _seen16, _enumStop;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int EnumDisplayModes(nint self, uint flags, byte* desc, nint ctx, nint cb)
    {
        if (cb == 0) return Layer.E_INVALIDARG;
        _enumCb = (delegate* unmanaged[Stdcall]<byte*, nint, int>)cb; _enumCtx = ctx;
        _seen32 = _seen16 = _enumStop = false;
        nint real = Layer.Get<DirectDraw7>(self).Real;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, byte*, nint, nint, int>)Layer.Fn(real, 8))(real, flags, desc, 0,
            (nint)(delegate* unmanaged[Stdcall]<byte*, nint, int>)&EnumModeHook);
        if (_enumStop) return hr;
        byte* d = stackalloc byte[0x7C];
        foreach (int bpp in new[] { 32, 16 })
        {
            if (bpp == 32 ? _seen32 : _seen16) continue;
            new Span<byte>(d, 0x7C).Clear();
            *(uint*)d = 0x7C;
            *(uint*)(d + 4) = 0x2 | 0x4 | 0x8 | 0x1000; // HEIGHT | WIDTH | PITCH | PIXELFORMAT
            *(int*)(d + 8) = Layer.ModeH; *(int*)(d + 0xC) = Layer.ModeW; *(int*)(d + 0x10) = Layer.ModeW * bpp / 8;
            Surface7.PixelFormatOf(bpp == 32 ? 22 : 23, (uint*)(d + 0x48));
            Log.Info($"D9: modo de vídeo {Layer.ModeW}x{Layer.ModeH}x{bpp} añadido a la lista (el monitor no lo tiene)");
            if (_enumCb(d, _enumCtx) == 0) break;
        }
        return hr;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int EnumModeHook(byte* d, nint ctx)
    {
        if (*(int*)(d + 0xC) == Layer.ModeW && *(int*)(d + 8) == Layer.ModeH)
        {
            uint bits = *(uint*)(d + 0x48 + 0xC);
            if (bits == 32) _seen32 = true; else if (bits == 16) _seen16 = true;
        }
        int r = _enumCb(d, _enumCtx);
        if (r == 0) _enumStop = true;
        return r;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetDisplayMode(nint self, byte* d)
    {
        if (d == null) return Layer.E_INVALIDARG;
        new Span<byte>(d, 0x7C).Clear();
        *(uint*)d = 0x7C;
        *(uint*)(d + 4) = 0x2 | 0x4 | 0x8 | 0x1000;
        *(int*)(d + 8) = Display.Height; *(int*)(d + 0xC) = Display.Width; *(int*)(d + 0x10) = Display.Width * 4;
        Surface7.PixelFormatOf(22, (uint*)(d + 0x48));
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetGDISurface(nint self, nint* outp)
    {
        var dd = Layer.Get<DirectDraw7>(self);
        if (dd.Primary == 0) { *outp = 0; return Layer.DDERR_NOTFOUND; }
        Layer.AddRefCom(dd.Primary); *outp = dd.Primary; return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetVerticalBlankStatus(nint self, int* b) { if (b != null) *b = 1; return 0; }

    // La espera del retrazado vertical la hace Present (D3DPRESENT_INTERVAL_ONE)
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int WaitForVerticalBlank(nint self, uint flags, nint ev) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Ok0(nint self) => 0;

    private static int _surfLogs;

    /// <summary>
    /// caps: PRIMARYSURFACE 0x200, BACKBUFFER 0x4, TEXTURE 0x1000, ZBUFFER 0x20000, SYSTEMMEMORY 0x800,
    /// OFFSCREENPLAIN 0x40. dwFlags: CAPS 1, HEIGHT 2, WIDTH 4, BACKBUFFERCOUNT 0x20, PIXELFORMAT 0x1000.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int CreateSurface(nint self, byte* d, nint* outp, nint outer) => CreateSurfaceCore(self, d, outp);

    public static int CreateSurfaceCore(nint self, byte* d, nint* outp)
    {
        try
        {
            if (d == null || outp == null) return Layer.E_INVALIDARG;
            *outp = 0;
            var dd = Layer.Get<DirectDraw7>(self);
            uint flags = *(uint*)(d + 4), caps = *(uint*)(d + 0x68);
            int w = (flags & 4) != 0 ? *(int*)(d + 0xC) : Display.Width, h = (flags & 2) != 0 ? *(int*)(d + 8) : Display.Height;
            uint* pf = (flags & 0x1000) != 0 ? (uint*)(d + 0x48) : null;
            var s = new Surface7 { W = w, H = h };
            for (int i = 0; i < 4; i++) s.Caps[i] = ((uint*)(d + 0x68))[i];

            if ((caps & 0x200) != 0) // primaria (con su back buffer)
            {
                if (!Backend.Current.Create()) return Layer.E_FAIL;
                s.Type = Surface7.Kind.Primary; s.W = Display.Width; s.H = Display.Height; s.SetFormat(22);
                s.Caps[0] |= 0x20; // FRONTBUFFER
                nint p = s.Create(self);
                var b = new Surface7 { Type = Surface7.Kind.Back, W = s.W, H = s.H };
                b.SetFormat(22);
                b.Caps[0] = 0x4 | 0x2000 | 0x10 | 0x4000; // BACKBUFFER 3DDEVICE FLIP VIDEOMEMORY
                s.Back = b.Create(self);
                dd.Primary = p;
                *outp = p;
            }
            else if ((caps & 0x20000) != 0) // Z-buffer: el del dispositivo (D24S8)
            {
                s.Type = Surface7.Kind.Z; s.SetFormat(75);
                *outp = s.Create(self);
            }
            else if ((caps & 0x1000) != 0) // textura gestionada, un nivel
            {
                s.Type = Surface7.Kind.Texture;
                s.SetFormat(Surface7.FormatFrom(pf, out _));
                if (!Backend.Current.CreateTexture(s)) return Layer.E_FAIL;
                *outp = s.Create(self);
            }
            else // superficie fuera de pantalla: en memoria del sistema
            {
                s.Type = Surface7.Kind.SysMem;
                s.SetFormat(Surface7.FormatFrom(pf, out _));
                if (!Backend.Current.CreateSysMem(s)) return Layer.E_FAIL;
                *outp = s.Create(self);
            }
            if (_surfLogs++ < 30)
                Log.Info($"D9: CreateSurface {s.Type} {s.W}x{s.H} formato {s.Fmt} (caps 0x{caps:X})");
            return 0;
        }
        catch (Exception ex) { Log.Error("D9: CreateSurface: " + ex.Message); return Layer.E_FAIL; }
    }
}
