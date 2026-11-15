using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Cara antigua (IDirectDrawSurface v1, v2 y v3) de nuestras superficies, para DirectShow (amstream.dll, vídeos).
/// Mismo orden de métodos que IDirectDrawSurface7 en sus 40 primeras casillas, pero con estructuras más pequeñas:
/// DDSURFACEDESC (0x6C bytes) en vez de DDSURFACEDESC2 (0x7C) y DDSCAPS (4 bytes) en vez de DDSCAPS2 (16). Es otro
/// bloque nativo (Native1) que apunta al mismo objeto C#.
/// </summary>
internal sealed unsafe partial class Surface7
{
    public nint Native1;

    public const string IID_Surface1 = "6C14DB81-A733-11CE-A521-0020AF0BE560";
    public const string IID_Surface2 = "57805885-6EEC-11CF-9441-A82303C10E27";
    public const string IID_Surface3 = "DA044E00-69B2-11D0-A1D5-00AA00B8DFBB";
    public const string IID_Surface4 = "0B2B8630-AD35-11D0-8EA6-00609797EA5B";

    public static bool IsOld(nint riid) => Layer.GuidIs(riid, IID_Surface1) || Layer.GuidIs(riid, IID_Surface2) || Layer.GuidIs(riid, IID_Surface3);

    public nint V1()
    {
        if (Native1 == 0) Native1 = Layer.NewObject(VT1.Ptr, this);
        return Native1;
    }

    // (después de VT, que se inicializa antes en Surface7.cs; los campos estáticos van en orden de aparición por archivo)
    private static Layer.VTable? _vt1;
    public static Layer.VTable VT1 => _vt1 ??= BuildV1();

    private static Layer.VTable BuildV1()
    {
        var v = new Layer.VTable("Surface1", Names[..40], Args[..40]);
        // casillas que valen igual para las dos caras
        foreach (int i in new[] { 3, 5, 11, 13, 16, 17, 18, 21, 24, 26, 27, 28, 29, 31, 32, 36, 37, 38 })
            v.Set(i, ((nint*)VT.Ptr)[i]);
        v.Set(0, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&QueryInterface1);
        v.Set(12, (nint)(delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)&GetAttachedSurface1);
        v.Set(14, (nint)(delegate* unmanaged[Stdcall]<nint, uint*, int>)&GetCaps1);
        v.Set(22, (nint)(delegate* unmanaged[Stdcall]<nint, byte*, int>)&GetSurfaceDesc1);
        v.Set(25, (nint)(delegate* unmanaged[Stdcall]<nint, int*, byte*, uint, nint, int>)&Lock1);
        v.Set(20, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetPalette);
        return v;
    }

    /// <summary>Sin paleta (DDERR_NOPALETTEATTACHED).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetPalette(nint self, nint* pal) { if (pal != null) *pal = 0; return unchecked((int)0x887600E8); }

