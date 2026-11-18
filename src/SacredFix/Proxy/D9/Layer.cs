using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SacredFix.Core;
using SacredFix.Native;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Capa propia DirectDraw 7 / Direct3D 7 -> Direct3D 9 ([Display] Renderer=d3d9).
///
/// Los objetos COM que ve el juego son bloques de memoria nativa: +0 tabla de funciones, +4 contador de
/// referencias, +8 índice del objeto C# (Layer.Objects), +0xC objeto real de Windows al que reenviar (si lo hay).
/// Las tablas se rellenan con métodos [UnmanagedCallersOnly] y, para lo que no se implementa, con adaptadores
/// nativos: «reenviar» (cambia this por el objeto real y salta a su método) o «no implementado» (lo registra una vez
/// y devuelve E_NOTIMPL limpiando los argumentos de la pila).
/// </summary>
internal static unsafe class Layer
{
    public static bool Enabled { get; private set; }
    public static int MultiSample { get; private set; }
    /// <summary>[Display] D3D9Window: 0 pantalla completa exclusiva, 1 sin bordes (por defecto), 2 ventana con marco.</summary>
    public static int WindowStyle { get; private set; } = 1;
    /// <summary>[Display] RenderScale (solo d3d11): «auto» = a la altura de la pantalla; un número = esa escala; 1 = sin escala.</summary>
    public static string RenderScale { get; private set; } = "auto";
    /// <summary>[Display] Anisotropy (solo d3d11): filtrado anisótropo de lo 3D, con mipmaps en las texturas; 0/1 = no.</summary>
    public static int Anisotropy { get; private set; } = 16;
    /// <summary>[Display] Sharpen (solo d3d11): enfoque adaptativo (CAS) al presentar, de 0 (nada) a 1.</summary>
    public static float Sharpen { get; set; } = 0.4f;
    /// <summary>[Display] FontSharp (solo d3d11 con RenderScale): nitidez de las letras del juego ampliadas, 0 (bilineal normal) a 1.</summary>
    public static float FontSharp { get; set; } = 1f;
    /// <summary>[Display] AlphaToCoverage (solo d3d11, con MSAA): bordes suaves en lo recortado por transparencia.</summary>
    public static bool AlphaToCoverage { get; set; } = true;
    /// <summary>[Display] CloseGaps2D (solo d3d11, con MSAA): ensanchado de las piezas 2D del mundo para tapar las juntas, en píxeles del juego; 0 = no.</summary>
    public static float CloseGaps2D { get; private set; } = 0f;
    /// <summary>[Display] KeepAspect (solo d3d11): la imagen conserva su proporción, con bandas negras si el monitor no es igual.</summary>
    public static bool KeepAspect { get; private set; } = true;
    /// <summary>[Display] PixelLighting (solo d3d11): luz por píxel en lo 3D iluminado con normales (personajes, objetos).</summary>
    public static bool PixelLighting { get; set; } = true;
    /// <summary>[Display] Gamma, Contrast, Brightness, Saturation (solo d3d11): ajuste de color al presentar (neutro: 1, 1, 0, 1).</summary>
    public static float Gamma { get; set; } = 1f;
    public static float Contrast { get; set; } = 1f;
    public static float Brightness { get; set; } = 0f;
    public static float Saturation { get; set; } = 1f;

