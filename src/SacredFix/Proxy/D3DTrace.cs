using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SacredFix.Core;
using SacredFix.Native;

namespace SacredFix.Proxy;

/// <summary>
/// Trazador de DirectDraw 7 / Direct3D 7 (diagnóstico). Se activa con [Debug] D3DTrace=1.
///
/// Cómo engancha (todo con "parcheo de vtable": se sustituye una entrada de la tabla de métodos
/// del objeto COM por nuestra función; los métodos COM son stdcall con 'this' como primer argumento,
/// así que no hace falta ningún adaptador):
///   DirectDrawCreateEx → objeto IDirectDraw7 → vtable[0] QueryInterface
///   QueryInterface(IID_IDirect3D7) → objeto IDirect3D7 → vtable[4] CreateDevice
///   CreateDevice → objeto IDirect3DDevice7 → EndScene, SetTransform, SetViewport,
///                  DrawPrimitive, DrawIndexedPrimitive, DrawPrimitiveVB, DrawIndexedPrimitiveVB
///
/// Qué registra:
///   - cada SetViewport y cada SetTransform con valores distintos a los anteriores (máx. 60 líneas)
///   - cada 300 fotogramas (EndScene), y durante los primeros 60 resúmenes: número de llamadas por
///     tipo y formato de vértice (FVF) y, para vértices en coordenadas de pantalla (XYZRHW),
///     el rango mínimo y máximo de x e y.
/// </summary>
internal static unsafe class D3DTrace
{
    private static bool _enabled;
    private static bool _ddPatched;
    private static readonly HashSet<nint> _d3dVtables = new(), _devVtables = new();

    // Originales
    private static delegate* unmanaged[Stdcall]<nint, nint, nint*, int> _qi;
    private static delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int> _createDevice;
    private static delegate* unmanaged[Stdcall]<nint, int> _endScene;
    private static delegate* unmanaged[Stdcall]<nint, int, float*, int> _setTransform;
    private static delegate* unmanaged[Stdcall]<nint, int*, int> _setViewport;
    private static delegate* unmanaged[Stdcall]<nint, uint, int*, uint, uint, float, uint, int> _clear;
    private static delegate* unmanaged[Stdcall]<nint, int, uint, nint, uint, uint, int> _drawPrim;
    private static delegate* unmanaged[Stdcall]<nint, int, uint, nint, uint, ushort*, uint, uint, int> _drawIdx;
    private static delegate* unmanaged[Stdcall]<nint, int, nint, uint, uint, uint, int> _drawPrimVB;
    private static delegate* unmanaged[Stdcall]<nint, int, uint, nint, uint, ushort*, uint, uint, int> _drawIdxStrided;
    private static delegate* unmanaged[Stdcall]<nint, int, nint, uint, uint, ushort*, uint, uint, int> _drawIdxVB;
    private static delegate* unmanaged[Stdcall]<nint, uint, nint, int> _setTexture;

    // IID_IDirect3D7 {F5049E77-4861-11D2-A407-00A0C90629A8}
    private static readonly Guid IID_IDirect3D7 = new("F5049E77-4861-11D2-A407-00A0C90629A8");

    private sealed class Stat
    {
        public int Calls, Prims;
        public float MinX = float.MaxValue, MaxX = float.MinValue, MinY = float.MaxValue, MaxY = float.MinValue;
        public bool Rhw;
        // Posición en pantalla de los vértices (RHW tal cual; XYZ proyectados con las matrices actuales)
        public bool Proj;
        public long N, In, Behind;
        public double SumX, SumY;
        public float MinZ = float.MaxValue, MaxZ = float.MinValue;
    }
    // Clave sin cadenas (evita crear basura en cada llamada de dibujo: el GC causaba tirones)
    private static readonly Dictionary<(int api, int type, uint fvf), Stat> _stats = new();
    private static readonly string[] ApiNames = { "DP", "DIP", "DPVB", "DIPVB", "DIPStrided" };
    private const int MaxReports = 60; // ~5 min a 60 fps: cubre menú, carga y partida
    private static int _frames, _reports, _changeLines;
    private static readonly float[] _lastMatrix = new float[16 * 3];
    internal static readonly int[] _lastVp = new int[6];
    /// <summary>Matrices actuales WORLD, VIEW y PROJECTION (16 floats cada una, fila a fila, convención D3D v*M).</summary>
    private static readonly float[] _cur = new float[16 * 3];
    private const int MaxVertsPerCall = 64;

    public static void Init(Config cfg)
    {
        _enabled = cfg.GetBool("Debug", "D3DTrace", false);
        if (!_enabled) _reports = MaxReports; // sin traza: los ganchos solo sirven al lienzo de la interfaz
        // Experimento: [Display] CamShift (px) desplaza el ojo y el objetivo de la cámara 3D del mundo
        // ([0x182EE00] y [0x182EE20], x) en CamShift · zoom píxeles. Requiere D3DTrace=1 (usa su gancho).
        _camShift = cfg.GetInt("Display", "CamShift", 0);
        _orthoHalf = 267.0 * Resolution.Width(cfg) / 1024.0;
    }

    private static int _camShift;
    private static double _orthoHalf;
    private static bool _camBaseRead;
    private static float _eyeBaseX, _targetBaseX;

    /// <summary>Con cada PROJECTION ortográfica: zoom = 1 / (PROJ[0][0] · semiancho); mueve ojo y objetivo.</summary>
    private static void ApplyCamShift(float* m)
    {
        if (_camShift == 0 || m[11] != 0 || m[15] != 1 || m[0] <= 0) return; // solo ortográficas
        if (!_camBaseRead) { _eyeBaseX = *(float*)0x182EE00; _targetBaseX = *(float*)0x182EE20; _camBaseRead = true; }
        double zoom = 1.0 / (m[0] * _orthoHalf);
        float units = (float)(_camShift * zoom * (267.0 / 512.0));
        *(float*)0x182EE00 = _eyeBaseX + units;
        *(float*)0x182EE20 = _targetBaseX + units;
    }

    /// <summary>Llamado tras DirectDrawCreateEx con éxito.</summary>
    public static void OnDirectDrawCreated(nint dd)
    {
        if (dd != 0 && _ddPatched) ApiInventory.OnDirectDraw(dd); // otros adaptadores (con otra tabla, si la tienen)
        if ((!_enabled && !UiCanvas.Enabled) || dd == 0 || _ddPatched) return;
        _ddPatched = true;
        nint vt = *(nint*)dd;
        _qi = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Patch(vt, 0, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&QueryInterfaceHook);
        Log.Info($"D3DTrace: IDirectDraw7 0x{dd:X8}, vtable 0x{vt:X8} enganchada (QueryInterface)");
        ApiInventory.OnDirectDraw(dd);
    }

