using System;
using System.Collections.Generic;
using System.Text;
using SacredFix.Core;

namespace SacredFix.Proxy;

/// <summary>
/// Diagnóstico de los menús ([Debug] UiDump=1): cada ~2 s registra un fotograma de la interfaz con cada
/// llamada de dibujo 2D (XYZRHW): tipo, vértices, rectángulo en coordenadas del lienzo de 1024x768, textura y
/// su tamaño, y rango de coordenadas de textura. Indica también si en ese fotograma se dibujó el mundo.
/// Sirve para distinguir las imágenes de fondo de los menús (que se estirarán a toda la pantalla) de los
/// botones y paneles (que se quedan centrados). Solo se escribe si el fotograma cambia respecto al anterior.
/// </summary>
internal static unsafe class UiDump
{
    public static bool Enabled;
    private static nint _tex;
    private static bool _collect;
    private static int _frame, _dumps, _world, _n;
    private static readonly StringBuilder _sb = new();
    private static string _lastSig = "";
    private static readonly StringBuilder _sig = new();
    private static readonly Dictionary<nint, (int w, int h)> _sizes = new();
    private const int MaxDumps = 40, MaxEntries = 120;

    public static void Init(Config cfg) => Enabled = cfg.GetBool("Debug", "UiDump", false);

    public static void OnSetTexture(uint stage, nint tex) { if (stage == 0) _tex = tex; }

    private static int _worldLines;

    public static void OnWorldDraw(int api, uint fvf, nint verts, uint count, int stride)
    {
        _world++;
        if (!_collect || _worldLines >= 16) return;
        _worldLines++;
        try
        {
            string[] names = { "DP", "DIP", "DPVB", "DIPVB", "DIPStrided" };
            _sb.Append($"\n    [mundo] {names[api]} fvf=0x{fvf:X} n={count}");
            if (verts != 0 && stride >= 12 && count > 0)
            {
                float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
                uint n = Math.Min(count, 20000u);
                for (uint i = 0; i < n; i++)
                {
                    float* p = (float*)((byte*)verts + i * stride);
                    D3DTrace.Project(p[0], p[1], p[2], out float sx, out float sy, out _, out bool behind);
                    if (behind) continue;
                    x0 = Math.Min(x0, sx); x1 = Math.Max(x1, sx); y0 = Math.Min(y0, sy); y1 = Math.Max(y1, sy);
                }
                var vp = D3DTrace._lastVp;
                _sb.Append($" pantalla x {x0:0}..{x1:0} y {y0:0}..{y1:0} (viewport {vp[0]},{vp[1]} {vp[2]}x{vp[3]})");
            }
        }
        catch { }
    }

    public static void OnUiDraw(int api, int type, uint fvf, float* v, uint count, int stride, bool outside = false)
    {
        if (!_collect || v == null || count == 0) return;
        if (_n >= MaxEntries) return;
        try
        {
            float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
            float u0 = float.MaxValue, u1 = float.MinValue, t0 = float.MaxValue, t1 = float.MinValue;
            int uvOff = 16 + ((fvf & 0x40) != 0 ? 4 : 0) + ((fvf & 0x80) != 0 ? 4 : 0);
            bool hasUv = ((fvf >> 8) & 0xF) > 0 && uvOff + 8 <= stride;
            for (uint i = 0; i < count; i++)
            {
                float* p = (float*)((byte*)v + i * stride);
                x0 = Math.Min(x0, p[0]); x1 = Math.Max(x1, p[0]); y0 = Math.Min(y0, p[1]); y1 = Math.Max(y1, p[1]);
                if (hasUv)
                {
                    float* uv = (float*)((byte*)p + uvOff);
                    u0 = Math.Min(u0, uv[0]); u1 = Math.Max(u1, uv[0]); t0 = Math.Min(t0, uv[1]); t1 = Math.Max(t1, uv[1]);
                }
            }
            if (x1 - x0 < 100 && y1 - y0 < 60) return; // solo piezas grandes (fondos, franjas, paneles)
            _n++;
            var (tw, th) = TexSize(_tex);
            _sb.Append($"\n    {(outside ? "[fuera del lienzo] " : "")}{(api == 0 ? "DP" : "DIP")} t{type} n={count,-4} x {x0,6:0}..{x1,-6:0} y {y0,5:0}..{y1,-5:0} tex 0x{_tex:X8} {tw}x{th}");
            if (hasUv) _sb.Append($" uv {u0:0.##}..{u1:0.##},{t0:0.##}..{t1:0.##}");
            _sig.Append($"{type}:{count}:{x0:0}:{x1:0}:{y0:0}:{y1:0}:{tw}x{th};");
        }
        catch { }
    }

    public static void OnEndScene()
    {
        if (!Enabled) return;
        _frame++;
        if (_collect)
        {
            _collect = false;
            string sig = _sig.ToString();
            if (_n > 0 && sig != _lastSig && _dumps < MaxDumps)
            {
                _dumps++;
                _lastSig = sig;
                Log.Info($"UiDump #{_dumps} (fotograma {_frame}, mundo dibujado: {(_world > 0 ? "sí" : "no")}, {_n} llamadas 2D):{_sb}");
            }
        }
        _world = 0;
        if (_frame % 120 == 0 && _dumps < MaxDumps)
        {
            _collect = true; _n = 0; _worldLines = 0; _sb.Clear(); _sig.Clear();
        }
    }

    private static (int, int) TexSize(nint tex)
    {
        if (tex == 0) return (0, 0);
        if (_sizes.TryGetValue(tex, out var s)) return s;
        s = (0, 0);
        try
        {
            byte* d = stackalloc byte[124];
            new Span<byte>(d, 124).Clear();
            *(int*)d = 124;
            var getDesc = (delegate* unmanaged[Stdcall]<nint, byte*, int>)(*(nint**)tex)[22]; // IDirectDrawSurface7::GetSurfaceDesc
            if (getDesc(tex, d) == 0) s = (*(int*)(d + 12), *(int*)(d + 8));
        }
        catch { }
        _sizes[tex] = s;
        return s;
    }
}
