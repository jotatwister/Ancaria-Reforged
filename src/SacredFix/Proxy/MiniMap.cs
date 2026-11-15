using System;
using SacredFix.Core;

namespace SacredFix.Proxy;

/// <summary>
/// Minimapa siempre visible ([MiniMap] Enabled=1). El juego no tiene uno: se reutiliza la rutina que dibuja el mapa
/// del tabulador (cUI_OverviewMap, render 0x6C6C50), 0x6CCBD0, que es cdecl con 17 argumentos:
///   (device, viewport x, y, ancho, alto, minZ, maxZ, x del héroe, y del héroe,
///    1, 1, 0, 1, 1, 0, 1, alfa)
/// El viewport (D3DVIEWPORT7 copiado en la pila) es el rectángulo donde se dibuja: el tabulador usa 162,120 700x525;
/// aquí uno pequeño en la esquina. La posición del héroe: 0x60D6C0(-1) devuelve el personaje del jugador y
/// 0x6113C0 / 0x6113D0 (thiscall) su x / y. La transparencia del tabulador sale de las opciones (0x81B8A0 sobre
/// 0x182EE78); aquí es fija ([MiniMap] Alpha).
/// Se dibuja al terminar la interfaz (fuera del lienzo: coordenadas de pantalla), solo en la partida y cuando el
/// mapa del tabulador no está abierto.
/// </summary>
internal static unsafe class MiniMap
{
    public static bool Enabled { get; set; }
    private static int _x = 6, _y = 6, _size = 160, _alpha = 224;
    private static float _scale = 0.5f, _textScale = 0.8f;
    // Línea de texto en curso (letras consecutivas): ancla en x (comienzo) e y (centro), y borde derecho de la última letra
    private static float _runX, _runY, _runY0, _runY1, _runRight;
    private static bool _run;
    /// <summary>Dibujando el minimapa: D3DTrace transforma los vértices 2D con Transform.</summary>
    public static bool Active;
    private static float _srcX, _srcY, _dstX, _dstY;
    private static byte* _buf;
    private static int _bufSize;
    private static int _errors;
    private static bool _drawn;
    private static int _calls, _diag;

    /// <summary>Fin de fotograma (D3DTrace EndScene): se puede volver a dibujar.</summary>
    public static void OnEndScene() => _drawn = false;

    private static readonly delegate* unmanaged[Cdecl]<nint, int, int, int, int, float, float, int, int, int, int, int, int, int, int, int, int, void> DrawMap =
        (delegate* unmanaged[Cdecl]<nint, int, int, int, int, float, float, int, int, int, int, int, int, int, int, int, int, void>)0x6CCBD0;
    private static readonly delegate* unmanaged[Cdecl]<int, nint> GetPlayer = (delegate* unmanaged[Cdecl]<int, nint>)0x60D6C0;
    private static readonly delegate* unmanaged[Thiscall]<nint, int> GetX = (delegate* unmanaged[Thiscall]<nint, int>)0x6113C0;
    private static readonly delegate* unmanaged[Thiscall]<nint, int> GetY = (delegate* unmanaged[Thiscall]<nint, int>)0x6113D0;

    public static void Init(Config cfg)
    {
        Enabled = cfg.GetBool("MiniMap", "Enabled", false);
        _x = cfg.GetInt("MiniMap", "X", 6);
        _y = cfg.GetInt("MiniMap", "Y", 6);
        _size = Math.Clamp(cfg.GetInt("MiniMap", "Size", 120), 48, 600);
        _alpha = Math.Clamp(cfg.GetInt("MiniMap", "Alpha", 224), 0x60, 255);
        _scale = Math.Clamp(cfg.GetInt("MiniMap", "Zoom", 50), 10, 200) / 100f;
        _textScale = Math.Clamp(cfg.GetInt("MiniMap", "TextZoom", 80), 10, 200) / 100f;
        Log.Info($"MiniMap: {_size}x{_size} en {_x},{_y}, alfa {_alpha}");
    }

