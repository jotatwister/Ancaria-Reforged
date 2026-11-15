using System;
using System.Runtime.InteropServices;

namespace SacredFix.Core;

/// <summary>
/// Tamaño de la imagen del juego ([Display] Width / Height) y compatibilidad con el escalado de Windows.
/// - Width=auto: el ancho sale de la proporción del monitor principal con 768 de alto (16:9 -> 1366, 16:10 -> 1229,
///   4:3 -> 1024, 5:4 -> 1024), limitado a 1024-1366: por encima de 1366 de ancho otros proyectos ven cambios de zoom y
///   parpadeos, así que los ultrapanorámicos se quedan en 1366 con bandas a los lados.
/// - Escalado de Windows (DPI): el juego no se declara compatible, así que con el escalado al 125-200% (lo normal en
///   4K y portátiles) Windows le hace creer que la pantalla es más pequeña y amplía su imagen él mismo, borrosa. Se
///   declara compatible por monitor (v2) al cargar la DLL, antes de que el juego cree su ventana.
/// </summary>
internal static class Resolution
{
    public const int MinWidth = 1024, MaxWidth = 1366;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern int SetProcessDPIAware();
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern nint GetModuleHandleA(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern nint GetProcAddress(nint mod, string name);

    private static int _auto;

    public static int Height(Config cfg) => cfg.GetInt("Display", "Height", 768);

    public static int Width(Config cfg)
    {
        string s = cfg.GetString("Display", "Width", "1366").Trim().ToLowerInvariant();
        if (s != "auto") return int.TryParse(s, out int w) ? w : 1366;
        if (_auto != 0) return _auto;
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1); // SM_CXSCREEN, SM_CYSCREEN (monitor principal)
        int h = Height(cfg);
        int width = sw > 0 && sh > 0 ? (int)Math.Round(h * (double)sw / sh) : 1366;
        width = Math.Clamp(width & ~1, MinWidth, MaxWidth);
        _auto = width;
        Log.Info($"Width=auto: monitor {sw}x{sh} -> imagen del juego {width}x{h}");
        return width;
    }

    /// <summary>Al cargar la DLL: el proceso se declara compatible con el escalado de Windows ([Display] DpiAware).</summary>
    public static unsafe void DeclareDpiAware(Config cfg)
    {
        string r = cfg.GetString("Display", "Renderer", "ddraw").Trim().ToLowerInvariant();
        if (r == "ddraw" || !cfg.GetBool("Display", "DpiAware", true)) return;
        nint u = GetModuleHandleA("user32.dll");
        nint f = u != 0 ? GetProcAddress(u, "SetProcessDpiAwarenessContext") : 0;
        if (f != 0 && ((delegate* unmanaged[Stdcall]<nint, int>)f)(-4 /* PER_MONITOR_AWARE_V2 */) != 0)
            Log.Info("Escalado de Windows: el juego se declara compatible por monitor (DPI v2)");
        else if (SetProcessDPIAware() != 0)
            Log.Info("Escalado de Windows: el juego se declara compatible (DPI de sistema)");
        else
            Log.Warn("Escalado de Windows: no se pudo declarar compatible (¿ya lo estaba?)");
    }
}
