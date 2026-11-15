using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Proxy;

/// <summary>
/// Teclado moderno ([Keys]). El juego tiene las teclas fijas (1-5 armas, 6-0 elegir el arte activo, que se lanza con
/// el botón derecho; Espacio Q W E R pociones). Se sustituye el procedimiento de la ventana del juego para:
///   - Remap: traducir teclas antes de que el juego las vea («F1:1» = F1 hace de 1). Se cambian la tecla virtual y
///     el código de exploración del lParam en WM_KEYDOWN/UP; WM_CHAR también si las dos teclas son imprimibles.
///   - QuickCast: tras una tecla de arte (6-0, ya traducida) se envía un clic derecho en la posición del cursor, así
///     el arte se elige y se lanza con una sola pulsación. No se hace con el chat abierto (ventana 9 visible).
/// El juego lee el ratón con getClientCursorPos (no del lParam), así que basta con el mensaje del botón.
/// </summary>
internal static unsafe partial class KeyInput
{
    public static bool Enabled { get; private set; }
    private static bool _quickCast, _installed;
    private static readonly Dictionary<uint, uint> _remap = new();
    private static nint _hwnd;
    // Botón derecho «pulsado» por el lanzamiento con una tecla: hasta cuándo (ms) y si falta enviar el soltado
    private static long _holdUntil;
    private static bool _pendingUp;
    private static int _holdMs = 350;
    private static int _castDelayMs = 80;
    private static long _castAt; // clic derecho pendiente: el juego cambia el arte activo en un fotograma, luego se pulsa
    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private static delegate* unmanaged[Stdcall]<int, short> _getAsync;