    /// <summary>Lo mismo que los buffos para dibujar otras cosas de la partida (Overlay: nivel y supervivencia).</summary>
    /// (Con el mapa del tabulador abierto, la ventana 6, también: ese mapa no tapa el retrato.)
    public static bool InGameView() { try { return ShouldDraw(tabMapOk: true) && GetPlayer(-1) != 0; } catch { return false; } }

    /// <summary>¿Partida en curso, sin ventana de pantalla completa ni el mapa del tabulador abierto?</summary>
    private static bool ShouldDraw(bool tabMapOk = false)
    {
        nint mgr = *(nint*)0x17EEBBC;
        if (mgr == 0) return false;
        uint flags = *(uint*)(mgr + 8);
        if ((flags & 0x04) == 0 || (flags & 0x10) != 0) return false; // en partida, sin escena cinemática
        // ventanas que tapan el mundo: mapa grande (5), mapa del tabulador (6), guardar (11), opciones (15), personaje (20)
        // (la 19, retratos del grupo, está siempre visible)
        foreach (int w in new[] { 5, 6, 11, 15, 20 })
        {
            if (tabMapOk && w == 6) continue;
            nint win = *(nint*)(mgr + 0x80 + w * 4);
            if (win != 0 && (*(uint*)(win + 0x10) & 1) != 0) return false;
        }
        return true;
    }

    // ---- Marco: el de los huecos redondos del arma y del escudo de la barra inferior ----
    // El juego lo dibuja como un cuadro de 64x64 con la textura del atlas de la barra (256x256) y uv
    // 0,32..0,58 x 0,66..0,91 en los dos huecos (diagnóstico UiDump, panoramica_72). Se captura esa textura cuando
    // el juego la usa (GetTexture de la etapa 0) y se reutiliza. El hueco interior ocupa ~48 de los 64 px.
    // Marco a escala entera del original (64 px -> 128) y sin suavizado: nítido. El mapa ocupa el hueco (96 px).
    private const float FrameScale = 128f / 96f;
    private static nint _frameTex;
    private static float _fu0 = 0.32f, _fu1 = 0.58f, _fv0 = 0.66f, _fv1 = 0.91f;
    public static bool NeedsFrame => false; // el marco ya no se copia del juego (64 px: borroso al ampliarlo); se dibuja

