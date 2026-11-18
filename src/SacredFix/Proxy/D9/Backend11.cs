using System;
using System.Runtime.InteropServices;
using SacredFix.Core;
using SacredFix.Native;
using static SacredFix.Proxy.D9.Display;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Motor Direct3D 11 ([Display] Renderer=d3d11). FASE 1: dispositivo, cadena DXGI con flip model (sin bordes y
/// ventana; el tamaño sigue al área de la ventana con ResizeBuffers), imagen interna del tamaño del juego que se estira
/// al presentar con Catmull-Rom, borrados de color y la superposición (FPS, recargas). FASE 2: texturas en la GPU
/// (copia en memoria para Lock, subida al desbloquear) y la pantalla accesible con Lock y GDI. El dibujo 3D/2D del juego
/// llega en las fases siguientes.
/// Las llamadas van por las tablas de funciones de D3D11 / DXGI (índices en los comentarios de cada constante).
/// </summary>
internal sealed unsafe partial class Backend11 : Backend
{
    public Backend11() => InitStates();

    // ID3D11Device
    private const int DevCreateBuffer = 3, DevCreateTexture2D = 5, DevCreateSRV = 7, DevCreateRTV = 9, DevCreateInputLayout = 11,
        DevCreateVS = 12, DevCreatePS = 15, DevCreateBlend = 20, DevCreateRaster = 22, DevCreateSampler = 23;
    // ID3D11DeviceContext
    private const int CtxVSSetCB = 7, CtxPSSetSRV = 8, CtxPSSetShader = 9, CtxPSSetSamplers = 10, CtxVSSetShader = 11, CtxDraw = 13,
        CtxMap = 14, CtxUnmap = 15, CtxPSSetCB = 16, CtxIASetLayout = 17, CtxIASetVB = 18, CtxIASetTopology = 24,
        CtxOMSetRT = 33, CtxOMSetBlend = 35, CtxRSSetState = 43, CtxRSSetViewports = 44, CtxUpdateSub = 48, CtxClearRTV = 50;
    // IDXGISwapChain / IDXGIFactory2 / IDXGIDevice
    private const int ScPresent = 8, ScGetBuffer = 9, ScResizeBuffers = 13, FacMakeWindowAssociation = 8, FacCreateForHwnd = 15, DxgiDevGetAdapter = 7, ObjGetParent = 6;

    private nint _dev, _ctx, _sc;
    private nint _bbRtv;                   // back buffer de la cadena
    private nint _scene, _sceneRtv, _sceneSrv;
    private nint _vsFull, _psUp, _vsOver, _psOver, _layoutOver, _cb, _vb, _samp, _blendOver, _raster;
    private int _vbSize;
    private bool _failed;
    private nint _screenCpu;
    private const int CtxGenerateMips = 54;
    private static bool Mips => Layer.Anisotropy > 1;

    private static Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static Guid IID_IDXGIDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static Guid IID_IDXGIFactory2 = new("50C83A1C-E072-4C48-87B0-3630FA36A6D0");

    private static nint Fn(nint o, int i) => Layer.Fn(o, i);

    // ---- dispositivo ----

    public override bool Ready => _dev != 0;

    /// <summary>
    /// Crea el dispositivo. Si D3D11 no puede (Windows 7/8 sin lo necesario, tarjeta sin Direct3D 10, ...), el motor pasa
    /// a ser Backend9 (Direct3D 9) para el resto de la partida: se llama al principio, antes de crear ninguna superficie.
    /// [Debug] D3D11ForceFail=1 lo fuerza, para probarlo.
    /// </summary>
    public override bool Create()
    {
        if (_dev != 0) return true;
        if (!_failed && CreateCore()) return true;
        if (_fellBack) return false;
        _fellBack = true;
        Log.Warn("D11: no se pudo usar Direct3D 11: se pasa al motor Direct3D 9");
        Display.Fullscreen = Layer.WindowStyle == 0;
        var b9 = new Backend9();
        Backend.Current = b9;
        return b9.Create();
    }

    public static bool ForceFail;
    private bool _fellBack;

    private bool CreateCore()
    {
        _failed = true;
        try
        {
            if (ForceFail) { Log.Warn("D11: fallo forzado ([Debug] D3D11ForceFail=1)"); return false; }
            _exclusive = Layer.WindowStyle == 0;
            Fullscreen = false; // (la exclusiva la pone DXGI sobre la ventana sin bordes: el ratón se convierte igual)
            Device9.PrepareWindow();
            nint mod = Win32.LoadLibraryW(System.IO.Path.Combine(Win32.GetSystemDirectory(), "d3d11.dll"));
            nint create = mod != 0 ? Win32.GetProcAddress(mod, "D3D11CreateDevice") : 0;
            if (create == 0) { Log.Error("D11: no está d3d11.dll"); return false; }
            nint dev = 0, ctx = 0; int fl = 0;
            int hr = ((delegate* unmanaged[Stdcall]<nint, int, nint, uint, nint, uint, uint, nint*, int*, nint*, int>)create)(0, 1 /* HARDWARE */, 0, 0x20 /* BGRA */, 0, 0, 7, &dev, &fl, &ctx);
            if (hr != 0) { Log.Error($"D11: D3D11CreateDevice -> 0x{hr:X8}"); return false; }
            if (fl < 0xA000) { Log.Error($"D11: la tarjeta solo llega al nivel 0x{fl:X} (hace falta Direct3D 10)"); Layer.ReleaseCom(ctx); Layer.ReleaseCom(dev); return false; }
            _dev = dev; _ctx = ctx;
            // fábrica DXGI del adaptador del dispositivo
            nint dxgiDev = 0, adapter = 0, factory = 0;
            fixed (Guid* g = &IID_IDXGIDevice) ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Fn(dev, 0))(dev, g, &dxgiDev);
            ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Fn(dxgiDev, DxgiDevGetAdapter))(dxgiDev, &adapter);
            fixed (Guid* g = &IID_IDXGIFactory2) ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Fn(adapter, ObjGetParent))(adapter, g, &factory);
            Layer.ReleaseCom(dxgiDev); Layer.ReleaseCom(adapter);
            if (factory == 0) { Log.Error("D11: sin IDXGIFactory2 (hace falta Windows 8 o posterior)"); return false; }
            ClientSize(out int cw, out int ch);
            BBW = OutW = cw; BBH = OutH = ch;
            uint* d = stackalloc uint[12];
            new Span<uint>(d, 12).Clear();
            d[0] = (uint)cw; d[1] = (uint)ch; d[2] = 87;        // B8G8R8A8_UNORM
            d[4] = 1;                                           // SampleDesc.Count
            d[6] = 0x20;                                        // RENDER_TARGET_OUTPUT
            d[7] = 2;                                           // BufferCount
            d[8] = 0;                                           // DXGI_SCALING_STRETCH
            nint sc = 0;
            // FLIP_DISCARD (Windows 10), FLIP_SEQUENTIAL (Windows 8), DISCARD clásico (Windows 7)
            foreach (uint fx in new uint[] { 4, 3, 0 })
            {
                d[9] = fx; d[7] = fx == 0 ? 1u : 2u;
                hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint*, nint, nint, nint*, int>)Fn(factory, FacCreateForHwnd))(factory, dev, Hwnd, d, 0, 0, &sc);
                if (hr == 0) { if (fx != 4) Log.Info($"D11: modo de presentación {(fx == 3 ? "FLIP_SEQUENTIAL" : "DISCARD")} (no hay FLIP_DISCARD)"); break; }
                Log.Warn($"D11: modo de presentación {fx} -> 0x{hr:X8}");
            }
            if (hr == 0) ((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)Fn(factory, FacMakeWindowAssociation))(factory, Hwnd, 2 /* NO_ALT_ENTER */);
            Layer.ReleaseCom(factory);
            if (hr != 0) { Log.Error($"D11: CreateSwapChainForHwnd -> 0x{hr:X8}"); return false; }
            _sc = sc;
            FitImage(cw, ch);
            ChooseRenderSize(Display.ImgW, Display.ImgH);
            if (!CreateBackBufferView() || !CreateScene() || !CreateSmall() || !CreatePipelines() || !CreateDepth()) return false;
            CreateMsaa();
            if (Scaled) CreateBoundsGS();
            if (_vpSaved[2] == 0) { _vpSaved[2] = Width; _vpSaved[3] = Height; }
            MouseScale.Install();
            if (_exclusive) EnterExclusive();
            Log.Info($"D11: dispositivo creado (nivel 0x{fl:X}), imagen {Width}x{Height}, salida {cw}x{ch}");
            _failed = false;
            return true;
        }
        catch (Exception ex) { Log.Error("D11: " + ex.Message); return false; }
        finally { if (_failed && _dev != 0) { Layer.ReleaseCom(_sc); Layer.ReleaseCom(_ctx); Layer.ReleaseCom(_dev); _sc = _ctx = _dev = 0; } }
    }

    [DllImport("user32.dll")] private static extern int GetClientRect(nint hwnd, int* r);
    [DllImport("user32.dll")] private static extern int IsIconic(nint hwnd);

    private static void ClientSize(out int w, out int h)
    {
        int* r = stackalloc int[4];
        GetClientRect(Hwnd, r);
        w = Math.Max(1, r[2] - r[0]); h = Math.Max(1, r[3] - r[1]);
    }

    private bool CreateBackBufferView()
    {
        nint buf = 0, rtv = 0;
        fixed (Guid* g = &IID_ID3D11Texture2D) ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Fn(_sc, ScGetBuffer))(_sc, 0, g, &buf);
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Fn(_dev, DevCreateRTV))(_dev, buf, 0, &rtv);
        Layer.ReleaseCom(buf);
        if (hr != 0) { Log.Error($"D11: vista del back buffer -> 0x{hr:X8}"); return false; }
        _bbRtv = rtv;
        return true;
    }

    private bool CreateScene()
    {
        uint* t = stackalloc uint[11]; // D3D11_TEXTURE2D_DESC
        t[0] = (uint)RW; t[1] = (uint)RH; t[2] = 1; t[3] = 1; t[4] = 87; t[5] = 1; t[6] = 0;
        t[7] = 0; t[8] = 0x20 | 0x8; t[9] = 0; t[10] = 0; // DEFAULT; RENDER_TARGET | SHADER_RESOURCE
        nint tex = 0, rtv = 0, srv = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Fn(_dev, DevCreateTexture2D))(_dev, t, 0, &tex);
        if (hr == 0) hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Fn(_dev, DevCreateRTV))(_dev, tex, 0, &rtv);
        if (hr == 0) hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Fn(_dev, DevCreateSRV))(_dev, tex, 0, &srv);
        if (hr != 0) { Log.Error($"D11: imagen interna -> 0x{hr:X8}"); return false; }
        _scene = tex; _sceneRtv = rtv; _sceneSrv = srv;
        float* black = stackalloc float[4];
        black[0] = black[1] = black[2] = 0; black[3] = 1;
        ((delegate* unmanaged[Stdcall]<nint, nint, float*, void>)Fn(_ctx, CtxClearRTV))(_ctx, _sceneRtv, black);
        return true;
    }

    // ---- shaders ----

    private const string ShFull = @"
