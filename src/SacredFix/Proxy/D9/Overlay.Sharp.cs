using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Texto nítido de la superposición (nivel y supervivencia, segundos de recarga, contador de FPS). Antes las letras se
/// pintaban una vez a 24 px con ClearType y se reducían al dibujarlas, con filtro lineal: quedaban borrosas. Ahora hay
/// un atlas por tamaño, pintado al tamaño exacto en píxeles de la pantalla y en escala de grises, y cada texto se coloca
/// en píxeles enteros: cada texel cae en un píxel.
/// </summary>
internal static unsafe partial class Overlay
{
    private sealed class SharpAtlas
    {
        public nint Tex;
        public int W, H, CellH;
        public readonly Dictionary<char, (float u0, float v0, float u1, float v1, int w)> G = new();
        public float[] Verts = new float[28 * 64];
        public int Quads;
    }

    private static readonly Dictionary<int, SharpAtlas?> _sharp = new();

    /// <summary>Tamaño de letra (px) para que la celda mida cell píxeles de pantalla (la celda de Segoe UI ≈ 1,33 em).</summary>
    private static int SharpPx(float cell) => Math.Clamp((int)MathF.Round(cell * 0.75f), 6, 72);

    private static SharpAtlas? Sharp(int px)
    {
        if (_sharp.TryGetValue(px, out var a)) return a;
        a = BuildSharp(px);
        _sharp[px] = a;
        return a;
    }

    private static SharpAtlas? BuildSharp(int px)
    {
        int cellGuess = px * 2;
        int W = 1024, H = 64;
        int perRow = Math.Max(1, (W - 8) / (px + 4));
        while (H < (Chars.Length / perRow + 2) * (cellGuess + 2)) H *= 2;
        nint dc = CreateCompatibleDC(0);
        int* bmi = stackalloc int[10];
        new Span<int>(bmi, 10).Clear();
        bmi[0] = 40; bmi[1] = W; bmi[2] = -H; ((short*)(bmi + 3))[0] = 1; ((short*)(bmi + 3))[1] = 32;
        nint bits = 0;
        nint bmp = CreateDIBSection(dc, bmi, 0, &bits, 0, 0);
        if (dc == 0 || bmp == 0 || bits == 0) { if (dc != 0) DeleteDC(dc); return null; }
        nint oldBmp = SelectObject(dc, bmp);
        // escala de grises (ANTIALIASED_QUALITY), no ClearType: sin bordes de colores ni letras engordadas
        nint font = CreateFontW(-px, 0, 0, 0, 700, 0, 0, 0, 1 /* DEFAULT_CHARSET */, 0, 0, 4 /* ANTIALIASED_QUALITY */, 0, "Segoe UI");
        nint oldFont = SelectObject(dc, font);
        SetTextColor(dc, 0xFFFFFF);
        SetBkMode(dc, 1);
        var a = new SharpAtlas { W = W, H = H };
        int* sz = stackalloc int[2];
        GetTextExtentPoint32W(dc, "ÁÑgjČ", 5, sz);
        a.CellH = sz[1];
        int x = 1, y = 1;
        foreach (char ch in Chars)
        {
            string c = ch.ToString();
            GetTextExtentPoint32W(dc, c, 1, sz);
            if (x + sz[0] + 2 > W - 8) { x = 1; y += a.CellH + 2; }
            if (y + a.CellH + 1 > H) break;
            TextOutW(dc, x, y, c, 1);
            a.G[ch] = (x / (float)W, y / (float)H, (x + sz[0]) / (float)W, (y + a.CellH) / (float)H, sz[0]);
            x += sz[0] + 2;
        }
        uint* pxl = (uint*)NativeMemory.Alloc((nuint)(W * H * 4));
        for (int i = 0; i < W * H; i++)
        {
            uint p = ((uint*)bits)[i];
            uint al = Math.Max(p & 0xFF, Math.Max((p >> 8) & 0xFF, (p >> 16) & 0xFF));
            pxl[i] = (al << 24) | 0xFFFFFF;
        }
        a.Tex = Backend.Current.CreateRgbaTexture(W, H, pxl);
        NativeMemory.Free(pxl);
        SelectObject(dc, oldFont); SelectObject(dc, oldBmp);
        DeleteObject(font); DeleteObject(bmp); DeleteDC(dc);
        if (a.Tex == 0) { Log.Warn($"Superposición: no se pudo crear la letra de {px} px"); return null; }
        Log.Info($"Superposición: letra nítida de {px} px (atlas {W}x{H}, celda {a.CellH})");
        return a;
    }

    /// <summary>Ancho del texto en píxeles de pantalla, con la celda de cell píxeles.</summary>
    private static float SharpWidth(string s, float cell)
    {
        var a = Sharp(SharpPx(cell));
        if (a == null) return 0;
        int w = 0;
        foreach (char ch in s) if (a.G.TryGetValue(ch, out var g)) w += g.w;
        return w;
    }

    /// <summary>Alto real de la celda del atlas para cell (para centrar).</summary>
    private static float SharpCell(float cell) => Sharp(SharpPx(cell))?.CellH ?? cell;

    /// <summary>Texto con la celda de cell píxeles, en (x, y) redondeados a píxeles enteros.</summary>
    private static void SharpText(string s, float x, float y, float cell, uint color)
    {
        var a = Sharp(SharpPx(cell));
        if (a == null) return;
        float px = MathF.Round(x), py = MathF.Round(y);
        foreach (char ch in s)
        {
            if (!a.G.TryGetValue(ch, out var g)) continue;
            if (ch != ' ')
            {
                if ((a.Quads + 1) * 28 > a.Verts.Length) Array.Resize(ref a.Verts, a.Verts.Length * 2);
                fixed (float* v = &a.Verts[a.Quads * 28])
                {
                    float x1 = px + g.w, y1 = py + a.CellH;
                    Quad(v, 0, px - 0.5f, py - 0.5f, g.u0, g.v0, color);
                    Quad(v, 1, x1 - 0.5f, py - 0.5f, g.u1, g.v0, color);
                    Quad(v, 2, px - 0.5f, y1 - 0.5f, g.u0, g.v1, color);
                    Quad(v, 3, x1 - 0.5f, y1 - 0.5f, g.u1, g.v1, color);
                }
                a.Quads++;
            }
            px += g.w;
        }
    }

    /// <summary>Dibuja y vacía los textos nítidos. inScene: dentro de la escena (false si el motor no sabe).</summary>
    private static bool FlushSharp(bool inScene)
    {
        bool ok = true;
        foreach (var a in _sharp.Values)
        {
            if (a == null || a.Quads == 0) continue;
            fixed (float* v = a.Verts)
            {
                if (inScene) ok &= Backend.Current.DrawOverlayInScene(a.Tex, v, a.Quads);
                else Backend.Current.DrawOverlay(a.Tex, v, a.Quads);
            }
            a.Quads = 0;
        }
        return ok;
    }

    /// <summary>¿Hay textos nítidos pendientes de dibujar?</summary>
    private static bool SharpPending()
    {
        foreach (var a in _sharp.Values) if (a != null && a.Quads > 0) return true;
        return false;
    }
}
