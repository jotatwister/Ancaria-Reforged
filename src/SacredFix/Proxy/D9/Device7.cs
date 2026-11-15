using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Proxy.D9;

/// <summary>
/// IDirect3DDevice7 propio: guarda el estado de DirectX 7 (estados de render y de etapa, texturas, material, luces,
/// matrices, viewport) para GetX y para volver a ponerlo tras perder el dispositivo, y pasa cada llamada al motor de
/// dibujo (Backend.Current), siempre con la semántica de D3D7. Los dibujos «strided» (modelos de Granny) se
/// intercalan aquí en un búfer propio.
/// </summary>
internal sealed unsafe class Device7
{
    public nint Native, D3D, Target;
    private readonly uint[] _rs = new uint[256];
    private readonly uint[,] _tss = new uint[8, 32];
    private readonly nint[] _tex = new nint[8];
    private static Backend B => Backend.Current;
    // para volver a ponerlo todo tras un Reset (Alt+Tab)
    private readonly byte[] _material = new byte[68];
    private bool _hasMaterial;
    private readonly System.Collections.Generic.Dictionary<uint, byte[]> _lights = new();
    private readonly System.Collections.Generic.Dictionary<uint, int> _lightOn = new();
    private readonly System.Collections.Generic.Dictionary<int, float[]> _transforms = new();
    private readonly int[] _viewport = new int[6];
    private bool _hasViewport;

    /// <summary>Tras perder y recuperar el dispositivo: todo el estado guardado, otra vez al motor.</summary>
    private void Reapply()
    {
        for (int st = 0; st < 256; st++) B.SetRenderState(st, _rs[st]);
        for (uint stage = 0; stage < 8; stage++)
        {
            for (int t = 1; t < 29; t++) if (t != 12) B.SetTextureStageState(stage, t, _tss[stage, t]);
            B.SetTexture(stage, Layer.TryGet<Surface7>(_tex[stage]));
        }
        fixed (byte* m = _material) if (_hasMaterial) B.SetMaterial((nint)m);
        foreach (var kv in _lights) fixed (byte* l = kv.Value) B.SetLight(kv.Key, (nint)l);
        foreach (var kv in _lightOn) B.LightEnable(kv.Key, kv.Value);
        foreach (var kv in _transforms) fixed (float* m = kv.Value) B.SetTransform(kv.Key, m);
        fixed (int* v = _viewport) if (_hasViewport) B.SetViewport((nint)v);
        Log.Info("D9: estados del dispositivo restaurados");
    }

    private static readonly string[] Names =
    {
        "QueryInterface", "AddRef", "Release", "GetCaps", "EnumTextureFormats", "BeginScene", "EndScene", "GetDirect3D",
        "SetRenderTarget", "GetRenderTarget", "Clear", "SetTransform", "GetTransform", "SetViewport", "MultiplyTransform",
        "GetViewport", "SetMaterial", "GetMaterial", "SetLight", "GetLight", "SetRenderState", "GetRenderState",
        "BeginStateBlock", "EndStateBlock", "PreLoad", "DrawPrimitive", "DrawIndexedPrimitive", "SetClipStatus",
        "GetClipStatus", "DrawPrimitiveStrided", "DrawIndexedPrimitiveStrided", "DrawPrimitiveVB",
        "DrawIndexedPrimitiveVB", "ComputeSphereVisibility", "GetTexture", "SetTexture", "GetTextureStageState",
        "SetTextureStageState", "ValidateDevice", "ApplyStateBlock", "CaptureStateBlock", "DeleteStateBlock",
        "CreateStateBlock", "Load", "LightEnable", "GetLightEnable", "SetClipPlane", "GetClipPlane", "GetInfo",
    };
    private static readonly int[] Args =
    {
        2, 0, 0, 1, 2, 0, 0, 1, 2, 1, 6, 2, 2, 1, 2, 1, 1, 1, 2, 2, 2, 2, 0, 1, 1, 5, 7, 1, 1, 5, 7, 5, 7, 5, 2, 2, 3, 3,
        1, 1, 1, 1, 2, 5, 2, 2, 2, 2, 3,
    };
    public static readonly Layer.VTable VT = Build();