struct VO { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
VO main(uint id : SV_VertexID)
{
    VO o;
    float2 t = float2((id << 1) & 2, id & 2);
    o.uv = t;
    o.pos = float4(t * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}";

    private const string ShUpscale = @"
Texture2D t0 : register(t0);
SamplerState s0 : register(s0);
cbuffer C : register(b0) { float4 ts; float4 sh; float4 cg; }; // ancho, alto, 1/ancho, 1/alto; sh.x = enfoque; cg = gamma, contraste, brillo, saturación
float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float2 p = uv * ts.xy;
    float2 t1 = floor(p - 0.5) + 0.5;
    float2 f = p - t1;
    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 c0 = (t1 - 1.0) * ts.zw, c3 = (t1 + 2.0) * ts.zw, c12 = (t1 + w2 / w12) * ts.zw;
    float4 r = 0;
    r += t0.SampleLevel(s0, float2(c0.x,  c0.y),  0) * w0.x  * w0.y;
    r += t0.SampleLevel(s0, float2(c12.x, c0.y),  0) * w12.x * w0.y;
    r += t0.SampleLevel(s0, float2(c3.x,  c0.y),  0) * w3.x  * w0.y;
    r += t0.SampleLevel(s0, float2(c0.x,  c12.y), 0) * w0.x  * w12.y;
    r += t0.SampleLevel(s0, float2(c12.x, c12.y), 0) * w12.x * w12.y;
    r += t0.SampleLevel(s0, float2(c3.x,  c12.y), 0) * w3.x  * w12.y;
    r += t0.SampleLevel(s0, float2(c0.x,  c3.y),  0) * w0.x  * w3.y;
    r += t0.SampleLevel(s0, float2(c12.x, c3.y),  0) * w12.x * w3.y;
    r += t0.SampleLevel(s0, float2(c3.x,  c3.y),  0) * w3.x  * w3.y;
    float3 c = saturate(r.rgb);
    if (sh.x > 0)
    {
        // enfoque adaptativo (como CAS): más donde hay poco contraste, menos en los bordes ya marcados
        float3 n = t0.SampleLevel(s0, uv + float2(0, -ts.w), 0).rgb, so = t0.SampleLevel(s0, uv + float2(0, ts.w), 0).rgb;
        float3 e = t0.SampleLevel(s0, uv + float2(ts.z, 0), 0).rgb, w = t0.SampleLevel(s0, uv + float2(-ts.z, 0), 0).rgb;
        float3 mn = min(c, min(min(n, so), min(e, w))), mx = max(c, max(max(n, so), max(e, w)));
        float3 amp = sqrt(saturate(min(mn, 1 - mx) / max(mx, 1e-4)));
        float3 wt = -amp * lerp(0.125, 0.2, sh.x);
        c = saturate((c + (n + so + e + w) * wt) / (1 + 4 * wt));
    }
    // ajuste de color (neutro: 1, 1, 0, 1)
    c = pow(c, 1 / max(cg.x, 0.05));
    c = saturate((c - 0.5) * cg.y + 0.5 + cg.z);
    float luma = dot(c, float3(0.2126, 0.7152, 0.0722));
    c = saturate(lerp(luma.xxx, c, cg.w));
    return float4(c, 1.0);
}";

