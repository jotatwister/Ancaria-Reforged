using System;
using System.Runtime.InteropServices;
using SacredFix.Core;
using SacredFix.Native;
using static SacredFix.Proxy.D9.Display;

namespace SacredFix.Proxy.D9;

/// <summary>
/// El dispositivo Direct3D 9 único de la capa (el juego solo crea un dispositivo de Direct3D 7). Se crea al crear la
/// superficie primaria, con la ventana de SetCooperativeLevel y el tamaño de SetDisplayMode.
/// </summary>
internal static unsafe class Device9
{
    public static nint D3D;      // IDirect3D9
    public static nint Dev;      // IDirect3DDevice9
    public static nint BackBuffer, DepthStencil; // IDirect3DSurface9
    // (ventana, tamaños y modo: en Display)

    // IDirect3D9
    private const int D3D_CreateDevice = 16, D3D_CheckDeviceMultiSampleType = 11;
    // IDirect3DDevice9
    public const int Present = 17, GetBackBuffer = 18, CreateTexture = 23, CreateOffscreenPlainSurface = 36,
        ColorFill = 35, GetDepthStencilSurface = 40, BeginScene = 41, EndScene = 42, Clear = 43, SetTransform = 44,
        GetTransform = 45, MultiplyTransform = 46, SetViewport = 47, GetViewport = 48, SetMaterial = 49,
        GetMaterial = 50, SetLight = 51, GetLight = 52, LightEnable = 53, GetLightEnable = 54, SetRenderState = 57,
        SetTexture = 65, SetTextureStageState = 67, SetSamplerState = 69, DrawPrimitiveUP = 83,
        DrawIndexedPrimitiveUP = 84, SetFVF = 89, TestCooperativeLevel = 3, EvictManagedResources = 5;

    private static nint Direct3DCreate9(uint sdk)
    {
        nint m = Win32.LoadLibraryW(System.IO.Path.Combine(Win32.GetSystemDirectory(), "d3d9.dll"));
        nint f = m != 0 ? Win32.GetProcAddress(m, "Direct3DCreate9") : 0;
        return f != 0 ? ((delegate* unmanaged[Stdcall]<uint, nint>)f)(sdk) : 0;
    }

