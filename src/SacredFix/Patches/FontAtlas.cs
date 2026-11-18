using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SacredFix.Core;
using SacredFix.Native;
using SacredFix.Proxy.D9;

namespace SacredFix.Patches;

/// <summary>
/// Letras del juego nítidas con la imagen ampliada ([Display] FontSharp, motor D3D11).
///
/// El gestor de fuentes pinta cada letra la primera vez que la usa (0x64E200…): la escribe en blanco sobre negro en una
/// superficie auxiliar de 256x256 con TextOutW (0x64E3B5, en (0, 1)) o DrawTextW (0x64E3EF, en el rectángulo (1, 1, …));
/// las dos van por punteros de la capa Unicode del juego ([0xA23F98] y [0xA23FAC]). Luego bloquea la auxiliar y su
/// atlas y copia la letra con 0x64E860 (cdecl: descripción del atlas, descripción de la auxiliar, &amp;{x, y} en el atlas,
/// &amp;{x, y, ancho, alto} en la auxiliar), pasándola a A4R4G4B4 (blanco, alfa = media de los canales).
/// - Se cambian esos dos punteros por funciones que anotan la letra, la fuente del DC y el origen, y llaman a las de
///   Windows.
/// - En la entrada de 0x64E860 se marcan las superficies bloqueadas como de letras (filtro Font2D) y el motor pinta la
///   misma letra, a la escala de la imagen interna, en el atlas sombra (Backend11.Shadow.cs).
/// Entrada de 0x64E860: «sub esp, 8 / mov eax, [esp+0x18]» (7 bytes), que se copian al puente.
/// </summary>
internal sealed unsafe class FontAtlas : IPatch
{
    public string Name => "Letras nítidas (atlas)";
    public string IniKey => "FontAtlas";
    public bool EnabledByDefault => true;

    private const nint CopyGlyph = 0x64E860, PtrTextOutW = 0xA23F98, PtrDrawTextW = 0xA23FAC;
    private static readonly byte[] Prologue = { 0x83, 0xEC, 0x08, 0x8B, 0x44, 0x24, 0x18 };
    private static int _marked;

    // ---- huecos de la caché de letras ----
    // El gestor (0x64DD00…) reparte cada atlas de 256x256 en huecos de (tmHeight + 1) x (tmMaxCharWidth + 1). En
    // AntiquaSSK el más ancho es una letra normal; en fuentes con muchos símbolos (la de recambio del checo, Book Antiqua,
    // Palatino…) tmMaxCharWidth es enorme: caben pocas letras, la caché se llena y el juego expulsa y repinta letras en cada
    // imagen (FPS muy bajos y rayitas: restos de la letra anterior en el hueco). Se cambia por el ancho real de las letras
    // que usa el juego (latín con acentos, cirílico y signos de puntuación), sin pasar nunca del original.
    private const nint MetricsCall = 0x64DDBE;
    private static nint _getMetrics;
    private static int _metricLogs;

    [DllImport("gdi32.dll")] private static extern int GetCharABCWidthsW(nint dc, uint first, uint last, int* abc);
    [DllImport("gdi32.dll")] private static extern int GetCharWidth32W(nint dc, uint first, uint last, int* w);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TextMetricsHook(nint dc, int* tm)
    {
        int r = ((delegate* unmanaged[Stdcall]<nint, int*, int>)_getMetrics)(dc, tm);
        try
        {
            if (r == 0 || tm == null) return r;
            int orig = tm[6], need = 0; // tmMaxCharWidth (+0x18)
            int* abc = stackalloc int[3 * 0x160];
            foreach (var (a, b) in new (uint, uint)[] { (0x20, 0x17F), (0x400, 0x45F), (0x2010, 0x2026) })
            {
                int n = (int)(b - a + 1);
                if (GetCharABCWidthsW(dc, a, b, abc) != 0)
                    for (int i = 0; i < n; i++) need = Math.Max(need, Math.Max(abc[i * 3 + 1] + Math.Max(abc[i * 3], 0), abc[i * 3] + abc[i * 3 + 1] + abc[i * 3 + 2]));
                else if (GetCharWidth32W(dc, a, b, abc) != 0)
                    for (int i = 0; i < n; i++) need = Math.Max(need, abc[i]);
            }
            need += 2; // (bordes suavizados)
            if (need > 2 && need < orig)
            {
                tm[6] = need;
                if (_metricLogs++ < 12) Log.Info($"[Letras nítidas (atlas)] fuente de alto {tm[0]}: hueco de {orig + 1} -> {need + 1} px de ancho");
            }
        }
        catch { }
        return r;
    }

    private static nint _textOut, _drawText;
    // última letra pintada por el juego (en la superficie auxiliar)
    private static char _ch;
    private static nint _font;
    private static int _ox, _oy;
    private static bool _have;

    [DllImport("gdi32.dll")] private static extern nint GetCurrentObject(nint dc, uint type);

