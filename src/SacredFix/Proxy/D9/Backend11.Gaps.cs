using System;
using System.Runtime.InteropServices;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Motor Direct3D 11: cerrar las juntas entre piezas 2D con MSAA ([Display] CloseGaps2D, en píxeles del juego).
/// El juego coloca las piezas de las paredes (rectángulos 2D) casi borde con borde, pero entre ellas queda un hueco de
/// una fracción de píxel. Sin MSAA casi nunca cae en el centro de un píxel y no se ve; con MSAA se ve en proporción a
/// su tamaño: líneas finas en las juntas al moverse. Ajustar los vértices al borde de píxel lo empeoraba (el hueco,
/// cuando caía en el umbral, pasaba a ser un píxel entero).
/// Arreglo: cada pieza 2D del mundo (con Z-buffer, o fuera de la interfaz: las piezas grandes del escenario del
/// Underworld, formato 0x1C4, se dibujan sin Z; captura 162) que sea un rectángulo recto
/// de 4 vértices seguidos se ensancha CloseGaps2D por cada lado, con las mismas coordenadas de textura (la pieza se
/// estira una fracción de píxel, menos del 1%). Prolongarlas no sirve: en el borde se saldrían de la textura y, con el
/// modo de repetición, leerían el lado contrario, otra vez una línea. Las baldosas del suelo (rombos) no cambian.
/// </summary>
internal sealed unsafe partial class Backend11
{
    private nint _qbuf;
    private int _qbufSize;

    private bool WantCloseGaps(uint fvf, uint vcount) =>
        Layer.CloseGaps2D > 0 && _msRtv != 0 && (fvf & 0xE) == 0x4 && (_rs[7] != 0 || !UiCanvas.Active) && vcount % 4 == 0 && vcount <= 16384;

    private bool WantCloseGapsStrip(uint fvf, uint vcount) =>
        Layer.CloseGaps2D > 0 && _msRtv != 0 && (fvf & 0xE) == 0x4 && (_rs[7] != 0 || !UiCanvas.Active) && vcount >= 4 && vcount <= 16384;

    /// <summary>
    /// Tiras de triángulos 2D del mundo (el escenario del Underworld también se dibuja así, captura 163): si la tira es una
    /// columna (solo dos valores de x) se ensancha hacia los lados, y si es una fila (dos valores de y), hacia arriba y
    /// abajo; además se prolongan sus extremos. Las tiras de otra forma no se tocan.
    /// </summary>
    private nint CloseGapsStrip(nint src, uint n, int stride)
    {
        float xa = float.NaN, xb = float.NaN, ya = float.NaN, yb = float.NaN;
        bool twoX = true, twoY = true;
        float xmin = float.MaxValue, xmax = float.MinValue, ymin = float.MaxValue, ymax = float.MinValue;
        for (uint i = 0; i < n; i++)
        {
            float* p = (float*)((byte*)src + i * stride);
            float x = p[0], y = p[1];
            if (twoX)
            {
                if (float.IsNaN(xa) || Math.Abs(x - xa) < 1e-3f) xa = float.IsNaN(xa) ? x : xa;
                else if (float.IsNaN(xb) || Math.Abs(x - xb) < 1e-3f) xb = float.IsNaN(xb) ? x : xb;
                else twoX = false;
            }
            if (twoY)
            {
                if (float.IsNaN(ya) || Math.Abs(y - ya) < 1e-3f) ya = float.IsNaN(ya) ? y : ya;
                else if (float.IsNaN(yb) || Math.Abs(y - yb) < 1e-3f) yb = float.IsNaN(yb) ? y : yb;
                else twoY = false;
            }
            xmin = Math.Min(xmin, x); xmax = Math.Max(xmax, x); ymin = Math.Min(ymin, y); ymax = Math.Max(ymax, y);
        }
        if (!(twoX || twoY) || xmax - xmin < 1 || ymax - ymin < 1) return src;
        int bytes = (int)n * stride;
        if (_qbufSize < bytes)
        {
            if (_qbuf != 0) NativeMemory.Free((void*)_qbuf);
            _qbuf = (nint)NativeMemory.Alloc((nuint)bytes); _qbufSize = bytes;
        }
        Buffer.MemoryCopy((void*)src, (void*)_qbuf, bytes, bytes);
        float d = Layer.CloseGaps2D, cx = (xmin + xmax) * 0.5f, cy = (ymin + ymax) * 0.5f;
        for (uint i = 0; i < n; i++)
        {
            float* p = (float*)((byte*)_qbuf + i * stride);
            if (twoX || Math.Abs(p[0] - xmin) < 1e-3f || Math.Abs(p[0] - xmax) < 1e-3f) p[0] += p[0] < cx ? -d : d;
            if (twoY || Math.Abs(p[1] - ymin) < 1e-3f || Math.Abs(p[1] - ymax) < 1e-3f) p[1] += p[1] < cy ? -d : d;
        }
        return _qbuf;
    }

    /// <summary>Copia de los vértices con las piezas rectangulares ensanchadas (o los originales si no hay ninguna).</summary>
    private nint CloseGaps(nint src, uint n, int stride, uint fvf)
    {
        int bytes = (int)n * stride;
        if (_qbufSize < bytes)
        {
            if (_qbuf != 0) NativeMemory.Free((void*)_qbuf);
            _qbuf = (nint)NativeMemory.Alloc((nuint)bytes); _qbufSize = bytes;
        }
        Buffer.MemoryCopy((void*)src, (void*)_qbuf, bytes, bytes);
        float d = Layer.CloseGaps2D;
        bool any = false;
        float* ox = stackalloc float[4], oy = stackalloc float[4];
        for (uint g = 0; g < n / 4; g++)
        {
            byte* b = (byte*)_qbuf + g * 4 * stride;
            float xmin = float.MaxValue, xmax = float.MinValue, ymin = float.MaxValue, ymax = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                float* p = (float*)(b + i * stride);
                ox[i] = p[0]; oy[i] = p[1];
                xmin = Math.Min(xmin, p[0]); xmax = Math.Max(xmax, p[0]); ymin = Math.Min(ymin, p[1]); ymax = Math.Max(ymax, p[1]);
            }
            if (xmax - xmin < 1 || ymax - ymin < 1) continue;
            bool rect = true;
            for (int i = 0; i < 4 && rect; i++)
                rect = (Math.Abs(ox[i] - xmin) < 1e-3f || Math.Abs(ox[i] - xmax) < 1e-3f) && (Math.Abs(oy[i] - ymin) < 1e-3f || Math.Abs(oy[i] - ymax) < 1e-3f);
            if (!rect) continue;
            for (int i = 0; i < 4; i++)
            {
                float* p = (float*)(b + i * stride);
                p[0] += ox[i] < (xmin + xmax) * 0.5f ? -d : d;
                p[1] += oy[i] < (ymin + ymax) * 0.5f ? -d : d;
            }
            any = true;
        }
        return any ? _qbuf : src;
    }
}
