using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Cara antigua (IDirectDraw v1 y v2) de nuestro DirectDraw, para DirectShow (amstream.dll, vídeos). Mismo orden
/// que IDirectDraw7 en las 24 primeras casillas, pero con DDSURFACEDESC (0x6C) y SetDisplayMode de 3 argumentos.
/// Las consultas reenvían a un IDirectDraw v1 real (+0xC); CreateSurface pasa por el nuestro y devuelve la cara v1.
/// </summary>
internal sealed unsafe partial class DirectDraw7
{
    public nint Native1;

    public const string IID_DD1 = "6C14DB80-A733-11CE-A521-0020AF0BE560";
    public const string IID_DD2 = "B3A6F3E0-2B43-11CF-A2DE-00AA00B93356";
    public const string IID_DD4 = "9C59509A-39BD-11D1-8C4A-00C04FD930C5";

    private static Layer.VTable? _vt1;
    private static Layer.VTable VT1 => _vt1 ??= BuildV1();

    private static readonly int[] Args1 = { 2, 0, 0, 0, 3, 4, 3, 2, 4, 4, 0, 2, 1, 2, 1, 1, 1, 1, 1, 0, 2, 3, 2, 3 };

    private static Layer.VTable BuildV1()
    {
        var v = new Layer.VTable("DirectDraw1", Names[..24], Args1);
        v.Set(0, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&QueryInterface1);
        foreach (int i in new[] { 4, 8, 11, 13, 15, 16, 23 }) v.Forward(i);
        foreach (int i in new[] { 3, 10, 17, 19, 22 }) v.Set(i, ((nint*)VT.Ptr)[i]); // Compact, FlipToGDI, VBlankStatus, RestoreDisplayMode, WaitForVBlank
        v.Set(6, (nint)(delegate* unmanaged[Stdcall]<nint, byte*, nint*, nint, int>)&CreateSurface1);
        v.Set(12, (nint)(delegate* unmanaged[Stdcall]<nint, byte*, int>)&GetDisplayMode1);
        v.Set(14, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetGDISurface1);
        v.Set(20, (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, int>)&Ok2);           // SetCooperativeLevel: el nivel lo pone el juego
        v.Set(21, (nint)(delegate* unmanaged[Stdcall]<nint, int, int, int, int>)&SetDisplayMode1);
        return v;
    }

    /// <summary>La cara v1, con un IDirectDraw v1 real para las consultas.</summary>
    public nint V1()
    {
        if (Native1 != 0) return Native1;
        nint real1 = 0;
        Guid iid = new(IID_DD1);
        ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Layer.Fn(Real, 0))(Real, &iid, &real1);
        Native1 = Layer.NewObject(VT1.Ptr, this, real1);
        return Native1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterface1(nint self, nint riid, nint* ppv)
    {
        if (ppv == null) return Layer.E_INVALIDARG;
        var dd = Layer.Get<DirectDraw7>(self);
        if (Layer.GuidIs(riid, IID_DD1) || Layer.GuidIs(riid, IID_DD2) || Layer.GuidIs(riid, Layer.IID_IUnknown)) { *ppv = self; Layer.AddRefCom(self); return 0; }
        if (Layer.GuidIs(riid, Layer.IID_IDirectDraw7) || Layer.GuidIs(riid, IID_DD4)) { *ppv = dd.Native; Layer.AddRefCom(dd.Native); return 0; }
        if (Layer.GuidIs(riid, Layer.IID_IDirect3D7))
            return ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Layer.Fn(dd.Native, 0))(dd.Native, riid, ppv);
        *ppv = 0;
        // IMediaStream / IAMMediaStream: DirectShow pregunta si el objeto es un stream; no lo es (lo esperado)
        if (!Layer.GuidIs(riid, "B502D1BD-9A57-11D0-8FDE-00C04FD9189D") && !Layer.GuidIs(riid, "BEBE595D-9A6F-11D0-8FDE-00C04FD9189D"))
            Log.Warn($"D9: DirectDraw1::QueryInterface({*(Guid*)riid}) no soportado");
        return Layer.E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int CreateSurface1(nint self, byte* d1, nint* outp, nint outer)
    {
        if (d1 == null || outp == null) return Layer.E_INVALIDARG;
        var dd = Layer.Get<DirectDraw7>(self);
        byte* d2 = stackalloc byte[0x7C];
        new Span<byte>(d2, 0x7C).Clear();
        Buffer.MemoryCopy(d1, d2, 0x6C, 0x6C);
        *(uint*)d2 = 0x7C;
        nint s7 = 0;
        int hr = CreateSurfaceCore(dd.Native, d2, &s7);
        if (hr != 0 || s7 == 0) { *outp = 0; return hr != 0 ? hr : Layer.E_FAIL; }
        *outp = Layer.Get<Surface7>(s7).V1();
        Log.Info($"D9: CreateSurface desde la interfaz antigua (vídeo): {*(int*)(d1 + 0xC)}x{*(int*)(d1 + 8)} caps 0x{*(uint*)(d1 + 0x68):X}");
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetDisplayMode1(nint self, byte* d)
    {
        if (d == null) return Layer.E_INVALIDARG;
        new Span<byte>(d, 0x6C).Clear();
        *(uint*)d = 0x6C;
        *(uint*)(d + 4) = 0x2 | 0x4 | 0x8 | 0x1000;
        *(int*)(d + 8) = Display.Height; *(int*)(d + 0xC) = Display.Width; *(int*)(d + 0x10) = Display.Width * 4;
        Surface7.PixelFormatOf(22, (uint*)(d + 0x48));
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetGDISurface1(nint self, nint* outp)
    {
        var dd = Layer.Get<DirectDraw7>(self);
        var p = Layer.TryGet<Surface7>(dd.Primary);
        if (p == null) { *outp = 0; return Layer.DDERR_NOTFOUND; }
        nint f = p.V1();
        Layer.AddRefCom(f);
        *outp = f;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetDisplayMode1(nint self, int w, int h, int bpp) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Ok2(nint self, nint a, uint b) => 0;
}
