using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Proxy.D9;

/// <summary>
/// IDirect3D7 propio. EnumDevices y EnumZBufferFormats reenvían al IDirect3D7 real de Windows (+0xC), así el juego
/// ve los mismos dispositivos y capacidades de siempre; CreateDevice crea nuestro dispositivo sobre Direct3D 9.
/// </summary>
internal sealed unsafe class Direct3D7
{
    public nint Native, DD;
    public static readonly byte[] TnLDesc = new byte[0xEC]; // D3DDEVICEDESC7 del T&L HAL real (para GetCaps)
    public static bool HaveDesc;

    private static readonly string[] Names = { "QueryInterface", "AddRef", "Release", "EnumDevices", "CreateDevice", "CreateVertexBuffer", "EnumZBufferFormats", "EvictManagedTextures" };
    private static readonly int[] Args = { 2, 0, 0, 2, 3, 3, 3, 0 };
    public static readonly Layer.VTable VT = Build();

    private static Layer.VTable Build()
    {
        var v = new Layer.VTable("Direct3D7", Names, Args);
        v.Set(0, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&QueryInterface);
        v.Forward(3);
        v.Set(4, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)&CreateDevice);
        v.Forward(6);
        v.Set(7, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Evict);
        return v;
    }

    public static nint Create(nint dd)
    {
        nint realDD = Layer.Real(dd), real = 0;
        Guid iid = new(Layer.IID_IDirect3D7);
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Layer.Fn(realDD, 0))(realDD, &iid, &real);
        if (hr != 0 || real == 0) { Log.Error($"D9: el DirectDraw real no da IDirect3D7 (0x{hr:X8})"); return 0; }
        if (!HaveDesc)
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Layer.Fn(real, 3))(real, (nint)(delegate* unmanaged[Stdcall]<nint, nint, byte*, nint, int>)&CaptureDesc, 0);
        var o = new Direct3D7 { DD = dd };
        o.Native = Layer.NewObject(VT.Ptr, o, real);
        return o.Native;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int CaptureDesc(nint desc, nint name, byte* dev, nint ctx)
    {
        if (dev != null && *(Guid*)(dev + 0xC4) == new Guid(Layer.IID_IDirect3DTnLHalDevice))
        {
            for (int i = 0; i < 0xEC; i++) TnLDesc[i] = dev[i];
            HaveDesc = true;
        }
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterface(nint self, nint riid, nint* ppv)
    {
        if (Layer.GuidIs(riid, Layer.IID_IDirect3D7) || Layer.GuidIs(riid, Layer.IID_IUnknown)) { Layer.AddRefCom(self); *ppv = self; return 0; }
        *ppv = 0;
        return Layer.E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int CreateDevice(nint self, nint clsid, nint surface, nint* outp)
    {
        try
        {
            if (!Backend.Current.Create()) return Layer.E_FAIL;
            *outp = Device7.Create(self, surface);
            Log.Info($"D9: CreateDevice({*(Guid*)clsid}) -> dispositivo propio");
            return 0;
        }
        catch (Exception ex) { Log.Error("D9: CreateDevice: " + ex.Message); return Layer.E_FAIL; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Evict(nint self)
    {
        Backend.Current.Evict();
        return 0;
    }
}