    private static nint Patch(nint vtable, int index, nint hook)
    {
        nint slot = vtable + index * sizeof(nint);
        nint orig = *(nint*)slot;
        Mem.Write(slot, hook);
        return orig;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterfaceHook(nint self, nint riid, nint* ppv)
    {
        int hr = _qi(self, riid, ppv);
        try
        {
            // Con dgVoodoo2 cada IDirect3D7 (uno por adaptador) tiene su propia tabla: se engancha cada tabla nueva
            // (el juego usa el último). Con el de Windows todas comparten una y se engancha una vez.
            // (por tabla: con el inventario de la fase 0 la casilla ya no apunta a nuestro gancho, sino a su adaptador)
            if (hr == 0 && *(Guid*)riid == IID_IDirect3D7 && *ppv != 0 && _d3dVtables.Add(*(nint*)*ppv))
            {
                nint vt = *(nint*)*ppv;
                _createDevice = (delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Patch(vt, 4, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)&CreateDeviceHook);
                Log.Info($"D3DTrace: IDirect3D7 0x{*ppv:X8} enganchado (CreateDevice)");
                ApiInventory.OnDirect3D(*ppv);
            }
        }
        catch (Exception ex) { Log.Error("D3DTrace QI: " + ex.Message); }
        return hr;
    }

    private static int _createLogs, _heldProjLogs;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int CreateDeviceHook(nint self, nint clsid, nint surface, nint* ppDev)
    {
        int hr = _createDevice(self, clsid, surface, ppDev);
        try
        {
            if (_createLogs++ < 6) Log.Info($"D3DTrace: CreateDevice({*(Guid*)clsid}, 0x{surface:X}) -> 0x{hr:X8}");
            if (hr == 0 && *ppDev != 0 && _devVtables.Add(*(nint*)*ppDev))
            {
                Device = *ppDev;
                nint vt = *(nint*)*ppDev;
                _endScene = (delegate* unmanaged[Stdcall]<nint, int>)Patch(vt, 6, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&EndSceneHook);
                _setTransform = (delegate* unmanaged[Stdcall]<nint, int, float*, int>)Patch(vt, 11, (nint)(delegate* unmanaged[Stdcall]<nint, int, float*, int>)&SetTransformHook);
                _setViewport = (delegate* unmanaged[Stdcall]<nint, int*, int>)Patch(vt, 13, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&SetViewportHook);
                _clear = (delegate* unmanaged[Stdcall]<nint, uint, int*, uint, uint, float, uint, int>)Patch(vt, 10, (nint)(delegate* unmanaged[Stdcall]<nint, uint, int*, uint, uint, float, uint, int>)&ClearHook);
                _drawPrim = (delegate* unmanaged[Stdcall]<nint, int, uint, nint, uint, uint, int>)Patch(vt, 25, (nint)(delegate* unmanaged[Stdcall]<nint, int, uint, nint, uint, uint, int>)&DrawPrimitiveHook);
                _drawIdx = (delegate* unmanaged[Stdcall]<nint, int, uint, nint, uint, ushort*, uint, uint, int>)Patch(vt, 26, (nint)(delegate* unmanaged[Stdcall]<nint, int, uint, nint, uint, ushort*, uint, uint, int>)&DrawIndexedPrimitiveHook);
                _drawIdxStrided = (delegate* unmanaged[Stdcall]<nint, int, uint, nint, uint, ushort*, uint, uint, int>)Patch(vt, 30, (nint)(delegate* unmanaged[Stdcall]<nint, int, uint, nint, uint, ushort*, uint, uint, int>)&DrawIndexedPrimitiveStridedHook);
                _drawPrimVB = (delegate* unmanaged[Stdcall]<nint, int, nint, uint, uint, uint, int>)Patch(vt, 31, (nint)(delegate* unmanaged[Stdcall]<nint, int, nint, uint, uint, uint, int>)&DrawPrimitiveVBHook);
                _drawIdxVB = (delegate* unmanaged[Stdcall]<nint, int, nint, uint, uint, ushort*, uint, uint, int>)Patch(vt, 32, (nint)(delegate* unmanaged[Stdcall]<nint, int, nint, uint, uint, ushort*, uint, uint, int>)&DrawIndexedPrimitiveVBHook);
                if (UiDump.Enabled) // solo para el diagnóstico de menús (con el gancho puesto, en modo ventana Direct3D recibía una etapa absurda)
                    _setTexture = (delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Patch(vt, 35, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, int>)&SetTextureHook);
                Log.Info($"D3DTrace: IDirect3DDevice7 0x{*ppDev:X8} enganchado (vtable 0x{vt:X8})");
                ApiInventory.OnDevice(*ppDev);
                ApiInventory.OnSurface(surface);
            }
        }
        catch (Exception ex) { Log.Error("D3DTrace CreateDevice: " + ex.Message); }
        return hr;
    }

    // ---- Dispositivo ----

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetViewportHook(nint self, int* vp)
    {
        if (UiCanvas.Active && !UiCanvas.Overlay && vp != null)
        {
            int* m = stackalloc int[6];
            for (int i = 0; i < 6; i++) m[i] = vp[i];
            m[0] += UiCanvas.Shift;
            return _setViewport(self, m);
        }
        try
        {
            bool changed = false;
            for (int i = 0; i < 4; i++) if (_lastVp[i] != vp[i]) { changed = true; _lastVp[i] = vp[i]; }
            if (changed && _enabled && _changeLines++ < 60)
                Log.Info($"D3DTrace: SetViewport x={vp[0]} y={vp[1]} w={vp[2]} h={vp[3]} z={*(float*)&vp[4]:0.##}..{*(float*)&vp[5]:0.##}");
        }
        catch { }
        return _setViewport(self, vp);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetTransformHook(nint self, int state, float* m)
    {
        try
        {
            // D3DTRANSFORMSTATE_WORLD=1, VIEW=2, PROJECTION=3
            if (state == 1) _worldSets++;
            if (state >= 1 && state <= 3)
                for (int i = 0; i < 16; i++) _cur[(state - 1) * 16 + i] = m[i];
            if (state == 3) ApplyCamShift(m);
            if (_enabled && state is 1 or 2 or 3) LogCaller(state == 1 ? "WORLD" : state == 2 ? "VIEW" : "PROJECTION", (nint)(&self), m);
            if (state >= 1 && state <= 3 && state != 1)
            {
                int o = (state - 1) * 16;
                bool changed = false;
                for (int i = 0; i < 16; i++) if (_lastMatrix[o + i] != m[i]) { changed = true; _lastMatrix[o + i] = m[i]; }
                if (changed && _enabled && _changeLines++ < 60)
                {
                    var sb = new StringBuilder($"D3DTrace: SetTransform({(state == 2 ? "VIEW" : "PROJECTION")})");
                    for (int r = 0; r < 4; r++)
                        sb.Append($" [{m[r * 4]:0.####} {m[r * 4 + 1]:0.####} {m[r * 4 + 2]:0.####} {m[r * 4 + 3]:0.####}]");
                    Log.Info(sb.ToString());
                }
            }
        }
        catch { }
        if (state == 3 && UiCanvas.Overlay && UiCanvas.Active && m != null)
        {
            // Cursor (y el objeto que lleva): viewport de toda la pantalla. Como beginOverlay3D de sacred-bild, la
            // proyección se ajusta para que el 3D caiga donde caería en el lienzo: x' = a·x + b·w, a = 1024/W,
            // b = (2·Left + 1024)/W - 1 = 0 (el lienzo está centrado). Al acabar, OverlayEnd vuelve a poner la original.
            float* tmp = stackalloc float[16];
            for (int i = 0; i < 16; i++) tmp[i] = m[i];
            float a = 1024f / (UiCanvas.Left * 2 + 1024);
            tmp[0] *= a; tmp[4] *= a; tmp[8] *= a; tmp[12] *= a;
            if (_heldProjLogs++ < 2) Log.Info($"D3DTrace: cursor: proyección 3D ajustada al lienzo ({m[0]:0.#####} -> {tmp[0]:0.#####})");
            return _setTransform(self, state, tmp);
        }
        return _setTransform(self, state, m);
    }

    private static readonly HashSet<nint> _callers = new();

    /// <summary>
    /// Registra (una vez por dirección) qué código del juego llama a SetTransform. La dirección de
    /// retorno está en la pila junto a los argumentos; como no sabemos si el compilador copió 'self',
    /// se busca en los 16 dwords siguientes un valor dentro de .text de Sacred.exe precedido de una
    /// instrucción CALL [reg+0x2C] (FF 5x 2C: método 11 de la vtable, SetTransform).
    /// </summary>
    private static void LogCaller(string what, nint argAddr, float* m)
    {
        if (_callers.Count >= 80) return;
        nint* p = (nint*)argAddr;
        for (int i = -2; i < 16; i++)
        {
            nint ret = p[i];
            if (ret < 0x401003 || ret >= 0x890000) continue;
            byte* c = (byte*)ret - 3;
            if (c[0] != 0xFF || (c[1] & 0xF8) != 0x50 || c[2] != 0x2C) continue;
            if (!_callers.Add(ret)) return;
            Log.Info($"D3DTrace: SetTransform({what}) llamado desde 0x{ret:X8}  fila3 [{m[12]:G7} {m[13]:G7} {m[14]:G7}]  diag [{m[0]:G7} {m[5]:G7} {m[10]:G7}]");
            return;
        }
    }

    /// <summary>Quién dibuja los cuadros 2D sueltos (DrawPrimitive, FVF 0x244): por dirección de retorno, con su rango de x.</summary>
    private sealed class CallerStat { public int Calls; public float MinX = float.MaxValue, MaxX = float.MinValue; public double SumX; public long N; }
    private static readonly Dictionary<nint, CallerStat> _dpCallers = new();

    private static void AccountCaller(nint argAddr, float* v, uint count, int stride)
    {
        nint* p = (nint*)argAddr;
        nint ret = 0;
        for (int i = -2; i < 16; i++)
        {
            nint r = p[i];
            if (r < 0x401003 || r >= 0x890000) continue;
            byte* c = (byte*)r - 3;
            if (c[0] == 0xFF && (c[1] & 0xF8) == 0x50 && c[2] == 0x64) { ret = r; break; } // call [reg+0x64] = DrawPrimitive
        }
        if (!_dpCallers.TryGetValue(ret, out var st)) _dpCallers[ret] = st = new CallerStat();
        st.Calls++;
        for (uint i = 0; i < count && i < 8; i++)
        {
            float x = *(float*)((byte*)v + i * stride);
            if (x < st.MinX) st.MinX = x;
            if (x > st.MaxX) st.MaxX = x;
            st.SumX += x; st.N++;
        }
    }

    /// <summary>
    /// Compara, para las últimas colocaciones de objetos, la x en pantalla según la cámara 3D (proyectando su
    /// posición del mundo con VIEW·PROJ actuales) con la que espera el suelo 2D: 683 + (px − cámara_px) / zoom.
    /// </summary>
    private static void AppendPlacement(StringBuilder sb)
    {
        int n = Math.Min(CallCounters.PlaceCount, 8);
        if (n == 0) return;
        float m0 = _cur[32];
        double zoom = m0 > 0 ? 1.0 / (m0 * _orthoHalf) : 0;
        int camPx = *(int*)0xAD7998;
        sb.Append($"\n    colocación (zoom {zoom:0.###}, cámara_px {camPx}):");
        for (int i = 0; i < n; i++)
        {
            float px = CallCounters.PlaceSamples[i * 4], py = CallCounters.PlaceSamples[i * 4 + 1];
            float wx = CallCounters.PlaceSamples[i * 4 + 2], wy = CallCounters.PlaceSamples[i * 4 + 3];
            Project(wx, wy, 0, out float sx, out float sy, out _, out _);
            double sx2d = _lastVp[2] / 2.0 + (px - camPx) / (zoom > 0 ? zoom : 1);
            sb.Append($" [px {px:0},{py:0} mundo {wx:0.#},{wy:0.#} -> 3D x {sx:0} | suelo x {sx2d:0} | dif {sx - sx2d:0}]");
        }
        CallCounters.PlaceCount = 0;
    }

    // ---- Lienzo de la interfaz (UiCanvas): desplazamiento de lo 2D mientras se dibuja la interfaz ----

    private static byte* _scratch;
    private static int _scratchSize;
    private static readonly int[] _savedVp = new int[6];
    private static bool _savedVpValid;

    /// <summary>Copia los vértices XYZRHW a un búfer propio con x + Left (no se tocan los del juego).</summary>
    private static nint MapUi(nint verts, uint count, int stride)
    {
        int bytes = (int)count * stride;
        if (bytes <= 0 || stride < 8) return verts;
        if (bytes > _scratchSize)
        {
            if (_scratch != null) NativeMemory.Free(_scratch);
            _scratchSize = Math.Max(bytes, 64 * 1024);
            _scratch = (byte*)NativeMemory.Alloc((nuint)_scratchSize);
        }
        Buffer.MemoryCopy((void*)verts, _scratch, _scratchSize, bytes);
        float left = UiCanvas.Shift;
        for (uint i = 0; i < count; i++) *(float*)(_scratch + i * stride) += left;
        return (nint)_scratch;
    }

    /// <summary>
    /// Pieza del fondo de un menú: los fondos se dibujan como una cuadrícula de 4x3 cuadros de 256x256 (más,
    /// en el menú principal, una franja animada de 1024x256) alineados a múltiplos de 256 en el lienzo.
    /// Los paneles, botones y el HUD nunca caen exactamente en esa rejilla.
    /// </summary>
    private static bool IsBackgroundTile(float* v, int stride)
    {
        float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
        for (int i = 0; i < 4; i++)
        {
            float* p = (float*)((byte*)v + i * stride);
            x0 = Math.Min(x0, p[0]); x1 = Math.Max(x1, p[0]); y0 = Math.Min(y0, p[1]); y1 = Math.Max(y1, p[1]);
        }
        static bool On256(float a) => Math.Abs(a - 256f * MathF.Round(a / 256f)) < 0.6f;
        if (!On256(x0) || !On256(x1) || !On256(y0) || !On256(y1)) return false;
        float w = x1 - x0, h = y1 - y0;
        if (x0 < -0.6f || x1 > 1024.6f || y0 < -0.6f || y1 > 768.6f) return false;
        if (w > 1023 && h > 255 && h < 257) _stripSeen = true; // franja de nubes: solo en el menú principal
        return (w > 255 && h > 255) || (w > 1023 && h > 255);
    }

    // ---- Escena 3D del menú principal (agua y logo bajo el fondo): se estira igual que el fondo ----
    private static bool _stripSeen, _mainMenu;
    /// <summary>Al final de cada fotograma: ¿estamos en el menú principal? </summary>
    private static void UpdateMainMenu(nint self)
    {
        bool now = _stripSeen;
        _stripSeen = false;
        if (now == _mainMenu || _setTransform == null) return;
        _mainMenu = now;
        Log.Info($"D3DTrace: menú principal {(now ? "visible: su escena 3D se dibuja a toda la pantalla" : "cerrado")}");
    }

    /// <summary>
    /// El escenario 3D de los menús (agua del menú principal, niebla de la selección de personaje; los personajes
    /// van por DrawIndexedPrimitiveStrided y no se tocan) se dibuja dentro de la interfaz, con el viewport del lienzo de
    /// 1024: se recortaba ahí. Se dibuja con el viewport de toda la pantalla, lo que la estira como el fondo.
    /// </summary>
    /// <summary>
    /// ¿Se ven los menús (no la partida)? cUI_Manager* en [0x17EEBBC], flags en +8: 0x01 = menús, 0x04 = partida
    /// (dato de sacred-bild, src/sacred/ui.h).
    /// </summary>
    private static bool InMenus()
    {
        nint mgr = *(nint*)0x17EEBBC;
        if (mgr == 0) return false;
        uint flags = *(uint*)(mgr + 8);
        return (flags & 0x01) != 0 && (flags & 0x04) == 0;
    }

    private static int _seqLogs;
    // Piezas de personaje del fotograma actual, en orden de dibujo: centro, mínimo y máximo (x en NDC) y vértices.
    private struct Piece { public float C, Min, Max; public uint N; public nint Ret, Tex, Pos; public int Worlds, Char; }
    private static nint _tex0;
    private static int _worldSets;
    private static readonly List<Piece> _pieces = new();
    // Ancla (x NDC del cuerpo de su personaje) de cada pieza por su número de orden, calculada en el fotograma anterior
    private static int _pieceIndex;

    /// <summary>Centro y extremos en x (NDC) de una pieza; WORLD es la identidad (Granny entrega los vértices ya en el
    /// espacio del mundo).</summary>
    /// <summary>Dirección de retorno al juego de una llamada a DrawIndexedPrimitiveStrided (método 30, [reg+0x78]).</summary>
    private static nint StridedCaller(nint argAddr)
    {
        nint* p = (nint*)argAddr;
        for (int i = -4; i < 40; i++)
        {
            nint r = p[i];
            if (r < 0x401000 || r > 0x880000) continue;
            byte* b = (byte*)r;
            if ((b[-3] == 0xFF && b[-1] == 0x78) || (b[-6] == 0xFF && b[-4] == 0x78 && b[-3] == 0)) return r;
        }
        return 0;
    }

    private static bool PieceRange(nint pos, int stride, uint count, out Piece pc)
    {
        pc = default;
        if (pos == 0 || stride < 12 || count == 0) return false;
        double sum = 0; int n = 0;
        float mn = float.MaxValue, mx = float.MinValue;
        uint step = Math.Max(1u, count / 400);
        Span<float> a = stackalloc float[4];
        Span<float> b = stackalloc float[4];
        for (uint i = 0; i < count; i += step)
        {
            float* v = (float*)(pos + (nint)(i * (uint)stride));
            a[0] = v[0]; a[1] = v[1]; a[2] = v[2]; a[3] = 1;
            for (int m = 0; m < 3; m++)
            {
                int o = m * 16;
                for (int c = 0; c < 4; c++)
                    b[c] = a[0] * _cur[o + c] + a[1] * _cur[o + 4 + c] + a[2] * _cur[o + 8 + c] + a[3] * _cur[o + 12 + c];
                b.CopyTo(a);
            }
            if (a[3] <= 1e-6f) continue;
            float x = a[0] / a[3];
            sum += x; n++;
            mn = Math.Min(mn, x); mx = Math.Max(mx, x);
        }
        if (n == 0) return false;
        pc = new Piece { C = (float)(sum / n), Min = mn, Max = mx, N = count };
        return true;
    }

    // Contador de entradas en la función que dibuja un personaje de la selección (0x6F1B30): cada pieza sabe a
    // qué personaje pertenece (número de orden en el fotograma)
    private static int* _charCounter;
    private static int _charBase;
    private static float[] _prevAnchors = Array.Empty<float>(); // ancla de cada personaje (por orden) del fotograma anterior

    /// <summary>
    /// Pone un contador al principio de 0x6F1B30 (dibuja un personaje de la selección: cuerpo y 19 piezas de
    /// equipo; se llama por una tabla). Los 8 bytes «sub esp,0x60 ; mov eax,imm32» pasan a un trozo propio:
    /// inc [contador], las dos instrucciones originales y salto de vuelta a 0x6F1B38.
    /// </summary>
    public static void InstallCharacterCounter()
    {
        const nint fn = 0x6F1B30;
        ReadOnlySpan<byte> expected = stackalloc byte[] { 0x83, 0xEC, 0x60, 0xB8 };
        for (int i = 0; i < expected.Length; i++)
            if (Mem.Read<byte>(fn + i) != expected[i]) { Log.Warn("D3DTrace: 0x6F1B30 no tiene los bytes esperados; personajes de la selección sin agrupar"); return; }
        _charCounter = (int*)Mem.AllocCode(new byte[4]);
        var c = new byte[6 + 8 + 5];
        c[0] = 0xFF; c[1] = 0x05; BitConverter.TryWriteBytes(c.AsSpan(2), (int)(nint)_charCounter);
        for (int i = 0; i < 8; i++) c[6 + i] = Mem.Read<byte>(fn + i);
        c[14] = 0xE9;
        nint stub = Mem.AllocCode(c);
        *(int*)(stub + 15) = (int)((fn + 8) - (stub + 19));
        Span<byte> j = stackalloc byte[8];
        j[0] = 0xE9; BitConverter.TryWriteBytes(j[1..5], (int)(stub - (fn + 5)));
        j[5] = 0x90; j[6] = 0x90; j[7] = 0x90;
        Mem.WriteBytes(fn, j);
        Log.Info("D3DTrace: contador de personajes de la selección en 0x6F1B30");
    }

    /// <summary>
    /// Fin de fotograma: las piezas se agrupan por el personaje que las dibujó (contador de 0x6F1B30). El ancla del
    /// personaje es el centro de su pieza con más vértices (el cuerpo), así las armas se mueven con quien las lleva.
    /// </summary>
    private static void UpdateCharacterGroups()
    {
        _pieceIndex = 0;
        if (_charCounter != null) _charBase = *_charCounter;
        if (_pieces.Count == 0) { _prevAnchors = Array.Empty<float>(); return; }
        int chars = 0;
        foreach (var q in _pieces) chars = Math.Max(chars, q.Char + 1);
        var anchors = new float[chars];
        var bodyN = new uint[chars];
        foreach (var q in _pieces) if (q.Char >= 0 && q.N > bodyN[q.Char]) { bodyN[q.Char] = q.N; anchors[q.Char] = q.C; }
        if (_seqLogs < 2 && chars != _prevAnchors.Length)
        {
            _seqLogs++;
            var sb = new StringBuilder($"D3DTrace: selección de personaje: {_pieces.Count} piezas de {chars} personajes; cuerpo (px desde el centro):");
            for (int k = 0; k < chars; k++) sb.Append($" {anchors[k] * 512:0}");
            Log.Info(sb.ToString());
        }
        _prevAnchors = anchors;
        _pieces.Clear();
    }

    /// <summary>Ancla de una pieza: el cuerpo de su personaje en el fotograma anterior (o su propio centro).</summary>
    private static float AnchorFor(int idx, in Piece pc)
    {
        if (pc.Char >= 0 && pc.Char < _prevAnchors.Length && _prevAnchors[pc.Char] != 0) return _prevAnchors[pc.Char];
        return pc.C;
    }

    /// <summary>
    /// Proyección para una pieza de personaje de los menús dibujada con el viewport de toda la pantalla (ancho W):
    /// x de NDC escalada por 1024/W (conserva el tamaño del lienzo) más un desplazamiento t para que el personaje se
    /// aleje del centro W/1024 veces, como el fondo estirado.
    /// </summary>
    private static bool MenuCharacterProjection(float* outP, nint pos, int stride, uint count, nint ret)
    {
        float w = UiCanvas.Left * 2 + 1024, s = 1024f / w;
        if (!PieceRange(pos, stride, count, out var pc)) return false;
        pc.Ret = ret; pc.Tex = _tex0; pc.Pos = pos; pc.Worlds = _worldSets; _worldSets = 0;
        pc.Char = _charCounter != null ? *_charCounter - _charBase - 1 : -1;
        int idx = _pieceIndex++;
        _pieces.Add(pc);
        float anchor = AnchorFor(idx, pc); // el dibujo no sigue a la ventana (solo el clic): se desplaza aquí
        float t = anchor * (1f - s);
        for (int i = 0; i < 16; i++) outP[i] = _cur[32 + i];
        for (int r = 0; r < 4; r++) outP[r * 4] = s * _cur[32 + r * 4] + t * _cur[32 + r * 4 + 3];
        return true;
    }

    private static bool BeginMenu3D(nint self, int* saved)
    {
        if (!UiCanvas.Active || UiCanvas.Overlay || _setViewport == null || !InMenus()) return false;
        var getVp = (delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)self)[15];
        if (getVp(self, saved) != 0) return false;
        int* full = stackalloc int[6];
        full[0] = 0; full[1] = 0; full[2] = UiCanvas.Left * 2 + 1024; full[3] = 768; full[4] = saved[4]; full[5] = saved[5];
        _setViewport(self, full);
        return true;
    }

    private static bool _barsLogged;

    /// <summary>
    /// Bandas negras de las cinemáticas y fundidos a negro: un rectángulo 2D (4 vértices) de color negro (con cualquier
    /// transparencia) que va de x = 0 a x = 1024 del lienzo. Centrado solo taparía el centro: se estira a todo el ancho.
    /// Los elementos con textura de la interfaz llevan color de vértice blanco y no se tocan.
    /// </summary>
    private static bool IsBlackFullWidth(uint fvf, float* v, uint count, int stride)
    {
        if (count != 4 || (fvf & 0x40) == 0 || v == null) return false;
        float x0 = float.MaxValue, x1 = float.MinValue;
        for (int i = 0; i < 4; i++)
        {
            float* p = (float*)((byte*)v + i * stride);
            if ((*(uint*)(p + 4) & 0xFFFFFF) != 0) return false; // difuso (tras XYZRHW): RGB negro
            x0 = Math.Min(x0, p[0]); x1 = Math.Max(x1, p[0]);
        }
        if (x0 > 0.6f || x1 < 1023.4f || x1 > 1024.6f) return false;
        if (!_barsLogged)
        {
            _barsLogged = true;
            float y0 = float.MaxValue, y1 = float.MinValue;
            for (int i = 0; i < 4; i++) { float* p = (float*)((byte*)v + i * stride); y0 = Math.Min(y0, p[1]); y1 = Math.Max(y1, p[1]); }
            Log.Info($"D3DTrace: banda o fundido negro de todo el lienzo (y {y0:0}..{y1:0}): se estira a todo el ancho");
        }
        return true;
    }

    /// <summary>Dibuja una pieza del fondo estirada a toda la pantalla: x · W/1024, con el viewport completo.</summary>
    private static int DrawStretched(nint self, int type, uint fvf, nint verts, uint count, uint flags, int stride)
    {
        int bytes = (int)count * stride;
        byte* buf = stackalloc byte[bytes];
        Buffer.MemoryCopy((void*)verts, buf, bytes, bytes);
        int w = UiCanvas.Left * 2 + 1024;
        float k = w / 1024f;
        // Bordes en píxeles enteros: así los cuadros vecinos comparten el borde y no quedan rendijas
        for (int i = 0; i < count; i++) { float* x = (float*)(buf + i * stride); *x = MathF.Round(*x * k); }
        int* saved = stackalloc int[6];
        var getVp = (delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)self)[15];
        bool ok = _setViewport != null && getVp(self, saved) == 0;
        if (ok)
        {
            int* full = stackalloc int[6];
            full[0] = 0; full[1] = 0; full[2] = w; full[3] = 768; full[4] = saved[4]; full[5] = saved[5];
            _setViewport(self, full);
        }
        // Coordenadas de textura con CLAMP (D3DTSS_ADDRESSU/V = 13/14): con el filtrado lineal, WRAP mezcla el
        // borde con el lado opuesto del cuadro y al estirar se ven líneas verticales en las juntas.
        var getTss = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, int>)(*(nint**)self)[36];
        var setTss = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint, int>)(*(nint**)self)[37];
        uint au = 0, av = 0;
        bool tss = getTss(self, 0, 13, &au) == 0 && getTss(self, 0, 14, &av) == 0;
        if (tss) { setTss(self, 0, 13, 3); setTss(self, 0, 14, 3); }
        int hr = _drawPrim(self, type, fvf, (nint)buf, count, flags);
        if (tss) { setTss(self, 0, 13, au); setTss(self, 0, 14, av); }
        if (ok) _setViewport(self, saved);
        return hr;
    }

    /// <summary>Al empezar la interfaz: guarda el viewport y pone el del lienzo (Left, 0, 1024, 768).</summary>
    /// <summary>Dispositivo 3D del juego (el último creado), para los ámbitos que no lo reciben como argumento.</summary>
    public static nint Device;

    public static void BeginUi(nint device)
    {
        if (device == 0 || _setViewport == null) return;
        try
        {
            fixed (int* vp = _savedVp)
            {
                var getVp = (delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)device)[15];
                _savedVpValid = getVp(device, vp) == 0;
                int* c = stackalloc int[6];
                int sh = UiCanvas.Shift;
                // el del lienzo: los modelos 3D de la interfaz (arma, escudo, objetos) se colocan respecto a él
                c[0] = sh; c[1] = 0; c[2] = 1024; c[3] = 768; c[4] = vp[4]; c[5] = vp[5];
                if (_savedVpValid) _setViewport(device, c);
            }
        }
        catch { _savedVpValid = false; }
    }

    private static readonly int[] _canvasVp = new int[6];
    private static bool _canvasVpValid;

    /// <summary>Objeto en el cursor: viewport de todo el lienzo (Left, 0, 1024, 768), sin el recorte de la ventana.</summary>
    public static void BeginCanvas(nint device)
    {
        _canvasVpValid = false;
        if (device == 0 || _setViewport == null) return;
        fixed (int* vp = _canvasVp)
        {
            var getVp = (delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)device)[15];
            if (getVp(device, vp) != 0) return;
            _canvasVpValid = true;
            int* c = stackalloc int[6];
            c[0] = UiCanvas.Left; c[1] = 0; c[2] = 1024; c[3] = 768; c[4] = vp[4]; c[5] = vp[5];
            _setViewport(device, c);
        }
    }

    public static void EndCanvas(nint device)
    {
        if (device == 0 || _setViewport == null || !_canvasVpValid) return;
        fixed (int* vp = _canvasVp) _setViewport(device, vp);
        _canvasVpValid = false;
    }

    private static readonly int[] _overlayVp = new int[6];
    private static bool _overlayVpValid;

    /// <summary>Cursor: viewport de toda la pantalla (el del lienzo lo recortaría en las franjas laterales).</summary>
    public static void BeginOverlay(nint device)
    {
        _overlayVpValid = false;
        if (device == 0 || _setViewport == null) return;
        try
        {
            fixed (int* vp = _overlayVp)
            {
                var getVp = (delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)device)[15];
                if (getVp(device, vp) != 0) return;
                _overlayVpValid = true;
                int* c = stackalloc int[6];
                c[0] = 0; c[1] = 0; c[2] = UiCanvas.Left * 2 + 1024; c[3] = 768; c[4] = vp[4]; c[5] = vp[5];
                _setViewport(device, c);
            }
        }
        catch { _overlayVpValid = false; }
    }

    /// <summary>Tras dibujar el cursor: la proyección que quedó puesta, sin el ajuste del modo superposición.</summary>
    public static void RestoreProjection(nint device)
    {
        if (device == 0 || _setTransform == null) return;
        fixed (float* p = &_cur[32]) _setTransform(device, 3, p);
    }

    public static void EndOverlay(nint device)
    {
        if (device == 0 || _setViewport == null || !_overlayVpValid) return;
        fixed (int* vp = _overlayVp) _setViewport(device, vp);
        _overlayVpValid = false;
    }

    /// <summary>Durante un vídeo: pinta de negro las franjas fuera del lienzo (si no, se ve el fotograma anterior).</summary>
    internal static void ClearVideoSides(nint device)
    {
        if (_setViewport == null || _clear == null) return;
        int l = UiCanvas.Left, w = l * 2 + 1024;
        int* full = stackalloc int[6];
        full[0] = 0; full[1] = 0; full[2] = w; full[3] = 768;
        int* cur = stackalloc int[6];
        var getVp = (delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)device)[15];
        if (getVp(device, cur) != 0) return;
        full[4] = cur[4]; full[5] = cur[5];
        _setViewport(device, full);
        int* rects = stackalloc int[8];
        rects[0] = 0; rects[1] = 0; rects[2] = l; rects[3] = 768;
        rects[4] = l + 1024; rects[5] = 0; rects[6] = w; rects[7] = 768;
        _clear(device, 2, rects, 1 /* D3DCLEAR_TARGET */, 0xFF000000, 1.0f, 0);
        _setViewport(device, cur);
    }

    public static void EndUi(nint device)
    {
        if (device == 0 || _setViewport == null || !_savedVpValid) return;
        fixed (int* vp = _savedVp) _setViewport(device, vp);
        _savedVpValid = false;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int ClearHook(nint self, uint count, int* rects, uint flags, uint color, float z, uint stencil)
    {
        if (UiCanvas.Active && count > 0 && rects != null && count <= 64)
        {
            int* m = stackalloc int[(int)count * 4];
            for (int i = 0; i < count * 4; i++) m[i] = rects[i];
            int sh = UiCanvas.Shift;
            for (int i = 0; i < count; i++) { m[i * 4] += sh; m[i * 4 + 2] += sh; }
            return _clear(self, count, m, flags, color, z, stencil);
        }
        return _clear(self, count, rects, flags, color, z, stencil);
    }

    private static int Stride(uint fvf)
    {
        int s = (fvf & 0xE) switch { 0x2 => 12, 0x4 => 16, 0x6 => 16, 0x8 => 20, 0xA => 24, 0xC => 28, 0xE => 32, _ => 0 };
        if ((fvf & 0x10) != 0) s += 12;  // NORMAL
        if ((fvf & 0x20) != 0) s += 4;   // PSIZE (D3DFVF_RESERVED1 en DX7)
        if ((fvf & 0x40) != 0) s += 4;   // DIFFUSE
        if ((fvf & 0x80) != 0) s += 4;   // SPECULAR
        int tex = (int)((fvf >> 8) & 0xF);
        for (int t = 0; t < tex; t++)
        {
            int fmt = (int)((fvf >> (16 + t * 2)) & 3); // 0=2D, 1=3D, 2=4D, 3=1D
            s += fmt switch { 0 => 8, 1 => 12, 2 => 16, _ => 4 };
        }
        return s;
    }

    private static void Account(int api, int type, uint fvf, nint verts, uint count, int prims)
    {
        var key = (api, type, fvf);
        if (!_stats.TryGetValue(key, out var st)) _stats[key] = st = new Stat();
        st.Calls++;
        st.Prims += prims;
        if ((fvf & 0xE) == 0x4 && verts != 0) // XYZRHW: coordenadas de pantalla
        {
            st.Rhw = true;
            int stride = Stride(fvf);
            for (uint i = 0; i < count; i++)
            {
                float* v = (float*)(verts + (nint)(i * stride));
                if (v[0] < st.MinX) st.MinX = v[0];
                if (v[0] > st.MaxX) st.MaxX = v[0];
                if (v[1] < st.MinY) st.MinY = v[1];
                if (v[1] > st.MaxY) st.MaxY = v[1];
                if (i < MaxVertsPerCall) AddPoint(st, v[0], v[1], v[2]);
            }
        }
        else if ((fvf & 0xE) == 0x2 && verts != 0) // XYZ: lo transforma D3D; lo proyectamos igual que él
        {
            st.Proj = true;
            int stride = Stride(fvf);
            uint n = Math.Min(count, MaxVertsPerCall);
            for (uint i = 0; i < n; i++)
            {
                float* v = (float*)(verts + (nint)(i * stride));
                Project(v[0], v[1], v[2], out float sx, out float sy, out float sz, out bool behind);
                if (behind) { st.Behind++; continue; }
                AddPoint(st, sx, sy, sz);
            }
        }
    }

    private static void AddPoint(Stat st, float x, float y, float z)
    {
        st.N++;
        st.SumX += x; st.SumY += y;
        if (x >= _lastVp[0] && x < _lastVp[0] + _lastVp[2] && y >= _lastVp[1] && y < _lastVp[1] + _lastVp[3]) st.In++;
        if (z < st.MinZ) st.MinZ = z;
        if (z > st.MaxZ) st.MaxZ = z;
    }

    /// <summary>v * WORLD * VIEW * PROJECTION, división por w y viewport (como hace D3D).</summary>
    internal static void Project(float x, float y, float z, out float sx, out float sy, out float sz, out bool behind)
    {
        Span<float> a = stackalloc float[4] { x, y, z, 1 };
        Span<float> b = stackalloc float[4];
        for (int m = 0; m < 3; m++)
        {
            int o = m * 16;
            for (int c = 0; c < 4; c++)
                b[c] = a[0] * _cur[o + c] + a[1] * _cur[o + 4 + c] + a[2] * _cur[o + 8 + c] + a[3] * _cur[o + 12 + c];
            b.CopyTo(a);
        }
        behind = a[3] <= 1e-6f;
        float w = behind ? 1 : a[3];
        sx = _lastVp[0] + (a[0] / w + 1) * 0.5f * _lastVp[2];
        sy = _lastVp[1] + (1 - a[1] / w) * 0.5f * _lastVp[3];
        sz = a[2] / w;
    }

    private static string Mat(int state)
    {
        int o = (state - 1) * 16;
        var sb = new StringBuilder();
        for (int r = 0; r < 4; r++)
            sb.Append($"[{_cur[o + r * 4]:G7} {_cur[o + r * 4 + 1]:G7} {_cur[o + r * 4 + 2]:G7} {_cur[o + r * 4 + 3]:G7}]");
        return sb.ToString();
    }

    private static int Prims(int type, uint n) => type switch { 4 => (int)n / 3, 5 or 6 => (int)n - 2, 2 => (int)n / 2, 3 => (int)n - 1, _ => (int)n };

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int DrawPrimitiveHook(nint self, int type, uint fvf, nint verts, uint count, uint flags)
    {
        if (UiCanvas.InVideo && (fvf & 0xE) == 0x4) ClearVideoSides(self);
        if (UiDump.Enabled) { if ((fvf & 0xE) == 0x4) UiDump.OnUiDraw(0, type, fvf, (float*)verts, count, Stride(fvf), !UiCanvas.Active); else UiDump.OnWorldDraw(0, fvf, verts, count, Stride(fvf)); }
        if (MiniMap.NeedsFrame && UiCanvas.Active && type == 5 && count == 4 && (fvf & 0xE) == 0x4 && verts != 0) MiniMap.CheckFrame(self, (float*)verts, Stride(fvf), fvf);
        if (MiniMap.Active && (fvf & 0xE) == 0x4) verts = MiniMap.Transform(verts, count, Stride(fvf));
        if (UiCanvas.Active && (fvf & 0xE) == 0x4 && verts != 0)
        {
            int stride = Stride(fvf);
            if (UiCanvas.StretchBackground && !UiCanvas.Overlay && type == 5 && count == 4 && IsBackgroundTile((float*)verts, stride))
                return DrawStretched(self, type, fvf, verts, count, flags, stride);
            if (!UiCanvas.Overlay && IsBlackFullWidth(fvf, (float*)verts, count, stride))
                return DrawStretched(self, type, fvf, verts, count, flags, stride);
            verts = MapUi(verts, count, stride);
        }
        try
        {
            if (_reports < MaxReports)
            {
                Account(0, type, fvf, verts, count, Prims(type, count));
                if (fvf == 0x244 && verts != 0) AccountCaller((nint)(&self), (float*)verts, count, Stride(fvf));
            }
        }
        catch { }
        return _drawPrim(self, type, fvf, verts, count, flags);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int DrawIndexedPrimitiveHook(nint self, int type, uint fvf, nint verts, uint vcount, ushort* idx, uint icount, uint flags)
    {
        if (UiDump.Enabled) { if ((fvf & 0xE) == 0x4) UiDump.OnUiDraw(1, type, fvf, (float*)verts, vcount, Stride(fvf), !UiCanvas.Active); else UiDump.OnWorldDraw(1, fvf, verts, vcount, Stride(fvf)); }
        if (MiniMap.Active && (fvf & 0xE) == 0x4) verts = MiniMap.Transform(verts, vcount, Stride(fvf));
        if (UiCanvas.Active && (fvf & 0xE) == 0x4 && verts != 0) verts = MapUi(verts, vcount, Stride(fvf));
        try { if (_reports < MaxReports) Account(1, type, fvf, verts, vcount, Prims(type, icount)); } catch { }
        if ((fvf & 0xE) != 0x4)
        {
            int* saved = stackalloc int[6];
            if (BeginMenu3D(self, saved))
            {
                int r = _drawIdx(self, type, fvf, verts, vcount, idx, icount, flags);
                _setViewport(self, saved);
                return r;
            }
        }
        return _drawIdx(self, type, fvf, verts, vcount, idx, icount, flags);
    }

    /// <summary>
    /// DrawIndexedPrimitiveStrided (método 30): lo usan los modelos de Granny (personajes). Los datos van
    /// en D3DDRAWPRIMITIVESTRIDEDDATA, cuyo primer elemento es la posición {puntero, separación}.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int DrawIndexedPrimitiveStridedHook(nint self, int type, uint fvf, nint data, uint vcount, ushort* idx, uint icount, uint flags)
    {
        try
        {
            if (_reports < MaxReports && data != 0)
            {
                nint pos = *(nint*)data;
                uint stride = *(uint*)(data + sizeof(nint));
                AccountStrided(type, fvf, pos, stride, vcount, Prims(type, icount));
            }
        }
        catch { }
        {
            int* saved = stackalloc int[6];
            if (BeginMenu3D(self, saved))
            {
                // Personaje de un menú: mismo tamaño que en el lienzo, pero desplazado hacia fuera como el fondo
                float* p = stackalloc float[16];
                bool ok = MenuCharacterProjection(p, *(nint*)data, (int)*(uint*)(data + sizeof(nint)), vcount, StridedCaller((nint)(&self)));
                if (ok) _setTransform(self, 3, p);
                int r = _drawIdxStrided(self, type, fvf, data, vcount, idx, icount, flags);
                if (ok) { fixed (float* cur = &_cur[32]) _setTransform(self, 3, cur); }
                _setViewport(self, saved);
                return r;
            }
        }
        if (UiDump.Enabled && data != 0) UiDump.OnWorldDraw(4, fvf, *(nint*)data, vcount, (int)*(uint*)(data + sizeof(nint)));
        return _drawIdxStrided(self, type, fvf, data, vcount, idx, icount, flags);
    }

    private static void AccountStrided(int type, uint fvf, nint pos, uint stride, uint count, int prims)
    {
        var key = (4, type, fvf);
        if (!_stats.TryGetValue(key, out var st)) _stats[key] = st = new Stat();
        st.Calls++;
        st.Prims += prims;
        if (pos == 0 || (fvf & 0xE) != 0x2) return;
        st.Proj = true;
        uint n = Math.Min(count, MaxVertsPerCall);
        for (uint i = 0; i < n; i++)
        {
            float* v = (float*)(pos + (nint)(i * stride));
            Project(v[0], v[1], v[2], out float sx, out float sy, out float sz, out bool behind);
            if (behind) { st.Behind++; continue; }
            AddPoint(st, sx, sy, sz);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetTextureHook(nint self, uint stage, nint tex)
    {
        UiDump.OnSetTexture(stage, tex);
        if (stage == 0) _tex0 = tex;
        return _setTexture(self, stage, tex);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int DrawPrimitiveVBHook(nint self, int type, nint vb, uint start, uint count, uint flags)
    {
        try { if (_reports < MaxReports) Account(2, type, 0, 0, count, Prims(type, count)); } catch { }
        if (UiDump.Enabled) UiDump.OnWorldDraw(2, 0, 0, count, 0);
        return _drawPrimVB(self, type, vb, start, count, flags);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int DrawIndexedPrimitiveVBHook(nint self, int type, nint vb, uint start, uint count, ushort* idx, uint icount, uint flags)
    {
        try { if (_reports < MaxReports) Account(3, type, 0, 0, count, Prims(type, icount)); } catch { }
        if (UiDump.Enabled) UiDump.OnWorldDraw(3, 0, 0, count, 0);
        return _drawIdxVB(self, type, vb, start, count, idx, icount, flags);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int EndSceneHook(nint self)
    {
        try { UiDump.OnEndScene(); } catch { }
        MiniMap.OnEndScene();
        try { if (UiCanvas.StretchBackground) { UpdateMainMenu(self); UpdateCharacterGroups(); } } catch { }
        try
        {
            if (++_frames % 300 == 0 && _reports < MaxReports)
            {
                _reports++;
                var sb = new StringBuilder($"D3DTrace: resumen de 300 fotogramas (#{_reports}, total {_frames}):");
                foreach (var (key, s) in _stats)
                {
                    string k = $"{ApiNames[key.api]} t{key.type} fvf=0x{key.fvf:X}";
                    sb.Append($"\n    {k,-22} llamadas {s.Calls,6}  primitivas {s.Prims,8}");
                    if (s.Rhw) sb.Append($"  pantalla x {s.MinX:0}..{s.MaxX:0}  y {s.MinY:0}..{s.MaxY:0}");
                    if (s.N > 0)
                        sb.Append($"  {(s.Proj ? "proyectado" : "")} media ({s.SumX / s.N:0},{s.SumY / s.N:0}) dentro {100.0 * s.In / s.N:0}% z {s.MinZ:0.###}..{s.MaxZ:0.###}");
                    if (s.Behind > 0) sb.Append($" detrás de la cámara {s.Behind}");
                }
                if (_dpCallers.Count > 0)
                {
                    sb.Append("\n    DP 0x244 por llamador:");
                    var list = new List<KeyValuePair<nint, CallerStat>>(_dpCallers);
                    list.Sort((a, b) => b.Value.Calls.CompareTo(a.Value.Calls));
                    for (int i = 0; i < list.Count && i < 8; i++)
                    {
                        var (k, c) = (list[i].Key, list[i].Value);
                        sb.Append($" [0x{k:X8}: {c.Calls} x {c.MinX:0}..{c.MaxX:0} media {(c.N > 0 ? c.SumX / c.N : 0):0}]");
                    }
                    _dpCallers.Clear();
                }
                CallCounters.AppendReport(sb);
                AppendPlacement(sb);
                // Datos de la cámara del mundo (para cuadrar lo 3D con el suelo): posición en píxeles y base del ojo
                sb.Append($"\n    camara: px=({*(int*)0xAD7998},{*(int*)0xAD799C}) base_ojo=({*(float*)0x182EE00:G7},{*(float*)0x182EE04:G7},{*(float*)0x182EE08:G7}) objetivo=({*(float*)0x182EE20:G7},{*(float*)0x182EE24:G7},{*(float*)0x182EE28:G7})");
                sb.Append($"\n    viewport {_lastVp[0]},{_lastVp[1]} {_lastVp[2]}x{_lastVp[3]}  VIEW {Mat(2)}  PROJ {Mat(3)}  WORLD(última) {Mat(1)}");
                Log.Info(sb.ToString());
                _stats.Clear();
            }
        }
        catch { }
        return _endScene(self);
    }
}
