using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Atlas «sombra» de las letras del juego (con RenderScale). El juego pinta cada letra a su tamaño (1366x768) en un atlas
/// A4R4G4B4 de 256x256 (16 niveles de alfa) y al ampliar la imagen salían borrosas. Por cada atlas de letras se lleva una
/// copia a la escala de la imagen interna (256·Sx, en A8R8G8B8): cada vez que el juego copia una letra a su atlas
/// (Patches/FontAtlas, 0x64E860), aquí se pinta la misma letra, con la misma fuente a Sx veces su alto, en el mismo
/// recuadro escalado. Al dibujar se usa la sombra en lugar del atlas: las coordenadas de textura son relativas, así que
/// caen en el mismo sitio. Si una letra no se puede repetir (no se sabe cuál era), ese atlas deja de usar la sombra.
/// </summary>
internal sealed unsafe partial class Backend11
{
    private sealed class Shadow
    {
        public nint Dc, Bmp, Bits, OldBmp;
        public int W, H;
        public float K;
        public bool Broken;
    }

    private readonly Dictionary<Surface7, Shadow> _shadows = new();
    private readonly Dictionary<nint, nint> _bigFonts = new();
    private int _shadowLogs;

    [DllImport("gdi32.dll")] private static extern int GetObjectW(nint obj, int size, void* buf);
    [DllImport("gdi32.dll")] private static extern nint CreateFontIndirectW(void* lf);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern uint SetTextAlign(nint dc, uint align);
    [DllImport("gdi32.dll")] private static extern int TextOutW(nint dc, int x, int y, char* s, int n);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int x0, int y0, int x1, int y1);
    [DllImport("gdi32.dll")] private static extern int SelectClipRgn(nint dc, nint rgn);

    /// <summary>¿Se puede llevar sombra? (solo con la imagen interna ampliada)</summary>
    public override bool ShadowsOn => Scaled && Layer.FontSharp > 0;

    public override void FontGlyph(Surface7 atlas, int dx, int dy, int w, int h, char ch, nint font, int ox, int oy, bool known)
    {
        try
        {
            if (!Scaled || atlas.Tex == 0) return;
            if (!_shadows.TryGetValue(atlas, out var sh)) { sh = NewShadow(atlas); _shadows[atlas] = sh; }
            if (sh.Broken) return;
            if (!known || font == 0)
            {
                // letra desconocida: esta sombra ya no está completa; el atlas vuelve a su textura normal
                sh.Broken = true; ReleaseShadowTex(atlas);
                if (_shadowLogs++ < 8) Log.Warn($"D11: letra sin datos en el atlas {atlas.W}x{atlas.H}: ese atlas vuelve a la resolución del juego");
                return;
            }
            if (sh.Dc == 0) return;
            float k = sh.K;
            int x0 = (int)MathF.Round(dx * k), y0 = (int)MathF.Round(dy * k);
            int x1 = Math.Min(sh.W, (int)MathF.Round((dx + w) * k)), y1 = Math.Min(sh.H, (int)MathF.Round((dy + h) * k));
            if (x1 <= x0 || y1 <= y0) return;
            uint* px = (uint*)sh.Bits;
            for (int y = y0; y < y1; y++) new Span<uint>(px + y * sh.W + x0, x1 - x0).Clear();
            nint big = BigFont(font, k);
            if (big != 0)
            {
                nint old = SelectObject(sh.Dc, big);
                nint rgn = CreateRectRgn(x0, y0, x1, y1);
                SelectClipRgn(sh.Dc, rgn);
                char c = ch;
                TextOutW(sh.Dc, (int)MathF.Round(ox * k), (int)MathF.Round(oy * k), &c, 1);
                SelectClipRgn(sh.Dc, 0);
                DeleteObject(rgn);
                SelectObject(sh.Dc, old);
                GdiFlush();
            }
            // blanco con el alfa de la cobertura (como hace el juego: media de los canales)
            int cw = x1 - x0, chh = y1 - y0;
            uint* o = (uint*)Conv(cw * chh * 4);
            for (int y = 0; y < chh; y++)
            {
                uint* row = px + (y0 + y) * sh.W + x0;
                for (int x = 0; x < cw; x++)
                {
                    uint p = row[x];
                    uint a = (((p >> 16) & 0xFF) + ((p >> 8) & 0xFF) + (p & 0xFF)) / 3;
                    o[y * cw + x] = a << 24 | 0xFFFFFFu;
                }
            }
            Upload(atlas.ShadowTex, x0, y0, x1, y1, (byte*)o, cw * 4);
        }
        catch (Exception ex) { if (_shadowLogs++ < 8) Log.Error("D11: sombra de letras: " + ex.Message); }
    }

    private Shadow NewShadow(Surface7 atlas)
    {
        var sh = new Shadow { K = Sx };
        sh.W = (int)MathF.Round(atlas.W * Sx); sh.H = (int)MathF.Round(atlas.H * Sy);
        sh.K = sh.W / (float)atlas.W;
        // textura de D3D11 (sin mipmaps: lo 2D no los usa), a cero
        uint* t = stackalloc uint[11];
        t[0] = (uint)sh.W; t[1] = (uint)sh.H; t[2] = 1; t[3] = 1; t[4] = 87; t[5] = 1; t[6] = 0; t[7] = 0; t[8] = 0x8; t[9] = 0; t[10] = 0;
        byte* zero = Conv(sh.W * sh.H * 4);
        new Span<byte>(zero, sh.W * sh.H * 4).Clear();
        uint* init = stackalloc uint[3];
        init[0] = (uint)(nint)zero; init[1] = (uint)(sh.W * 4); init[2] = 0;
        nint tex = 0, srv = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, uint*, nint*, int>)Fn(_dev, DevCreateTexture2D))(_dev, t, init, &tex);
        if (hr == 0) hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Fn(_dev, DevCreateSRV))(_dev, tex, 0, &srv);
        if (hr != 0) { Layer.ReleaseCom(tex); sh.Broken = true; Log.Error($"D11: sombra de letras {sh.W}x{sh.H} -> 0x{hr:X8}"); return sh; }
        // DIB donde GDI pinta las letras grandes
        int* bmi = stackalloc int[10];
        new Span<int>(bmi, 10).Clear();
        bmi[0] = 40; bmi[1] = sh.W; bmi[2] = -sh.H; bmi[3] = 1 | (32 << 16);
        nint dc = CreateCompatibleDC(0), bits = 0;
        nint bmp = dc != 0 ? CreateDIBSection(dc, bmi, 0, &bits, 0, 0) : 0;
        if (bmp == 0 || bits == 0)
        {
            if (dc != 0) DeleteDC(dc);
            Layer.ReleaseCom(srv); Layer.ReleaseCom(tex); sh.Broken = true; return sh;
        }
        sh.Dc = dc; sh.Bmp = bmp; sh.Bits = bits; sh.OldBmp = SelectObject(dc, bmp);
        new Span<byte>((void*)bits, sh.W * sh.H * 4).Clear();
        SetBkMode(dc, 1); SetTextColor(dc, 0xFFFFFF); SetTextAlign(dc, 0);
        atlas.ShadowTex = tex; atlas.ShadowSrv = srv;
        Log.Info($"D11: sombra de letras {sh.W}x{sh.H} para el atlas {atlas.W}x{atlas.H} (x{sh.K:F2})");
        return sh;
    }

    /// <summary>La misma fuente a k veces su tamaño, con suavizado en grises (una por fuente del juego).</summary>
    private nint BigFont(nint font, float k)
    {
        if (_bigFonts.TryGetValue(font, out nint f)) return f;
        byte* lf = stackalloc byte[92]; // LOGFONTW
        f = 0;
        if (GetObjectW(font, 92, lf) > 0)
        {
            ((int*)lf)[0] = (int)MathF.Round(((int*)lf)[0] * k);
            ((int*)lf)[1] = (int)MathF.Round(((int*)lf)[1] * k);
            lf[26] = 4; // ANTIALIASED_QUALITY
            f = CreateFontIndirectW(lf);
        }
        _bigFonts[font] = f;
        return f;
    }

    private static void ReleaseShadowTex(Surface7 s)
    {
        Layer.ReleaseCom(s.ShadowSrv); s.ShadowSrv = 0;
        Layer.ReleaseCom(s.ShadowTex); s.ShadowTex = 0;
    }

    private void ReleaseShadow(Surface7 s)
    {
        ReleaseShadowTex(s);
        if (!_shadows.Remove(s, out var sh) || sh.Dc == 0) return;
        SelectObject(sh.Dc, sh.OldBmp);
        DeleteObject(sh.Bmp); DeleteDC(sh.Dc);
    }
}