    /// <summary>D3DTrace, en cada cuadro 2D de la interfaz: ¿es el marco del arma? Entonces se guarda su textura.</summary>
    public static void CheckFrame(nint device, float* v, int stride, uint fvf)
    {
        int uvOff = 16 + ((fvf & 0x40) != 0 ? 4 : 0) + ((fvf & 0x80) != 0 ? 4 : 0);
        if (((fvf >> 8) & 0xF) == 0 || uvOff + 8 > stride) return;
        float x0 = float.MaxValue, x1 = float.MinValue, u0 = float.MaxValue, u1 = float.MinValue, t0 = float.MaxValue, t1 = float.MinValue;
        for (int i = 0; i < 4; i++)
        {
            float* p = (float*)((byte*)v + i * stride);
            float* uv = (float*)((byte*)p + uvOff);
            x0 = Math.Min(x0, p[0]); x1 = Math.Max(x1, p[0]);
            u0 = Math.Min(u0, uv[0]); u1 = Math.Max(u1, uv[0]); t0 = Math.Min(t0, uv[1]); t1 = Math.Max(t1, uv[1]);
        }
        if (Math.Abs(x1 - x0 - 64) > 1 || Math.Abs(u0 - 0.32f) > 0.02f || Math.Abs(t0 - 0.66f) > 0.02f || u1 - u0 < 0.2f) return;
        nint tex = 0;
        var getTex = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)device)[34];
        if (getTex(device, 0, &tex) != 0 || tex == 0) return;
        _frameTex = tex; // con la referencia que da GetTexture: se queda viva
        _fu0 = u0; _fu1 = u1; _fv0 = t0; _fv1 = t1;
        Log.Info($"MiniMap: marco capturado (textura 0x{tex:X}, uv {u0:0.###}..{u1:0.###} x {t0:0.###}..{t1:0.###})");
    }

    private static void DrawFrame(nint device, float x, float y, float size)
    {
        if (_frameTex == 0) return;
        float* q = stackalloc float[7 * 4]; // XYZRHW | DIFFUSE | TEX1
        void V(int i, float px, float py, float u, float t)
        {
            float* p = q + i * 7;
            p[0] = px - 0.5f; p[1] = py - 0.5f; p[2] = 0; p[3] = 1; *(uint*)(p + 4) = 0xFFFFFFFF; p[5] = u; p[6] = t;
        }
        V(0, x, y, _fu0, _fv0); V(1, x + size, y, _fu1, _fv0); V(2, x, y + size, _fu0, _fv1); V(3, x + size, y + size, _fu1, _fv1);
        var setTex = (delegate* unmanaged[Stdcall]<nint, uint, nint, int>)(*(nint**)device)[35];
        var draw = (delegate* unmanaged[Stdcall]<nint, int, uint, nint, uint, uint, int>)(*(nint**)device)[25];
        var getTss = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, int>)(*(nint**)device)[36];
        var setTss = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint, int>)(*(nint**)device)[37];
        uint mag = 0, min = 0;
        getTss(device, 0, 16, &mag); getTss(device, 0, 17, &min); // D3DTSS_MAGFILTER / MINFILTER
        setTss(device, 0, 16, 1); setTss(device, 0, 17, 1);       // D3DTFG_POINT / D3DTFN_POINT
        setTex(device, 0, _frameTex);
        draw(device, 5 /* TRIANGLESTRIP */, 0x144, (nint)q, 4, 0);
        setTss(device, 0, 16, mag); setTss(device, 0, 17, min);
    }

    // ---- Marco dibujado y recorte circular ----
    private const float Border = 10.5f; // ancho del aro (px)
    private const int Segments = 120;

    /// <summary>Estados de render (y de la etapa 0) cambiados, para dejarlos como estaban.</summary>
    internal struct States
    {
        private readonly nint _dev;
        private readonly uint[] _rs, _rsVal, _tss, _tssVal;
        private int _nRs, _nTss;
        public States(nint dev) { _dev = dev; _rs = new uint[24]; _rsVal = new uint[24]; _tss = new uint[8]; _tssVal = new uint[8]; _nRs = _nTss = 0; }
        public void Set(uint state, uint value)
        {
            bool known = false;
            for (int i = 0; i < _nRs; i++) if (_rs[i] == state) known = true;
            if (!known && _nRs < _rs.Length)
            {
                uint v = 0;
                ((delegate* unmanaged[Stdcall]<nint, uint, uint*, int>)(*(nint**)_dev)[21])(_dev, state, &v);
                _rs[_nRs] = state; _rsVal[_nRs] = v; _nRs++;
            }
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)(*(nint**)_dev)[20])(_dev, state, value);
        }
        public void Tss(uint type, uint value)
        {
            bool known = false;
            for (int i = 0; i < _nTss; i++) if (_tss[i] == type) known = true;
            if (!known && _nTss < _tss.Length)
            {
                uint v = 0;
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, int>)(*(nint**)_dev)[36])(_dev, 0, type, &v);
                _tss[_nTss] = type; _tssVal[_nTss] = v; _nTss++;
            }
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, int>)(*(nint**)_dev)[37])(_dev, 0, type, value);
        }
        private void Restore(uint state)
        {
            for (int i = 0; i < _nRs; i++)
                if (_rs[i] == state) ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)(*(nint**)_dev)[20])(_dev, state, _rsVal[i]);
        }
        public void RestoreBlend() { foreach (uint s in new uint[] { 27, 19, 20, 7, 14, 15, 22 }) Restore(s); }
        public void RestoreTss()
        {
            for (int i = 0; i < _nTss; i++) ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, int>)(*(nint**)_dev)[37])(_dev, 0, _tss[i], _tssVal[i]);
        }
        public void RestoreAll()
        {
            for (int i = 0; i < _nRs; i++) ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)(*(nint**)_dev)[20])(_dev, _rs[i], _rsVal[i]);
            RestoreTss();
        }
    }

    private static int _stencilBits = -1;
    internal static bool HasStencil(nint device)
    {
        if (_stencilBits < 0) { _stencilBits = 0; LogStencil(device); }
        return _stencilBits > 0;
    }

    /// <summary>Círculo relleno (abanico de triángulos, XYZRHW | DIFFUSE).</summary>
    internal static void Disc(nint device, float cx, float cy, float r, uint color)
    {
        float* v = stackalloc float[5 * (Segments + 2)];
        void P(int i, float x, float y) { float* p = v + i * 5; p[0] = x; p[1] = y; p[2] = 0; p[3] = 1; *(uint*)(p + 4) = color; }
        P(0, cx, cy);
        for (int i = 0; i <= Segments; i++)
        {
            float a = i * MathF.PI * 2 / Segments;
            P(i + 1, cx + MathF.Cos(a) * r, cy + MathF.Sin(a) * r);
        }
        ((delegate* unmanaged[Stdcall]<nint, int, uint, nint, uint, uint, int>)(*(nint**)device)[25])(device, 6 /* TRIANGLEFAN */, 0x44, (nint)v, (uint)(Segments + 2), 0);
    }

    // Aro metálico: paradas radiales (distancia al borde interior, color ARGB). La luz viene de arriba a la izquierda.
    private static readonly (float r, uint c)[] RingStops =
    {
        (-2.5f, 0x00000000), // sombra interior suave sobre el mapa
        (0f,    0x60000000),
        (0.01f, 0xFF4A3418), // filo interior (fino)
        (0.9f,  0xFF5A4020),
        (2f,    0xFFD0A862), // filo dorado
        (3.5f,  0xFFF2DC9C),
        (5f,    0xFFB08040), // cuerpo de bronce
        (7.5f,  0xFF8A6230),
        (8.6f,  0xFFC89C58), // filo exterior dorado
        (9.4f,  0xFF9A7038),
        (10.5f, 0x009A7038), // se funde con el fondo (sin borde negro)
    };

    private static uint Shade(uint c, float k)
    {
        uint a = c >> 24;
        uint r = (uint)Math.Clamp(((c >> 16) & 0xFF) * k, 0, 255), g = (uint)Math.Clamp(((c >> 8) & 0xFF) * k, 0, 255), b = (uint)Math.Clamp((c & 0xFF) * k, 0, 255);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    /// <summary>Aro metálico; scale estrecha las bandas (1 = el del minimapa, 10,5 px).</summary>
    internal static void Ring(nint device, float cx, float cy, float r0, float scale = 1f)
    {
        float* v = stackalloc float[5 * 2 * (Segments + 1)];
        var draw = (delegate* unmanaged[Stdcall]<nint, int, uint, nint, uint, uint, int>)(*(nint**)device)[25];
        for (int s = 0; s + 1 < RingStops.Length; s++)
        {
            var (ra, ca) = RingStops[s];
            var (rb, cb) = RingStops[s + 1];
            for (int i = 0; i <= Segments; i++)
            {
                float a = i * MathF.PI * 2 / Segments;
                float cos = MathF.Cos(a), sin = MathF.Sin(a);
                // luz desde arriba a la izquierda (225°, con y hacia abajo)
                float k = 0.78f + 0.38f * MathF.Cos(a - 1.25f * MathF.PI);
                float* p = v + (i * 2) * 5;
                p[0] = cx + cos * (r0 + ra * scale); p[1] = cy + sin * (r0 + ra * scale); p[2] = 0; p[3] = 1; *(uint*)(p + 4) = s == 0 ? ca : Shade(ca, k);
                p += 5;
                p[0] = cx + cos * (r0 + rb * scale); p[1] = cy + sin * (r0 + rb * scale); p[2] = 0; p[3] = 1; *(uint*)(p + 4) = s == 0 ? cb : Shade(cb, k);
            }
            draw(device, 5 /* TRIANGLESTRIP */, 0x44, (nint)v, (uint)(2 * (Segments + 1)), 0);
        }
    }

    private static bool _windowsLogged;
    /// <summary>Una vez: las ventanas de la partida (cUI_Manager.windows): clase (RTTI), tabla, render, posición.</summary>
    private static void LogWindows()
    {
        try
        {
            nint mgr = *(nint*)0x17EEBBC;
            var sb = new System.Text.StringBuilder("MiniMap: ventanas de la partida:");
            for (int w = 0; w < 23; w++)
            {
                nint win = *(nint*)(mgr + 0x80 + w * 4);
                if (win == 0) continue;
                nint vt = *(nint*)win;
                string name = "?";
                try
                {
                    nint col = *(nint*)(vt - 4), td = *(nint*)(col + 12);
                    name = new string((sbyte*)(td + 8));
                }
                catch { }
                sb.Append($"\n    [{w}] 0x{win:X} {name} vtable 0x{vt:X} render 0x{*(nint*)(vt + 0x14):X} x={*(int*)(win + 0x24)} y={*(int*)(win + 0x28)} {*(short*)(win + 0x2C)}x{*(short*)(win + 0x2E)} flags 0x{*(uint*)(win + 0x10):X} padre 0x{*(nint*)(win + 0x50):X}");
            }
            Log.Info(sb.ToString());
        }
        catch (Exception ex) { Log.Error("MiniMap: " + ex.Message); }
    }

    private static bool _stencilLogged;
    /// <summary>Una vez: ¿tiene canal de plantilla el búfer de profundidad? (para recortar el mapa en círculo)</summary>
    private static void LogStencil(nint device)
    {
        if (_stencilLogged) return;
        _stencilLogged = true;
        try
        {
            nint rt = 0;
            ((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)device)[9])(device, &rt); // GetRenderTarget
            if (rt == 0) return;
            uint* caps = stackalloc uint[4]; caps[0] = 0x20000; caps[1] = caps[2] = caps[3] = 0; // DDSCAPS_ZBUFFER
            nint z = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)(*(nint**)rt)[12])(rt, caps, &z);
            if (z != 0)
            {
                byte* d = stackalloc byte[124];
                new Span<byte>(d, 124).Clear(); *(int*)d = 124;
                ((delegate* unmanaged[Stdcall]<nint, byte*, int>)(*(nint**)z)[22])(z, d);
                Log.Info($"MiniMap: profundidad {*(uint*)(d + 72 + 12)} bits, plantilla {*(uint*)(d + 72 + 16)} bits (flags 0x{*(uint*)(d + 72 + 4):X})");
                _stencilBits = (int)*(uint*)(d + 72 + 16);
                ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)z)[2])(z);
            }
            else Log.Info("MiniMap: sin búfer de profundidad");
            ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)rt)[2])(rt);
        }
        catch (Exception ex) { Log.Error("MiniMap: " + ex.Message); }
    }

    /// <summary>Copia de los vértices XYZRHW con x, y llevados al minimapa (centro y escala).</summary>
    public static nint Transform(nint verts, uint count, int stride)
    {
        int bytes = (int)count * stride;
        if (verts == 0 || bytes <= 0 || stride < 8) return verts;
        if (bytes > _bufSize)
        {
            if (_buf != null) System.Runtime.InteropServices.NativeMemory.Free(_buf);
            _bufSize = Math.Max(bytes, 64 * 1024);
            _buf = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)_bufSize);
        }
        Buffer.MemoryCopy((void*)verts, _buf, _bufSize, bytes);
        if (count == 4 && _textScale != _scale && SmallQuad(_buf, stride, out float x0, out float x1, out float y0, out float y1))
        {
            // Letra o símbolo: posición con la escala del mapa (desde el comienzo de su línea) y tamaño con la del
            // texto. Las letras seguidas de una misma línea comparten ancla, así no se montan unas sobre otras.
            bool same = _run && Math.Abs(y0 - _runY0) <= 2 && Math.Abs(y1 - _runY1) <= 2 && x0 >= _runRight - 6 && x0 <= _runRight + 16;
            if (!same) { _run = true; _runX = x0; _runY = (y0 + y1) / 2; _runY0 = y0; _runY1 = y1; }
            _runRight = x1;
            float ax = _dstX + (_runX - _srcX) * _scale, ay = _dstY + (_runY - _srcY) * _scale;
            for (uint i = 0; i < count; i++)
            {
                float* p = (float*)(_buf + i * stride);
                p[0] = ax + (p[0] - _runX) * _textScale;
                p[1] = ay + (p[1] - _runY) * _textScale;
            }
            return (nint)_buf;
        }
        _run = false;
        for (uint i = 0; i < count; i++)
        {
            float* p = (float*)(_buf + i * stride);
            p[0] = _dstX + (p[0] - _srcX) * _scale;
            p[1] = _dstY + (p[1] - _srcY) * _scale;
        }
        return (nint)_buf;
    }

    /// <summary>¿Cuadro pequeño (≤ 40x40: letra o símbolo del mapa)?</summary>
    private static bool SmallQuad(byte* v, int stride, out float x0, out float x1, out float y0, out float y1)
    {
        x0 = y0 = float.MaxValue; x1 = y1 = float.MinValue;
        for (int i = 0; i < 4; i++)
        {
            float* p = (float*)(v + i * stride);
            x0 = Math.Min(x0, p[0]); x1 = Math.Max(x1, p[0]); y0 = Math.Min(y0, p[1]); y1 = Math.Max(y1, p[1]);
        }
        return x1 - x0 <= 40 && y1 - y0 <= 40;
    }

    /// <summary>Al terminar de dibujar la interfaz (UiCanvas, fuera del lienzo).</summary>
    /// <summary>
    /// Buffs junto al retrato: al EMPEZAR la interfaz (UiCanvas), para que las ventanas y las ayudas del juego
    /// («Current Health», etc.) queden por encima. Viewport de toda la pantalla y coordenadas de pantalla.
    /// </summary>
    public static void DrawBuffs(nint device)
    {
        if (!Enabled || device == 0) return;
        try
        {
            if (!ShouldDraw() || GetPlayer(-1) == 0) return;
            int* saved = stackalloc int[6];
            var getVp = (delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)device)[15];
            var setVp = (delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)device)[13];
            if (getVp(device, saved) != 0) return;
            int* full = stackalloc int[6];
            full[0] = 0; full[1] = 0; full[2] = UiCanvas.Left * 2 + 1024; full[3] = 768; full[4] = 0; *(float*)(full + 5) = 1f;
            setVp(device, full);
            try { BuffBar.Draw(device, UiCanvas.Left * 2 + 1024); }
            finally { setVp(device, saved); }
        }
        catch (Exception ex) { if (_errors++ < 3) Log.Error("BuffBar: " + ex.Message); }
    }

    public static void Draw(nint device)
    {
        if (device == 0 || _drawn) return;
        if (!Enabled)
        {
            // sin minimapa, lo que va colgado de su dibujo sigue funcionando: lanzar con una tecla y el diagnóstico
            _drawn = true;
            try
            {
                KeyInput.EnsureInstalled();
                nint mgr = *(nint*)0x17EEBBC;
                if (DebugScan.Pending && mgr != 0 && (*(uint*)(mgr + 8) & 4) != 0)
                { nint om = *(nint*)0xAD5C40; if (om != 0) DebugScan.Run(((delegate* unmanaged[Thiscall]<nint, nint>)0x603E30)(om)); }
            }
            catch { }
            return;
        }
        _drawn = true;
        if (++_calls % 300 == 1 && _diag++ < 20)
        {
            nint mgr = *(nint*)0x17EEBBC;
            uint fl = mgr != 0 ? *(uint*)(mgr + 8) : 0;
            var vis = new System.Text.StringBuilder();
            if (mgr != 0) for (int w = 0; w < 23; w++) { nint win = *(nint*)(mgr + 0x80 + w * 4); if (win != 0 && (*(uint*)(win + 0x10) & 1) != 0) vis.Append($" {w}"); }
            nint h = 0; int hx = 0, hy = 0;
            try { if ((fl & 4) != 0) { h = GetPlayer(-1); if (h != 0) { hx = GetX(h); hy = GetY(h); } } } catch { }
            Log.Info($"MiniMap: llamada {_calls}, flags 0x{fl:X}, ventanas visibles:{vis}, dibujar {ShouldDraw()}, héroe 0x{h:X} ({hx},{hy})");
        }
        try
        {
            KeyInput.EnsureInstalled();
            if (!ShouldDraw()) return;
            if (!_windowsLogged) { _windowsLogged = true; LogWindows(); }
            nint hero = GetPlayer(-1);
            if (hero == 0) return;
            int hx = GetX(hero), hy = GetY(hero);
            if (DebugScan.Pending) { nint om = *(nint*)0xAD5C40; if (om != 0) DebugScan.Run(((delegate* unmanaged[Thiscall]<nint, nint>)0x603E30)(om)); }
            int* saved = stackalloc int[6];
            var getVp = (delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)device)[15];
            var setVp = (delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)device)[13];
            bool ok = getVp(device, saved) == 0;
            // La rutina dibuja el mapa encima del mundo, centrado en la posición de pantalla del héroe (centro de la
            // pantalla); el viewport solo recorta. Se trasladan los vértices 2D al centro del minimapa y se reducen.
            _srcX = 512; _srcY = 384; // coordenadas del lienzo de 1024x768 (el mapa del tabulador está centrado en 512,382)
            // X, Y es la esquina del marco; el mapa va en su hueco circular, centrado
            float frame = _size + 2 * Border;
            _dstX = _x + frame / 2f; _dstY = _y + frame / 2f;
            int mx = (int)(_dstX - _size / 2f), my = (int)(_dstY - _size / 2f);
            int* full = stackalloc int[6];
            full[0] = 0; full[1] = 0; full[2] = UiCanvas.Left * 2 + 1024; full[3] = 768; full[4] = 0; *(float*)(full + 5) = 1f;
            setVp(device, full);
            var rs = new States(device);
            // 1) Círculo en la plantilla (sin pintar: mezcla ZERO / ONE) -> el mapa solo se dibuja dentro
            bool stencil = HasStencil(device);
            if (stencil)
            {
                int* rc = stackalloc int[4];
                rc[0] = mx; rc[1] = my; rc[2] = mx + _size; rc[3] = my + _size;
                ((delegate* unmanaged[Stdcall]<nint, uint, int*, uint, uint, float, uint, int>)(*(nint**)device)[10])(device, 1, rc, 4 /* STENCIL */, 0, 1f, 0);
                rs.Set(52, 1); rs.Set(56, 8 /* ALWAYS */); rs.Set(57, 1); rs.Set(58, 0xFF); rs.Set(59, 0xFF);
                rs.Set(53, 1); rs.Set(54, 1); rs.Set(55, 3 /* REPLACE */);
                rs.Set(27, 1); rs.Set(19, 1 /* ZERO */); rs.Set(20, 2 /* ONE */); rs.Set(7, 0); rs.Set(14, 0); rs.Set(15, 0); rs.Set(22, 1 /* CULL_NONE */);
                rs.Tss(1, 3); rs.Tss(4, 3); // COLOROP / ALPHAOP = SELECTARG2 (difuso): sin textura
                Disc(device, _dstX, _dstY, _size / 2f, 0xFFFFFFFF);
                rs.RestoreBlend();
                rs.Set(56, 3 /* EQUAL */); rs.Set(55, 1 /* KEEP */);
                rs.RestoreTss();
            }
            Active = true;
            try { DrawMap(device, mx, my, _size, _size, 0f, 1f, hx, hy, 1, 1, 0, 1, 1, 0, 1, _alpha); }
            finally { Active = false; }
            setVp(device, full);
            rs.Set(52, 0); // sin plantilla para el marco
            // 2) Marco: aro metálico dibujado a la resolución de la pantalla (nítido a cualquier tamaño)
            rs.Set(27, 1); rs.Set(19, 5 /* SRCALPHA */); rs.Set(20, 6 /* INVSRCALPHA */); rs.Set(7, 0); rs.Set(14, 0); rs.Set(15, 0); rs.Set(22, 1 /* CULL_NONE */);
            rs.Tss(1, 3); rs.Tss(4, 3);
            Ring(device, _dstX, _dstY, _size / 2f);
            rs.RestoreAll();
            if (ok) setVp(device, saved);
        }
        catch (Exception ex) { if (_errors++ < 3) Log.Error("MiniMap: " + ex.Message); }
    }
}
