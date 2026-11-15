using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using SacredFix.Core;
using SacredFix.Native;

namespace SacredFix.Patches;

/// <summary>
/// Parche 3: cuelgue al arrancar esperando el primer plano.
///
/// 0x811440(hwnd, timeout) hace:
///   while (GetForegroundWindow() != hwnd) SetForegroundWindow(hwnd);
///   while (GetFocus() != hwnd)            SetFocus(hwnd);
/// Se llama desde 0x817EE1 (al crear la ventana, antes de usar DirectDraw), 0x816C1C (antes de
/// iniciar DirectDraw) y 0x817F39, siempre con timeout = [0xA1CCC4] ("infinito"). Desde Windows 98,
/// SetForegroundWindow falla si el usuario ha tocado otra ventana durante el arranque, o si el juego
/// no se lanzó desde el programa en primer plano, así que el bucle no termina nunca: ventana negra,
/// sin errores, y el proceso «responde».
///
/// Arreglo:
///   1. La IAT de SetForegroundWindow (0x890340) apunta a nuestra función, que se une a la cola de
///      entrada del hilo en primer plano (AttachThreadInput) para que Windows permita el cambio.
///   2. Dentro de 0x811440, en los dos bucles, «seguir si el tiempo &lt; timeout o si es infinito»
///      pasa a «seguir si el tiempo &lt; 5000 ms». Si no se consigue, el juego sigue en vez de colgarse.
///
/// Como 0x817EE1 ocurre antes de la primera llamada a ddraw, esto se aplica pronto: DllMain
/// (native/early.c) lanza un hilo que llama a <see cref="Early"/>, que espera a que SteamStub
/// descifre el código y rellene la IAT. Apply() (en el arranque normal) lo completa si hiciera falta.
/// </summary>
internal sealed unsafe class FocusFix : IPatch
{
    public string Name => "Arreglo del foco al arrancar";
    public string IniKey => "FocusFix";
    public bool EnabledByDefault => true;

    private const nint IatSetForegroundWindow = 0x890340;
    private const int TimeoutMs = 5000;

    /// <summary>Comprobación del tiempo de los dos bucles (idéntica en ambos, 16 bytes):
    /// sub eax,[esp+10h] / cmp eax,edi / jb +0C / cmp edi,[0A1CCC4h] / je +04</summary>
    private static readonly nint[] TimeoutChecks = { 0x81148F, 0x8114C6 };
    private static ReadOnlySpan<byte> ExpectedCheck => new byte[]
        { 0x2B, 0x44, 0x24, 0x10, 0x3B, 0xC7, 0x72, 0x0C, 0x3B, 0x3D, 0xC4, 0xCC, 0xA1, 0x00, 0x74, 0x04 };
    /// <summary>sub eax,[esp+10h] / cmp eax,5000 / jb +09 (seguir) / jmp +03 (rendirse) / nop×3</summary>
    private static ReadOnlySpan<byte> NewCheck => new byte[]
        { 0x2B, 0x44, 0x24, 0x10, 0x3D, 0x88, 0x13, 0x00, 0x00, 0x72, 0x09, 0xEB, 0x03, 0x90, 0x90, 0x90 };

    private static int _applied; // 0 = no, 1 = sí
    private static int _calls, _forced, _failed;

    public void Apply(Config cfg)
    {
        if (Volatile.Read(ref _applied) == 1)
        {
            Log.Info($"[{Name}] ya aplicado al cargar la DLL (antes de crear la ventana)");
            return;
        }
        nint real = RealSetForegroundWindow();
        nint cur = Mem.Read<nint>(IatSetForegroundWindow);
        if (cur != real)
            Log.Warn($"[{Name}] IAT 0x{IatSetForegroundWindow:X8} = 0x{cur:X8}, user32!SetForegroundWindow = 0x{real:X8} (¿otro gancho?); se sustituye igualmente");
        ApplyCore();
    }

