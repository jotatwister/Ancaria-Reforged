using System;
using System.Diagnostics;
using System.Text;
using SacredFix.Core;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Medidor de tiempos del motor Direct3D 11 ([Debug] D3D11Perf=1).
/// - Cada 10 s escribe en el log: FPS, el peor fotograma, cuántos pasaron de 33 ms y, por tipo de operación, el tiempo
///   total (ms por segundo), cuántas veces y la más lenta.
/// - Cada fotograma de más de 50 ms (tirón) escribe en qué se fue su tiempo, hasta 100 por partida.
/// Los tiempos son de la CPU: el dibujo cuenta lo que cuesta mandarlo, no lo que tarda la tarjeta. «Presentar» incluye
/// la espera de la sincronización vertical, así que es normal que sea alto. «Dibujo» incluye la compilación de los
/// shaders nuevos, que también se cuenta aparte.
/// </summary>
internal sealed unsafe partial class Backend11
{
    private enum Pf { Dibujo, Shader, TexturaNueva, SubidaTextura, LeerPantalla, EscribirPantalla, Gdi, Presentar, N }
    private static readonly string[] PfName =
    {
        "dibujo", "compilar shaders", "crear texturas", "subir texturas", "leer la pantalla (GPU->CPU)",
        "escribir la pantalla (CPU->GPU)", "GDI", "presentar (con la espera de vsync)",
    };

    public static bool PerfOn;
    private static readonly long[] _pT = new long[(int)Pf.N], _pMax = new long[(int)Pf.N], _fT = new long[(int)Pf.N];
    private static readonly int[] _pN = new int[(int)Pf.N], _fN = new int[(int)Pf.N];
    private static long _pStart, _lastFrame, _worstFrame;
    private static int _frames, _slow, _spikes;

    private static double Ms(long t) => t * 1000.0 / Stopwatch.Frequency;
    private static long T0() => PerfOn ? Stopwatch.GetTimestamp() : 0;

    private static void T1(Pf k, long t0)
    {
        if (!PerfOn || t0 == 0) return;
        long d = Stopwatch.GetTimestamp() - t0;
        int i = (int)k;
        _pT[i] += d; _pN[i]++; if (d > _pMax[i]) _pMax[i] = d;
        _fT[i] += d; _fN[i]++;
    }

    /// <summary>Al final de cada fotograma del juego.</summary>
    private static void PerfFrame()
    {
        if (!PerfOn) return;
        long now = Stopwatch.GetTimestamp();
        if (_pStart == 0) { _pStart = _lastFrame = now; Log.Info("D11: medidor de tiempos activado ([Debug] D3D11Perf)"); return; }
        long ft = now - _lastFrame;
        _lastFrame = now;
        _frames++;
        if (ft > _worstFrame) _worstFrame = ft;
        if (Ms(ft) > 33.4) _slow++;
        if (Ms(ft) > 50 && _spikes < 100)
        {
            _spikes++;
            var sb = new StringBuilder($"D11: tirón de {Ms(ft):F0} ms:");
            for (int i = 0; i < (int)Pf.N; i++)
                if (Ms(_fT[i]) >= 1) sb.Append($" {PfName[i]} {Ms(_fT[i]):F1} ms ({_fN[i]}),");
            sb.Append($" resto (juego) {Math.Max(0, Ms(ft) - Ms(_fT[(int)Pf.Dibujo]) - Ms(_fT[(int)Pf.TexturaNueva]) - Ms(_fT[(int)Pf.SubidaTextura]) - Ms(_fT[(int)Pf.LeerPantalla]) - Ms(_fT[(int)Pf.EscribirPantalla]) - Ms(_fT[(int)Pf.Gdi]) - Ms(_fT[(int)Pf.Presentar])):F1} ms");
            Log.Info(sb.ToString());
        }
        Array.Clear(_fT); Array.Clear(_fN);
        double el = Ms(now - _pStart) / 1000;
        if (el < 10) return;
        var r = new StringBuilder($"D11: últimos {el:F0} s: {_frames / el:F1} FPS, peor fotograma {Ms(_worstFrame):F0} ms, {_slow} de más de 33 ms");
        for (int i = 0; i < (int)Pf.N; i++)
            if (_pN[i] > 0) r.Append($"\n    {PfName[i],-38} {Ms(_pT[i]) / el,7:F2} ms/s  {_pN[i] / el,9:F0} veces/s  la más lenta {Ms(_pMax[i]),6:F2} ms");
        if (_cacheHits + _cacheNew > 0) r.Append($"\n    shaders: {_cacheHits} leídos de la caché, {_cacheNew} compilados y guardados");
        _cacheHits = _cacheNew = 0;
        Log.Info(r.ToString());
        Array.Clear(_pT); Array.Clear(_pN); Array.Clear(_pMax);
        _frames = 0; _slow = 0; _worstFrame = 0; _pStart = now;
    }
}
