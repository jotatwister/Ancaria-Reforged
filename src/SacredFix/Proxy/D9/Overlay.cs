using System;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Contador de fotogramas por segundo ([Fps] Enabled=1), dibujado por la capa Direct3D 9 encima de todo, al final de
/// cada fotograma y a la resolución de la pantalla (nítido aunque la imagen del juego se escale).
/// La fuente: al empezar se dibujan con GDI los caracteres necesarios en una textura de 256x32 (blanco con el alfa de
/// la letra) y luego cada carácter es un cuadrado con su trozo de textura. Lleva una sombra para leerse en cualquier fondo.
/// </summary>
internal static unsafe partial class Overlay
{
    public static bool Enabled { get; private set; }
    public static bool Cooldowns { get; private set; }
    public static bool HeroInfo { get; private set; }
    public static bool Any => Enabled || Cooldowns || HeroInfo;
    private static int _x = 150, _y = 6, _size = 16;
    // todos los caracteres latinos (ASCII, Latin-1 y Latin Extended-A: también č, ř, š, ž...) para los textos del juego
    private static readonly string Chars = BuildChars();
    private static string BuildChars()
    {
        var sb = new System.Text.StringBuilder();
        for (int c = 32; c < 127; c++) sb.Append((char)c);
        for (int c = 0xA1; c < 0x180; c++) sb.Append((char)c);
        return sb.ToString();
    }
    private static readonly System.Collections.Generic.Dictionary<char, int> _ix = BuildIndex();
    private static System.Collections.Generic.Dictionary<char, int> BuildIndex()
    {
        var d = new System.Collections.Generic.Dictionary<char, int>();
        for (int i = 0; i < Chars.Length; i++) d[Chars[i]] = i;
        return d;
    }
    private const int AtlasW = 512, AtlasH = 512, RowH = 36;
    private static readonly float[] _v0 = new float[Chars.Length];
    private static nint _font;          // IDirect3DTexture9
    private static readonly float[] _u0 = new float[Chars.Length], _u1 = new float[Chars.Length];
    private static readonly int[] _adv = new int[Chars.Length];
    private static int _cellH;
    private static bool _failed;

    // FPS
    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private static int _frames;
    private static long _lastUpdate;
    private static string _text = "";

    public static void Init(Config cfg)
    {
        Enabled = cfg.GetBool("Fps", "Enabled", false);
        _x = cfg.GetInt("Fps", "X", 150);
        _y = cfg.GetInt("Fps", "Y", 6);
        _size = Math.Clamp(cfg.GetInt("Fps", "Size", 16), 8, 64);
        Cooldowns = cfg.GetBool("Keys", "CooldownSeconds", true);
        HeroInfo = cfg.GetBool("Hud", "HeroInfo", true);
        QuestTracker.Init(cfg);
        AutoGold.Init(cfg);
        TrackEnemy.Init(cfg);
        ItemCompare.Init(cfg);
    }

    /// <summary>Desde el menú propio: contador de FPS (sí/no, tamaño).</summary>
    public static void SetFps(bool? on, int? size)
    {
        if (on.HasValue) Enabled = on.Value;
        if (size.HasValue) _size = Math.Clamp(size.Value, 8, 64);
    }
    public static void SetCooldowns(bool on) => Cooldowns = on;
    public static void SetHeroInfo(bool on) => HeroInfo = on;
    // La interfaz de la partida empezó a dibujarse en este fotograma (UiCanvas, con el mundo a la vista, como los buffos):
    // el nivel y la supervivencia solo salen entonces, no en la pantalla de carga ni en los menús.
    private static bool _gameUi, _questsDone;
    public static void MarkGameUi() => _gameUi = true;

    /// <summary>Un fotograma del juego presentado.</summary>
    public static void Frame()
    {
        _frames++;
        long now = _clock.ElapsedMilliseconds;
        if (now - _lastUpdate >= 500)
        {
            double fps = _frames * 1000.0 / Math.Max(1, now - _lastUpdate);
            _text = $"{fps:0} FPS";
            _frames = 0;
            _lastUpdate = now;
        }
    }

    // ---- fuente (GDI) ----

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

