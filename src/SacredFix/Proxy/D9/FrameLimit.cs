using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SacredFix.Core;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Límite de fotogramas por segundo ([Display] MaxFps, 0 = sin límite), para los dos motores.
/// La presentación espera a la sincronización vertical del monitor, así que en uno de 60 Hz el juego va a 60 FPS. En
/// uno de 120/144 Hz podría ir más rápido, y no está probado si eso acelera animaciones o la lógica del juego. Con
/// MaxFps=60 se queda como en un monitor de 60 Hz. Espera con Sleep hasta 2 ms antes del momento y el resto en vueltas
/// cortas, con el temporizador de Windows a 1 ms para que Sleep sea preciso.
/// </summary>
internal static class FrameLimit
{
    private static long _period, _next;

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);

    public static void Init(int maxFps)
    {
        if (maxFps <= 0) { _period = 0; return; }
        maxFps = Math.Clamp(maxFps, 20, 1000);
        _period = Stopwatch.Frequency / maxFps;
        timeBeginPeriod(1);
        Log.Info($"D9: límite de {maxFps} FPS ([Display] MaxFps)");
    }

    public static void Wait()
    {
        if (_period == 0) return;
        long now = Stopwatch.GetTimestamp();
        if (_next == 0 || now - _next > _period * 4) { _next = now + _period; return; } // (tras una pausa larga, sin recuperar)
        long twoMs = Stopwatch.Frequency / 500;
        while (true)
        {
            now = Stopwatch.GetTimestamp();
            long left = _next - now;
            if (left <= 0) break;
            if (left > twoMs) Thread.Sleep(1); else Thread.SpinWait(50);
        }
        _next += _period;
    }
}
