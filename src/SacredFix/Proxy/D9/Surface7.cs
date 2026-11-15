using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Proxy.D9;

/// <summary>
/// IDirectDrawSurface7 sobre Direct3D 9. Tipos: primaria y back buffer (el back buffer del dispositivo; la primaria
/// se trata igual para Lock / GetDC, y al soltarla se presenta), Z-buffer (el del dispositivo), textura (textura
/// gestionada de un nivel) y superficie en memoria del sistema (superficie fuera de pantalla).
/// DDSURFACEDESC2 (0x7C bytes): dwFlags +4, alto +8, ancho +0xC, pitch +0x10, back buffers +0x14, lpSurface +0x24,
/// formato de píxel +0x48 (0x20 bytes), caps +0x68 (DDSCAPS2, 0x10 bytes).
/// </summary>
internal sealed unsafe partial class Surface7 : IDisposable
{
    public enum Kind { Primary, Back, Z, Texture, SysMem }

    public Kind Type;
    public int W, H, Bpp;
    public int Fmt;                // D3DFORMAT
    public readonly uint[] Pf = new uint[8];   // DDPIXELFORMAT
    public readonly uint[] Caps = new uint[4]; // DDSCAPS2
    public nint Tex, Surf;       // recursos del motor de dibujo (en D3D9: IDirect3DTexture9 / IDirect3DSurface9)
    public nint Cpu;             // en D3D11: la copia en memoria con el formato del juego (la que ve Lock)
    public nint Native;
    public nint DD;                // nuestro IDirectDraw7
    public nint Back, ZBuf;        // superficies enganchadas (nativas)
    public uint Uniq = 1;
    private bool _locked;

    private static readonly string[] Names =
    {
        "QueryInterface", "AddRef", "Release", "AddAttachedSurface", "AddOverlayDirtyRect", "Blt", "BltBatch", "BltFast",
        "DeleteAttachedSurface", "EnumAttachedSurfaces", "EnumOverlayZOrders", "Flip", "GetAttachedSurface",
        "GetBltStatus", "GetCaps", "GetClipper", "GetColorKey", "GetDC", "GetFlipStatus", "GetOverlayPosition",
        "GetPalette", "GetPixelFormat", "GetSurfaceDesc", "Initialize", "IsLost", "Lock", "ReleaseDC", "Restore",
        "SetClipper", "SetColorKey", "SetOverlayPosition", "SetPalette", "Unlock", "UpdateOverlay",
        "UpdateOverlayDisplay", "UpdateOverlayZOrder", "GetDDInterface", "PageLock", "PageUnlock", "SetSurfaceDesc",
        "SetPrivateData", "GetPrivateData", "FreePrivateData", "GetUniquenessValue", "ChangeUniquenessValue",
        "SetPriority", "GetPriority", "SetLOD", "GetLOD",
    };
    private static readonly int[] Args =
    {
        2, 0, 0, 1, 1, 5, 3, 5, 2, 2, 3, 2, 2, 1, 1, 1, 2, 1, 1, 2, 1, 1, 1, 2, 0, 4, 1, 0, 1, 2, 2, 1, 1, 5, 1, 2,
        1, 1, 1, 2, 4, 3, 1, 1, 0, 1, 1, 1, 1,
    };

    // (después de Names y Args: los campos estáticos se inicializan en orden de aparición)
    public static readonly Layer.VTable VT = Build();