    private static float F(Config cfg, string key, float def, float min, float max) =>
        float.TryParse(cfg.GetString("Display", key, def.ToString(System.Globalization.CultureInfo.InvariantCulture)).Trim(),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? Math.Clamp(v, min, max) : def;
    /// <summary>Tamaño que pedirá el juego (para añadirlo a la lista de modos de vídeo si el monitor no lo tiene).</summary>
    public static int ModeW { get; private set; } = 1366;
    public static int ModeH { get; private set; } = 768;

    public const int DD_OK = 0;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int DDERR_NOTFOUND = unchecked((int)0x887600FF);
    public const int DDERR_NOTLOCKED = unchecked((int)0x887602E6);

    public static readonly List<object?> Objects = new() { null };

    /// <summary>
    /// Al recuperar el foco en pantalla completa, el procesador de mensajes del juego (0x812E05) vuelve a poner el modo
    /// de vídeo, restaura las superficies y vacía las texturas («flushTextures»): llama a cResourceMgr::disposeAll
    /// (0x65F180, thiscall) con el gestor de [0x13E7838]. Si la activación llega durante el arranque (con D3D11, tras los
    /// vídeos), el gestor aún no existe y el juego se cierra al leer de un puntero nulo. Con el gestor nulo no hace nada.
    /// </summary>
    private static void GuardDisposeAll()
    {
        const nint at = 0x65F180;
        try
        {
            if (!Mem.Matches(at, new byte[] { 0x53, 0x56, 0x57, 0x8B, 0xF1 })) { Log.Warn("D9: disposeAll no es el esperado: sin protección"); return; }
            byte[] code =
            {
                0x85, 0xC9,             // test ecx, ecx
                0x75, 0x03,             // jnz +3
                0xC2, 0x04, 0x00,       // ret 4
                0x53, 0x56, 0x57, 0x8B, 0xF1, // lo que había: push ebx / push esi / push edi / mov esi, ecx
                0xE9, 0, 0, 0, 0,       // jmp 0x65F185
            };
            nint stub = Mem.AllocCode(code);
            Mem.Write(stub + 13, (int)(at + 5 - (stub + 17)));
            Mem.WriteJump(at, stub);
        }
        catch (Exception ex) { Log.Warn("D9: protección de disposeAll: " + ex.Message); }
    }

    /// <summary>
    /// Que el juego no se minimice al perder el foco (Alt+Tab, pinchar fuera) en sin bordes y ventana. En sus dos
    /// rutas de desactivación (0x811D0C y 0x812EB8: «- restoreDisplayMode», marca de inactivo 0x20000 en [0x182EE38],
    /// Sleep(50), IDirectDraw7::RestoreDisplayMode) acaba con ShowWindow(hwnd, SW_MINIMIZE), pensado para su pantalla
    /// completa. El «push 6» pasa a «push 8» (SW_SHOWNA: no cambia nada). La pausa por inactividad se mantiene.
    /// </summary>
    private static void KeepWindowOnFocusLoss()
    {
        nint[] at = { 0x811D37, 0x812EE7 };
        try
        {
            foreach (var a in at)
                if (!Mem.Matches(a, new byte[] { 0x6A, 0x06 })) { Log.Warn($"D9: minimizado al perder el foco: bytes inesperados en 0x{a:X8}, sin cambiar"); return; }
            foreach (var a in at) Mem.WriteBytes(a + 1, new byte[] { 0x08 });
            Log.Info("D9: el juego ya no se minimiza al perder el foco (sin bordes / ventana)");
        }
        catch (Exception ex) { Log.Warn("D9: minimizado al perder el foco: " + ex.Message); }
    }

    public static void Init(Config cfg)
    {
        string r = cfg.GetString("Display", "Renderer", "ddraw").Trim().ToLowerInvariant();
        Enabled = r == "d3d9" || r == "d3d11";
        Backend.Current = r == "d3d11" ? new Backend11() : new Backend9();
        if (r == "d3d11") Log.Info("D9: motor Direct3D 11 (en desarrollo: fase 2, todavía no dibuja el juego)");
        if (Enabled) GuardDisposeAll();
        if (Enabled) OptionsMenu.Init(cfg);
        Backend11.PerfOn = r == "d3d11" && cfg.GetBool("Debug", "D3D11Perf", false);
        Backend11.Trace2D = r == "d3d11" && cfg.GetBool("Debug", "D3D11Trace2D", false);
        Backend11.ForceFail = r == "d3d11" && cfg.GetBool("Debug", "D3D11ForceFail", false);
        FrameLimit.Init(cfg.GetInt("Display", "MaxFps", 0));
        MultiSample = Math.Clamp(cfg.GetInt("Display", "MultiSample", 0), 0, 16);
        string w = cfg.GetString("Display", "D3D9Window", "borderless").Trim().ToLowerInvariant();
        RenderScale = cfg.GetString("Display", "RenderScale", "auto").Trim().ToLowerInvariant();
        Anisotropy = Math.Clamp(cfg.GetInt("Display", "Anisotropy", 16), 0, 16);
        AlphaToCoverage = cfg.GetBool("Display", "AlphaToCoverage", true);
        KeepAspect = cfg.GetBool("Display", "KeepAspect", true);
        PixelLighting = cfg.GetBool("Display", "PixelLighting", true);
        Gamma = F(cfg, "Gamma", 1f, 0.3f, 3f); Contrast = F(cfg, "Contrast", 1f, 0.3f, 3f);
        Brightness = F(cfg, "Brightness", 0f, -0.5f, 0.5f); Saturation = F(cfg, "Saturation", 1f, 0f, 3f);
        if (cfg.GetBool("Fixes", "Widescreen", false)) { ModeW = Resolution.Width(cfg); ModeH = Resolution.Height(cfg); } else { ModeW = 1024; ModeH = 768; }
        CloseGaps2D = float.TryParse(cfg.GetString("Display", "CloseGaps2D", "0.5").Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float cg) ? Math.Clamp(cg, 0f, 2f) : 0f;
        Sharpen = float.TryParse(cfg.GetString("Display", "Sharpen", "0.4").Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float sh) ? Math.Clamp(sh, 0f, 1f) : 0.4f;
        FontSharp = float.TryParse(cfg.GetString("Display", "FontSharp", "1").Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float fs) ? Math.Clamp(fs, 0f, 1f) : 1f;
        WindowStyle = w switch { "fullscreen" or "0" => 0, "window" or "2" => 2, _ => 1 };
        if (Enabled && WindowStyle != 0 && cfg.GetBool("Fixes", "KeepWindowOnFocusLoss", true)) KeepWindowOnFocusLoss();
        if (Enabled) Log.Info($"D9: capa propia DirectDraw 7 / Direct3D 7 -> Direct3D 9 activada (MSAA {MultiSample}x)");
        if (Enabled && !cfg.GetBool("Display", "D3D9Videos", true)) SkipVideos();
    }

    /// <summary>
    /// Vídeos: el juego los prepara con DirectShow (amstream.dll) en 0x6A0D80 (cdecl: fichero, IDirectDraw,
    /// &amp;stream), que pide al DirectDraw interfaces que la capa no tiene. Si el stream sale vacío, el juego se salta
    /// la reproducción (je tras la llamada). Las 10 llamadas pasan a una función que no crea nada.
    /// </summary>
    private static void SkipVideos()
    {
        nint[] sites = { 0x6A15F1, 0x6A1811, 0x6A18C2, 0x6A1976, 0x6A1A2F, 0x6A1AF4, 0x6A1BAD, 0x6A1C66, 0x6A1D1F, 0x6A1DD8 };
        nint stub = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint*, int>)&NoVideo;
        int n = 0;
        foreach (nint site in sites)
        {
            if (*(byte*)site != 0xE8 || site + 5 + *(int*)(site + 1) != 0x6A0D80) { Log.Warn($"D9: 0x{site:X} no llama a 0x6A0D80"); continue; }
            Mem.RedirectCall(site, stub); n++;
        }
        Log.Info($"D9: vídeos desactivados ({n} llamadas): la capa aún no los reproduce");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int NoVideo(nint file, nint dd, nint* stream)
    {
        if (stream != null) *stream = 0;
        return E_FAIL;
    }

    // ---- Objetos nativos ----

    public static nint NewObject(nint vtable, object managed, nint real = 0)
    {
        nint p = (nint)NativeMemory.AllocZeroed(16);
        *(nint*)p = vtable;
        *(int*)(p + 4) = 1;
        lock (Objects) { *(int*)(p + 8) = Objects.Count; Objects.Add(managed); }
        *(nint*)(p + 12) = real;
        return p;
    }

    public static T Get<T>(nint self) where T : class => (T)Objects[*(int*)(self + 8)]!;
    public static T? TryGet<T>(nint self) where T : class
    {
        if (self == 0) return null;
        int i = *(int*)(self + 8);
        return i > 0 && i < Objects.Count ? Objects[i] as T : null;
    }
    public static nint Real(nint self) => *(nint*)(self + 12);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    public static uint AddRef(nint self) => (uint)System.Threading.Interlocked.Increment(ref *(int*)(self + 4));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    public static uint Release(nint self)
    {
        int n = System.Threading.Interlocked.Decrement(ref *(int*)(self + 4));
        if (n == 0)
        {
            // el bloque nativo no se libera (puede quedar algún puntero viejo); sí los recursos de D3D9
            // la cara v1 de una superficie comparte el objeto C#: solo se liberan los recursos al soltar la v7
            try { var o = Objects[*(int*)(self + 8)]; if (o is IDisposable d && !(o is Surface7 s && s.Native != self)) d.Dispose(); } catch { }
        }
        return (uint)Math.Max(n, 0);
    }

    // ---- Tablas de funciones ----

    /// <summary>Tabla nativa de n casillas; las que no se rellenan devuelven «no implementado» (con su nº de argumentos).</summary>
    public sealed class VTable
    {
        public readonly nint Ptr;
        private readonly string _iface;
        private readonly string[] _names;
        private readonly int[] _args;
        public VTable(string iface, string[] names, int[] args)
        {
            _iface = iface; _names = names; _args = args;
            Ptr = (nint)NativeMemory.AllocZeroed((nuint)(names.Length * sizeof(nint)));
            for (int i = 0; i < names.Length; i++) ((nint*)Ptr)[i] = NotImplStub(RegisterName($"{iface}::{names[i]}"), args[i]);
            ((nint*)Ptr)[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
            ((nint*)Ptr)[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        }
        public void Set(int i, nint fn) => ((nint*)Ptr)[i] = fn;
        /// <summary>Esta casilla reenvía al mismo método del objeto real (+0xC).</summary>
        public void Forward(int i) => ((nint*)Ptr)[i] = ForwardStub(i);
    }

    private static readonly List<string> _names = new();
    private static readonly HashSet<int> _logged = new();
    private static int RegisterName(string n) { lock (_names) { _names.Add(n); return _names.Count - 1; } }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void LogNotImpl(int id)
    {
        try { lock (_logged) if (!_logged.Add(id)) return; Log.Warn($"D9: no implementado: {_names[id]}"); } catch { }
    }

    /// <summary>pushad; push id; call LogNotImpl; popad; mov eax, E_NOTIMPL; ret 4·(args+1)</summary>
    private static nint NotImplStub(int id, int args)
    {
        var c = new List<byte> { 0x60, 0x68 };
        c.AddRange(BitConverter.GetBytes(id));
        int call = c.Count;
        c.Add(0xE8); c.AddRange(new byte[4]);
        c.Add(0x61);
        c.Add(0xB8); c.AddRange(BitConverter.GetBytes(E_NOTIMPL));
        c.Add(0xC2); c.AddRange(BitConverter.GetBytes((ushort)(4 * (args + 1))));
        nint code = AllocSmall(c.ToArray());
        nint target = (nint)(delegate* unmanaged[Stdcall]<int, void>)&LogNotImpl;
        *(int*)(code + call + 1) = (int)(target - (code + call + 5));
        return code;
    }

    /// <summary>mov eax, [esp+4]; mov eax, [eax+0xC]; mov [esp+4], eax; mov ecx, [eax]; jmp [ecx + 4·i]</summary>
    private static nint ForwardStub(int i)
    {
        var c = new List<byte> { 0x8B, 0x44, 0x24, 0x04, 0x8B, 0x40, 0x0C, 0x89, 0x44, 0x24, 0x04, 0x8B, 0x08, 0xFF, 0xA1 };
        c.AddRange(BitConverter.GetBytes(4 * i));
        return AllocSmall(c.ToArray());
    }

    // Los adaptadores son pequeños: se reparten en páginas propias en vez de una por adaptador
    private static nint _page;
    private static int _used = 4096;
    private static nint AllocSmall(byte[] code)
    {
        lock (_names)
        {
            int size = (code.Length + 15) & ~15;
            if (_used + size > 4096)
            {
                _page = Win32.VirtualAlloc(0, 4096, Win32.MEM_COMMIT | Win32.MEM_RESERVE, Win32.PAGE_EXECUTE_READWRITE);
                _used = 0;
            }
            nint p = _page + _used;
            _used += size;
            for (int k = 0; k < code.Length; k++) ((byte*)p)[k] = code[k];
            return p;
        }
    }

    // ---- Llamadas a objetos COM por su tabla ----

    public static nint Fn(nint obj, int i) => (*(nint**)obj)[i];
    public static void ReleaseCom(nint obj) { if (obj != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Fn(obj, 2))(obj); }
    public static void AddRefCom(nint obj) { if (obj != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Fn(obj, 1))(obj); }

    public static bool GuidIs(nint riid, string g) => riid != 0 && *(Guid*)riid == new Guid(g);

    public const string IID_IUnknown = "00000000-0000-0000-C000-000000000046";
    public const string IID_IDirectDraw7 = "15E65EC0-3B9C-11D2-B92F-00609797EA5B";
    public const string IID_IDirectDrawSurface7 = "06675A80-3B9B-11D2-B92F-00609797EA5B";
    public const string IID_IDirectDrawSurface7b = "6C14DB81-A733-11CE-A521-0020AF0BE560"; // el que pide el juego (IDirectDrawSurface)
    public const string IID_IDirect3D7 = "F5049E77-4861-11D2-A407-00A0C90629A8";
    public const string IID_IDirect3DTnLHalDevice = "F5049E78-4861-11D2-A407-00A0C90629A8";
}
