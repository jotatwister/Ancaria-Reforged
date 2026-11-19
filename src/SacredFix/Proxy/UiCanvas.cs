using System.Collections.Generic;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Proxy;

/// <summary>
/// Interfaz y menús centrados en un «lienzo» de 1024x768 ([Display] UiCenter=1, con el panorámico).
///
/// Idea tomada de kirides/sacred-bild (GPL-3, src/game/ui_canvas.*, device_proxy.*, frame_hooks.cpp).
/// Solo se usan sus datos (direcciones y método), no su código. Versión mínima: escala 1
/// y origen ((W - 1024) / 2, 0), así que con H = 768 basta con desplazar x.
///
/// Dibujo: mientras el juego dibuja la interfaz (cUI_Manager::render, 0x758ED0, llamado desde 0x60E9D4 y 0x7571C1)
/// los vértices ya transformados (XYZRHW) se desplazan +Left, el viewport pasa a (Left, 0, 1024, 768) para los
/// modelos 3D de la interfaz y los rectángulos de Clear se desplazan. Lo aplica D3DTrace (sus ganchos del
/// dispositivo se instalan también sin la traza cuando UiCenter está activo).
///
/// Ratón: la interfaz trabaja en coordenadas del lienzo (x - Left) y el mundo en píxeles reales:
///   - getClientCursorPos (0x66E280, cdecl (HWND, POINT*)): x -= Left para todo el juego.
///   - lecturas del mundo: cMouse::getX (0x655850) en 6 llamadas -> x + Left; cMouse_instance (0x654F60) en 5
///     llamadas -> copia de cMouse con x + Left; cEngine_worldMouse (0x6172C0) en sus 8 llamadas -> el evento
///     (x en +8) se pasa en píxeles reales; isCursorOverUi (0x75AA90) en 0x611EE3 recibe píxeles reales y se
///     le pasa x - Left.
/// </summary>
internal static unsafe class UiCanvas
{
    public static bool Enabled { get; private set; }
    /// <summary>Dentro de cUI_Manager::render o del cursor: D3DTrace desplaza el dibujo 2D.</summary>
    public static bool Active => _depth > 0;
    private static int _depth;
    public static int Left;

    /// <summary>
    /// Desplazamiento efectivo del lienzo: Left, o 0 mientras el mapa grande (cUI_Manager.windows[5], cUI_Megamap)
    /// está abierto. Esa ventana ya mide 1366x768 y su bucle de piezas cubre x < 0x556: se dibuja (y recibe el
    /// ratón) en coordenadas de pantalla, a pantalla completa.
    /// </summary>
    public static int Shift => Left; // (probado: con el mapa grande sin lienzo, su lámina y su marco son de 1024 y se pegaban a la izquierda)
    /// <summary>¿Está abierto el mapa grande (windows[5])?</summary>
    public static bool FullScreenWindow
    {
        get
        {
            nint mgr = *(nint*)0x17EEBBC;
            if (mgr == 0) return false;
            nint win = *(nint*)(mgr + 0x80 + 5 * 4);
            return win != 0 && (*(uint*)(win + 0x10) & 1) != 0;
        }
    }

    private static delegate* unmanaged[Thiscall]<nint, nint, void> _uiRender;
    private static delegate* unmanaged[Cdecl]<nint, int*, void> _getCursor;
    private static delegate* unmanaged[Thiscall]<nint, nint, int, void> _renderCursor;
    private static delegate* unmanaged[Thiscall]<nint, nint, nint, nint, int, int, int> _playVideo;
    private static delegate* unmanaged[Stdcall]<nint, int, int, int, int, nint, int, int, uint, int> _bitBlt;
    private static delegate* unmanaged[Stdcall]<nint, nint, int, int*, uint, int> _drawText;
    /// <summary>Reproduciendo un vídeo: D3DTrace pinta de negro las franjas laterales antes de cada fotograma.</summary>
    public static bool InVideo;
    /// <summary>[Display] MenuBackground=1: los fondos de los menús se estiran a toda la pantalla (lo demás sigue centrado).</summary>
    public static bool StretchBackground;
    private static delegate* unmanaged[Thiscall]<nint, nint, int, uint> _worldMouse;
    private static delegate* unmanaged[Thiscall]<nint, int> _getX;
    private static delegate* unmanaged[Cdecl]<nint> _mouseInstance;
    private static delegate* unmanaged[Thiscall]<nint, int, int, int> _overUi;
    private static nint _shadow; // copia de cMouse con x en píxeles reales