    private static Layer.VTable Build()
    {
        var v = new Layer.VTable("Device7", Names, Args);
        v.Set(0, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&QueryInterface);
        v.Set(3, (nint)(delegate* unmanaged[Stdcall]<nint, byte*, int>)&GetCaps);
        v.Set(4, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&EnumTextureFormats);
        v.Set(5, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&BeginScene);
        v.Set(6, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&EndScene);
        v.Set(7, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetDirect3D);
        v.Set(8, (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, int>)&SetRenderTarget);
        v.Set(9, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetRenderTarget);
        v.Set(10, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, uint, uint, float, uint, int>)&Clear);
        v.Set(11, (nint)(delegate* unmanaged[Stdcall]<nint, int, float*, int>)&SetTransform);
        v.Set(12, (nint)(delegate* unmanaged[Stdcall]<nint, int, float*, int>)&GetTransform);
        v.Set(13, (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&SetViewport);
        v.Set(14, (nint)(delegate* unmanaged[Stdcall]<nint, int, float*, int>)&MultiplyTransform);
        v.Set(15, (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&GetViewport);
        v.Set(16, (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&SetMaterial);
        v.Set(17, (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&GetMaterial);
        v.Set(18, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, int>)&SetLight);
        v.Set(19, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, int>)&GetLight);
        v.Set(20, (nint)(delegate* unmanaged[Stdcall]<nint, int, uint, int>)&SetRenderState);
        v.Set(21, (nint)(delegate* unmanaged[Stdcall]<nint, int, uint*, int>)&GetRenderState);
        v.Set(24, (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&Ok1p);   // PreLoad
        v.Set(25, (nint)(delegate* unmanaged[Stdcall]<nint, int, uint, nint, uint, uint, int>)&DrawPrimitive);
        v.Set(26, (nint)(delegate* unmanaged[Stdcall]<nint, int, uint, nint, uint, ushort*, uint, uint, int>)&DrawIndexedPrimitive);
        v.Set(27, (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&Ok1p);   // SetClipStatus
        v.Set(28, (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&Ok1p);   // GetClipStatus
        v.Set(29, (nint)(delegate* unmanaged[Stdcall]<nint, int, uint, nint*, uint, uint, int>)&DrawPrimitiveStrided);
        v.Set(30, (nint)(delegate* unmanaged[Stdcall]<nint, int, uint, nint*, uint, ushort*, uint, uint, int>)&DrawIndexedPrimitiveStrided);
        v.Set(34, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)&GetTexture);
        v.Set(35, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, int>)&SetTexture);
        v.Set(36, (nint)(delegate* unmanaged[Stdcall]<nint, uint, int, uint*, int>)&GetTextureStageState);
        v.Set(37, (nint)(delegate* unmanaged[Stdcall]<nint, uint, int, uint, int>)&SetTextureStageState);
        v.Set(38, (nint)(delegate* unmanaged[Stdcall]<nint, uint*, int>)&ValidateDevice);
        v.Set(44, (nint)(delegate* unmanaged[Stdcall]<nint, uint, int, int>)&LightEnable);
        v.Set(45, (nint)(delegate* unmanaged[Stdcall]<nint, uint, int*, int>)&GetLightEnable);
        return v;
    }

    public static nint Create(nint d3d, nint target)
    {
        var o = new Device7 { D3D = d3d, Target = target };
        o.Native = Layer.NewObject(VT.Ptr, o);
        Backend.AfterReset += o.Reapply;
        // valores iniciales: los del motor (los de D3D7 que no existen en él quedan a 0)
        for (int s = 0; s < 256; s++) o._rs[s] = B.DefaultRenderState(s);
        for (uint st = 0; st < 8; st++)
            for (int t = 1; t < 32; t++) o._tss[st, t] = B.DefaultTextureStageState(st, t);
        return o.Native;
    }

    private static Device7 Me(nint self) => Layer.Get<Device7>(self);

    // ---- estados ----

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetRenderState(nint self, int s, uint v)
    {
        if (s < 0 || s >= 256) return Layer.E_INVALIDARG;
        Me(self)._rs[s] = v;
        return B.SetRenderState(s, v);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetRenderState(nint self, int s, uint* v)
    {
        if (s < 0 || s >= 256 || v == null) return Layer.E_INVALIDARG;
        *v = Me(self)._rs[s];
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetTextureStageState(nint self, uint stage, int t, uint v)
    {
        if (stage >= 8 || t <= 0 || t >= 32) return Layer.E_INVALIDARG;
        Me(self)._tss[stage, t] = v;
        if (t == 12) { Me(self)._tss[stage, 13] = v; Me(self)._tss[stage, 14] = v; } // ADDRESS: U y V
        return B.SetTextureStageState(stage, t, v);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetTextureStageState(nint self, uint stage, int t, uint* v)
    {
        if (stage >= 8 || t <= 0 || t >= 32 || v == null) return Layer.E_INVALIDARG;
        *v = Me(self)._tss[stage, t];
        return 0;
    }

    // ---- matrices, viewport, material, luces ----

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetTransform(nint self, int s, float* m)
    {
        if (m != null) { var me = Me(self); if (!me._transforms.TryGetValue(s, out var c)) me._transforms[s] = c = new float[16]; for (int i = 0; i < 16; i++) c[i] = m[i]; }
        return B.SetTransform(s, m);
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetTransform(nint self, int s, float* m) => B.GetTransform(s, m);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int MultiplyTransform(nint self, int s, float* m) => B.MultiplyTransform(s, m);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetViewport(nint self, nint vp)
    {
        if (vp != 0) { var me = Me(self); for (int i = 0; i < 6; i++) me._viewport[i] = ((int*)vp)[i]; me._hasViewport = true; }
        return B.SetViewport(vp);
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetViewport(nint self, nint vp) => B.GetViewport(vp);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetMaterial(nint self, nint m)
    {
        if (m != 0) { var me = Me(self); Marshal.Copy(m, me._material, 0, 68); me._hasMaterial = true; }
        return B.SetMaterial(m);
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetMaterial(nint self, nint m) => B.GetMaterial(m);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetLight(nint self, uint i, nint l)
    {
        if (l != 0) { var me = Me(self); if (!me._lights.TryGetValue(i, out var c)) me._lights[i] = c = new byte[104]; Marshal.Copy(l, c, 0, 104); }
        return B.SetLight(i, l);
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetLight(nint self, uint i, nint l) => B.GetLight(i, l);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int LightEnable(nint self, uint i, int on)
    {
        Me(self)._lightOn[i] = on;
        return B.LightEnable(i, on);
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetLightEnable(nint self, uint i, int* on) => B.GetLightEnable(i, on);

    // ---- escena ----

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int BeginScene(nint self)
    {
        B.BeginFrame();
        return B.BeginScene();
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int EndScene(nint self) => B.EndScene();

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Clear(nint self, uint count, nint rects, uint flags, uint color, float z, uint stencil)
        => B.Clear(count, rects, flags, color, z, stencil);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetRenderTarget(nint self, nint surf, uint flags)
    {
        var s = Layer.TryGet<Surface7>(surf);
        if (s == null || (s.Type != Surface7.Kind.Back && s.Type != Surface7.Kind.Primary)) Surface7.NotImplOnce("SetRenderTarget a una superficie que no es el back buffer");
        Me(self).Target = surf;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetRenderTarget(nint self, nint* outp)
    {
        nint t = Me(self).Target;
        Layer.AddRefCom(t);
        *outp = t;
        return t != 0 ? 0 : Layer.E_FAIL;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetDirect3D(nint self, nint* outp)
    {
        nint d = Me(self).D3D;
        Layer.AddRefCom(d);
        *outp = d;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetCaps(nint self, byte* desc)
    {
        if (desc == null || !Direct3D7.HaveDesc) return Layer.E_FAIL;
        for (int i = 0; i < 0xEC; i++) desc[i] = Direct3D7.TnLDesc[i];
        return 0;
    }

    /// <summary>Formatos de textura que se ofrecen (los que usa el juego y alguno más).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int EnumTextureFormats(nint self, nint cb, nint ctx)
    {
        uint* pf = stackalloc uint[8];
        foreach (int f in new[] { 21, 22, 26, 25, 23, 24 })
        {
            Surface7.PixelFormatOf(f, pf);
            if (((delegate* unmanaged[Stdcall]<uint*, nint, int>)cb)(pf, ctx) == 0) break;
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterface(nint self, nint riid, nint* ppv)
    {
        if (Layer.GuidIs(riid, Layer.IID_IUnknown) || Layer.GuidIs(riid, "F5049E79-4861-11D2-A407-00A0C90629A8")) // IDirect3DDevice7
        { Layer.AddRefCom(self); *ppv = self; return 0; }
        *ppv = 0;
        return Layer.E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int ValidateDevice(nint self, uint* passes) { if (passes != null) *passes = 1; return 0; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Ok1p(nint self, nint a) => 0;

    // ---- texturas ----

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetTexture(nint self, uint stage, nint surf)
    {
        if (stage >= 8) return Layer.E_INVALIDARG;
        Me(self)._tex[stage] = surf;
        return B.SetTexture(stage, Layer.TryGet<Surface7>(surf));
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetTexture(nint self, uint stage, nint* outp)
    {
        if (stage >= 8 || outp == null) return Layer.E_INVALIDARG;
        nint t = Me(self)._tex[stage];
        Layer.AddRefCom(t);
        *outp = t;
        return 0;
    }

    // ---- dibujo ----

    public static int Stride(uint fvf)
    {
        int s = (fvf & 0xE) switch { 0x2 => 12, 0x4 => 16, 0x6 => 16, 0x8 => 20, 0xA => 24, 0xC => 28, 0xE => 32, _ => 0 };
        if ((fvf & 0x10) != 0) s += 12;
        if ((fvf & 0x20) != 0) s += 4;
        if ((fvf & 0x40) != 0) s += 4;
        if ((fvf & 0x80) != 0) s += 4;
        int n = (int)((fvf >> 8) & 0xF);
        for (int i = 0; i < n; i++)
        {
            int size = (int)((fvf >> (16 + i * 2)) & 3); // 0 = 2 floats, 1 = 3, 2 = 4, 3 = 1
            s += size switch { 0 => 8, 1 => 12, 2 => 16, _ => 4 };
        }
        return s;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int DrawPrimitive(nint self, int type, uint fvf, nint verts, uint count, uint flags)
        => B.Draw(type, fvf, verts, count, Stride(fvf));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int DrawIndexedPrimitive(nint self, int type, uint fvf, nint verts, uint vcount, ushort* idx, uint icount, uint flags)
        => B.DrawIndexed(type, fvf, verts, vcount, idx, icount, Stride(fvf));

    private byte[] _strided = new byte[1 << 16];

    /// <summary>
    /// D3DDRAWPRIMITIVESTRIDEDDATA: posición, normal, difuso, especular y 8 coordenadas de textura, cada una
    /// { puntero, separación }. Se copian intercaladas en el orden del FVF.
    /// </summary>
    private nint Interleave(uint fvf, nint* data, uint count, out int stride)
    {
        stride = Stride(fvf);
        int bytes = stride * (int)count;
        if (_strided.Length < bytes) _strided = new byte[Math.Max(bytes, _strided.Length * 2)];
        int pos = (fvf & 0xE) switch { 0x2 => 12, 0x4 => 16, _ => Stride(fvf & 0xE) };
        int nTex = (int)((fvf >> 8) & 0xF);
        // tamaños de cada elemento, en el orden del FVF (índice en D3DDRAWPRIMITIVESTRIDEDDATA: 0 posición, 1 normal,
        // 2 difuso, 3 especular, 4+ coordenadas de textura)
        int* part = stackalloc int[12];
        int* size = stackalloc int[12];
        int np = 0;
        part[np] = 0; size[np++] = pos;
        if ((fvf & 0x10) != 0) { part[np] = 1; size[np++] = 12; }
        if ((fvf & 0x40) != 0) { part[np] = 2; size[np++] = 4; }
        if ((fvf & 0x80) != 0) { part[np] = 3; size[np++] = 4; }
        for (int t = 0; t < nTex && np < 12; t++)
        {
            part[np] = 4 + t;
            size[np++] = (int)((fvf >> (16 + t * 2)) & 3) switch { 0 => 8, 1 => 12, 2 => 16, _ => 4 };
        }
        fixed (byte* dst0 = _strided)
        {
            for (uint i = 0; i < count; i++)
            {
                byte* dst = dst0 + i * stride;
                for (int k = 0; k < np; k++)
                {
                    byte* src = (byte*)data[part[k] * 2] + i * (uint)data[part[k] * 2 + 1];
                    Buffer.MemoryCopy(src, dst, size[k], size[k]);
                    dst += size[k];
                }
            }
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int DrawPrimitiveStrided(nint self, int type, uint fvf, nint* data, uint count, uint flags)
    {
        var me = Me(self);
        if (data == null || count == 0) return 0;
        me.Interleave(fvf, data, count, out int stride);
        fixed (byte* v = me._strided) return B.Draw(type, fvf, (nint)v, count, stride);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int DrawIndexedPrimitiveStrided(nint self, int type, uint fvf, nint* data, uint vcount, ushort* idx, uint icount, uint flags)
    {
        var me = Me(self);
        if (data == null || idx == null || vcount == 0) return 0;
        me.Interleave(fvf, data, vcount, out int stride);
        fixed (byte* v = me._strided) return B.DrawIndexed(type, fvf, (nint)v, vcount, idx, icount, stride);
    }
}