    // superposición: vértices ya en píxeles de pantalla (x, y, z, rhw), color, coordenadas de textura
    private const string ShOverVS = @"
cbuffer C : register(b0) { float4 ts; }; // ancho y alto de la salida
struct VI { float4 pos : POSITION; float4 col : COLOR0; float2 uv : TEXCOORD0; };
struct VO { float4 pos : SV_Position; float4 col : COLOR0; float2 uv : TEXCOORD0; };
VO main(VI i)
{
    VO o;
    o.pos = float4((i.pos.x + 0.5) / ts.x * 2 - 1, 1 - (i.pos.y + 0.5) / ts.y * 2, 0, 1);
    o.col = i.col; o.uv = i.uv;
    return o;
}";
    private const string ShOverPS = @"
Texture2D t0 : register(t0);
SamplerState s0 : register(s0);
float4 main(float4 pos : SV_Position, float4 col : COLOR0, float2 uv : TEXCOORD0) : SV_Target { return t0.Sample(s0, uv) * col; }";

    private static nint _compile;

    /// <summary>D3DCompile (d3dcompiler_47): devuelve el ID3DBlob con el código (null si falla).</summary>
    private static nint Compile(string src, string target)
    {
        long t = T0();
        nint r = CachedCompile(src, target);
        T1(Pf.Shader, t);
        return r;
    }

    private static nint CompileCore(string src, string target)
    {
        if (_compile == 0)
        {
            nint m = Win32.LoadLibraryW(System.IO.Path.Combine(Win32.GetSystemDirectory(), "d3dcompiler_47.dll"));
            _compile = m != 0 ? Win32.GetProcAddress(m, "D3DCompile") : 0;
            if (_compile == 0) { Log.Error("D11: no está d3dcompiler_47.dll"); return 0; }
        }
        byte[] s = System.Text.Encoding.ASCII.GetBytes(src);
        byte[] e = System.Text.Encoding.ASCII.GetBytes("main\0"), t = System.Text.Encoding.ASCII.GetBytes(target + "\0");
        nint code = 0, err = 0;
        int hr;
        fixed (byte* ps = s) fixed (byte* pe = e) fixed (byte* pt = t)
            hr = ((delegate* unmanaged[Stdcall]<byte*, nuint, nint, nint, nint, byte*, byte*, uint, uint, nint*, nint*, int>)_compile)(ps, (nuint)s.Length, 0, 0, 0, pe, pt, 0, 0, &code, &err);
        if (hr != 0)
        {
            string msg = err != 0 ? Marshal.PtrToStringAnsi(((delegate* unmanaged[Stdcall]<nint, nint>)Fn(err, 3))(err)) ?? "" : "";
            Log.Error($"D11: shader {target} no compila (0x{hr:X8}): {msg}");
            Layer.ReleaseCom(err);
            return 0;
        }
        Layer.ReleaseCom(err);
        return code;
    }
    private static nint BlobPtr(nint b) => ((delegate* unmanaged[Stdcall]<nint, nint>)Fn(b, 3))(b);
    private static nuint BlobSize(nint b) => ((delegate* unmanaged[Stdcall]<nint, nuint>)Fn(b, 4))(b);