    public static bool Create()
    {
        if (Dev != 0) return true;
        try
        {
            if (!Fullscreen) PrepareWindow();
            else FullscreenSize();
            D3D = Direct3DCreate9(32);
            if (D3D == 0) { Log.Error("D9: Direct3DCreate9 ha fallado"); return false; }
            int* pp = (int*)NativeMemory.AllocZeroed(14 * 4); // se guarda para Reset
            _pp = pp;
            if (BBW == 0) { BBW = Width; BBH = Height; }
            pp[0] = BBW; pp[1] = BBH;
            pp[2] = 22;                 // D3DFMT_X8R8G8B8
            pp[3] = 1;                  // un back buffer
            pp[6] = Fullscreen ? 1 : 3; // DISCARD; en ventana COPY (para presentar solo el área de la ventana)
            pp[7] = (int)Hwnd;
            pp[8] = Fullscreen ? 0 : 1; // Windowed
            pp[9] = 1;                  // EnableAutoDepthStencil
            pp[10] = 75;                // D3DFMT_D24S8
            pp[11] = 1;                 // D3DPRESENTFLAG_LOCKABLE_BACKBUFFER (el juego bloquea la primaria)
            pp[12] = 0;                 // frecuencia por defecto
            pp[13] = 1;                 // D3DPRESENT_INTERVAL_ONE (sincronía vertical)
            nint dev = 0;
            // D3DCREATE_FPU_PRESERVE 2 | MULTITHREADED 4 | HARDWARE_VERTEXPROCESSING 0x40
                        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, int, nint, uint, int*, nint*, int>)Layer.Fn(D3D, D3D_CreateDevice))(D3D, 0, 1, Hwnd, 0x46, pp, &dev);
            if (hr != 0)
            {
                Log.Warn($"D9: CreateDevice (T&L por hardware) -> 0x{hr:X8}; se prueba por software");
                hr = ((delegate* unmanaged[Stdcall]<nint, uint, int, nint, uint, int*, nint*, int>)Layer.Fn(D3D, D3D_CreateDevice))(D3D, 0, 1, Hwnd, 0x26, pp, &dev);
            }
            if (hr != 0 && Fullscreen)
            {
                // la exclusiva no se pudo crear: sin bordes, que no depende de los modos del monitor
                Log.Warn($"D9: CreateDevice en exclusiva -> 0x{hr:X8}; se usa la pantalla completa sin bordes");
                Fullscreen = false;
                PrepareWindow();
                pp[0] = BBW; pp[1] = BBH; pp[6] = 3; pp[8] = 0 + 1;
                hr = ((delegate* unmanaged[Stdcall]<nint, uint, int, nint, uint, int*, nint*, int>)Layer.Fn(D3D, D3D_CreateDevice))(D3D, 0, 1, Hwnd, 0x46, pp, &dev);
                if (hr != 0) hr = ((delegate* unmanaged[Stdcall]<nint, uint, int, nint, uint, int*, nint*, int>)Layer.Fn(D3D, D3D_CreateDevice))(D3D, 0, 1, Hwnd, 0x26, pp, &dev);
            }
            if (hr != 0) { Log.Error($"D9: CreateDevice -> 0x{hr:X8}"); return false; }
            Dev = dev;
            nint bb = 0, ds = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, int, nint*, int>)Layer.Fn(Dev, GetBackBuffer))(Dev, 0, 0, 0, &bb);
            ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Layer.Fn(Dev, GetDepthStencilSurface))(Dev, &ds);
            BackBuffer = bb; DepthStencil = ds;
            Log.Info($"D9: dispositivo Direct3D 9 creado: {Width}x{Height} {(Fullscreen ? "pantalla completa" : "ventana")}, ventana 0x{Hwnd:X}");
            SetupTargets();
            if (!Fullscreen) MouseScale.Install(); // en ventana el tamaño puede cambiar (maximizar, arrastrar el borde)
            return true;
        }
        catch (Exception ex) { Log.Error("D9: " + ex.Message); return false; }
    }

    // ---- Ventana (sin bordes o con marco) ----
    // Con Direct3D 9 en modo ventana el dispositivo no se pierde con Alt+Tab ni al pinchar en otro monitor. Sin
    // bordes: ventana WS_POPUP del tamaño del monitor (el back buffer, Width x Height, se escala al presentar). Con
    // marco: WS_OVERLAPPEDWINDOW con el área de dibujo de Width x Height, centrada en el monitor.
    /// <summary>
    /// Exclusiva: el back buffer mide como el monitor en su modo actual (siempre válido), no como el juego. Con el tamaño
    /// del juego (p. ej. 1364x768, que no es un modo del monitor) CreateDevice devolvía D3DERR_INVALIDCALL y el juego se
    /// cerraba; y con un modo real de 1366x768 el monitor cambiaría de resolución y se vería borroso. Así se usa el mismo
    /// escalado que sin bordes (SceneTex + Catmull-Rom, ratón con MouseScale).
    /// </summary>
    private static void FullscreenSize()
    {
        if (Hwnd == 0) return;
        int* mi = stackalloc int[10];
        mi[0] = 40;
        nint mon = Win32.MonitorFromWindow(Hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (mon == 0 || !Win32.GetMonitorInfoW(mon, mi)) return;
        BBW = mi[3] - mi[1]; BBH = mi[4] - mi[2];
        Log.Info($"D9: pantalla completa exclusiva a la resolución del monitor: {BBW}x{BBH}, imagen de {Width}x{Height}");
    }

    public static void PrepareWindow()
    {
        if (Hwnd == 0) return;
        int* mi = stackalloc int[10];
        mi[0] = 40;
        nint mon = Win32.MonitorFromWindow(Hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        int mx = 0, my = 0, mw = Width, mh = Height;
        if (mon != 0 && Win32.GetMonitorInfoW(mon, mi)) { mx = mi[1]; my = mi[2]; mw = mi[3] - mi[1]; mh = mi[4] - mi[2]; }
        const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, WS_OVERLAPPEDWINDOW = 0x00CF0000;
        const uint SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40, SWP_NOZORDER = 0x4;
        if (Layer.WindowStyle == 2)
        {
            uint style = WS_OVERLAPPEDWINDOW | WS_VISIBLE;
            Win32.SetWindowLongW(Hwnd, GWL_STYLE, (int)style);
            Win32.SetWindowLongW(Hwnd, GWL_EXSTYLE, 0);
            int* r = stackalloc int[4];
            r[0] = 0; r[1] = 0; r[2] = Width; r[3] = Height;
            Win32.AdjustWindowRectEx(r, style, false, 0);
            int ww = r[2] - r[0], wh = r[3] - r[1];
            Win32.SetWindowPos(Hwnd, 0, mx + Math.Max(0, (mw - ww) / 2), my + Math.Max(0, (mh - wh) / 2), ww, wh, SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOZORDER);
            BBW = mw; BBH = mh;
            Log.Info($"D9: ventana con marco, área {Width}x{Height} (back buffer del tamaño del monitor: {mw}x{mh})");
        }
        else
        {
            Win32.SetWindowLongW(Hwnd, GWL_STYLE, unchecked((int)(WS_POPUP | WS_VISIBLE)));
            Win32.SetWindowLongW(Hwnd, GWL_EXSTYLE, 0);
            Win32.SetWindowPos(Hwnd, 0, mx, my, mw, mh, SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOZORDER);
            BBW = mw; BBH = mh;
            Log.Info($"D9: pantalla completa sin bordes: {mw}x{mh} en ({mx},{my}), imagen de {Width}x{Height}");
        }
    }

    // ---- Antialiasing (MSAA) ----
    // El juego bloquea el back buffer y le pide un DC (pantalla de carga), y un back buffer con MSAA no se puede
    // bloquear: el 3D se dibuja en un render target aparte con MSAA (y su Z-buffer MSAA) y al presentar se copia al
    // back buffer con StretchRect, que resuelve el antialiasing.
    public static nint MsaaTarget, MsaaDepth;
    private const int CreateRenderTarget = 28, CreateDepthStencilSurface = 29, StretchRect = 34, SetRenderTarget = 37, SetDepthStencilSurface = 39;

    private static void SetupMultiSample()
    {
        int ms = Layer.MultiSample;
        if (ms < 2) return;
        uint q = 0, qz = 0;
        var check = (delegate* unmanaged[Stdcall]<nint, uint, int, int, int, int, uint*, int>)Layer.Fn(D3D, D3D_CheckDeviceMultiSampleType);
        while (ms >= 2 && (check(D3D, 0, 1, 22, Fullscreen ? 0 : 1, ms, &q) != 0 || check(D3D, 0, 1, 75, Fullscreen ? 0 : 1, ms, &qz) != 0)) ms--;
        if (ms < 2) { Log.Warn($"D9: la tarjeta no admite MSAA {Layer.MultiSample}x"); return; }
        nint rt = 0, dsm = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, int, int, uint, int, nint*, nint, int>)Layer.Fn(Dev, CreateRenderTarget))(Dev, (uint)Width, (uint)Height, 22, ms, 0, 0, &rt, 0);
        if (hr == 0) hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, int, int, uint, int, nint*, nint, int>)Layer.Fn(Dev, CreateDepthStencilSurface))(Dev, (uint)Width, (uint)Height, 75, ms, 0, 0, &dsm, 0);
        if (hr != 0) { Log.Error($"D9: no se pudo crear el destino MSAA {ms}x: 0x{hr:X8}"); Layer.ReleaseCom(rt); Layer.ReleaseCom(dsm); return; }
        MsaaTarget = rt; MsaaDepth = dsm;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Layer.Fn(Dev, SetRenderTarget))(Dev, 0, rt);
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Layer.Fn(Dev, SetDepthStencilSurface))(Dev, dsm);
        Log.Info($"D9: antialiasing MSAA {ms}x activado");
    }

    /// <summary>
    /// El juego ha escrito en la pantalla con GDI o Lock (pantalla de carga): en ese fotograma no se resuelve el MSAA,
    /// que traería encima la última imagen 3D y taparía lo escrito.
    /// </summary>
    public static bool CpuWrote;

    /// <summary>Antes de Present: lo dibujado con MSAA al lienzo (resuelto).</summary>
    public static void Resolve()
    {
        if (CpuWrote) { CpuWrote = false; return; }
        if (MsaaTarget == 0) return;
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, nint, int, int>)Layer.Fn(Dev, StretchRect))(Dev, MsaaTarget, 0, Canvas, 0, 0);
    }

    // ---- Escalado al monitor (sin bordes) ----
    // Si el back buffer (monitor) no mide lo mismo que la imagen del juego, el juego dibuja en una textura de su
    // tamaño (SceneTex, con su Z-buffer) y al presentar se estira al back buffer con un píxel shader Catmull-Rom
    // (bicúbico de 9 lecturas bilineales), mucho más nítido que el estirado lineal. El estado del dispositivo se guarda
    // y se restaura con un state block. Sin shader (no hay d3dcompiler_47): StretchRect lineal.
    public static nint SceneTex, SceneSurf, GameDepth, Shader, StateBlock;
    // Espejo de la escena para GDI y Lock (a una textura de render no se le puede pedir DC ni bloquear)
    public static nint Mirror;

    /// <summary>Antes de GetDC / Lock sobre la pantalla: la escena al espejo; devuelve la superficie a usar.</summary>
    public static nint BeginCpuAccess()
    {
        if (SceneSurf == 0) return BackBuffer;
        if (Mirror == 0) return 0;
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, nint, int, int>)Layer.Fn(Dev, StretchRect))(Dev, SceneSurf, 0, Mirror, 0, 0);
        return Mirror;
    }

    /// <summary>Tras ReleaseDC / Unlock: el espejo de vuelta a la escena.</summary>
    public static void EndCpuAccess()
    {
        CpuWrote = true;
        if (SceneSurf == 0 || Mirror == 0) return;
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, nint, int, int>)Layer.Fn(Dev, StretchRect))(Dev, Mirror, 0, SceneSurf, 0, 0);
    }
    /// <summary>Superficie del tamaño del juego con la imagen final: la de la escena si se escala; si no, el back buffer.</summary>
    public static nint Canvas => SceneSurf != 0 ? SceneSurf : BackBuffer;

    private static void SetupTargets()
    {
        if (Scaling)
        {
            nint tex = 0, surf = 0, ds = 0;
            int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, int, int, nint*, nint, int>)Layer.Fn(Dev, CreateTexture))(Dev, (uint)Width, (uint)Height, 1, 1 /* RENDERTARGET */, 22, 0 /* DEFAULT */, &tex, 0);
            if (hr == 0) ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Layer.Fn(tex, 18))(tex, 0, &surf);
            if (hr == 0) hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, int, int, uint, int, nint*, nint, int>)Layer.Fn(Dev, CreateDepthStencilSurface))(Dev, (uint)Width, (uint)Height, 75, 0, 0, 0, &ds, 0);
            if (hr != 0) { Log.Error($"D9: no se pudo crear la escena de {Width}x{Height}: 0x{hr:X8}"); Layer.ReleaseCom(surf); Layer.ReleaseCom(tex); }
            else
            {
                SceneTex = tex; SceneSurf = surf; GameDepth = ds;
                nint mirror = 0;
                int hm = ((delegate* unmanaged[Stdcall]<nint, uint, uint, int, int, nint*, nint, int>)Layer.Fn(Dev, CreateOffscreenPlainSurface))(Dev, (uint)Width, (uint)Height, 22, 0 /* DEFAULT */, &mirror, 0);
                if (hm != 0) Log.Warn($"D9: no se pudo crear el espejo de la escena: 0x{hm:X8}");
                Mirror = mirror;
                ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Layer.Fn(Dev, SetRenderTarget))(Dev, 0, surf);
                ((delegate* unmanaged[Stdcall]<nint, nint, int>)Layer.Fn(Dev, SetDepthStencilSurface))(Dev, ds);
                if (Shader == 0) Shader = CompileUpscaler();
                Log.Info($"D9: imagen de {Width}x{Height} escalada a {BBW}x{BBH} ({(Shader != 0 ? "Catmull-Rom" : "lineal")})");
                MouseScale.Install();
            }
        }
        SetupMultiSample();
        if (StateBlock == 0)
        {
            nint sb = 0;
            ((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Layer.Fn(Dev, 59))(Dev, 1 /* D3DSBT_ALL */, &sb);
            StateBlock = sb;
        }
    }

    /// <summary>Destino de dibujo y Z-buffer (para dibujar algo propio encima, p. ej. el contador de FPS).</summary>
    public static void SetTarget(nint rt, nint depth)
    {
        ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Layer.Fn(Dev, SetRenderTarget))(Dev, 0, rt);
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Layer.Fn(Dev, SetDepthStencilSurface))(Dev, depth);
    }

    /// <summary>El destino y el Z-buffer en los que dibuja el juego.</summary>
    public static void RestoreTarget()
    {
        nint rt = MsaaTarget != 0 ? MsaaTarget : SceneSurf != 0 ? SceneSurf : BackBuffer;
        nint ds = MsaaDepth != 0 ? MsaaDepth : GameDepth != 0 ? GameDepth : DepthStencil;
        SetTarget(rt, ds);
    }

    private static void ReleaseTargets()
    {
        Layer.ReleaseCom(StateBlock); StateBlock = 0;
        Layer.ReleaseCom(Mirror); Mirror = 0;
        Layer.ReleaseCom(SceneSurf); SceneSurf = 0;
        Layer.ReleaseCom(SceneTex); SceneTex = 0;
        Layer.ReleaseCom(GameDepth); GameDepth = 0;
    }

    private const string UpscalerSource = @"
