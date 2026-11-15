using System;
using System.Runtime.InteropServices;
using System.Text;
using SacredFix.Core;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Depuración del motor Direct3D 11 ([Debug] D3D11Trace2D=1): al pulsar F11, escribe en el log los dibujos 2D (XYZRHW)
/// sin Z-buffer (la interfaz) del fotograma siguiente: tipo, número de vértices, textura (tamaño, filtros, modo de borde), mezcla, Z y los cuatro
/// primeros vértices (x, y, u, v). Sirve para ver cómo arma el juego una pieza de la interfaz o del mundo.
/// </summary>
internal sealed unsafe partial class Backend11
{
    public static bool Trace2D;
    private bool _traceArmed, _traceOn, _f11Down;
    private int _traceDraws;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    /// <summary>Al presentar: F11 prepara la captura del fotograma siguiente.</summary>
    private void TraceFrame()
    {
        if (!Trace2D) return;
        if (_traceOn) { _traceOn = false; Log.Info($"D11: fin de la captura 2D ({_traceDraws} dibujos)"); }
        if (_traceArmed) { _traceArmed = false; _traceOn = true; _traceDraws = 0; Log.Info("D11: captura 2D de este fotograma:"); }
        bool down = (GetAsyncKeyState(0x7A /* F11 */) & 0x8000) != 0;
        if (down && !_f11Down) _traceArmed = true;
        _f11Down = down;
    }

    private readonly System.Collections.Generic.Dictionary<Surface7, ulong> _traceHash = new();

    /// <summary>Huella (FNV-1a de 64 bits) del contenido de una textura, para reconocerla entre partidas.</summary>
    public static ulong TexHash(Surface7 t)
    {
        if (t.Cpu == 0) return 0;
        ulong h = 0xCBF29CE484222325;
        byte* p = (byte*)t.Cpu; int n = t.W * t.H * t.Bpp;
        for (int i = 0; i < n; i++) h = (h ^ p[i]) * 0x100000001B3;
        return h;
    }

    private void TraceDraw(int type, uint fvf, nint verts, uint vcount, ushort* idx, uint icount, int stride)
    {
        if (!_traceOn || _traceDraws >= 12000) return;
        _traceDraws++;
        // Todos los dibujos, en una línea cada uno: tipo, FVF y el rectángulo que ocupan (todos los vértices) si son 2D
        // (XYZRHW); los 3D solo se cuentan. Los 2D sin Z siguen además con el detalle de los 4 primeros vértices.
        bool rhw = (fvf & 0xE) == 0x4;
        var bb = new StringBuilder($"  @{_traceDraws} tipo {type} fvf 0x{fvf:X} v{vcount}" + (idx != null ? $" i{icount}" : "") + (rhw ? "" : " 3D"));
        if (rhw && vcount > 0 && vcount < 100000)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (uint i = 0; i < vcount; i++)
            {
                float* p = (float*)((byte*)verts + i * stride);
                if (p[0] < x0) x0 = p[0]; if (p[0] > x1) x1 = p[0];
                if (p[1] < y0) y0 = p[1]; if (p[1] > y1) y1 = p[1];
            }
            bb.Append($" caja {x0:F1},{y0:F1} .. {x1:F1},{y1:F1} z{_rs[7]}");
        }
        if (_tex[0] != null) bb.Append($" tex {_tex[0]!.W}x{_tex[0]!.H} huella {TexHash(_tex[0]!):X16}");
        Log.Info(bb.ToString());
        if (!rhw || _rs[7] != 0) return; // detalle: solo lo 2D sin Z (la interfaz)
        var sb = new StringBuilder($"  #{_traceDraws} tipo {type} fvf 0x{fvf:X} v{vcount}" + (idx != null ? $" i{icount}" : ""));
        var t = _tex[0];
        if (t != null) sb.Append($" tex {t.W}x{t.H} f{t.Fmt} huella {TexHash(t):X16}");
        else sb.Append(" sin textura");
        sb.Append($" mag{_tss[0, 16]} min{_tss[0, 17]} dir{_tss[0, 13]}/{_tss[0, 14]} op{_tss[0, 1]}/{_tss[0, 4]}");
        sb.Append($" mezcla{_rs[27]}({_rs[19]},{_rs[20]}) alfa{_rs[15]}({_rs[25]},{_rs[24]}) z{_rs[7]}/{_rs[14]}");
        sb.Append($" vp {_vpSaved[0]},{_vpSaved[1]} {_vpSaved[2]}x{_vpSaved[3]}");
        int toff = 16 + ((fvf & 0x10) != 0 ? 12 : 0) + ((fvf & 0x20) != 0 ? 4 : 0) + ((fvf & 0x40) != 0 ? 4 : 0) + ((fvf & 0x80) != 0 ? 4 : 0);
        bool hasTex = TexCount(fvf) > 0;
        for (int i = 0; i < Math.Min(vcount, 4u); i++)
        {
            float* p = (float*)((byte*)verts + i * stride);
            sb.Append($"\n      ({p[0]:F2}, {p[1]:F2}, z {p[2]:F4})");
            if (hasTex) { float* u = (float*)((byte*)verts + i * stride + toff); sb.Append($" uv ({u[0]:F5}, {u[1]:F5})"); }
        }
        Log.Info(sb.ToString());
    }
}
