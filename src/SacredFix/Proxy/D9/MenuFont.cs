using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SacredFix.Core;
using SacredFix.Native;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Letras del menú propio: las dos fuentes TrueType del propio juego (carpeta font): AntiquaSSK (ANTQS__.TTF) para el
/// texto y Carolingia (CAROLING.TTF) para los títulos. Se cargan en privado (AddFontResourceEx, FR_PRIVATE: solo
/// este proceso, sin instalarlas en Windows) y se dibujan con GDI en un atlas de 1024x512, con suavizado en escala
/// de grises (no ClearType, que da bordes de colores) y más grandes de lo que se ven: al pintarlas se reducen con
/// filtro lineal y quedan suaves. Un carácter que una fuente no tenga (tildes, ñ...) se toma de la otra o de Palatino.
/// En la esquina hay un bloque blanco para los rectángulos de color.
/// </summary>
internal static unsafe class MenuFont
{
    private const int W = 1024, H = 512, BodyPx = 32, TitlePx = 40;
    public const string Chars = " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~áéíóúüñÁÉÍÓÚÜÑ¿¡·–«»‹›";
    private static readonly Dictionary<char, (float u0, float v0, float u1, float v1, int adv)>[] _g = { new(), new() };
    private static readonly int[] _cellH = new int[2];
    private static nint _tex;
    private static bool _failed;
    /// <summary>Alto de celda del texto (las llamadas escalan con k = alto deseado / CellH).</summary>
    public static int CellH => _cellH[0];
    public static nint Texture => _tex;
    public const float WhiteU = 2f / W, WhiteV = 2f / H;

    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, int* bmi, uint usage, nint* bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern int DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern int DeleteDC(nint dc);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int TextOutW(nint dc, int x, int y, string s, int n);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int GetTextExtentPoint32W(nint dc, string s, int n, int* size);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern uint GetGlyphIndicesW(nint dc, string s, int n, ushort* idx, uint flags);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int AddFontResourceExW(string file, uint flags, nint reserved);

    private static nint Font(int px, string face) =>
        CreateFontW(-px, 0, 0, 0, 400, 0, 0, 0, 1 /* DEFAULT_CHARSET */, 0, 0, 4 /* ANTIALIASED_QUALITY */, 0, face);

    /// <summary>¿Tiene la fuente seleccionada este carácter?</summary>
    private static bool Has(nint dc, char ch)
    {
        ushort g = 0;
        return GetGlyphIndicesW(dc, ch.ToString(), 1, &g, 1 /* GGI_MARK_NONEXISTING_GLYPHS */) != 0xFFFFFFFF && g != 0xFFFF;
    }