    private nint VS(string src, out nint blob)
    {
        blob = Compile(src, "vs_4_0");
        if (blob == 0) return 0;
        nint vs = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint, nuint, nint, nint*, int>)Fn(_dev, DevCreateVS))(_dev, BlobPtr(blob), BlobSize(blob), 0, &vs);
        return vs;
    }
    private nint PS(string src)
    {
        nint blob = Compile(src, "ps_4_0");
        if (blob == 0) return 0;
        nint ps = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint, nuint, nint, nint*, int>)Fn(_dev, DevCreatePS))(_dev, BlobPtr(blob), BlobSize(blob), 0, &ps);
        Layer.ReleaseCom(blob);
        return ps;
    }

    private bool CreatePipelines()
    {
        _vsFull = VS(ShFull, out nint b1); Layer.ReleaseCom(b1);
        _psUp = PS(ShUpscale);
        _vsOver = VS(ShOverVS, out nint b2);
        _psOver = PS(ShOverPS);
        if (_vsFull == 0 || _psUp == 0 || _vsOver == 0 || _psOver == 0) { Layer.ReleaseCom(b2); return false; }
        // descripción de los vértices de la superposición (28 bytes: 4 floats, color BGRA, 2 floats)
        byte[] sPos = System.Text.Encoding.ASCII.GetBytes("POSITION\0"), sCol = System.Text.Encoding.ASCII.GetBytes("COLOR\0"), sTex = System.Text.Encoding.ASCII.GetBytes("TEXCOORD\0");
        nint layout = 0;
        fixed (byte* p1 = sPos) fixed (byte* p2 = sCol) fixed (byte* p3 = sTex)
        {
            uint* el = stackalloc uint[21];
            el[0] = (uint)(nint)p1; el[1] = 0; el[2] = 2;  el[3] = 0; el[4] = 0;  el[5] = 0; el[6] = 0;  // R32G32B32A32_FLOAT
            el[7] = (uint)(nint)p2; el[8] = 0; el[9] = 87; el[10] = 0; el[11] = 16; el[12] = 0; el[13] = 0; // B8G8R8A8_UNORM
            el[14] = (uint)(nint)p3; el[15] = 0; el[16] = 16; el[17] = 0; el[18] = 20; el[19] = 0; el[20] = 0; // R32G32_FLOAT
            ((delegate* unmanaged[Stdcall]<nint, uint*, uint, nint, nuint, nint*, int>)Fn(_dev, DevCreateInputLayout))(_dev, el, 3, BlobPtr(b2), BlobSize(b2), &layout);
        }
        Layer.ReleaseCom(b2);
        _layoutOver = layout;
        // constantes (16 bytes)
        uint* bd = stackalloc uint[6];
        bd[0] = 48; bd[1] = 0; bd[2] = 4 /* CONSTANT */; bd[3] = 0; bd[4] = 0; bd[5] = 0;
        nint cb = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Fn(_dev, DevCreateBuffer))(_dev, bd, 0, &cb);
        _cb = cb;
        // muestreo lineal con borde fijo
        uint* sd = stackalloc uint[13];
        new Span<uint>(sd, 13).Clear();
        sd[0] = 0x15; sd[1] = sd[2] = sd[3] = 3; sd[6] = 1; ((float*)sd)[12] = float.MaxValue;
        nint samp = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)Fn(_dev, DevCreateSampler))(_dev, sd, &samp);
        _samp = samp;
        // mezcla con alfa para la superposición
        uint* bl = stackalloc uint[66];
        new Span<uint>(bl, 66).Clear();
        bl[2] = 1; bl[3] = 5; bl[4] = 6; bl[5] = 1; bl[6] = 2; bl[7] = 6; bl[8] = 1; bl[9] = 0xF;
        nint blend = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)Fn(_dev, DevCreateBlend))(_dev, bl, &blend);
        _blendOver = blend;
        // sin descarte de caras
        uint* rd = stackalloc uint[10];
        new Span<uint>(rd, 10).Clear();
        rd[0] = 3; rd[1] = 1; rd[6] = 1;
        nint ras = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)Fn(_dev, DevCreateRaster))(_dev, rd, &ras);
        _raster = ras;
        return true;
    }

    private void SetConstants(float a, float b, float c, float d)
    {
        float* v = stackalloc float[12];
        v[0] = a; v[1] = b; v[2] = c; v[3] = d; v[4] = Layer.Sharpen; v[5] = v[6] = v[7] = 0;
        v[8] = Layer.Gamma; v[9] = Layer.Contrast; v[10] = Layer.Brightness; v[11] = Layer.Saturation;
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, float*, uint, uint, void>)Fn(_ctx, CtxUpdateSub))(_ctx, _cb, 0, 0, v, 0, 0);
    }

    private void ViewportAt(float x, float y, float w, float h)
    {
        float* vp = stackalloc float[6];
        vp[0] = x; vp[1] = y; vp[2] = w; vp[3] = h; vp[4] = 0; vp[5] = 1;
        ((delegate* unmanaged[Stdcall]<nint, uint, float*, void>)Fn(_ctx, CtxRSSetViewports))(_ctx, 1, vp);
    }

    /// <summary>Rectángulo de la imagen dentro del área de la ventana: entera, o con su proporción y bandas negras.</summary>
    private void FitImage(int cw, int ch)
    {
        int x = 0, y = 0, w = cw, h = ch;
        if (Layer.KeepAspect && Width > 0 && Height > 0)
        {
            double s = Math.Min(cw / (double)Width, ch / (double)Height);
            w = Math.Min(cw, (int)Math.Round(Width * s)); h = Math.Min(ch, (int)Math.Round(Height * s));
            if (cw - w <= 2) w = cw; // (diferencias de redondeo: sin bandas)
            if (ch - h <= 2) h = ch;
            x = (cw - w) / 2; y = (ch - h) / 2;
        }
        bool changed = w != Display.ImgW || h != Display.ImgH || x != Display.ImgX || y != Display.ImgY;
        Display.ImgX = x; Display.ImgY = y; Display.ImgW = w; Display.ImgH = h;
        if (changed && (w != cw || h != ch)) Log.Info($"D11: imagen de {w}x{h} en ({x},{y}) dentro de {cw}x{ch}, con bandas negras ([Display] KeepAspect)");
    }

    private void Viewport(float w, float h)
    {
        float* vp = stackalloc float[6];
        vp[0] = 0; vp[1] = 0; vp[2] = w; vp[3] = h; vp[4] = 0; vp[5] = 1;
        ((delegate* unmanaged[Stdcall]<nint, uint, float*, void>)Fn(_ctx, CtxRSSetViewports))(_ctx, 1, vp);
    }

    // ---- presentación ----

    private void CheckResize(bool force = false)
    {
        if (IsIconic(Hwnd) != 0) return;
        ClientSize(out int w, out int h);
        if (w == BBW && h == BBH && !force) return;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)Fn(_ctx, CtxOMSetRT))(_ctx, 0, null, 0);
        Layer.ReleaseCom(_bbRtv); _bbRtv = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, int, uint, int>)Fn(_sc, ScResizeBuffers))(_sc, 0, (uint)w, (uint)h, 0, 0);
        if (hr != 0) Log.Warn($"D11: ResizeBuffers -> 0x{hr:X8}");
        CreateBackBufferView();
        BBW = OutW = w; BBH = OutH = h;
        FitImage(w, h);
        if (!force) Log.Info($"D11: la ventana cambia de tamaño: {w}x{h}");
    }

    public override void Present(bool gameFrame)
    {
        long t = T0();
        PresentCore(gameFrame);
        T1(Pf.Presentar, t);
        if (gameFrame) { PerfFrame(); TraceFrame(); }
    }

    private void PresentCore(bool gameFrame)
    {
        if (_dev == 0) return;
        if (_exclusive) KeepExclusive();
        CheckResize();
        if (_bbRtv == 0) return;
        NoGS();
        Resolve();
        // la imagen del juego, estirada al back buffer con Catmull-Rom
        nint rtv = _bbRtv;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)Fn(_ctx, CtxOMSetRT))(_ctx, 1, &rtv, 0);
        if (Display.ImgW != BBW || Display.ImgH != BBH)
        {
            float* blk = stackalloc float[4];
            blk[0] = blk[1] = blk[2] = 0; blk[3] = 1;
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, void>)Fn(_ctx, CtxClearRTV))(_ctx, _bbRtv, blk); // bandas negras
        }
        ViewportAt(Display.ImgX, Display.ImgY, Display.ImgW, Display.ImgH);
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)Fn(_ctx, CtxRSSetState))(_ctx, _raster);
        ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)Fn(_ctx, CtxOMSetBlend))(_ctx, 0, null, 0xFFFFFFFF);
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)Fn(_ctx, CtxIASetLayout))(_ctx, 0);
        ((delegate* unmanaged[Stdcall]<nint, int, void>)Fn(_ctx, CtxIASetTopology))(_ctx, 4 /* TRIANGLELIST */);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Fn(_ctx, CtxVSSetShader))(_ctx, _vsFull, null, 0);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Fn(_ctx, CtxPSSetShader))(_ctx, _psUp, null, 0);
        SetConstants(RW, RH, 1f / RW, 1f / RH);
        nint cb = _cb, srv = _sceneSrv, samp = _samp;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxPSSetCB))(_ctx, 0, 1, &cb);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxPSSetSRV))(_ctx, 0, 1, &srv);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxPSSetSamplers))(_ctx, 0, 1, &samp);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)Fn(_ctx, CtxDraw))(_ctx, 3, 0);
        nint nul = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxPSSetSRV))(_ctx, 0, 1, &nul);
        if (gameFrame) Overlay.Frame();
        if (Overlay.Any && gameFrame) Overlay.Draw(Display.ImgW / (float)Width, Display.ImgH / (float)Height);
        OptionsMenu.Draw();
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Fn(_sc, ScPresent))(_sc, 1, 0);
        Unbind();
    }

    // ---- superposición ----

    public override nint CreateRgbaTexture(int w, int h, uint* pixels)
    {
        uint* t = stackalloc uint[11]; // D3D11_TEXTURE2D_DESC
        t[0] = (uint)w; t[1] = (uint)h; t[2] = 1; t[3] = 1; t[4] = 87; t[5] = 1; t[6] = 0; t[7] = 1 /* IMMUTABLE */; t[8] = 0x8; t[9] = 0; t[10] = 0;
        uint* init = stackalloc uint[3];
        init[0] = (uint)(nint)pixels; init[1] = (uint)(w * 4); init[2] = 0;
        nint tex = 0, srv = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, uint*, nint*, int>)Fn(_dev, DevCreateTexture2D))(_dev, t, init, &tex);
        if (hr == 0) hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Fn(_dev, DevCreateSRV))(_dev, tex, 0, &srv);
        Layer.ReleaseCom(tex);
        if (hr != 0) { Log.Warn($"D11: textura de la superposición -> 0x{hr:X8}"); return 0; }
        return srv; // (en este motor, la «textura» de la superposición es su vista)
    }

    public override (int w, int h) SceneSize => (RW, RH);

    public override bool DrawOverlayInScene(nint tex, float* verts, int quads)
    {
        if (_dev == 0 || quads <= 0) return false;
        BindScene();
        nint rtv = TargetRtv;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)Fn(_ctx, CtxOMSetRT))(_ctx, 1, &rtv, 0);
        SetDepth(DepthState(false, false, 8), 0);
        int* r = stackalloc int[4];
        r[0] = 0; r[1] = 0; r[2] = RW; r[3] = RH;
        ((delegate* unmanaged[Stdcall]<nint, uint, int*, void>)Fn(_ctx, CtxRSSetScissor))(_ctx, 1, r);
        OverlayDraw(tex, verts, quads, RW, RH, 0, 0, RasterState(1));
        Drew();
        // lo cambiado, a poner de nuevo en el siguiente dibujo del juego
        _curVS = _curPS = _curLayout = _curBlend = _curRaster = 0;
        _curSrv[0] = 0; _curSamp[0] = 0; _vpCur[2] = -1; _bound = false;
        return true;
    }

    public override void DrawOverlay(nint tex, float* verts, int quads)
    {
        if (_dev == 0 || quads <= 0 || _bbRtv == 0) return;
        nint rtv = _bbRtv;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)Fn(_ctx, CtxOMSetRT))(_ctx, 1, &rtv, 0);
        OverlayDraw(tex, verts, quads, BBW, BBH, Display.ImgX, Display.ImgY, 0);
    }

    /// <summary>Cuadrados de la superposición sobre el destino ya puesto (w x h píxeles), desplazados (ox, oy).</summary>
    private void OverlayDraw(nint tex, float* verts, int quads, int w, int h, int ox, int oy, nint raster)
    {
        NoGS();
        // los cuadrados (tiras de 4) como lista de triángulos: 6 vértices cada uno
        int bytes = quads * 6 * 28;
        if (_vb == 0 || _vbSize < bytes)
        {
            Layer.ReleaseCom(_vb);
            _vbSize = Math.Max(bytes, 64 * 1024);
            uint* bd = stackalloc uint[6];
            bd[0] = (uint)_vbSize; bd[1] = 2 /* DYNAMIC */; bd[2] = 1 /* VERTEX */; bd[3] = 0x10000 /* WRITE */; bd[4] = 0; bd[5] = 0;
            nint vb = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Fn(_dev, DevCreateBuffer))(_dev, bd, 0, &vb);
            _vb = vb;
        }
        nint* map = stackalloc nint[3];
        if (((delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, nint*, int>)Fn(_ctx, CtxMap))(_ctx, _vb, 0, 4 /* WRITE_DISCARD */, 0, map) != 0) return;
        float* dst = (float*)map[0];
        int* order = stackalloc int[6] { 0, 1, 2, 2, 1, 3 };
        // Solo escritura: el búfer abierto con WRITE_DISCARD es memoria de la tarjeta (write-combining) y leer de ella es
        // lentísimo. Antes se copiaba el vértice y se sumaba el desplazamiento leyendo del destino («o[0] += ox»): con la
        // lista de misiones (~1300 cuadrados) se llevaba la mitad del procesador (medido con un perfil del procesador).
        for (int q = 0; q < quads; q++)
        {
            float* s = verts + q * 28;
            for (int k = 0; k < 6; k++)
            {
                float* o = dst + (q * 6 + k) * 7, v = s + order[k] * 7;
                o[0] = v[0] + ox; o[1] = v[1] + oy; o[2] = v[2]; o[3] = v[3]; o[4] = v[4]; o[5] = v[5]; o[6] = v[6];
            }
        }
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Fn(_ctx, CtxUnmap))(_ctx, _vb, 0);
        Viewport(w, h);
        if (raster != 0) ((delegate* unmanaged[Stdcall]<nint, nint, void>)Fn(_ctx, CtxRSSetState))(_ctx, raster);
        float* f = stackalloc float[4];
        ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)Fn(_ctx, CtxOMSetBlend))(_ctx, _blendOver, f, 0xFFFFFFFF);
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)Fn(_ctx, CtxIASetLayout))(_ctx, _layoutOver);
        nint vbp = _vb; uint stride = 28, off = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, uint*, void>)Fn(_ctx, CtxIASetVB))(_ctx, 0, 1, &vbp, &stride, &off);
        ((delegate* unmanaged[Stdcall]<nint, int, void>)Fn(_ctx, CtxIASetTopology))(_ctx, 4);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Fn(_ctx, CtxVSSetShader))(_ctx, _vsOver, null, 0);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Fn(_ctx, CtxPSSetShader))(_ctx, _psOver, null, 0);
        SetConstants(w, h, 0, 0);
        nint cb = _cb, srv = tex, samp = _samp;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxVSSetCB))(_ctx, 0, 1, &cb);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxPSSetSRV))(_ctx, 0, 1, &srv);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Fn(_ctx, CtxPSSetSamplers))(_ctx, 0, 1, &samp);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)Fn(_ctx, CtxDraw))(_ctx, (uint)(quads * 6), 0);
    }

    // ---- superficies ----
    // Cada superficie tiene su copia en memoria (s.Cpu) con el formato del juego, y Lock la entrega tal cual. Las
    // texturas tienen además su textura en la GPU (s.Tex, BGRA8; su vista en s.Surf): al desbloquear, la zona
    // bloqueada se convierte a BGRA y se sube. La pantalla (primaria y back buffer) es la imagen interna. Para Lock y
    // GDI se trae a memoria (_screenCpu, la memoria de un DIB, para poder darle un DC) y al soltarla se vuelve a subir.

    private const int CtxCopyResource = 47;
    private nint _staging;                 // copia de la imagen interna que la CPU puede leer
    private bool _gpuNewer;                // la imagen interna tiene algo que _screenCpu todavía no
    private nint _screenDc;
    private nint _conv; private int _convSize; // memoria para convertir a BGRA antes de subir
    private readonly System.Collections.Generic.Dictionary<Surface7, (int x0, int y0, int x1, int y1, bool ro)> _locks = new();
    private readonly System.Collections.Generic.Dictionary<Surface7, (nint dc, nint bmp, nint bits, int stride)> _dcs = new();
    private static int _texErrors;

    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, int* bmi, uint usage, nint* bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern int DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern int DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int GdiFlush();

    private byte* Conv(int bytes)
    {
        if (_convSize < bytes) { if (_conv != 0) NativeMemory.Free((void*)_conv); _conv = (nint)NativeMemory.Alloc((nuint)bytes); _convSize = bytes; }
        return (byte*)_conv;
    }

    public override bool CreateTexture(Surface7 s)
    {
        long t = T0();
        bool r = CreateTextureCore(s);
        T1(Pf.TexturaNueva, t);
        return r;
    }

    private bool CreateTextureCore(Surface7 s)
    {
        if (!Create()) return false;
        s.Cpu = (nint)NativeMemory.AllocZeroed((nuint)(s.W * s.H * s.Bpp));
        uint* t = stackalloc uint[11]; // D3D11_TEXTURE2D_DESC
        bool mips = Mips;
        t[0] = (uint)s.W; t[1] = (uint)s.H; t[2] = mips ? 0u : 1u; t[3] = 1; t[4] = 87; t[5] = 1; t[6] = 0; t[7] = 0 /* DEFAULT */;
        t[8] = mips ? 0x28u : 0x8u; t[9] = 0; t[10] = mips ? 1u : 0u; // (con mipmaps: también destino, para GenerateMips)
        byte* zero = Conv(s.W * s.H * 4);
        new Span<byte>(zero, s.W * s.H * 4).Clear();
        uint* init = stackalloc uint[3];
        init[0] = (uint)(nint)zero; init[1] = (uint)(s.W * 4); init[2] = 0;
        nint tex = 0, srv = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, uint*, nint*, int>)Fn(_dev, DevCreateTexture2D))(_dev, t, mips ? null : init, &tex);
        if (hr == 0) hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Fn(_dev, DevCreateSRV))(_dev, tex, 0, &srv);
        if (hr == 0 && mips)
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, byte*, uint, uint, void>)Fn(_ctx, CtxUpdateSub))(_ctx, tex, 0, 0, zero, (uint)(s.W * 4), 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)Fn(_ctx, CtxGenerateMips))(_ctx, srv);
        }
        if (hr != 0)
        {
            Layer.ReleaseCom(tex);
            if (_texErrors++ < 8) Log.Error($"D11: textura {s.W}x{s.H} formato {s.Fmt} -> 0x{hr:X8}");
            return false;
        }
        s.Tex = tex; s.Surf = srv;
        return true;
    }

    public override bool CreateSysMem(Surface7 s)
    {
        if (!Create()) return false;
        s.Cpu = (nint)NativeMemory.AllocZeroed((nuint)(s.W * s.H * s.Bpp));
        return s.Cpu != 0;
    }

    public override void Release(Surface7 s)
    {
        ReleaseShadow(s);
        if (_dcs.Remove(s, out var d)) { DeleteDC(d.dc); DeleteObject(d.bmp); }
        _locks.Remove(s);
        Layer.ReleaseCom(s.Surf); s.Surf = 0;
        Layer.ReleaseCom(s.Tex); s.Tex = 0;
        if (s.Cpu != 0) NativeMemory.Free((void*)s.Cpu);
        s.Cpu = 0;
    }

    /// <summary>La pantalla en memoria: el DIB (con su DC) creado una vez, al tamaño del juego.</summary>
    private bool ScreenMem()
    {
        if (_screenCpu != 0) return true;
        int* bmi = stackalloc int[10];
        new Span<int>(bmi, 10).Clear();
        bmi[0] = 40; bmi[1] = Width; bmi[2] = -Height; bmi[3] = 1 | (32 << 16);
        nint dc = CreateCompatibleDC(0), bits = 0;
        nint bmp = CreateDIBSection(dc, bmi, 0, &bits, 0, 0);
        if (bmp == 0 || bits == 0) { Log.Error("D11: no se pudo crear la pantalla en memoria (DIB)"); DeleteDC(dc); return false; }
        SelectObject(dc, bmp);
        _screenDc = dc; _screenCpu = bits;
        return true;
    }

    /// <summary>La imagen interna a _screenCpu (solo si ha cambiado desde la última vez).</summary>
    private void ReadScreen()
    {
        if (!_gpuNewer) return;
        long t = T0();
        ReadScreenCore();
        T1(Pf.LeerPantalla, t);
    }

    private void ReadScreenCore()
    {
        if (!_gpuNewer || _scene == 0) return;
        Resolve();
        if (_staging == 0)
        {
            uint* t = stackalloc uint[11];
            t[0] = (uint)Width; t[1] = (uint)Height; t[2] = 1; t[3] = 1; t[4] = 87; t[5] = 1; t[6] = 0;
            t[7] = 3 /* STAGING */; t[8] = 0; t[9] = 0x20000 /* CPU READ */; t[10] = 0;
            nint st = 0;
            int h = ((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Fn(_dev, DevCreateTexture2D))(_dev, t, 0, &st);
            if (h != 0) { Log.Error($"D11: copia legible de la imagen -> 0x{h:X8}"); return; }
            _staging = st;
        }
        if (Scaled) Downsample();
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Fn(_ctx, CtxCopyResource))(_ctx, _staging, Scaled ? _small : _scene);
        nint* m = stackalloc nint[3]; // D3D11_MAPPED_SUBRESOURCE { pData, RowPitch, DepthPitch }
        if (((delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, nint*, int>)Fn(_ctx, CtxMap))(_ctx, _staging, 0, 1 /* READ */, 0, m) != 0) return;
        byte* src = (byte*)m[0]; int sp = (int)m[1], dp = Width * 4;
        for (int y = 0; y < Height; y++) Buffer.MemoryCopy(src + y * sp, (byte*)_screenCpu + y * dp, dp, dp);
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Fn(_ctx, CtxUnmap))(_ctx, _staging, 0);
        _gpuNewer = false;
    }

    private void Upload(nint res, int x0, int y0, int x1, int y1, byte* bits, int pitch)
    {
        if (x1 <= x0 || y1 <= y0) return;
        uint* box = stackalloc uint[6];
        box[0] = (uint)x0; box[1] = (uint)y0; box[2] = 0; box[3] = (uint)x1; box[4] = (uint)y1; box[5] = 1;
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint*, byte*, uint, uint, void>)Fn(_ctx, CtxUpdateSub))(_ctx, res, 0, box, bits, (uint)pitch, 0);
    }

    private void UploadScreen(int x0, int y0, int x1, int y1)
    {
        long t = T0();
        UploadScreenCore(x0, y0, x1, y1);
        T1(Pf.EscribirPantalla, t);
    }

    private void UploadScreenCore(int x0, int y0, int x1, int y1)
    {
        if (x1 <= x0 || y1 <= y0) return;
        if (Scaled)
        {
            Upload(_small, x0, y0, x1, y1, (byte*)_screenCpu + y0 * Width * 4 + x0 * 4, Width * 4);
            Upsample(x0, y0, x1, y1);
        }
        else Upload(_scene, x0, y0, x1, y1, (byte*)_screenCpu + y0 * Width * 4 + x0 * 4, Width * 4);
        if (_msRtv != 0) { _sceneNewer = true; _bound = false; }
    }

    /// <summary>Una zona de la copia en memoria de una textura, convertida a BGRA8, a su textura de la GPU.</summary>
    private void UploadTexture(Surface7 s, int x0, int y0, int x1, int y1)
    {
        long t = T0();
        UploadTextureCore(s, x0, y0, x1, y1);
        T1(Pf.SubidaTextura, t);
    }

    private void UploadTextureCore(Surface7 s, int x0, int y0, int x1, int y1)
    {
        if (s.Tex == 0 || s.Cpu == 0 || x1 <= x0 || y1 <= y0) return;
        int w = x1 - x0, h = y1 - y0, sp = s.W * s.Bpp;
        uint* dst = (uint*)Conv(w * h * 4);
        for (int y = 0; y < h; y++)
        {
            byte* row = (byte*)s.Cpu + (y0 + y) * sp + x0 * s.Bpp;
            uint* o = dst + y * w;
            switch (s.Fmt)
            {
                case 21: Buffer.MemoryCopy(row, o, w * 4, w * 4); break;
                case 20: for (int x = 0; x < w; x++) o[x] = 0xFF000000u | (uint)(row[x * 3] | row[x * 3 + 1] << 8 | row[x * 3 + 2] << 16); break;
                case 23:
                    for (int x = 0; x < w; x++)
                    {
                        uint p = ((ushort*)row)[x];
                        uint r = (p >> 11) & 31, g = (p >> 5) & 63, b = p & 31;
                        o[x] = 0xFF000000u | ((r << 3 | r >> 2) << 16) | ((g << 2 | g >> 4) << 8) | (b << 3 | b >> 2);
                    }
                    break;
                case 24 or 25:
                    for (int x = 0; x < w; x++)
                    {
                        uint p = ((ushort*)row)[x];
                        uint r = (p >> 10) & 31, g = (p >> 5) & 31, b = p & 31;
                        uint a = s.Fmt == 24 || (p & 0x8000) != 0 ? 0xFF000000u : 0;
                        o[x] = a | ((r << 3 | r >> 2) << 16) | ((g << 3 | g >> 2) << 8) | (b << 3 | b >> 2);
                    }
                    break;
                case 26:
                    for (int x = 0; x < w; x++)
                    {
                        uint p = ((ushort*)row)[x];
                        o[x] = ((p >> 12) & 15) * 0x11000000u | ((p >> 8) & 15) * 0x110000u | ((p >> 4) & 15) * 0x1100u | (p & 15) * 0x11u;
                    }
                    break;
                default: for (int x = 0; x < w; x++) o[x] = ((uint*)row)[x] | 0xFF000000u; break; // X8R8G8B8
            }
        }
        Upload(s.Tex, x0, y0, x1, y1, (byte*)dst, w * 4);
        if (Mips) ((delegate* unmanaged[Stdcall]<nint, nint, void>)Fn(_ctx, CtxGenerateMips))(_ctx, s.Surf);
    }

    private static void Clip(Surface7 s, int* rect, out int x0, out int y0, out int x1, out int y1)
    {
        x0 = 0; y0 = 0; x1 = s.W; y1 = s.H;
        if (rect == null) return;
        x0 = Math.Clamp(rect[0], 0, s.W); y0 = Math.Clamp(rect[1], 0, s.H);
        x1 = Math.Clamp(rect[2], x0, s.W); y1 = Math.Clamp(rect[3], y0, s.H);
    }

    public override bool Lock(Surface7 s, int* rect, uint flags, out byte* bits, out int pitch)
    {
        bits = null; pitch = 0;
        Clip(s, rect, out int x0, out int y0, out int x1, out int y1);
        byte* mem;
        if (s.Screen)
        {
            if (!ScreenMem()) return false;
            ReadScreen();
            mem = (byte*)_screenCpu; pitch = Width * 4;
        }
        else
        {
            if (s.Cpu == 0) return false;
            mem = (byte*)s.Cpu; pitch = s.W * s.Bpp;
        }
        _locks[s] = (x0, y0, x1, y1, (flags & 0x10) != 0 /* DDLOCK_READONLY */);
        bits = mem + y0 * pitch + x0 * s.Bpp;
        return true;
    }

    public override void Unlock(Surface7 s)
    {
        if (!_locks.Remove(s, out var l) || l.ro) return;
        if (s.Screen) UploadScreen(l.x0, l.y0, l.x1, l.y1);
        else UploadTexture(s, l.x0, l.y0, l.x1, l.y1);
    }

    public override int GetDC(Surface7 s, nint* dc)
    {
        long t = T0();
        int r = GetDCCore(s, dc);
        T1(Pf.Gdi, t);
        return r;
    }

    private int GetDCCore(Surface7 s, nint* dc)
    {
        *dc = 0;
        if (s.Screen)
        {
            if (!ScreenMem()) return Layer.E_FAIL;
            ReadScreen();
            *dc = _screenDc;
            return 0;
        }
        if (s.Cpu == 0 || _dcs.ContainsKey(s)) return Layer.E_FAIL;
        // DIB temporal con el formato de la superficie: se copia la memoria, y de vuelta en ReleaseDC
        int bpp = s.Bpp * 8, stride = (s.W * bpp + 31) / 32 * 4, sp = s.W * s.Bpp;
        int* bmi = stackalloc int[13];
        new Span<int>(bmi, 13).Clear();
        bmi[0] = 40; bmi[1] = s.W; bmi[2] = -s.H; bmi[3] = 1 | (bpp << 16);
        if (s.Bpp == 2) { bmi[4] = 3 /* BI_BITFIELDS */; bmi[10] = (int)s.Pf[4]; bmi[11] = (int)s.Pf[5]; bmi[12] = (int)s.Pf[6]; }
        nint mdc = CreateCompatibleDC(0), bits = 0;
        nint bmp = CreateDIBSection(mdc, bmi, 0, &bits, 0, 0);
        if (bmp == 0 || bits == 0) { DeleteDC(mdc); return Layer.E_FAIL; }
        SelectObject(mdc, bmp);
        for (int y = 0; y < s.H; y++) Buffer.MemoryCopy((byte*)s.Cpu + y * sp, (byte*)bits + y * stride, sp, sp);
        _dcs[s] = (mdc, bmp, bits, stride);
        *dc = mdc;
        return 0;
    }

    public override int ReleaseDC(Surface7 s, nint dc)
    {
        long t = T0();
        int r = ReleaseDCCore(s, dc);
        T1(Pf.Gdi, t);
        return r;
    }

    private int ReleaseDCCore(Surface7 s, nint dc)
    {
        GdiFlush();
        if (s.Screen) { UploadScreen(0, 0, Width, Height); return 0; }
        if (!_dcs.Remove(s, out var d)) return Layer.E_FAIL;
        int sp = s.W * s.Bpp;
        for (int y = 0; y < s.H; y++) Buffer.MemoryCopy((byte*)d.bits + y * d.stride, (byte*)s.Cpu + y * sp, sp, sp);
        DeleteDC(d.dc); DeleteObject(d.bmp);
        UploadTexture(s, 0, 0, s.W, s.H);
        return 0;
    }

    public override int FillScreen(int* rect, uint color)
    {
        if (rect == null || (rect[0] <= 0 && rect[1] <= 0 && rect[2] >= Width && rect[3] >= Height)) return ClearScene(color);
        // un trozo: en memoria y de vuelta (D3D11.0 no tiene borrado de una parte)
        if (!ScreenMem()) return Layer.E_FAIL;
        ReadScreen();
        int x0 = Math.Clamp(rect[0], 0, Width), y0 = Math.Clamp(rect[1], 0, Height), x1 = Math.Clamp(rect[2], x0, Width), y1 = Math.Clamp(rect[3], y0, Height);
        for (int y = y0; y < y1; y++)
        {
            uint* row = (uint*)_screenCpu + y * Width;
            for (int x = x0; x < x1; x++) row[x] = color | 0xFF000000u;
        }
        UploadScreen(x0, y0, x1, y1);
        return 0;
    }

    private int ClearScene(uint color)
    {
        if (_sceneRtv == 0) return 0;
        float* c = stackalloc float[4];
        c[0] = ((color >> 16) & 0xFF) / 255f; c[1] = ((color >> 8) & 0xFF) / 255f; c[2] = (color & 0xFF) / 255f; c[3] = 1;
        ((delegate* unmanaged[Stdcall]<nint, nint, float*, void>)Fn(_ctx, CtxClearRTV))(_ctx, _sceneRtv, c);
        if (_msRtv != 0) ((delegate* unmanaged[Stdcall]<nint, nint, float*, void>)Fn(_ctx, CtxClearRTV))(_ctx, _msRtv, c);
        _gpuNewer = true; _msaaNewer = false; _sceneNewer = false;
        return 0;
    }

    // ---- dispositivo 3D: el dibujo está en Backend11.Draw.cs y la tubería fija en Backend11.Ffp.cs ----

    public override void Evict() { }
    public override bool BeginFrame() => true;
    public override int BeginScene() => 0;
    public override int EndScene() => 0;
}