    private static bool BuildFont()
    {
        if (_font != 0) return true;
        if (_failed) return false;
        _failed = true;
        const int W = AtlasW, H = AtlasH;
        nint dc = CreateCompatibleDC(0);
        int* bmi = stackalloc int[10];
        new Span<int>(bmi, 10).Clear();
        bmi[0] = 40; bmi[1] = W; bmi[2] = -H; ((short*)(bmi + 3))[0] = 1; ((short*)(bmi + 3))[1] = 32;
        nint bits = 0;
        nint bmp = CreateDIBSection(dc, bmi, 0, &bits, 0, 0);
        if (dc == 0 || bmp == 0 || bits == 0) { Log.Warn("D9: contador de FPS: no se pudo preparar la fuente"); return false; }
        nint oldBmp = SelectObject(dc, bmp);
        nint font = CreateFontW(-24, 0, 0, 0, 700 /* negrita */, 0, 0, 0, 0, 0, 0, 5 /* CLEARTYPE */, 0, "Segoe UI");
        nint oldFont = SelectObject(dc, font);
        SetTextColor(dc, 0xFFFFFF);
        SetBkMode(dc, 1 /* TRANSPARENT */);
        int x = 1, row = 0;
        int* sz = stackalloc int[2];
        for (int i = 0; i < Chars.Length; i++)
        {
            string c = Chars[i].ToString();
            GetTextExtentPoint32W(dc, c, 1, sz);
            if (x + sz[0] + 2 > W - 6) { x = 1; row++; } // (la esquina superior derecha es el trozo blanco)
            int top = row * RowH;
            if (top + RowH > H) break;
            TextOutW(dc, x, top + 1, c, 1);
            _u0[i] = x / (float)W; _u1[i] = (x + sz[0]) / (float)W; _v0[i] = top / (float)H;
            _adv[i] = sz[0];
            _cellH = Math.Max(_cellH, Math.Min(sz[1], RowH - 2));
            x += sz[0] + 2;
        }
        // a una textura del motor: blanco, con el alfa del brillo de la letra
        uint* px = (uint*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)(W * H * 4));
        for (int y = 0; y < H; y++)
            for (int i = 0; i < W; i++)
            {
                uint p = ((uint*)bits)[y * W + i];
                uint a = Math.Max(p & 0xFF, Math.Max((p >> 8) & 0xFF, (p >> 16) & 0xFF));
                if (i >= W - 4 && y < 4) a = 255; // trozo blanco sólido (rectángulos)
                px[y * W + i] = (a << 24) | 0xFFFFFF;
            }
        _font = Backend.Current.CreateRgbaTexture(W, H, px);
        System.Runtime.InteropServices.NativeMemory.Free(px);
        int hr = _font != 0 ? 0 : -1;
        SelectObject(dc, oldFont); SelectObject(dc, oldBmp);
        DeleteObject(font); DeleteObject(bmp); DeleteDC(dc);
        if (_font == 0) { Log.Warn($"D9: contador de FPS: textura de la fuente 0x{hr:X8}"); return false; }
        _failed = false;
        return true;
    }

    // ---- dibujo ----

    /// <summary>Al final del fotograma, sobre el back buffer (antes de Present). sx, sy: escala de la imagen del juego.</summary>
    public static void Draw(float sx, float sy)
    {
        bool hudDone = _cdInScene; // (recargas y nivel ya dibujados dentro de la escena)
        _cdInScene = false;
        bool gameUi = _gameUi;
        _gameUi = false; _questsDone = false; // (se reinician en cada fotograma, aunque no haya nada que dibujar)
        if (!Any || !Backend.Current.Ready || !BuildFont()) return;
        _nq = 0;
        float shadow = Math.Max(1f, 1.5f * sy);
        if (Enabled && _text.Length > 0)
        {
            float cell = _size * sy, sh = MathF.Max(1f, MathF.Round(shadow));
            float x0 = _x * sx, y0 = _y * sy;
            SharpText(_text, x0 + sh, y0 + sh, cell, 0xC0000000);
            SharpText(_text, x0, y0, cell, 0xFFFFE066); // amarillo claro
        }
        if (!hudDone)
        {
            if (Cooldowns) AddCooldowns(CollectCooldowns(), sx, sy, shadow);
            if (HeroInfo && gameUi) AddHeroInfo(sx, sy, shadow);
        }
        if (_nq > 0) fixed (float* v = _verts) Backend.Current.DrawOverlay(_font, v, _nq);
        FlushSharp(false);
    }

    private static bool _cdInScene; // segundos de recarga ya dibujados dentro de la escena en este fotograma

    /// <summary>
    /// Segundos de recarga dentro de la escena, antes de las ayudas del juego y del cursor (UiCanvas: TipRenderHook y
    /// RenderCursorHook), para que queden por debajo. Una vez por fotograma; si el motor no sabe (Direct3D 9), al presentar.
    /// </summary>
    public static void DrawCooldownsInScene()
    {
        // misiones (letra del juego, otra textura): una vez por fotograma de la partida
        if (_gameUi && !_questsDone) { _questsDone = true; try { QuestTracker.DrawInScene(); } catch { } }
        if (_cdInScene || !(Cooldowns || HeroInfo) || !Backend.Current.Ready || !BuildFont()) return;
        var (w, h) = Backend.Current.SceneSize;
        if (w <= 0 || h <= 0) return;
        float sx = w / (float)Display.Width, sy = h / (float)Display.Height;
        int nCd = Cooldowns ? CollectCooldowns() : 0;
        _nq = 0;
        AddCooldowns(nCd, sx, sy, Math.Max(1f, 1.5f * sy));
        if (HeroInfo && _gameUi) AddHeroInfo(sx, sy, Math.Max(1f, 1.5f * sy));
        if (_nq == 0 && !SharpPending()) { _cdInScene = true; return; }
        bool ok = true;
        if (_nq > 0) fixed (float* v = _verts) ok = Backend.Current.DrawOverlayInScene(_font, v, _nq);
        ok &= FlushSharp(true);
        _cdInScene = ok;
        _nq = 0;
    }

    /// <summary>
    /// Nivel y bonus de supervivencia a la izquierda del retrato del héroe (arriba a la derecha, columna de retratos de
    /// 92 px pegada al borde; debajo de la fila de buffos), como la ficha (cUI_StatisticsChar):
    ///   nivel: word [héroe + 0x3A8 + 0x56];
    ///   supervivencia (0x6AA062): ms sobrevividos [0xAA5598 + 0x570C] -> minutos (÷1000, ÷60) ->
    ///   100·(1 − 1/((min − 1)/120 + 1)) (0x565D70; 0 con menos de 1 min); si [héroe + 0x4F4] > 0x1C y el byte
    ///   [[héroe + 0x4F8] + 0x1C] >= 6, suma 10 × (1; 1,34; 1,67; 2; 2,34 según la dificultad [0x182EE44]).
    /// Solo en los fotogramas en que se dibuja la interfaz de la partida (MarkGameUi) y con la barra de tareas visible.
    /// </summary>
    private static void AddHeroInfo(float sx, float sy, float shadow)
    {
        try
        {
            nint mgr = *(nint*)0x17EEBBC, om = *(nint*)0xAD5C40;
            if (mgr == 0 || om == 0) return; // (la partida ya la ha comprobado MarkGameUi)
            nint bar = *(nint*)(mgr + 0x80);
            if (bar == 0 || (*(uint*)(bar + 0x10) & 1) == 0) return;
            nint hero = ((delegate* unmanaged[Thiscall]<nint, nint>)0x603E30)(om);
            if (hero == 0) return;
            int lvl = *(ushort*)(hero + 0x3A8 + 0x56);
            if (lvl <= 0 || lvl > 500) return;
            float surv = 0;
            if (*(int*)0xAAAD20 != 0)
            {
                uint min = *(uint*)(0xAA5598 + 0x570C) / 1000 / 60;
                if (min >= 1) surv = 100f * (1f - 1f / ((min - 1) / 120f + 1f));
                if (*(uint*)(hero + 0x4F4) > 0x1C)
                {
                    nint p = *(nint*)(hero + 0x4F8);
                    if (p != 0 && *(byte*)(p + 0x1C) >= 6)
                        surv += 10f * (*(int*)0x182EE44 switch { 1 => 1.34f, 2 => 1.67f, 3 => 2f, 4 => 2.34f, _ => 1f });
                }
            }
            // textos del propio juego (global.res): salen en su idioma y con sus traducciones
            string l1 = $"{GameText.Get(GameText.Level, Lang.T("Nivel"))} {lvl}", l2 = $"{GameText.Get(GameText.SurvivalBonus, Lang.T("Supervivencia"))} {(int)surv}%";
            float cell = 13 * sy, lh = 16 * sy, sh = MathF.Max(1f, MathF.Round(shadow));
            float right = (Display.Width - 100) * sx, y = (14 + 32 + 6) * sy;
            foreach (var (t, yy) in new[] { (l1, y), (l2, y + lh) })
            {
                float x = right - SharpWidth(t, cell);
                SharpText(t, x + sh, yy + sh, cell, 0xD0000000);
                SharpText(t, x, yy, cell, 0xFFF0E0B0);
            }
        }
        catch { }
    }

    /// <summary>Rectángulo de un color con el trozo blanco sólido de la textura de la fuente.</summary>
    private static void Rect(float x0, float y0, float x1, float y1, uint color)
    {
        if ((_nq + 1) * 28 > _verts.Length) Array.Resize(ref _verts, _verts.Length * 2);
        const float u = (AtlasW - 2f) / AtlasW, t = 2f / AtlasH;
        fixed (float* v = &_verts[_nq * 28])
        {
            Quad(v, 0, x0, y0, u, t, color);
            Quad(v, 1, x1, y0, u, t, color);
            Quad(v, 2, x0, y1, u, t, color);
            Quad(v, 3, x1, y1, u, t, color);
        }
        _nq++;
    }

    private static void AddCooldowns(int nCd, float sx, float sy, float shadow)
    {
        for (int i = 0; i < nCd; i++)
        {
            // centrado en la casilla (coordenadas del lienzo de 1024 -> pantalla)
            float cell = _cdH[i] * 0.42f * sy, sh = MathF.Max(1f, MathF.Round(shadow));
            float w = SharpWidth(_cdText[i], cell);
            float cx = (_cdX[i] + UiCanvas.Left) * sx, cy = _cdY[i] * sy;
            float x0 = cx - w / 2, y0 = cy - SharpCell(cell) / 2;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0) SharpText(_cdText[i], x0 + dx * sh, y0 + dy * sh, cell, 0xE0000000);
            SharpText(_cdText[i], x0, y0, cell, 0xFFFFFFFF);
        }
    }

    private static float TextWidth(string s, float k)
    {
        float w = 0;
        foreach (char ch in s) if (_ix.TryGetValue(ch, out int i)) w += (_adv[i] + 2) * k;
        return Math.Max(0, w - 2 * k);
    }

    // cuadrados de la superposición de este fotograma: 4 vértices x 7 floats cada uno
    private static float[] _verts = new float[28 * 64];
    private static int _nq;

    private static void DrawText(nint dev, string text, float x, float y, float k, uint color)
    {
        float h = _cellH * k;
        foreach (char ch in text)
        {
            if (!_ix.TryGetValue(ch, out int i)) continue;
            float w = _adv[i] * k;
            if (ch != ' ')
            {
                float v0 = _v0[i] + 1f / AtlasH, v1 = _v0[i] + (_cellH + 1f) / AtlasH;
                if ((_nq + 1) * 28 > _verts.Length) Array.Resize(ref _verts, _verts.Length * 2);
                fixed (float* v = &_verts[_nq * 28])
                {
                    Quad(v, 0, x - 0.5f, y - 0.5f, _u0[i], v0, color);
                    Quad(v, 1, x + w - 0.5f, y - 0.5f, _u1[i], v0, color);
                    Quad(v, 2, x - 0.5f, y + h - 0.5f, _u0[i], v1, color);
                    Quad(v, 3, x + w - 0.5f, y + h - 0.5f, _u1[i], v1, color);
                }
                _nq++;
            }
            x += w + 2 * k;
        }
    }

    // ---- segundos de recarga sobre las casillas de artes de la barra ----
    // Barra = cUI_Manager.windows[0] (cUI_Taskbar2); sus 5 casillas son ventanas en barra + 0x178 + 4·k
    // (x +0x24, y +0x28, ancho word +0x2C, alto word +0x2E, en el lienzo de 1024x768; visible: bit 0 de +0x10).
    // Arte de la casilla: inventario del jugador ([0xAD4448] + [barra + 0x158]·0x1180), tipo en
    // inv + 0xC90 + (variante + 2k)·0x14 (2 hechizo, 3 arte), id en inv + (variante + 0xA1 + 2k)·0x14, traducido como
    // hace la barra con 0x552370(héroe, id, 0). Recarga: entrada del arte en la lista del héroe (+0x12 restante, s).
    private static readonly string[] _cdText = new string[5];
    private static readonly float[] _cdX = new float[5], _cdY = new float[5], _cdH = new float[5];
    private static bool _cdLogged;

    private static int CollectCooldowns()
    {
        try
        {
            nint mgr = *(nint*)0x17EEBBC, invs = *(nint*)0xAD4448, om = *(nint*)0xAD5C40;
            if (mgr == 0 || invs == 0 || om == 0 || (*(uint*)(mgr + 8) & 0x04) == 0) return 0; // solo en la partida
            nint bar = *(nint*)(mgr + 0x80);
            if (bar == 0 || (*(uint*)(bar + 0x10) & 1) == 0) return 0;
            nint hero = ((delegate* unmanaged[Thiscall]<nint, nint>)0x603E30)(om);
            if (hero == 0) return 0;
            int player = *(ushort*)(bar + 0x158);
            if (player >= 0x20) return 0;
            nint inv = invs + player * 0x1180;
            int alt = (*(byte*)(inv + 0x1162) >> 1) & 1;
            nint st = *(nint*)(hero + 0x3A8 + 0x102), en = *(nint*)(hero + 0x3A8 + 0x106);
            if (st == 0 || en <= st) return 0;
            int n = 0;
            for (int k = 0; k < 5; k++)
            {
                nint slot = *(nint*)(bar + 0x178 + k * 4);
                if (slot == 0 || (*(uint*)(slot + 0x10) & 1) == 0) continue;
                int type = *(short*)(inv + 0xC90 + (alt + k * 2) * 0x14);
                if (type != 2 && type != 3) continue;
                int id = *(int*)(inv + (alt + 0xA1 + k * 2) * 0x14);
                if (id <= 0) continue;
                id = ((delegate* unmanaged[Thiscall]<nint, int, int, int>)0x552370)(hero, id, 0) & 0xFFFF;
                float left = 0;
                for (nint e = st; e + 0x16 <= en; e += 0x16)
                    if (*(ushort*)(e + 4) == id) { left = *(float*)(e + 0x12); break; }
                if (!(left > 0.05f) || left > 3600) continue;
                _cdText[n] = left >= 10 ? ((int)Math.Ceiling(left)).ToString() : left.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                float w = *(short*)(slot + 0x2C), h = *(short*)(slot + 0x2E);
                // la posición de la casilla es relativa a su ventana madre (+0x50): se suman las de toda la cadena
                float ax = *(int*)(slot + 0x24), ay = *(int*)(slot + 0x28);
                for (nint p = *(nint*)(slot + 0x50), guard = 0; p != 0 && guard < 8; p = *(nint*)(p + 0x50), guard++)
                { ax += *(int*)(p + 0x24); ay += *(int*)(p + 0x28); }
                _cdX[n] = ax + w / 2; _cdY[n] = ay + h / 2; _cdH[n] = h;
                n++;
            }
            if (n > 0 && !_cdLogged)
            {
                _cdLogged = true;
                var sb = new System.Text.StringBuilder("D9: segundos de recarga sobre la barra; casillas (centro, alto):");
                for (int i = 0; i < n; i++) sb.Append($" ({_cdX[i]:0},{_cdY[i]:0},{_cdH[i]:0}) {_cdText[i]}s");
                Log.Info(sb.ToString());
            }
            return n;
        }
        catch { return 0; }
    }

    private static void Quad(float* v, int n, float x, float y, float u, float t, uint color)
    {
        float* p = v + n * 7;
        p[0] = x; p[1] = y; p[2] = 0; p[3] = 1; *(uint*)(p + 4) = color; p[5] = u; p[6] = t;
    }
}