    /// <summary>De DDSURFACEDESC2 a DDSURFACEDESC: los primeros 0x6C bytes son iguales (caps solo el primer dword).</summary>
    private static void ToDesc1(byte* d2, byte* d1)
    {
        Buffer.MemoryCopy(d2, d1, 0x6C, 0x6C);
        *(uint*)d1 = 0x6C;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterface1(nint self, nint riid, nint* ppv)
    {
        if (ppv == null) return Layer.E_INVALIDARG;
        var s = Layer.Get<Surface7>(self);
        if (IsOld(riid) || Layer.GuidIs(riid, Layer.IID_IUnknown)) { *ppv = self; Layer.AddRefCom(self); return 0; }
        if (Layer.GuidIs(riid, Layer.IID_IDirectDrawSurface7) || Layer.GuidIs(riid, IID_Surface4)) { *ppv = s.Native; Layer.AddRefCom(s.Native); return 0; }
        *ppv = 0;
        Log.Warn($"D9: Surface1::QueryInterface({*(Guid*)riid}) no soportado");
        return Layer.E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetAttachedSurface1(nint self, uint* caps, nint* outp)
    {
        var s = Layer.Get<Surface7>(self);
        nint r = 0;
        if (caps != null)
        {
            if ((caps[0] & 0x20000) != 0) r = s.ZBuf != 0 ? s.ZBuf : Layer.TryGet<Surface7>(s.Back)?.ZBuf ?? 0;
            else if ((caps[0] & 0x4) != 0) r = s.Back;
        }
        var o = Layer.TryGet<Surface7>(r);
        if (o == null) { *outp = 0; return Layer.DDERR_NOTFOUND; }
        nint f = o.V1();
        Layer.AddRefCom(f);
        *outp = f;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetCaps1(nint self, uint* caps)
    {
        if (caps == null) return Layer.E_INVALIDARG;
        *caps = Layer.Get<Surface7>(self).Caps[0];
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetSurfaceDesc1(nint self, byte* d)
    {
        if (d == null) return Layer.E_INVALIDARG;
        byte* d2 = stackalloc byte[0x7C];
        Layer.Get<Surface7>(self).Describe(d2);
        ToDesc1(d2, d);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Lock1(nint self, int* rect, byte* d, uint flags, nint ev)
    {
        var s = Layer.Get<Surface7>(self);
        if (d == null) return Layer.E_INVALIDARG;
        if (!s.LockRaw(rect, flags, out byte* bits, out int pitch)) return Layer.E_FAIL;
        byte* d2 = stackalloc byte[0x7C];
        s.Describe(d2);
        *(uint*)(d2 + 4) |= 0x800;
        *(int*)(d2 + 0x10) = pitch;
        *(nint*)(d2 + 0x24) = (nint)bits;
        ToDesc1(d2, d);
        return 0;
    }

    // ---- Blt con origen (copia por CPU, con estirado al más cercano) ----

    public static int CopyBlt(Surface7 dst, int* dr, Surface7 src, int* sr)
    {
        int dx0 = dr != null ? dr[0] : 0, dy0 = dr != null ? dr[1] : 0, dx1 = dr != null ? dr[2] : dst.W, dy1 = dr != null ? dr[3] : dst.H;
        int sx0 = sr != null ? sr[0] : 0, sy0 = sr != null ? sr[1] : 0, sx1 = sr != null ? sr[2] : src.W, sy1 = sr != null ? sr[3] : src.H;
        int dw = dx1 - dx0, dh = dy1 - dy0, sw = sx1 - sx0, sh = sy1 - sy0;
        if (dw <= 0 || dh <= 0 || sw <= 0 || sh <= 0) return 0;
        if (!src.LockRaw(null, 0x10, out byte* sb, out int sp)) return Layer.E_FAIL;
        if (!dst.LockRaw(null, 0, out byte* db, out int dp)) { src.UnlockRaw(); return Layer.E_FAIL; }
        try
        {
            for (int y = 0; y < dh; y++)
            {
                int ty = dy0 + y;
                if (ty < 0 || ty >= dst.H) continue;
                int syy = sy0 + (int)((long)y * sh / dh);
                if (syy < 0 || syy >= src.H) continue;
                byte* srow = sb + syy * sp, drow = db + ty * dp;
                for (int x = 0; x < dw; x++)
                {
                    int tx = dx0 + x;
                    if (tx < 0 || tx >= dst.W) continue;
                    int sxx = sx0 + (int)((long)x * sw / dw);
                    if (sxx < 0 || sxx >= src.W) continue;
                    uint c;
                    if (src.Bpp == 4) c = ((uint*)srow)[sxx];
                    else if (src.Bpp == 2)
                    {
                        uint v = ((ushort*)srow)[sxx];
                        c = src.Fmt == 23
                            ? 0xFF000000 | ((v & 0xF800) << 8) | ((v & 0x7E0) << 5) | ((v & 0x1F) << 3)
                            : 0xFF000000 | ((v & 0x7C00) << 9) | ((v & 0x3E0) << 6) | ((v & 0x1F) << 3);
                    }
                    else c = 0xFF000000 | (uint)(srow[sxx * 3] | (srow[sxx * 3 + 1] << 8) | (srow[sxx * 3 + 2] << 16));
                    if (dst.Bpp == 4) ((uint*)drow)[tx] = c;
                    else if (dst.Bpp == 2)
                        ((ushort*)drow)[tx] = dst.Fmt == 23
                            ? (ushort)(((c >> 8) & 0xF800) | ((c >> 5) & 0x7E0) | ((c >> 3) & 0x1F))
                            : (ushort)(((c >> 9) & 0x7C00) | ((c >> 6) & 0x3E0) | ((c >> 3) & 0x1F));
                }
            }
        }
        finally { dst.UnlockRaw(); src.UnlockRaw(); }
        return 0;
    }
}