sampler2D s0 : register(s0);
float4 ts : register(c0); // ancho, alto, 1/ancho, 1/alto de la imagen del juego
float4 main(float2 uv : TEXCOORD0) : COLOR
{
    float2 p = uv * ts.xy;
    float2 t1 = floor(p - 0.5) + 0.5;
    float2 f = p - t1;
    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 t0 = (t1 - 1.0) * ts.zw;
    float2 t3 = (t1 + 2.0) * ts.zw;
    float2 t12 = (t1 + w2 / w12) * ts.zw;
    float4 r = 0;
    r += tex2Dlod(s0, float4(t0.x,  t0.y,  0, 0)) * w0.x  * w0.y;
    r += tex2Dlod(s0, float4(t12.x, t0.y,  0, 0)) * w12.x * w0.y;
    r += tex2Dlod(s0, float4(t3.x,  t0.y,  0, 0)) * w3.x  * w0.y;
    r += tex2Dlod(s0, float4(t0.x,  t12.y, 0, 0)) * w0.x  * w12.y;
    r += tex2Dlod(s0, float4(t12.x, t12.y, 0, 0)) * w12.x * w12.y;
    r += tex2Dlod(s0, float4(t3.x,  t12.y, 0, 0)) * w3.x  * w12.y;
    r += tex2Dlod(s0, float4(t0.x,  t3.y,  0, 0)) * w0.x  * w3.y;
    r += tex2Dlod(s0, float4(t12.x, t3.y,  0, 0)) * w12.x * w3.y;
    r += tex2Dlod(s0, float4(t3.x,  t3.y,  0, 0)) * w3.x  * w3.y;
    return float4(saturate(r.rgb), 1.0);
}";

    private static nint CompileUpscaler()
    {
        try
        {
            nint m = Win32.LoadLibraryW(System.IO.Path.Combine(Win32.GetSystemDirectory(), "d3dcompiler_47.dll"));
            nint f = m != 0 ? Win32.GetProcAddress(m, "D3DCompile") : 0;
            if (f == 0) { Log.Warn("D9: no está d3dcompiler_47.dll: escalado lineal"); return 0; }
            byte[] src = System.Text.Encoding.ASCII.GetBytes(UpscalerSource);
            byte[] entry = System.Text.Encoding.ASCII.GetBytes("main\0"), target = System.Text.Encoding.ASCII.GetBytes("ps_3_0\0");
            nint code = 0, errors = 0;
            int hr;
            fixed (byte* s = src) fixed (byte* e = entry) fixed (byte* t = target)
                hr = ((delegate* unmanaged[Stdcall]<byte*, nuint, nint, nint, nint, byte*, byte*, uint, uint, nint*, nint*, int>)f)(s, (nuint)src.Length, 0, 0, 0, e, t, 0, 0, &code, &errors);
            if (hr != 0)
            {
                string msg = errors != 0 ? Marshal.PtrToStringAnsi(((delegate* unmanaged[Stdcall]<nint, nint>)Layer.Fn(errors, 3))(errors)) ?? "" : "";
                Log.Error($"D9: el shader de escalado no compila (0x{hr:X8}): {msg}");
                Layer.ReleaseCom(errors);
                return 0;
            }
            nint bytecode = ((delegate* unmanaged[Stdcall]<nint, nint>)Layer.Fn(code, 3))(code);
            nint ps = 0;
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Layer.Fn(Dev, 106))(Dev, bytecode, &ps);
            Layer.ReleaseCom(code); Layer.ReleaseCom(errors);
            if (hr != 0) { Log.Error($"D9: CreatePixelShader -> 0x{hr:X8}"); return 0; }
            return ps;
        }
        catch (Exception ex) { Log.Error("D9: shader de escalado: " + ex.Message); return 0; }
    }

    /// <summary>La imagen del juego (SceneTex) al back buffer, estirada.</summary>
    public static void Upscale()
    {
        if (SceneTex == 0) return;
        if (Shader == 0 || StateBlock == 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, nint, int, int>)Layer.Fn(Dev, StretchRect))(Dev, SceneSurf, 0, BackBuffer, 0, 2 /* LINEAR */);
            return;
        }
        var sb = StateBlock;
        ((delegate* unmanaged[Stdcall]<nint, int>)Layer.Fn(sb, 4))(sb); // Capture
        ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Layer.Fn(Dev, SetRenderTarget))(Dev, 0, BackBuffer);
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Layer.Fn(Dev, SetDepthStencilSurface))(Dev, 0);
        RS(7, 0); RS(15, 0); RS(27, 0); RS(22, 1); RS(137, 0); RS(28, 0); RS(52, 0); RS(174, 0); RS(168, 0xF); RS(194, 0); RS(161, 0);
        var ss = (delegate* unmanaged[Stdcall]<nint, uint, int, uint, int>)Layer.Fn(Dev, SetSamplerState);
        ss(Dev, 0, 5, 2); ss(Dev, 0, 6, 2); ss(Dev, 0, 7, 0); ss(Dev, 0, 1, 3); ss(Dev, 0, 2, 3); ss(Dev, 0, 11, 0);
        ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Layer.Fn(Dev, SetTexture))(Dev, 0, SceneTex);
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Layer.Fn(Dev, 92))(Dev, 0);       // SetVertexShader(NULL)
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Layer.Fn(Dev, 107))(Dev, Shader); // SetPixelShader
        float* c = stackalloc float[4];
        c[0] = Width; c[1] = Height; c[2] = 1f / Width; c[3] = 1f / Height;
        ((delegate* unmanaged[Stdcall]<nint, uint, float*, uint, int>)Layer.Fn(Dev, 109))(Dev, 0, c, 1);
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Layer.Fn(Dev, SetFVF))(Dev, 0x104); // XYZRHW | TEX1
        float* v = stackalloc float[24];
        float x1 = OutW - 0.5f, y1 = OutH - 0.5f;
        float[] q = { -0.5f, -0.5f, 0, 1, 0, 0,  x1, -0.5f, 0, 1, 1, 0,  -0.5f, y1, 0, 1, 0, 1,  x1, y1, 0, 1, 1, 1 };
        for (int i = 0; i < 24; i++) v[i] = q[i];
        ((delegate* unmanaged[Stdcall]<nint, int>)Layer.Fn(Dev, BeginScene))(Dev);
        ((delegate* unmanaged[Stdcall]<nint, int, uint, float*, uint, int>)Layer.Fn(Dev, DrawPrimitiveUP))(Dev, 5, 2, v, 24);
        ((delegate* unmanaged[Stdcall]<nint, int>)Layer.Fn(Dev, EndScene))(Dev);
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Layer.Fn(Dev, 107))(Dev, 0);
        ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Layer.Fn(Dev, SetRenderTarget))(Dev, 0, MsaaTarget != 0 ? MsaaTarget : SceneSurf);
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Layer.Fn(Dev, SetDepthStencilSurface))(Dev, MsaaDepth != 0 ? MsaaDepth : GameDepth);
        ((delegate* unmanaged[Stdcall]<nint, int>)Layer.Fn(sb, 5))(sb); // Apply
    }

    // ---- Dispositivo perdido (Alt+Tab) ----
    // Present devuelve D3DERR_DEVICELOST al perder la pantalla completa. Mientras TestCooperativeLevel diga
    // DEVICELOST no se puede hacer nada; cuando diga DEVICENOTRESET se sueltan los recursos de memoria de video
    // (back buffer, Z-buffer y los del MSAA), se llama a Reset con los mismos parametros y se vuelve a poner todo
    // (Device7 reaplica sus estados). Las texturas son gestionadas: Direct3D 9 las restaura solo.
    private static int* _pp;
    private static readonly System.Diagnostics.Stopwatch _lostClock = System.Diagnostics.Stopwatch.StartNew();
    private static long _lostLog;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int IsIconic(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ShowWindow(nint hwnd, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetForegroundWindow(nint hwnd);
    private static long _lostSince, _lastKick;
    public static bool Lost;
    public const int D3DERR_DEVICELOST = unchecked((int)0x88760868), D3DERR_DEVICENOTRESET = unchecked((int)0x88760869);
    private const int ResetIdx = 16;
    public static event Action? AfterReset;

    // ---- Cambio de tamaño de la ventana (modo ventana o sin bordes) ----
    // Si el área de la ventana ya no mide como el back buffer (maximizar, restaurar, arrastrar el borde), se resetea el
    // dispositivo con el tamaño nuevo: así la imagen del juego se escala con el shader en vez de con el estirado
    // lineal de Windows al presentar. En modo ventana un Reset no pierde nada más que lo de memoria de vídeo.
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetClientRect(nint hwnd, int* r);
    private static long _lastSizeCheck;
    private static int _resetFails;

    /// <summary>
    /// Tamaño de salida: el área actual de la ventana, sin pasar del back buffer (que mide como el monitor). La imagen
    /// del juego se estira a ese tamaño en la esquina del back buffer y se presenta solo ese rectángulo: Windows lo
    /// copia a la ventana 1:1, sin estirar. Así cambiar el tamaño de la ventana no necesita Reset.
    /// </summary>

    public static void CheckResize()
    {
        if (OutW == 0) { OutW = BBW; OutH = BBH; }
        if (Fullscreen || Dev == 0 || Hwnd == 0) return;
        long now = _lostClock.ElapsedMilliseconds;
        if (now - _lastSizeCheck < 100) return;
        _lastSizeCheck = now;
        int* r = stackalloc int[4];
        if (GetClientRect(Hwnd, r) == 0) return;
        int w = Math.Min(r[2] - r[0], BBW), h = Math.Min(r[3] - r[1], BBH);
        if (w <= 0 || h <= 0 || IsIconic(Hwnd) != 0 || (w == OutW && h == OutH)) return;
        Log.Info($"D9: la ventana cambia de tamaño: {OutW}x{OutH} -> {w}x{h}");
        OutW = w; OutH = h;
    }

    /// <summary>Suelta lo de memoria de vídeo, Reset con _pp, y vuelve a crearlo y a poner los estados.</summary>
    private static bool DoReset()
    {
        // Nada de memoria de vídeo puede quedar puesto en el dispositivo: destino y Z-buffer los originales,
        // ninguna textura en las etapas (si no, Reset devuelve D3DERR_INVALIDCALL)
        nint bbNow = BackBuffer;
        if (bbNow == 0) ((delegate* unmanaged[Stdcall]<nint, uint, uint, int, nint*, int>)Layer.Fn(Dev, GetBackBuffer))(Dev, 0, 0, 0, &bbNow);
        if (bbNow != 0) ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Layer.Fn(Dev, SetRenderTarget))(Dev, 0, bbNow);
        if (bbNow != BackBuffer) Layer.ReleaseCom(bbNow);
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Layer.Fn(Dev, SetDepthStencilSurface))(Dev, DepthStencil);
        for (uint i = 0; i < 8; i++) ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Layer.Fn(Dev, SetTexture))(Dev, i, 0);
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Layer.Fn(Dev, 107))(Dev, 0); // sin píxel shader
        Layer.ReleaseCom(BackBuffer); BackBuffer = 0;
        Layer.ReleaseCom(DepthStencil); DepthStencil = 0;
        Layer.ReleaseCom(MsaaTarget); MsaaTarget = 0;
        Layer.ReleaseCom(MsaaDepth); MsaaDepth = 0;
        ReleaseTargets();
        int hr = ((delegate* unmanaged[Stdcall]<nint, int*, int>)Layer.Fn(Dev, ResetIdx))(Dev, _pp);
        if (hr != 0)
        {
            // se reintenta en el siguiente fotograma (Recover), sin usar las superficies ya soltadas
            if (_resetFails++ < 10) Log.Warn($"D9: Reset -> 0x{hr:X8}");
            Lost = true;
            return false;
        }
        nint bb = 0, ds = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, int, nint*, int>)Layer.Fn(Dev, GetBackBuffer))(Dev, 0, 0, 0, &bb);
        ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Layer.Fn(Dev, GetDepthStencilSurface))(Dev, &ds);
        BackBuffer = bb; DepthStencil = ds;
        SetupTargets();
        try { AfterReset?.Invoke(); } catch (Exception ex) { Log.Error("D9: tras Reset: " + ex.Message); }
        return true;
    }

    public static void OnPresentResult(int hr)
    {
        if (hr == D3DERR_DEVICELOST && !Lost) { Lost = true; Log.Info("D9: dispositivo perdido (Alt+Tab)"); }
    }

    /// <summary>Si el dispositivo esta perdido, intenta recuperarlo. true = se puede dibujar.</summary>
    public static bool Recover()
    {
        if (!Lost || Dev == 0) return true;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int>)Layer.Fn(Dev, TestCooperativeLevel))(Dev);
        if (hr == 0 && BackBuffer == 0) hr = D3DERR_DEVICENOTRESET; // quedó a medias un Reset (cambio de tamaño)
        if (hr == 0) { Lost = false; return true; }
        bool fg = GetForegroundWindow() == Hwnd, iconic = IsIconic(Hwnd) != 0;
        if (_lostClock.ElapsedMilliseconds - _lostLog > 2000)
        {
            _lostLog = _lostClock.ElapsedMilliseconds;
            Log.Info($"D9: perdido: TestCooperativeLevel 0x{hr:X8}, ventana delante {fg}, minimizada {iconic}");
        }
        // de vuelta (la ventana está delante) pero minimizada: se restaura para que Direct3D recupere la pantalla
        if (fg && iconic) ShowWindow(Hwnd, 9 /* SW_RESTORE */);
        // Delante y sin minimizar pero aún perdido (pasa al pinchar en otro monitor y volver): Direct3D no ha recibido
        // la activación que espera. Se fuerza minimizando y restaurando (como un Alt+Tab de ida y vuelta).
        long t = _lostClock.ElapsedMilliseconds;
        if (fg && !iconic && hr == D3DERR_DEVICELOST)
        {
            if (_lostSince == 0) _lostSince = t;
            if (t - _lostSince > 1000 && t - _lastKick > 3000)
            {
                _lastKick = t;
                Log.Info("D9: perdido con la ventana delante: se minimiza y restaura para recuperar la pantalla completa");
                ShowWindow(Hwnd, 6 /* SW_MINIMIZE */);
                ShowWindow(Hwnd, 9 /* SW_RESTORE */);
                SetForegroundWindow(Hwnd);
            }
        }
        else _lostSince = 0;
        if (hr != D3DERR_DEVICENOTRESET) return false;
        if (!DoReset()) return false;
        Lost = false;
        Log.Info("D9: dispositivo recuperado (Reset)");
        return true;
    }

    // ---- atajos ----
    public static int RS(int state, uint value) => ((delegate* unmanaged[Stdcall]<nint, int, uint, int>)Layer.Fn(Dev, SetRenderState))(Dev, state, value);
}
