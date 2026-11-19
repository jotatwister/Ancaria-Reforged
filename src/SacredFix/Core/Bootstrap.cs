using System;
using System.IO;
using System.Threading;
using SacredFix.Native;
using SacredFix.Patches;

namespace SacredFix.Core;

/// <summary>
/// Arranque de SacredFix. Se ejecuta en la primera llamada del juego a cualquier función de ddraw.
///
/// ¿Por qué ahí y no al cargar la DLL? Sacred.exe está protegido con SteamStub: cuando Windows carga
/// nuestra DLL (antes de que arranque el juego) el código del juego todavía está cifrado. La primera
/// llamada a DirectDraw la hace el código del juego, así que en ese momento ya está descifrado,
/// y además es antes de que elija el modo de vídeo. Por si acaso, se comprueba con una firma de
/// bytes (GameVersion.IsDecrypted) y, si aún no está descifrado, se reintenta en la siguiente llamada.
/// </summary>
internal static class Bootstrap
{
    private static int _state; // 0 = pendiente, 1 = en curso, 2 = terminado

    /// <summary>
    /// Llamado por el hilo que lanza DllMain (native/early.c) al cargar la DLL, antes de que el juego
    /// arranque. Solo hace lo que no puede esperar a la primera llamada a ddraw (FocusFix).
    /// </summary>
    [System.Runtime.InteropServices.UnmanagedCallersOnly(EntryPoint = "SacredFix_Early")]
    public static void EarlyEntry()
    {
        try
        {
            string exe = Win32.GetModuleFileName(0);
            string dir = Path.GetDirectoryName(exe) ?? ".";
            var cfg = Config.Load(dir);
            Log.Init(dir, cfg.GetBool("General", "Log", false));
            Log.Info("Arranque temprano (hilo de DllMain)");
            if (!cfg.GetBool("General", "Enabled", true)) return;
            if (!GameVersion.IsSupported(GameVersion.Detect(exe, out _))) return;
            Resolution.DeclareDpiAware(cfg);
            FocusFix.Early(cfg);
        }
        catch (Exception ex)
        {
            Log.Error("Arranque temprano: " + ex.Message);
        }
    }

    public static void EnsureInitialized()
    {
        if (Volatile.Read(ref _state) == 2) return;
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;

        bool done = true;
        try
        {
            done = Initialize();
        }
        catch (Exception ex)
        {
            Log.Error("Fallo inesperado al iniciar: " + ex);
        }
        Volatile.Write(ref _state, done ? 2 : 0);
    }

    /// <returns>false si hay que reintentar más tarde (código aún cifrado).</returns>
    private static bool Initialize()
    {
        string exe = Win32.GetModuleFileName(0);
        string dir = Path.GetDirectoryName(exe) ?? ".";

        var cfg = Config.Load(dir);
        Log.Init(dir, cfg.GetBool("General", "Log", false));
        Log.Info($"SacredFix {typeof(Bootstrap).Assembly.GetName().Version} — {exe}");

        CrashLog.Init(cfg);
        SacredFix.Proxy.D3DTrace.Init(cfg);
        SacredFix.Proxy.UiDump.Init(cfg);
        SacredFix.Proxy.MiniMap.Init(cfg);
        SacredFix.Proxy.HeroWatch.Init(cfg);
        SacredFix.Proxy.BuffBar.Init(cfg);
        SacredFix.Proxy.KeyInput.Init(cfg);
        SacredFix.Proxy.DebugScan.Init(cfg);
        SacredFix.Proxy.ApiInventory.Init(cfg);
        SacredFix.Proxy.D9.Layer.Init(cfg);
        SacredFix.Proxy.D9.Overlay.Init(cfg);

        if (!cfg.GetBool("General", "Enabled", true))
        {
            Log.Info("Desactivado en SacredFix.ini: solo se reenvían las llamadas a ddraw.");
            return true;
        }

        var kind = GameVersion.Detect(exe, out var sha);
        Log.Info($"SHA256 del ejecutable: {sha}");
        if (!GameVersion.IsSupported(kind))
        {
            Log.Warn("Versión de Sacred.exe no reconocida: no se aplica ningún parche.");
            return true;
        }
        Log.Info($"Versión reconocida: {GameVersion.Describe(kind)}");

        if (!GameVersion.IsDecrypted())
        {
            Log.Warn("El código aún no está listo (Steam lo descifra al arrancar); se reintentará en la siguiente llamada.");
            return false;
        }

        PatchManager.ApplyAll(cfg);
        SacredFix.Proxy.CallCounters.Init(cfg);
        SacredFix.Proxy.UiCanvas.Init(cfg);
        return true;
    }
}