    [LibraryImport("user32.dll")] private static partial nint GetActiveWindow();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EnumWindows(nint cb, nint param);
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint hwnd, uint* pid);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindowVisible(nint hwnd);
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentProcessId();
    private static nint _found;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int EnumProc(nint hwnd, nint param)
    {
        uint pid = 0;
        GetWindowThreadProcessId(hwnd, &pid);
        // la ventana del juego: la de su procedimiento sacredWndProc (0x812BF0); hay otras del proceso (Steam, IME)
        if (pid == GetCurrentProcessId() && GetWindowLongA(hwnd, -4) == 0x812BF0) { _found = hwnd; return 0; }
        return 1;
    }
    [LibraryImport("user32.dll")] private static partial int SetWindowLongA(nint hwnd, int index, int value);
    [LibraryImport("user32.dll")] private static partial int GetWindowLongA(nint hwnd, int index);
    [LibraryImport("user32.dll")] private static partial nint CallWindowProcA(nint proc, nint hwnd, uint msg, nint w, nint l);
    [LibraryImport("user32.dll")] private static partial uint MapVirtualKeyA(uint code, uint type);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PostMessageA(nint hwnd, uint msg, nint w, nint l);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(int* pt);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ScreenToClient(nint hwnd, int* pt);

    /// <summary>Desde el menú propio: lanzar artes con una tecla.</summary>
    public static bool QuickCast
    {
        get => _quickCast;
        set { _quickCast = value; Enabled = _quickCast || _remap.Count > 0; }
    }

    /// <summary>Traducción de teclas actual, como en el ini («1:6,F1:1»).</summary>
    public static string RemapText()
    {
        lock (_remap)
        {
            var parts = new List<string>();
            foreach (var kv in _remap) parts.Add($"{KeyName(kv.Key)}:{KeyName(kv.Value)}");
            return string.Join(",", parts);
        }
    }

    /// <summary>Desde el menú propio: cambia la traducción de teclas al momento.</summary>
    public static void SetRemap(string text)
    {
        lock (_remap)
        {
            _remap.Clear();
            foreach (var pair in text.Split(',', ';'))
            {
                var kv = pair.Split(':');
                if (kv.Length == 2 && ParseKey(kv[0].Trim(), out uint from) && ParseKey(kv[1].Trim(), out uint to) && from != to) _remap[from] = to;
            }
        }
        Enabled = _quickCast || _remap.Count > 0;
    }

    /// <summary>La tecla física que hace de esta tecla del juego (la suya si no está traducida).</summary>
    public static uint PhysicalFor(uint gameVk)
    {
        lock (_remap)
        {
            foreach (var kv in _remap) if (kv.Value == gameVk) return kv.Key;
            return _remap.ContainsKey(gameVk) ? 0 : gameVk; // (0: su tecla hace otra cosa y nadie hace esta)
        }
    }

    /// <summary>Nombre de una tecla virtual como se escribe en el ini.</summary>
    public static string KeyName(uint vk)
    {
        if ((vk >= '0' && vk <= '9') || (vk >= 'A' && vk <= 'Z')) return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
        if (vk >= 0x60 && vk <= 0x69) return "NUM" + (vk - 0x60);
        return vk switch { 0x20 => "SPACE", 0x09 => "TAB", 0x6B => "NUM+", 0x6D => "NUM-", 0x0D => "ENTER", _ => $"0x{vk:X2}" };
    }

    public static void Init(Config cfg)
    {
        _quickCast = cfg.GetBool("Keys", "QuickCast", true);
        _holdMs = Math.Clamp(cfg.GetInt("Keys", "HoldMs", 350), 50, 2000);
        _castDelayMs = Math.Clamp(cfg.GetInt("Keys", "CastDelayMs", 80), 0, 1000);
        foreach (var pair in cfg.GetString("Keys", "Remap", "").Split(',', ';'))
        {
            var kv = pair.Split(':');
            if (kv.Length == 2 && ParseKey(kv[0].Trim(), out uint from) && ParseKey(kv[1].Trim(), out uint to)) _remap[from] = to;
        }
        Enabled = _quickCast || _remap.Count > 0;
        if (Enabled) Log.Info($"KeyInput: lanzar con una tecla {(_quickCast ? "sí" : "no")}, {_remap.Count} teclas traducidas");
        StartLowLevelRemap();
    }

    /// <summary>«1», «A», «F1», «SPACE», «NUM0», o un número de tecla virtual (0x70).</summary>
    private static bool ParseKey(string s, out uint vk)
    {
        vk = 0;
        s = s.ToUpperInvariant();
        if (s.Length == 1 && ((s[0] >= '0' && s[0] <= '9') || (s[0] >= 'A' && s[0] <= 'Z'))) { vk = s[0]; return true; }
        if (s.Length >= 2 && s[0] == 'F' && int.TryParse(s[1..], out int f) && f >= 1 && f <= 24) { vk = (uint)(0x70 + f - 1); return true; }
        if (s.StartsWith("NUM") && int.TryParse(s[3..], out int n) && n >= 0 && n <= 9) { vk = (uint)(0x60 + n); return true; }
        switch (s) { case "SPACE": vk = 0x20; return true; case "TAB": vk = 0x09; return true; case "NUM+": vk = 0x6B; return true; case "NUM-": vk = 0x6D; return true; case "ENTER": vk = 0x0D; return true; }
        if (s.StartsWith("0X") && uint.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out vk)) return true;
        return false;
    }

    /// <summary>
    /// En el hilo del juego (cada fotograma de la partida): se desvía una vez la entrada del procedimiento de ventana
    /// del juego (sacredWndProc 0x812BF0). No se cambia el GWL_WNDPROC de la ventana: la interfaz de Steam la tiene
    /// subclasificada y cambia esa cadena durante la partida; desviando la función del juego se ven todos los
    /// mensajes que le llegan. Su primera instrucción (mov eax, fs:[0], 6 bytes) no depende de su dirección.
    /// </summary>
    public static void EnsureInstalled()
    {
        if (_pendingUp && _clock.ElapsedMilliseconds >= _holdUntil && _hwnd != 0)
        {
            _pendingUp = false;
            int* pt = stackalloc int[2];
            GetCursorPos(pt); ScreenToClient(_hwnd, pt);
            PostMessageA(_hwnd, 0x205 /* WM_RBUTTONUP */, 0, (nint)((pt[1] << 16) | (pt[0] & 0xFFFF)));
        }
        if (_quickCast && _installed && _getAsync != null) PollArtKeys();
        if (_castAt != 0 && _clock.ElapsedMilliseconds >= _castAt && _hwnd != 0)
        {
            _castAt = 0;
            int* cp = stackalloc int[2];
            GetCursorPos(cp); ScreenToClient(_hwnd, cp);
            PostMessageA(_hwnd, 0x204 /* WM_RBUTTONDOWN */, 2, (nint)((cp[1] << 16) | (cp[0] & 0xFFFF)));
            _holdUntil = _clock.ElapsedMilliseconds + _holdMs;
            _pendingUp = true;
        }
        if (!_quickCast || _installed) return;
        _installed = true;
        // GetAsyncKeyState (IAT 0x8902B0): mientras dura el clic derecho inyectado, el botón figura pulsado
        _getAsync = (delegate* unmanaged[Stdcall]<int, short>)Mem.Read<nint>(0x8902B0);
        Mem.Write<nint>(0x8902B0, (nint)(delegate* unmanaged[Stdcall]<int, short>)&GetAsyncKeyStateHook);
        Log.Info("KeyInput: lanzar con una tecla preparado (GetAsyncKeyState enganchado)");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static short GetAsyncKeyStateHook(int vk)
    {
        if (vk == 2 /* VK_RBUTTON */ && _pendingUp) return unchecked((short)0x8000);
        return State(vk);
    }

    private static readonly bool[] _wasDown = new bool[10];
    // Traducción para las consultas de estado: tecla que pregunta el juego -> tecla física que la produce
    private static readonly Dictionary<int, int> _inverse = new();

    /// <summary>Estado de la tecla que pregunta el juego, ya traducido (Remap «de:a»: el juego ve «a» cuando se pulsa «de»).
    /// Una tecla que es destino de una traducción solo responde a su origen.</summary>
    private static short State(int vk)
    {
        if (_inverse.TryGetValue(vk, out int src)) return _getAsync(src);
        return _getAsync(vk);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int EnumProcAny(nint hwnd, nint param)
    {
        uint pid = 0;
        GetWindowThreadProcessId(hwnd, &pid);
        if (pid == GetCurrentProcessId() && IsWindowVisible(hwnd)) { _found = hwnd; return 0; }
        return 1;
    }
    private static int _pollLogs;

    /// <summary>
    /// El juego lee las teclas 6-0 consultando su estado (no llegan como mensajes al procedimiento de ventana): se
    /// detecta aquí la pulsación (flanco) y se inyecta el botón derecho «pulsado» durante HoldMs.
    /// </summary>
    private static void PollArtKeys()
    {
        if (!InGame() || ChatOpen()) return;
        for (int k = 0; k < 10; k++)
        {
            int vk = '0' + k;
            bool down = (State(vk) & 0x8000) != 0;
            bool edge = down && !_wasDown[k];
            _wasDown[k] = down;
            if (!edge || !(k == 0 || k >= 6)) continue;
            if (_pollLogs++ < 8) Log.Info($"KeyInput: tecla {(char)vk} pulsada");
            if (_hwnd == 0 && D9.Display.Hwnd != 0) _hwnd = D9.Display.Hwnd;
            if (_hwnd == 0)
            {
                _found = 0;
                EnumWindows((nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&EnumProcAny, 0);
                _hwnd = _found;
                Log.Info($"KeyInput: ventana del juego 0x{_hwnd:X}");
            }
            if (_hwnd == 0) continue;
            int* pt = stackalloc int[2];
            _castAt = _clock.ElapsedMilliseconds + _castDelayMs; // primero se elige el arte; el clic, después
        }
    }

    private static bool ChatOpen()
    {
        nint mgr = *(nint*)0x17EEBBC;
        if (mgr == 0) return false;
        nint con = *(nint*)(mgr + 0x80 + 9 * 4);
        return con != 0 && (*(uint*)(con + 0x10) & 1) != 0;
    }

    private static bool InGame()
    {
        nint mgr = *(nint*)0x17EEBBC;
        return mgr != 0 && (*(uint*)(mgr + 8) & 0x04) != 0;
    }
}