    public static bool Build()
    {
        if (_tex != 0) return true;
        if (_failed) return false;
        _failed = true;
        string dir = Path.Combine(Path.GetDirectoryName(Win32.GetModuleFileName(0)) ?? ".", "font");
        bool antiqua = AddFontResourceExW(Path.Combine(dir, "ANTQS__.TTF"), 0x10 /* FR_PRIVATE */, 0) > 0;
        bool caro = AddFontResourceExW(Path.Combine(dir, "CAROLING.TTF"), 0x10, 0) > 0;
        if (!antiqua || !caro) Log.Warn($"Menú: fuentes del juego: AntiquaSSK {(antiqua ? "sí" : "no")}, Carolingia {(caro ? "sí" : "no")} (se usa Palatino)");

        nint dc = CreateCompatibleDC(0);
        int* bmi = stackalloc int[10];
        new Span<int>(bmi, 10).Clear();
        bmi[0] = 40; bmi[1] = W; bmi[2] = -H; ((short*)(bmi + 3))[0] = 1; ((short*)(bmi + 3))[1] = 32;
        nint bits = 0;
        nint bmp = CreateDIBSection(dc, bmi, 0, &bits, 0, 0);
        if (dc == 0 || bmp == 0 || bits == 0) { Log.Warn("Menú: no se pudo preparar la letra"); return false; }
        nint oldBmp = SelectObject(dc, bmp);
        SetTextColor(dc, 0xFFFFFF);
        SetBkMode(dc, 1);
        int* sz = stackalloc int[2];
        int y = 8, x = 8;
        // la letra elegida en [Fonts] (Patches.GameFonts) o la del juego
        string[] faces = { Patches.GameFonts.TextFace.Length > 0 ? Patches.GameFonts.TextFace : antiqua ? "AntiquaSSK" : "Palatino Linotype",
                           Patches.GameFonts.TitleFace.Length > 0 ? Patches.GameFonts.TitleFace : caro ? "Carolingia" : "Palatino Linotype" };
        int[] px = { BodyPx, TitlePx };
        for (int f = 0; f < 2; f++)
        {
            nint main = Font(px[f], faces[f]), other = Font(px[f], faces[1 - f]), pal = Font(px[f], "Palatino Linotype");
            nint old = SelectObject(dc, main);
            GetTextExtentPoint32W(dc, "ÁÑgj", 4, sz);
            int cell = sz[1] + 4;
            _cellH[f] = cell;
            x = 8;
            foreach (char ch in Chars)
            {
                // la fuente que tenga el carácter: la propia, la otra del juego o Palatino
                SelectObject(dc, main);
                if (ch != ' ' && !Has(dc, ch)) { SelectObject(dc, other); if (!Has(dc, ch)) SelectObject(dc, pal); }
                string c = ch.ToString();
                GetTextExtentPoint32W(dc, c, 1, sz);
                if (x + sz[0] + 4 > W) { x = 8; y += cell; }
                if (y + cell > H) break;
                TextOutW(dc, x, y + 2, c, 1);
                _g[f][ch] = (x / (float)W, y / (float)H, (x + sz[0]) / (float)W, (y + cell) / (float)H, sz[0]);
                x += sz[0] + 6;
            }
            y += cell + 6;
            SelectObject(dc, old);
            DeleteObject(main); DeleteObject(other); DeleteObject(pal);
        }
        uint* pxl = (uint*)NativeMemory.Alloc((nuint)(W * H * 4));
        for (int i = 0; i < W * H; i++)
        {
            uint p = ((uint*)bits)[i];
            uint a = Math.Max(p & 0xFF, Math.Max((p >> 8) & 0xFF, (p >> 16) & 0xFF));
            pxl[i] = (a << 24) | 0xFFFFFF;
        }
        for (int yy = 0; yy < 5; yy++) for (int xx = 0; xx < 5; xx++) pxl[yy * W + xx] = 0xFFFFFFFF; // bloque blanco
        _tex = Backend.Current.CreateRgbaTexture(W, H, pxl);
        NativeMemory.Free(pxl);
        SelectObject(dc, oldBmp);
        DeleteObject(bmp); DeleteDC(dc);
        if (_tex == 0) { Log.Warn("Menú: no se pudo crear la textura de la letra"); return false; }
        Log.Info($"Menú: letras {faces[0]} y {faces[1]}, atlas de {W}x{H}");
        _failed = false;
        return true;
    }

    /// <summary>Ancho del texto (k como en Text: alto deseado / CellH del texto normal).</summary>
    public static float Width(string s, float k, bool title = false)
    {
        s = Lang.T(s); // (textos propios: castellano o inglés)
        int f = title ? 1 : 0;
        float kk = k * _cellH[0] / Math.Max(1, _cellH[f]), w = 0;
        foreach (char ch in s) if (_g[f].TryGetValue(ch, out var g)) w += (g.adv + 1) * kk;
        return w;
    }

    /// <summary>Texto con la letra del juego; title = Carolingia (títulos), si no AntiquaSSK. Mismo alto con el mismo k.</summary>
    public static void Text(List<float> v, string s, float x, float y, float k, uint color, bool title = false)
    {
        s = Lang.T(s); // (textos propios: castellano o inglés)
        int f = title ? 1 : 0;
        float kk = k * _cellH[0] / Math.Max(1, _cellH[f]);
        foreach (char ch in s)
        {
            if (!_g[f].TryGetValue(ch, out var g)) continue;
            float w = g.adv * kk, h = _cellH[f] * kk;
            if (ch != ' ') Quad(v, x, y, w, h, g.u0, g.v0, g.u1, g.v1, color);
            x += (g.adv + 1) * kk;
        }
    }

    public static void Rect(List<float> v, float x, float y, float w, float h, uint color)
        => Quad(v, x, y, w, h, WhiteU, WhiteV, WhiteU, WhiteV, color);

    /// <summary>Un cuadrado de la superposición: 4 vértices (tira) de 7 floats (x, y, z, rhw, color, u, v).</summary>
    private static void Quad(List<float> v, float x, float y, float w, float h, float u0, float v0, float u1, float v1, uint color)
    {
        float c = BitConverter.Int32BitsToSingle((int)color);
        x -= 0.5f; y -= 0.5f;
        v.AddRange(new[] { x, y, 0f, 1f, c, u0, v0, x + w, y, 0f, 1f, c, u1, v0, x, y + h, 0f, 1f, c, u0, v1, x + w, y + h, 0f, 1f, c, u1, v1 });
    }
}