    private static nint RealSetForegroundWindow()
        => Win32.GetProcAddress(Win32.GetModuleHandleW("user32.dll"), "SetForegroundWindow");

    private static void ApplyCore()
    {
        foreach (var at in TimeoutChecks)
            if (!Mem.Matches(at, ExpectedCheck))
                throw new InvalidOperationException($"bytes inesperados en 0x{at:X8}: {Mem.Hex(at, ExpectedCheck.Length)}");

        // Primero el código (el juego aún no está en el bucle) y luego la IAT (escritura atómica de 4 bytes)
        foreach (var at in TimeoutChecks) Mem.WriteBytes(at, NewCheck);
        delegate* unmanaged[Stdcall]<nint, int> hook = &SetForegroundWindowHook;
        nint old = Mem.Read<nint>(IatSetForegroundWindow);
        Mem.Write(IatSetForegroundWindow, (nint)hook);
        Volatile.Write(ref _applied, 1);
        Log.Info($"[FocusFix] IAT SetForegroundWindow 0x{IatSetForegroundWindow:X8}: 0x{old:X8} -> 0x{(nint)hook:X8}; " +
                 $"espera de primer plano en 0x811440: infinita -> {TimeoutMs} ms");
    }

    /// <summary>
    /// Hilo lanzado desde DllMain. Espera (hasta 60 s) a que el código esté descifrado y la IAT
    /// resuelta, y aplica el parche antes de que el juego cree su ventana.
    /// </summary>
    public static void Early(Config cfg)
    {
        if (!cfg.GetBool("Fixes", "FocusFix", true)) return;
        nint real = RealSetForegroundWindow();
        uint start = Win32.GetTickCount();
        while (Win32.GetTickCount() - start < 60_000)
        {
            if (Volatile.Read(ref _applied) == 1) return;
            // SteamStub descifra por partes: esperar a que estén exactamente los bytes que vamos a tocar
            if (Mem.Read<nint>(IatSetForegroundWindow) == real
                && Mem.Matches(TimeoutChecks[0], ExpectedCheck) && Mem.Matches(TimeoutChecks[1], ExpectedCheck))
            {
                try
                {
                    ApplyCore();
                    Log.Info($"[FocusFix] aplicado al cargar la DLL, {Win32.GetTickCount() - start} ms después");
                }
                catch (Exception ex) { Log.Error("[FocusFix] arranque temprano: " + ex.Message); }
                return;
            }
            Thread.Sleep(1);
        }
        Log.Warn("[FocusFix] arranque temprano: el código no se descifró en 60 s; se aplicará en el arranque normal");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetForegroundWindowHook(nint hwnd)
    {
        try
        {
            int n = Interlocked.Increment(ref _calls);
            if (Win32.SetForegroundWindow(hwnd)) return 1;

            // Windows lo ha denegado: unir temporalmente nuestra entrada a la del hilo en primer plano
            nint fg = Win32.GetForegroundWindow();
            uint fgThread = fg != 0 ? Win32.GetWindowThreadProcessId(fg, 0) : 0;
            uint me = Win32.GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && Win32.AttachThreadInput(me, fgThread, true);
            Win32.BringWindowToTop(hwnd);
            bool ok = Win32.SetForegroundWindow(hwnd);
            if (attached) Win32.AttachThreadInput(me, fgThread, false);

            if (ok)
            {
                if (Interlocked.Increment(ref _forced) <= 5)
                    Log.Info($"FocusFix: SetForegroundWindow(0x{hwnd:X}) denegado; conseguido uniendo la entrada al hilo {fgThread} (llamada #{n})");
                return 1;
            }
            if (Interlocked.Increment(ref _failed) is 1 or 100 or 1000)
                Log.Warn($"FocusFix: SetForegroundWindow(0x{hwnd:X}) sigue fallando (primer plano: 0x{fg:X}, hilo {fgThread}, intentos fallidos {_failed})");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("FocusFix: " + ex.Message);
            return 0;
        }
    }
}