    public static void Init(Config cfg)
    {
        if (!cfg.GetBool("Fixes", "Widescreen", false) || !cfg.GetBool("Display", "UiCenter", false)) return;
        int w = Resolution.Width(cfg);
        StretchBackground = cfg.GetBool("Display", "MenuBackground", true);
        Left = (w - 1024) / 2;
        if (Left <= 0) return;
        try
        {
            Redirect(new nint[] { 0x60E9D4, 0x7571C1 }, 0x758ED0, Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, nint, void>)&UiRenderHook), out var r1);
            _uiRender = (delegate* unmanaged[Thiscall]<nint, nint, void>)r1;
            // El cursor se dibuja aparte (cMouse_renderCursor 0x655450, thiscall(this, device, flag), ret 8): sin esto
            // sale en coordenadas del lienzo, 171 px a la izquierda de donde el juego registra el clic.
            Redirect(new nint[] { 0x612E6A, 0x612E91, 0x757227 }, 0x655450, Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, nint, int, void>)&RenderCursorHook), out var r0);
            _renderCursor = (delegate* unmanaged[Thiscall]<nint, nint, int, void>)r0;
            // Vídeos (playVideo 0x6A0EA0, thiscall(this, ddraw, primary, stream, w, h), ret 0x14): un cuadro de
            // (0,0)-(1024,768) con su propio bucle de fotogramas; se dibuja dentro del lienzo.
            Redirect(new nint[] { 0x6A1618, 0x6A1838, 0x6A18E9, 0x6A199D, 0x6A1A56, 0x6A1B1B, 0x6A1BD4, 0x6A1C8D, 0x6A1D46, 0x6A1DFF },
                     0x6A0EA0, Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int, int, int>)&PlayVideoHook), out var rv);
            _playVideo = (delegate* unmanaged[Thiscall]<nint, nint, nint, nint, int, int, int>)rv;
            // Pantalla de carga (0x6468A0): copia GDI de la imagen de 1024x768 con BitBlt en x = 0. En 0x6469B9,
            // «mov edi, [IAT BitBlt]» (8B 3D 34 00 89 00) pasa a «mov edi, gancho» (BF xx xx xx xx 90).
            InstallLoadingBitBlt();
            if (StretchBackground) { D3DTrace.InstallCharacterCounter(); InstallHeroWindows(); }
            InstallTipHook();
            if (cfg.GetBool("Display", "PortraitsRight", true)) InstallMercenary();
            // Objeto en el cursor (cInventoryEntry::render 0x5DC430, thiscall(this, device, flag), ret 8; lo llaman
            // las ventanas que aceptan objetos): sin el recorte de la ventana que lo dibuja (como sacred-bild).
            Redirect(new nint[] { 0x6A347D, 0x6A348B, 0x6ACC3F, 0x6C14F5, 0x6DAAA9, 0x6E8C3D, 0x6EAFB1, 0x6EAFC2 },
                     0x5DC430, Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, nint, int, void>)&HeldItemHook), out var rh);
            _heldItem = (delegate* unmanaged[Thiscall]<nint, nint, int, void>)rh;
            Redirect(new nint[] { 0x7571B6, 0x812337, 0x8123C4, 0x81245F, 0x81254D, 0x8125C4, 0x812652, 0x8126E0, 0x8131FC, 0x81325C, 0x8132E6, 0x8133A6, 0x813432 },
                     0x66E280, (nint)(delegate* unmanaged[Cdecl]<nint, int*, void>)&GetCursorHook, out var r2);
            _getCursor = (delegate* unmanaged[Cdecl]<nint, int*, void>)r2;
            Redirect(new nint[] { 0x618111, 0x618124, 0x61834F, 0x618516, 0x61865B, 0x618679, 0x6186E7, 0x618DBA },
                     0x6172C0, Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, nint, int, uint>)&WorldMouseHook), out var r3);
            _worldMouse = (delegate* unmanaged[Thiscall]<nint, nint, int, uint>)r3;
            Redirect(new nint[] { 0x611F54, 0x611F9D, 0x61273C, 0x6127C8, 0x617DFC, 0x617E69 },
                     0x655850, Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, int>)&GetXHook), out var r4);
            _getX = (delegate* unmanaged[Thiscall]<nint, int>)r4;
            _shadow = Mem.AllocCode(new byte[0x80]);
            Redirect(new nint[] { 0x4FB881, 0x60F268, 0x6104C7, 0x611EC3, 0x62764E },
                     0x654F60, (nint)(delegate* unmanaged[Cdecl]<nint>)&MouseInstanceHook, out var r5);
            _mouseInstance = (delegate* unmanaged[Cdecl]<nint>)r5;
            Redirect(new nint[] { 0x611EE3 }, 0x75AA90, Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, int, int, int>)&OverUiHook), out var r6);
            _overUi = (delegate* unmanaged[Thiscall]<nint, int, int, int>)r6;
            Enabled = true;
            Log.Info($"UiCanvas: interfaz y menús centrados (lienzo 1024x768 en x = {Left}), ratón convertido (33 llamadas), cursor (3), vídeos (10), pantalla de carga (3 copias y 2 textos)");
        }
        catch (Exception ex) { Log.Error("UiCanvas: " + ex.Message); }
    }

    /// <summary>Redirige todas las llamadas o ninguna (comprueba antes que cada una va a <paramref name="target"/>).</summary>
    private static void Redirect(nint[] sites, nint target, nint hook, out nint original)
    {
        foreach (var s in sites)
            if (Mem.Read<byte>(s) != 0xE8 || s + 5 + Mem.Read<int>(s + 1) != target)
                throw new InvalidOperationException($"0x{s:X8} no llama a 0x{target:X8}");
        foreach (var s in sites) Mem.RedirectCall(s, hook);
        original = target;
    }

    // ---- Personajes de la selección: cada uno es una ventana de la interfaz ----

    /// <summary>Las ventanas de los personajes de la selección se han movido (para el clic; el dibujo lo desplaza D3DTrace).</summary>
    public static bool HeroWindows;
    private static delegate* unmanaged[Thiscall]<nint, nint, uint, uint, uint> _heroRender;
    private static readonly Dictionary<nint, (int orig, int set)> _heroX = new();

    /// <summary>
    /// Cada personaje de la selección es una ventana (render 0x6F1B30: thiscall(this, device, a, b), ret 0xC; casilla 5 de su tabla virtual en 0x896CBC)
    /// que dibuja al personaje en su centro (x + ancho/2) y recibe el clic en su rectángulo (x en +0x24, ancho
    /// int16 en +0x2C, relativo a la ventana padre de +0x50). Con el fondo estirado, se mueve la ventana para
    /// que su centro se aleje del centro del lienzo W/1024 veces: el personaje queda sobre su roca y el clic cuadra.
    /// </summary>
    private static void InstallHeroWindows()
    {
        nint hook = Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint>)&HeroRenderHook);
        int n = 0;
        foreach (nint slot in new nint[] { 0x896CD0, 0x16A4464 })
        {
            if (Mem.Read<nint>(slot) != 0x6F1B30) continue;
            Mem.Write(slot, hook);
            n++;
        }
        if (n == 0) { Log.Warn("UiCanvas: no se encontró la tabla de las ventanas de personaje (0x6F1B30)"); return; }
        _heroRender = (delegate* unmanaged[Thiscall]<nint, nint, uint, uint, uint>)0x6F1B30;
        HeroWindows = true;
        Log.Info($"UiCanvas: ventanas de los personajes de la selección ({n} tablas)");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint HeroRenderHook(nint self, nint device, uint a, uint b)
    {
        try
        {
            int* px = (int*)(self + 0x24);
            if (!_heroX.TryGetValue(self, out var e) || *px != e.set)
            {
                // posición absoluta del centro (x es relativa a la ventana padre)
                int abs = *px + *(short*)(self + 0x2C) / 2;
                nint p = *(nint*)(self + 0x50);
                for (int guard = 0; p != 0 && guard < 8; guard++) { abs += *(int*)(p + 0x24); p = *(nint*)(p + 0x50); }
                float k = (Left * 2 + 1024) / 1024f;
                int nx = *px + (int)MathF.Round((abs - 512) * (k - 1));
                _heroX[self] = (*px, nx);
                *px = nx;
            }
        }
        catch { }
        InHeroWindow = true; // (la capa D3D9 no recorta lo 2D de estas ventanas: sus carteles salen del lienzo)
        try { return _heroRender(self, device, a, b); }
        finally { InHeroWindow = false; }
    }
    public static bool InHeroWindow;

    private static delegate* unmanaged[Thiscall]<nint, nint, int, void> _heldItem;
    private static int _heldLogs;
    /// <summary>Dibujando el objeto del cursor: D3DTrace corrige cada PROYECCIÓN que ponga el juego.</summary>
    public static bool InHeldItem;

    /// <summary>
    /// Dentro de su dibujo el juego pone tres proyecciones: dos de ±512 (lo 2D) y una de ±267 para el modelo 3D
    /// del objeto, cuya posición calcula con los valores del panorámico (±267·W/1024): se separaba del cursor hacia
    /// fuera, en proporción a la distancia al centro. A esa se le escala la x por 1024/W.
    /// </summary>
    public static void HeldItemProjection(float* m)
    {
        // Dentro pone tres: dos ortho de ±512 (las del lienzo, correctas) y la del modelo, que el grupo 10 del
        // panorámico ensancha de ±267 a ±356 (267·W/1024). En la interfaz el viewport es el lienzo de 1024, así que
        // esa se devuelve a la original (×W/1024); las de ±512 no se tocan.
        if (MathF.Abs(MathF.Abs(m[0]) - 2f / 1024f) < 1e-5f) return;
        float k = (Left * 2 + 1024) / 1024f;
        m[0] *= k; m[4] *= k; m[8] *= k; m[12] *= k;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void HeldItemHook(nint self, nint device, int flag)
    {
        // Como beginOverlay3D de sacred-bild: el objeto es un modelo 3D colocado respecto al viewport que puso la
        // ventana (x0, y0, w0, h0). Se dibuja en el viewport de todo el lienzo (sin recorte) y la proyección se
        // corrige para que cada punto quede donde lo habría puesto el viewport original:
        //   x' = a·x + b·w, y' = c·y + d·w;  a = w0/W, b = (2(x0-X)+w0)/W - 1, c = h0/H, d = 1 - (2(y0-Y)+h0)/H.
        int* vp0 = stackalloc int[6];
        float* saved = stackalloc float[16];
        var getVp = (delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)device)[15];
        var getT = (delegate* unmanaged[Stdcall]<nint, int, float*, int>)(*(nint**)device)[12];
        var setT = (delegate* unmanaged[Stdcall]<nint, int, float*, int>)(*(nint**)device)[11];
        bool ok = getVp(device, vp0) == 0 && getT(device, 3, saved) == 0 && vp0[2] > 0 && vp0[3] > 0;
        if (_heldLogs < 6 && ok)
        {
            _heldLogs++;
            int* cp = stackalloc int[2];
            _getCursor(0, cp);
            Log.Info($"UiCanvas: objeto del cursor: viewport {vp0[0]},{vp0[1]} {vp0[2]}x{vp0[3]}, cursor (lienzo) {cp[0]},{cp[1]}, PROJ [{saved[0]:0.#####} {saved[1]:0.#####} {saved[2]:0.#####} {saved[3]:0.#####}] [{saved[4]:0.#####} {saved[5]:0.#####} {saved[6]:0.#####} {saved[7]:0.#####}] [{saved[8]:0.#####} {saved[9]:0.#####} {saved[10]:0.#####} {saved[11]:0.#####}] [{saved[12]:0.#####} {saved[13]:0.#####} {saved[14]:0.#####} {saved[15]:0.#####}]");
        }
        if (ok)
        {
            // El juego vuelve a poner la proyección dentro de su dibujo: la corrección se hace en SetTransform
            // (D3DTrace) mientras InHeldItem; aquí solo se corrige la que ya está puesta.
            // (la que ya está puesta es la del mundo: no se toca; la corrección va en D3DTrace.SetTransform)
        }
        InHeldItem = true;
        try { _heldItem(self, device, flag); }
        finally
        {
            InHeldItem = false;
            if (ok) setT(device, 3, saved);
        }
    }

    // ---- Retratos del grupo (cUI_Mercenary) pegados al borde derecho de la pantalla ----

    private static delegate* unmanaged[Thiscall]<nint, nint, uint> _mercRender;
    private static readonly Dictionary<nint, (int orig, int set)> _mercX = new();

    /// <summary>
    /// Ventana de los retratos del grupo (cUI_Manager.windows[7], cUI_Mercenary: x = 932, 92x676 en el lienzo;
    /// render 0x6D70F0 = thiscall(this, device), ret 4; casilla 5 de su tabla en 0x8968CC). Se mueve Left píxeles a
    /// la derecha (pegada al borde de la pantalla; el clic sigue a la ventana) y se dibuja con el viewport de toda la
    /// pantalla para que no la recorte el del lienzo.
    /// </summary>
    private static void InstallMercenary()
    {
        const nint slot = 0x8968CC + 5 * 4;
        if (Mem.Read<nint>(slot) != 0x6D70F0) { Log.Warn("UiCanvas: la tabla de cUI_Mercenary no es la esperada; retratos sin mover"); return; }
        _mercRender = (delegate* unmanaged[Thiscall]<nint, nint, uint>)0x6D70F0;
        Mem.Write(slot, Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, nint, uint>)&MercRenderHook));
        // Ayudas de los retratos (vida, experiencia, efectos): se marcan las que abre la ventana mientras procesa el
        // ratón (casilla 4, receiveEvent 0x6D6C70, ret 4) o se dibuja, y tras colocarlas el juego se desplazan Left.
        const nint evSlot = 0x8968CC + 4 * 4;
        if (Mem.Read<nint>(evSlot) == 0x6D6C70)
        {
            _mercEvent = (delegate* unmanaged[Thiscall]<nint, nint, uint>)0x6D6C70;
            Mem.Write(evSlot, Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, nint, uint>)&MercEventHook));
            InstallPopupHooks();
        }
        Log.Info("UiCanvas: retratos del grupo pegados al borde derecho (con sus ayudas)");
    }

    private static delegate* unmanaged[Thiscall]<nint, nint, uint> _mercEvent;
    private static delegate* unmanaged[Thiscall]<nint, uint, uint, uint, uint> _setText, _setTextId;
    private static delegate* unmanaged[Thiscall]<nint, void> _popupLayout;
    private static bool _inMerc;
    private static delegate* unmanaged[Thiscall]<nint, nint, uint> _tipRender;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint TipRenderHook(nint self, nint device)
    {
        D9.Overlay.DrawCooldownsInScene(); // (los segundos de recarga, debajo de la ayuda)
        if (ItemCompare.IsCandidate(self, out int cmpItem))
        {
            uint r = _tipRender(self, device);
            ItemCompare.DrawFor(self, cmpItem, device); // (la ficha del objeto equipado, al lado de la del juego)
            return r;
        }
        if (!_mercPopups.Contains(self)) return _tipRender(self, device);
        D3DTrace.BeginOverlay(device);
        try { return _tipRender(self, device); }
        finally { D3DTrace.EndOverlay(device); }
    }
    private static readonly HashSet<nint> _mercPopups = new();

    /// <summary>
    /// setText (0x6E6EA0) y setTextId (0x6E6FB0) de cUI_Popup: thiscall(this, a, b, c), ret 0xC; tienen
    /// muchos llamadores, así que se desvían con un salto en su entrada (Detour).
    /// La colocación (0x6E7AF0, thiscall(this)) solo se llama en 0x6E7728.
    /// </summary>
    /// <summary>
    /// Dibujo de las ayudas (cUI_Tooltip, casilla 5 de 0x896B3C: 0x6E76C0, thiscall(this, device), ret 4). Antes de la
    /// primera ayuda del fotograma se dibujan los segundos de recarga, para que la ayuda quede encima. Las de los retratos
    /// van con el viewport de toda la pantalla (las del lienzo de 1024 las recortaría).
    /// </summary>
    private static void InstallTipHook()
    {
        const nint tipSlot = 0x896B3C + 5 * 4;
        if (Mem.Read<nint>(tipSlot) != 0x6E76C0) { Log.Warn("UiCanvas: la tabla de cUI_Tooltip no es la esperada"); return; }
        _tipRender = (delegate* unmanaged[Thiscall]<nint, nint, uint>)0x6E76C0;
        Mem.Write(tipSlot, Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, nint, uint>)&TipRenderHook));
    }

    private static void InstallPopupHooks()
    {
        _setText = (delegate* unmanaged[Thiscall]<nint, uint, uint, uint, uint>)Detour(0x6E6EA0, new byte[] { 0x8B, 0x44, 0x24, 0x04, 0x53, 0x55 }, Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint>)&SetTextHook));
        _setTextId = (delegate* unmanaged[Thiscall]<nint, uint, uint, uint, uint>)Detour(0x6E6FB0, new byte[] { 0x53, 0x55, 0x56, 0x57, 0x8B, 0x7C, 0x24, 0x18 }, Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint>)&SetTextIdHook));
        Redirect(new nint[] { 0x6E7728 }, 0x6E7AF0, Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, void>)&LayoutHook), out var lay);
        _popupLayout = (delegate* unmanaged[Thiscall]<nint, void>)lay;
    }

    /// <summary>
    /// Desvío en la entrada de una función: copia sus primeras instrucciones enteras (al menos 5 bytes) a un trozo
    /// que salta de vuelta, y pone un jmp al gancho. Devuelve el trozo (la función original).
    /// </summary>
    private static nint Detour(nint fn, byte[] prologue, nint hook)
    {
        // las instrucciones copiadas no dependen de su dirección (push / mov con [esp+n])
        if (!Mem.Matches(fn, prologue)) throw new InvalidOperationException($"0x{fn:X8} inesperado: {Mem.Hex(fn, prologue.Length)}");
        int n = prologue.Length;
        var c = new byte[n + 5];
        for (int i = 0; i < n; i++) c[i] = Mem.Read<byte>(fn + i);
        c[n] = 0xE9;
        nint tramp = Mem.AllocCode(c);
        *(int*)(tramp + n + 1) = (int)((fn + n) - (tramp + n + 5));
        Span<byte> j = stackalloc byte[n];
        j.Fill(0x90);
        j[0] = 0xE9; BitConverter.TryWriteBytes(j[1..5], (int)(hook - (fn + 5)));
        Mem.WriteBytes(fn, j);
        return tramp;
    }

    private static void Mark(nint popup)
    {
        if (_inMerc) _mercPopups.Add(popup); else _mercPopups.Remove(popup);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint SetTextHook(nint self, uint a, uint b, uint c) { Mark(self); return _setText(self, a, b, c); }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint SetTextIdHook(nint self, uint a, uint b, uint c) { Mark(self); return _setTextId(self, a, b, c); }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void LayoutHook(nint self)
    {
        _popupLayout(self);
        try
        {
            if (!_mercPopups.Contains(self)) return;
            // el juego la encaja en 16..1008 del lienzo; se lleva junto a los retratos (Left más a la derecha),
            // sin pasar de 16 px del borde derecho de la pantalla
            int* px = (int*)(self + 0x24);
            int w = *(short*)(self + 0x2C);
            int nx = *px + Left, max = Left + 1024 - 16 - w; // x del lienzo: el borde de la pantalla es Left + 1024
            *px = Math.Min(nx, max);
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint MercEventHook(nint self, nint ev)
    {
        bool was = _inMerc;
        _inMerc = true;
        try { return _mercEvent(self, ev); }
        finally { _inMerc = was; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint MercRenderHook(nint self, nint device)
    {
        try
        {
            int* px = (int*)(self + 0x24);
            if (!_mercX.TryGetValue(self, out var e) || *px != e.set)
            {
                int nx = *px + Left;
                _mercX[self] = (*px, nx);
                *px = nx;
            }
        }
        catch { }
        D3DTrace.BeginOverlay(device);
        bool was = _inMerc;
        _inMerc = true;
        try { return _mercRender(self, device); }
        finally { _inMerc = was; D3DTrace.EndOverlay(device); }
    }

    // ---- Dibujo de la interfaz ----

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void UiRenderHook(nint self, nint device)
    {
        Enter(device);
        if (_depth == 1)
        {
            // Buffs: antes que la interfaz del juego, para que sus ayudas queden encima (sin desplazar: pantalla)
            _depth = 0;
            if (MiniMap.InGameView()) { D9.Overlay.MarkGameUi(); AutoGold.Tick(); TrackEnemy.Tick(); HeroWatch.Tick(); } // (nivel y supervivencia, y el oro: solo en fotogramas de la partida)
            try { MiniMap.DrawBuffs(device); } finally { _depth = 1; }
        }
        try { _uiRender(self, device); }
        finally
        {
            // Minimapa: al final de la interfaz (con su estado gráfico, como el mapa del tabulador) pero sin el
            // desplazamiento del lienzo (coordenadas de pantalla)
            if (_depth == 1)
            {
                _depth = 0;
                try { MiniMap.Draw(device); } finally { _depth = 1; }
            }
            Leave(device);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void RenderCursorHook(nint self, nint device, int flag)
    {
        // Modo «superposición» (como sacred-bild): los vértices se desplazan, pero el viewport es el de toda la
        // pantalla, para que el cursor se vea también fuera del lienzo (en las franjas laterales).
        D9.Overlay.DrawCooldownsInScene(); // (si no hubo ayudas: debajo del cursor)
        D9.OptionsMenu.DrawInScene(); // (el menú propio, debajo del cursor del juego)
        bool wasOverlay = Overlay;
        _depth++;
        Overlay = true;
        D3DTrace.BeginOverlay(device);
        try { _renderCursor(self, device, flag); }
        finally
        {
            D3DTrace.EndOverlay(device);
            Overlay = wasOverlay;
            if (!wasOverlay) D3DTrace.RestoreProjection(device);
            _depth--;
        }
    }

    /// <summary>Dibujando el cursor: sin recortar al lienzo (D3DTrace no desplaza el viewport).</summary>
    public static bool Overlay;

    /// <summary>Ámbito de dibujo del lienzo; anidable (el viewport se cambia solo en el más externo).</summary>
    private static void Enter(nint device)
    {
        if (_depth++ == 0)
        {
            // mapa grande abierto: el mundo no se dibuja, así que los lados se pintan de negro (no el último fotograma)
            if (FullScreenWindow) D3DTrace.ClearVideoSides(device);
            D3DTrace.BeginUi(device);
        }
    }

    private static void Leave(nint device)
    {
        if (--_depth == 0) D3DTrace.EndUi(device);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int PlayVideoHook(nint self, nint ddraw, nint primary, nint stream, int w, int h)
    {
        // Con la capa D3D9 los vídeos (tincat2.dll, interfaces antiguas de DirectDraw) aún no se pueden mostrar: se saltan
        if (D9.Layer.Enabled) Log.Info("D9: vídeo (DirectShow sobre la capa D3D9)");
        _depth++;
        bool wasVideo = InVideo;
        InVideo = true;
        nint dev = D3DTrace.Device;
        if (_depth == 1 && dev != 0) D3DTrace.BeginUi(dev);
        try { return _playVideo(self, ddraw, primary, stream, w, h); }
        finally
        {
            if (_depth == 1 && dev != 0) D3DTrace.EndUi(dev);
            InVideo = wasVideo;
            _depth--;
        }
    }

    private static void InstallLoadingBitBlt()
    {
        const nint site = 0x6469B9;
        ReadOnlySpan<byte> expected = new byte[] { 0x8B, 0x3D, 0x34, 0x00, 0x89, 0x00 };
        if (!Mem.Matches(site, expected)) throw new InvalidOperationException($"0x6469B9 inesperado: {Mem.Hex(site, 6)}");
        _bitBlt = (delegate* unmanaged[Stdcall]<nint, int, int, int, int, nint, int, int, uint, int>)Mem.Read<nint>(0x890034);
        nint hook = (nint)(delegate* unmanaged[Stdcall]<nint, int, int, int, int, nint, int, int, uint, int>)&LoadingBitBltHook;
        Span<byte> code = stackalloc byte[6];
        code[0] = 0xBF; BitConverter.TryWriteBytes(code[1..5], (int)hook); code[5] = 0x90;
        Mem.WriteBytes(site, code);

        // Textos de la carga: «call dword ptr [IAT DrawTextA]» (FF 15 04 03 89 00) en 0x646AD7 y 0x646B12
        // -> «call gancho» (E8 rel32) + nop.
        _drawText = (delegate* unmanaged[Stdcall]<nint, nint, int, int*, uint, int>)Mem.Read<nint>(0x890304);
        nint textHook = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int, int*, uint, int>)&LoadingDrawTextHook;
        ReadOnlySpan<byte> callIat = new byte[] { 0xFF, 0x15, 0x04, 0x03, 0x89, 0x00 };
        foreach (nint t in new nint[] { 0x646AD7, 0x646B12 })
        {
            if (!Mem.Matches(t, callIat)) throw new InvalidOperationException($"0x{t:X8} inesperado: {Mem.Hex(t, 6)}");
            Span<byte> c = stackalloc byte[6];
            c[0] = 0xE8; BitConverter.TryWriteBytes(c[1..5], (int)(textHook - (t + 5))); c[5] = 0x90;
            Mem.WriteBytes(t, c);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int LoadingDrawTextHook(nint hdc, nint text, int len, int* rc, uint fmt)
    {
        if (rc == null) return _drawText(hdc, text, len, rc, fmt);
        int* r = stackalloc int[4];
        // Con la imagen estirada, el texto se mueve con ella (su centro x·W/1024) sin cambiar de ancho
        int d = StretchBackground ? (int)MathF.Round((rc[0] + rc[2]) / 2f * ((Left * 2 + 1024) / 1024f - 1)) : Left;
        r[0] = rc[0] + d; r[1] = rc[1]; r[2] = rc[2] + d; r[3] = rc[3];
        return _drawText(hdc, text, len, r, fmt);
    }

    /// <summary>
    /// Copias de la pantalla de carga (imagen y barra de progreso). Con [Display] MenuBackground=1 se estiran a todo
    /// el ancho (x·W/1024, como los fondos de los menús); si no, se centran. El fondo negro (BLACKNESS) cubre toda la
    /// pantalla.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int LoadingBitBltHook(nint hdc, int x, int y, int cx, int cy, nint src, int x1, int y1, uint rop)
    {
        int w = Left * 2 + 1024;
        if (rop == 0x00000042) { x = 0; cx = w; } // BLACKNESS: toda la pantalla
        else if (StretchBackground)
        {
            float k = w / 1024f;
            int nx = (int)MathF.Round(x * k), nr = (int)MathF.Round((x + cx) * k);
            Native.Win32.SetStretchBltMode(hdc, 4); // HALFTONE
            Native.Win32.SetBrushOrgEx(hdc, 0, 0, 0);
            return Native.Win32.StretchBlt(hdc, nx, y, nr - nx, cy, src, x1, y1, cx, cy, rop);
        }
        else
        {
            x += Left;
            // Laterales en negro: con los fondos de los menús estirados, ahí quedaba la selección de personaje
            _bitBlt(hdc, 0, 0, Left, 768, 0, 0, 0, 0x00000042);
            _bitBlt(hdc, Left + 1024, 0, Left, 768, 0, 0, 0, 0x00000042);
        }
        return _bitBlt(hdc, x, y, cx, cy, src, x1, y1, rop);
    }

    // ---- Ratón ----

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void GetCursorHook(nint hwnd, int* pt)
    {
        _getCursor(hwnd, pt);
        if (pt != null) pt[0] -= Shift;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint WorldMouseHook(nint self, nint ev, int flag)
    {
        if (ev == 0) return _worldMouse(self, ev, flag);
        int* x = (int*)(ev + 8);
        int old = *x;
        *x = old + Shift;
        uint r = _worldMouse(self, ev, flag);
        *x = old;
        return r;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetXHook(nint self) => _getX(self) + Shift;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint MouseInstanceHook()
    {
        nint m = _mouseInstance();
        if (m == 0) return m;
        Buffer.MemoryCopy((void*)m, (void*)_shadow, 0x80, 0x70);
        *(int*)(_shadow + 4) += Shift;   // x
        *(int*)(_shadow + 0xC) += Shift; // x dibujada
        return _shadow;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OverUiHook(nint self, int x, int y) => _overUi(self, x - Shift, y) & 0xFF;
}