    private static Layer.VTable Build()
    {
        var v = new Layer.VTable("Surface7", Names, Args);
        v.Set(0, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&QueryInterface);
        v.Set(3, (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&AddAttachedSurface);
        v.Set(5, (nint)(delegate* unmanaged[Stdcall]<nint, int*, nint, int*, uint, byte*, int>)&Blt);
        v.Set(9, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&EnumAttachedSurfaces);
        v.Set(11, (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, int>)&Flip);
        v.Set(12, (nint)(delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)&GetAttachedSurface);
        v.Set(13, (nint)(delegate* unmanaged[Stdcall]<nint, uint, int>)&Ok1);    // GetBltStatus
        v.Set(14, (nint)(delegate* unmanaged[Stdcall]<nint, uint*, int>)&GetCaps);
        v.Set(16, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, int>)&Ok2); // GetColorKey (sin color key)
        v.Set(17, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetDC);
        v.Set(18, (nint)(delegate* unmanaged[Stdcall]<nint, uint, int>)&Ok1);    // GetFlipStatus
        v.Set(21, (nint)(delegate* unmanaged[Stdcall]<nint, uint*, int>)&GetPixelFormat);
        v.Set(20, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetPalette);
        v.Set(22, (nint)(delegate* unmanaged[Stdcall]<nint, byte*, int>)&GetSurfaceDesc);
        v.Set(24, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Ok0);          // IsLost
        v.Set(25, (nint)(delegate* unmanaged[Stdcall]<nint, int*, byte*, uint, nint, int>)&Lock);
        v.Set(26, (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&ReleaseDC);
        v.Set(27, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Ok0);          // Restore
        v.Set(28, (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&Ok1p);   // SetClipper
        v.Set(29, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, int>)&Ok2); // SetColorKey (no lo usa el juego)
        v.Set(31, (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&Ok1p);   // SetPalette
        v.Set(32, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&Unlock);
        v.Set(36, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetDDInterface);
        v.Set(37, (nint)(delegate* unmanaged[Stdcall]<nint, uint, int>)&Ok1);    // PageLock
        v.Set(38, (nint)(delegate* unmanaged[Stdcall]<nint, uint, int>)&Ok1);    // PageUnlock
        v.Set(43, (nint)(delegate* unmanaged[Stdcall]<nint, uint*, int>)&GetUniquenessValue);
        v.Set(44, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&ChangeUniquenessValue);
        v.Set(45, (nint)(delegate* unmanaged[Stdcall]<nint, uint, int>)&Ok1);    // SetPriority
        v.Set(47, (nint)(delegate* unmanaged[Stdcall]<nint, uint, int>)&Ok1);    // SetLOD
        return v;
    }

    public nint Create(nint dd)
    {
        DD = dd;
        Native = Layer.NewObject(VT.Ptr, this);
        return Native;
    }

    public void Dispose()
    {
        if (Type == Kind.Texture || Type == Kind.SysMem) Backend.Current.Release(this);
    }

    // ---- formatos ----

    /// <summary>DDPIXELFORMAT -> D3DFORMAT (y bytes por píxel). Sin formato: el de la pantalla (X8R8G8B8).</summary>
    public static int FormatFrom(uint* pf, out int bpp)
    {
        bpp = 4;
        if (pf == null || (pf[1] & 0x40) == 0) return 22;
        uint bits = pf[3], r = pf[4], a = (pf[1] & 1) != 0 ? pf[7] : 0;
        if (bits == 32) return a != 0 ? 21 : 22;            // A8R8G8B8 / X8R8G8B8
        if (bits == 16)
        {
            bpp = 2;
            if (a == 0xF000) return 26;                      // A4R4G4B4
            if (a == 0x8000) return 25;                      // A1R5G5B5
            if (r == 0xF800) return 23;                      // R5G6B5
            return 24;                                       // X1R5G5B5
        }
        if (bits == 24) { bpp = 3; return 20; }              // R8G8B8
        return 22;
    }

    public static void PixelFormatOf(int fmt, uint* pf)
    {
        for (int i = 0; i < 8; i++) pf[i] = 0;
        pf[0] = 32; pf[1] = 0x40;
        switch (fmt)
        {
            case 21: pf[1] |= 1; pf[3] = 32; pf[4] = 0xFF0000; pf[5] = 0xFF00; pf[6] = 0xFF; pf[7] = 0xFF000000; break;
            case 26: pf[1] |= 1; pf[3] = 16; pf[4] = 0xF00; pf[5] = 0xF0; pf[6] = 0xF; pf[7] = 0xF000; break;
            case 25: pf[1] |= 1; pf[3] = 16; pf[4] = 0x7C00; pf[5] = 0x3E0; pf[6] = 0x1F; pf[7] = 0x8000; break;
            case 23: pf[3] = 16; pf[4] = 0xF800; pf[5] = 0x7E0; pf[6] = 0x1F; break;
            case 24: pf[3] = 16; pf[4] = 0x7C00; pf[5] = 0x3E0; pf[6] = 0x1F; break;
            case 20: pf[3] = 24; pf[4] = 0xFF0000; pf[5] = 0xFF00; pf[6] = 0xFF; break;
            case 75: pf[1] = 0x4400; pf[3] = 32; pf[4] = 8; pf[5] = 0xFFFFFF; pf[6] = 0xFF000000; break; // D24S8
            default: pf[3] = 32; pf[4] = 0xFF0000; pf[5] = 0xFF00; pf[6] = 0xFF; break;
        }
    }

    public void SetFormat(int fmt)
    {
        Fmt = fmt;
        Bpp = fmt == 26 || fmt == 25 || fmt == 23 || fmt == 24 ? 2 : fmt == 20 ? 3 : 4;
        fixed (uint* p = Pf) PixelFormatOf(fmt, p);
    }

    /// <summary>Primaria o back buffer (la «pantalla» del juego).</summary>
    public bool Screen => Type == Kind.Primary || Type == Kind.Back;

    // ---- métodos ----

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterface(nint self, nint riid, nint* ppv)
    {
        if (ppv == null) return Layer.E_INVALIDARG;
        if (Layer.GuidIs(riid, Layer.IID_IDirectDrawSurface7) || Layer.GuidIs(riid, IID_Surface4) || Layer.GuidIs(riid, Layer.IID_IUnknown))
        {
            *ppv = self; Layer.AddRefCom(self); return 0;
        }
        // interfaces antiguas (IDirectDrawSurface v1-v3: DirectShow, vídeos): la cara v1
        if (IsOld(riid))
        {
            nint f = Layer.Get<Surface7>(self).V1();
            *ppv = f; Layer.AddRefCom(f); return 0;
        }
        *ppv = 0;
        Log.Warn($"D9: Surface7::QueryInterface({*(Guid*)riid}) no soportado");
        return Layer.E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int AddAttachedSurface(nint self, nint other)
    {
        var s = Layer.Get<Surface7>(self);
        var o = Layer.TryGet<Surface7>(other);
        if (o?.Type == Kind.Z) s.ZBuf = other;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int EnumAttachedSurfaces(nint self, nint ctx, nint cb)
    {
        var s = Layer.Get<Surface7>(self);
        foreach (nint a in new[] { s.Back, s.ZBuf })
        {
            if (a == 0) continue;
            var o = Layer.Get<Surface7>(a);
            byte* d = stackalloc byte[0x7C];
            o.Describe(d);
            Layer.AddRefCom(a);
            if (((delegate* unmanaged[Stdcall]<nint, byte*, nint, int>)cb)(a, d, ctx) == 0) break;
        }
        return 0;
    }

    /// <summary>Solo rellenos de color (DDBLT_COLORFILL 0x400; DDBLTFX.dwFillColor en +0x50), que es lo que usa el juego.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Blt(nint self, int* dst, nint src, int* srcRect, uint flags, byte* fx)
    {
        var s = Layer.Get<Surface7>(self);
        try
        {
            if ((flags & 0x400) == 0 || fx == null)
            {
                var o = Layer.TryGet<Surface7>(src);
                if (o == null) { NotImplOnce($"Blt sin origen ni relleno (flags 0x{flags:X})"); return Layer.E_NOTIMPL; }
                return CopyBlt(s, dst, o, srcRect);
            }
            uint color = *(uint*)(fx + 0x50);
            if (s.Screen) return Backend.Current.FillScreen(dst, color);
            // texturas y superficies del sistema: a mano
            int x0 = dst != null ? dst[0] : 0, y0 = dst != null ? dst[1] : 0, x1 = dst != null ? dst[2] : s.W, y1 = dst != null ? dst[3] : s.H;
            if (!s.LockRaw(null, 0, out byte* bits, out int pitch)) return Layer.E_FAIL;
            for (int y = Math.Max(0, y0); y < Math.Min(s.H, y1); y++)
            {
                byte* row = bits + y * pitch;
                for (int x = Math.Max(0, x0); x < Math.Min(s.W, x1); x++)
                {
                    if (s.Bpp == 4) ((uint*)row)[x] = color;
                    else if (s.Bpp == 2) ((ushort*)row)[x] = (ushort)color;
                    else { row[x * 3] = (byte)color; row[x * 3 + 1] = (byte)(color >> 8); row[x * 3 + 2] = (byte)(color >> 16); }
                }
            }
            s.UnlockRaw();
            return 0;
        }
        catch (Exception ex) { Log.Error("D9: Blt: " + ex.Message); return Layer.E_FAIL; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Flip(nint self, nint target, uint flags)
    {
        PresentFrame();
        return 0;
    }

    /// <summary>Presenta lo dibujado (resolve = fotograma normal del juego; false = tras GDI o Lock en la pantalla).</summary>
    public static void PresentFrame(bool resolve = true)
    {
        // Juego inactivo (perdió el foco: marca 0x20000 en [0x182EE38], 0x811D0C / 0x812EB8) y ventana detrás: no dibuja la
        // escena, así que presentar dejaría la ventana en negro. Sin presentar, Windows sigue mostrando el último fotograma.
        if (InactiveBehind()) { System.Threading.Thread.Sleep(15); return; }
        if (resolve) FrameLimit.Wait();
        Backend.Current.Present(resolve);
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    private static bool InactiveBehind()
    {
        if (Layer.WindowStyle == 0 || (*(int*)0x182EE38 & 0x20000) == 0) return false;
        nint h = Display.Hwnd;
        return h != 0 && GetForegroundWindow() != h;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetAttachedSurface(nint self, uint* caps, nint* outp)
    {
        var s = Layer.Get<Surface7>(self);
        nint r = 0;
        if (caps != null)
        {
            if ((caps[0] & 0x20000) != 0) r = s.ZBuf != 0 ? s.ZBuf : Layer.TryGet<Surface7>(s.Back)?.ZBuf ?? 0; // ZBUFFER
            else if ((caps[0] & 0x4) != 0) r = s.Back;                                                                // BACKBUFFER
        }
        if (r == 0) { *outp = 0; return Layer.DDERR_NOTFOUND; }
        Layer.AddRefCom(r);
        *outp = r;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetCaps(nint self, uint* caps)
    {
        var s = Layer.Get<Surface7>(self);
        for (int i = 0; i < 4; i++) caps[i] = s.Caps[i];
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetDC(nint self, nint* hdc) => Backend.Current.GetDC(Layer.Get<Surface7>(self), hdc);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int ReleaseDC(nint self, nint hdc)
    {
        var s = Layer.Get<Surface7>(self);
        int hr = Backend.Current.ReleaseDC(s, hdc);
        if (s.Type == Kind.Primary) PresentFrame(false); // lo dibujado en la primaria (pantalla de carga) se ve ya
        return hr;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetPixelFormat(nint self, uint* pf)
    {
        var s = Layer.Get<Surface7>(self);
        for (int i = 0; i < 8; i++) pf[i] = s.Pf[i];
        return 0;
    }

    public void Describe(byte* d)
    {
        new Span<byte>(d, 0x7C).Clear();
        *(uint*)d = 0x7C;
        *(uint*)(d + 4) = 0x1 | 0x2 | 0x4 | 0x8 | 0x1000; // CAPS HEIGHT WIDTH PITCH PIXELFORMAT
        *(int*)(d + 8) = H; *(int*)(d + 0xC) = W; *(int*)(d + 0x10) = W * Bpp;
        if (Type == Kind.Primary) { *(uint*)(d + 4) |= 0x20; *(int*)(d + 0x14) = 1; }
        for (int i = 0; i < 8; i++) ((uint*)(d + 0x48))[i] = Pf[i];
        for (int i = 0; i < 4; i++) ((uint*)(d + 0x68))[i] = Caps[i];
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetSurfaceDesc(nint self, byte* d)
    {
        if (d == null) return Layer.E_INVALIDARG;
        Layer.Get<Surface7>(self).Describe(d);
        return 0;
    }

    public bool LockRaw(int* rect, uint flags, out byte* bits, out int pitch)
    {
        if (!Backend.Current.Lock(this, rect, flags, out bits, out pitch)) return false;
        _locked = true;
        return true;
    }

    public void UnlockRaw()
    {
        if (!_locked) return;
        _locked = false;
        Backend.Current.Unlock(this);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Lock(nint self, int* rect, byte* d, uint flags, nint ev)
    {
        var s = Layer.Get<Surface7>(self);
        if (d == null) return Layer.E_INVALIDARG;
        if (!s.LockRaw(rect, flags, out byte* bits, out int pitch)) return Layer.E_FAIL;
        s.Describe(d);
        *(uint*)(d + 4) |= 0x800; // LPSURFACE
        *(int*)(d + 0x10) = pitch;
        *(nint*)(d + 0x24) = (nint)bits;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Unlock(nint self, int* rect)
    {
        var s = Layer.Get<Surface7>(self);
        s.UnlockRaw();
        s.Uniq++;
        if (s.Type == Kind.Primary) PresentFrame(false);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetDDInterface(nint self, nint* outp)
    {
        var s = Layer.Get<Surface7>(self);
        Layer.AddRefCom(s.DD);
        *outp = s.DD;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetUniquenessValue(nint self, uint* v) { *v = Layer.Get<Surface7>(self).Uniq; return 0; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int ChangeUniquenessValue(nint self) { Layer.Get<Surface7>(self).Uniq++; return 0; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Ok0(nint self) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Ok1(nint self, uint a) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Ok1p(nint self, nint a) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Ok2(nint self, uint a, nint b) => 0;

    private static readonly HashSet<string> _once = new();
    public static void NotImplOnce(string what) { lock (_once) if (_once.Add(what)) Log.Warn($"D9: no implementado: {what}"); }
}