    public void Apply(Config cfg)
    {
        if (!Mem.Matches(CopyGlyph, Prologue)) throw new InvalidOperationException($"0x{CopyGlyph:X8} inesperado: {Mem.Hex(CopyGlyph, 7)}");
        nint gdi = Win32.GetModuleHandleW("gdi32.dll"), user = Win32.GetModuleHandleW("user32.dll");
        _textOut = gdi != 0 ? Win32.GetProcAddress(gdi, "TextOutW") : 0;
        _drawText = user != 0 ? Win32.GetProcAddress(user, "DrawTextW") : 0;
        if (_textOut == 0 || _drawText == 0) throw new InvalidOperationException("no se encuentran TextOutW / DrawTextW");
        Mem.Write<nint>(PtrTextOutW, (nint)(delegate* unmanaged[Stdcall]<nint, int, int, char*, int, int>)&TextOutHook);
        Mem.Write<nint>(PtrDrawTextW, (nint)(delegate* unmanaged[Stdcall]<nint, char*, int, int*, uint, int>)&DrawTextHook);

        nint fn = (nint)(delegate* unmanaged[Stdcall]<uint*, void>)&Mark;
        // pushad; pushfd; lea eax, [esp+0x24] (dirección de vuelta); push eax; mov eax, Mark; call eax; popfd; popad;
        // (prólogo original); jmp 0x64E867
        var c = new System.Collections.Generic.List<byte> { 0x60, 0x9C, 0x8D, 0x44, 0x24, 0x24, 0x50, 0xB8 };
        c.AddRange(BitConverter.GetBytes((int)fn));
        c.AddRange(new byte[] { 0xFF, 0xD0, 0x9D, 0x61 });
        c.AddRange(Prologue);
        c.Add(0xE9); int rel = c.Count; c.AddRange(new byte[4]);
        nint stub = Mem.AllocCode(c.ToArray());
        *(int*)(stub + rel) = (int)((CopyGlyph + 7) - (stub + rel + 4));
        var j = new byte[] { 0xE9, 0, 0, 0, 0, 0x90, 0x90 };
        BitConverter.TryWriteBytes(j.AsSpan(1), (int)(stub - (CopyGlyph + 5)));
        Mem.WriteBytes(CopyGlyph, j);
        // ancho de los huecos de la caché de letras: GetTextMetricsA de 0x64DDBE (call [IAT]) -> TextMetricsHook
        if (Mem.Matches(MetricsCall, new byte[] { 0xFF, 0x15, 0x50, 0x00, 0x89, 0x00 }))
        {
            _getMetrics = Mem.Read<nint>(0x890050);
            nint h = (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&TextMetricsHook;
            var call = new byte[] { 0xE8, 0, 0, 0, 0, 0x90 };
            BitConverter.TryWriteBytes(call.AsSpan(1), (int)(h - (MetricsCall + 5)));
            Mem.WriteBytes(MetricsCall, call);
        }
        else Log.Warn($"[{Name}] 0x{MetricsCall:X8} inesperado: los huecos de las letras quedan como el juego");
        Log.Info($"[{Name}] copia de letras 0x{CopyGlyph:X8} y TextOutW/DrawTextW enganchados; nitidez {Layer.FontSharp:0.##}");
    }

    private static void Note(nint dc, char* s, int n, int x, int y)
    {
        if (n == 1 && s != null) { _ch = s[0]; _font = GetCurrentObject(dc, 6 /* OBJ_FONT */); _ox = x; _oy = y; _have = true; }
        else _have = false;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TextOutHook(nint dc, int x, int y, char* s, int n)
    {
        try { Note(dc, s, n, x, y); } catch { }
        return ((delegate* unmanaged[Stdcall]<nint, int, int, char*, int, int>)_textOut)(dc, x, y, s, n);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int DrawTextHook(nint dc, char* s, int n, int* r, uint flags)
    {
        // (con DT_CALCRECT solo mide: no se anota)
        try { if ((flags & 0x400) == 0 && r != null) Note(dc, s, n, r[0], r[1]); } catch { }
        return ((delegate* unmanaged[Stdcall]<nint, char*, int, int*, uint, int>)_drawText)(dc, s, n, r, flags);
    }

    /// <summary>sp[0] = vuelta; sp[1..4] = argumentos de 0x64E860.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void Mark(uint* sp)
    {
        try
        {
            Surface7? atlas = null;
            nint dstBits = *(nint*)(sp[1] + 0x24); // lpSurface de la descripción del atlas
            foreach (var s in Surface7.Locked)
            {
                if (s.Screen) continue;
                if (!s.Font)
                {
                    s.Font = true;
                    if (_marked++ < 8) Log.Info($"[Letras nítidas (atlas)] textura de letras {s.W}x{s.H} (formato {s.Fmt})");
                }
                if (s.Cpu != 0 && dstBits >= s.Cpu && dstBits < s.Cpu + s.W * s.H * s.Bpp) atlas = s;
            }
            if (atlas == null || !Backend.Current.ShadowsOn) { _have = false; return; }
            int* dst = (int*)sp[3], src = (int*)sp[4];
            int dx = dst[0], dy = dst[1], sx = src[0], sy = src[1], w = src[2] & 0xFFFF, h = src[3] & 0xFFFF;
            // (si Lock devolvió el atlas desde una esquina, dstBits no es el principio: se corrige la posición)
            int off = (int)(dstBits - atlas.Cpu), pitch = atlas.W * atlas.Bpp;
            dx += off % pitch / atlas.Bpp; dy += off / pitch;
            Backend.Current.FontGlyph(atlas, dx, dy, w, h, _ch, _font, _ox - sx + dx, _oy - sy + dy, _have);
            _have = false;
        }
        catch { }
    }
}
