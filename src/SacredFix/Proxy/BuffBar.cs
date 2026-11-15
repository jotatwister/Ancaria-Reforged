using System;
using System.Collections.Generic;
using SacredFix.Core;

namespace SacredFix.Proxy;

/// <summary>
/// Buffs activos junto al retrato ([Buffs] Enabled=1). El juego no los muestra: se leen de las artes de combate del
/// héroe y se dibujan como iconos a la izquierda del retrato, con un velo que cubre el icono según pasa el tiempo.
///   - Héroe: cObjectManager::hero (0x603E30, thiscall) sobre [0xAD5C40].
///   - Artes aprendidas: vector [héroe + 0x4AA .. + 0x4AE] (= héroe + 0x3A8 + 0x102/0x106, como la barra), 0x16
///     bytes cada una: id (short) en +4, duración total (float) en +0xA, tiempo restante (float) en +0x12.
///   - Definición del arte (tabla fija 0x94FE28, 0x78 bytes, id en +0; la usa 0x4D1AF0): textura del icono (nombre)
///     en +0x0C.
///   - Textura: gestor 0x6569C0() (cdecl), id = 0x657360(gestor, nombre) (thiscall, 0 si no está registrada),
///     objeto = 0x65ED90([0x13E7838], id, 0) (thiscall), textura D3D en objeto + 0x14.
/// Se considera buff un arte con tiempo restante y una duración total de 5 s o más (las de ataque solo tienen un
/// temporizador corto de lanzamiento).
/// </summary>
internal static unsafe class BuffBar
{
    public static bool Enabled { get; set; }
    /// <summary>Pruebas desde el menú F10 (se hacen en el siguiente fotograma, en el hilo del juego).</summary>
    public static volatile bool ClearBuffs;
    public static void SetSize(int s) => _size = Math.Clamp(s, 16, 96);
    private static int _size = 32, _gap = 10, _top = 14, _right = 104;
    private static float _crop = 0.12f;
    // Buffs en curso por arte: momento del lanzamiento (ms) y duración (s); fin del último (ms)
    private static readonly Dictionary<short, (long start, float dur)> _active = new();
    private static readonly Dictionary<short, long> _ended = new();
    private static readonly Dictionary<short, float> _prevLeft = new();
    private static int _candLogs, _selLogs;
    /// <summary>Hechizos de ataque o control que el juego describe con duración y sí llevan icono (confirmados en el juego):
    /// Serafín: Rotating Blades of Light 17, Irritation 18, Conversion 19.</summary>
    private static readonly HashSet<short> _timedControl = new() { 17, 18, 19 };
    private static readonly Dictionary<short, (int b, int i)> _artFixed = new() { [1084] = (39, 1), [1089] = (26, 1) };
    private static long _gameMs, _lastReal;

    /// <summary>Invocaciones: hechizos de categoría 3, artes de categoría 3 (invocar) y 4 (resucitar), murciélagos.</summary>
    private static bool IsSummon(short id)
    {
        nint def = Def(id);
        if (def == 0) return false;
        // Hechizos de categoría 3 («invocación o zona»): solo son invocaciones los que no tienen duración; las zonas con
        // tiempo fijo (Purgatory, 25 s) sí llevan icono
        if (id < 1000) return (*(int*)(def + 0x3C) & 0xFFFF) == 3 && SpellDuration(def, 1) <= 0;
        int cat = *(int*)(def + 0x4C);
        return cat == 3 || cat == 4 || (id >= 1035 && id <= 1037);
    }

    /// <summary>Duración de un hechizo como la calcula su ayuda (0x4D26B0): def[+0x76 + 4k] + def[+0x78 + 4k]·nivel, con tope; 0 si no tiene.</summary>
    private static int SpellDuration(nint def, int lvl)
    {
        for (int k = 0; k < 2; k++)
        {
            int b = *(short*)(def + 0x76 + 4 * k), inc = *(short*)(def + 0x78 + 4 * k), cap = *(ushort*)(def + 0x82 + 2 * k);
            if (inc < 0 || inc > 5 || b < 0 || (inc == 0 && (b <= 0 || b > 120))) continue;
            int d = b + inc * lvl;
            if (cap > 0 && cap < d) d = cap;
            if (d > 0) return d;
        }
        return 0;
    }

    /// <summary>
    /// Arte seleccionado en la barra (el que lanza el botón derecho). Barra = cUI_Manager.windows[0]; jugador =
    /// [barra + 0x158]; su inventario = [0xAD4448] + jugador · 0x1180 (0x5DEBC0 / 0x5DEC90). Ranura seleccionada:
    /// word [inv + 0xD58 + 2] (0x5D9BA0(1)); cada ranura, 2 variantes de 0x14 bytes (la activa: bit 1 de [inv + 0x1162]):
    /// tipo en inv + 0xC90 + (variante + 2·ranura)·0x14 (0x5D9420), id en inv + (variante + 0xA1 + 2·ranura)·0x14 (0x5D9460).
    /// </summary>
    private static int SelectedArt(out int type)
    {
        type = 0;
        try
        {
            nint mgr = *(nint*)0x17EEBBC, invs = *(nint*)0xAD4448;
            if (mgr == 0 || invs == 0) return 0;
            nint bar = *(nint*)(mgr + 0x80);
            if (bar == 0) return 0;
            int player = *(ushort*)(bar + 0x158);
            if (player >= 0x20) return 0;
            nint inv = invs + player * 0x1180;
            int alt = (*(byte*)(inv + 0x1162) >> 1) & 1;
            int slot = *(ushort*)(inv + 0xD58 + 2);
            if (slot > 16) return 0;
            type = *(short*)(inv + 0xC90 + (alt + slot * 2) * 0x14);
            return *(int*)(inv + (alt + 0xA1 + slot * 2) * 0x14);
        }
        catch { return 0; }
    }
    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// Duración del efecto según la definición del arte (artes de combate: +0x50/+0x54 si es un tiempo; si no,
    /// +0x58/+0x5C; si no, la recarga +0x40/+0x44, que en algunas auras coincide con su efecto). «Turn into Vampire» (1022): 18 + 12·1 = 30 s, igual que lo medido (la vida máxima vuelve a los
    /// 29,7-30 s). Las artes de ataque no tienen incremento por nivel: 0 = no es un buff.
    /// </summary>
    private static float Duration(short id, int level)
    {
        if (_hide.Contains(id)) return 0;
        // Invocaciones fuera (no se sabe detectar cuándo mueren sus criaturas); [Buffs] Show= para mostrar alguna
        if (!_show.Contains(id) && IsSummon(id)) return 0;
        nint def = Def(id);
        if (def == 0) return 0;
        int lvl = Math.Max(1, level);
        if (id < 1000)
        {
            // Hechizo (tabla 0x952ACC). Categoría (+0x3C, word bajo): 0 ataque, 1 control de enemigos, 2 refuerzo,
            // 3 invocación o zona, 5 formas y auras. Se muestran 2, 3 y 5, los de _timedControl (control con duración)
            // y los de [Buffs] Show. Whirlwind 28 dura 44 + 1·nivel.
            int cat = *(int*)(def + 0x3C) & 0xFFFF;
            if (!_show.Contains(id) && !_timedControl.Contains(id) && !(cat == 2 || cat == 3 || cat == 5)) return 0;
            if (_permanent.Contains(id)) return float.PositiveInfinity;
            // Duración como la ayuda del juego (0x4D26B0, thiscall(def, k, nivel)): def[+0x76 + 4k] + def[+0x78 + 4k] · nivel,
            // con tope def[+0x82 + 2k] si no es 0. De los dos parámetros (k = 0, 1), el de la duración crece poco por
            // nivel (1-5 s) o es fijo (incremento 0); los otros son daño, curación o número de criaturas.
            if (!(cat == 3 && *(short*)(def + 0x76) == 0)) // invocación con el parámetro 0 a 0 (Companion of the Woods): permanente
                for (int k = 0; k < 2; k++)
                {
                    int b = *(short*)(def + 0x76 + 4 * k), inc = *(short*)(def + 0x78 + 4 * k), cap = *(ushort*)(def + 0x82 + 2 * k);
                    if (inc < 0 || inc > 5 || b < 0 || (inc == 0 && (b <= 0 || b > 120))) continue;
                    int d = b + inc * lvl;
                    if (cap > 0 && cap < d) d = cap;
                    if (d > 0) return d;
                }
            // Sin duración: las invocaciones duran hasta que mueren; lo demás (curaciones) no es un buff
            return cat == 3 ? float.PositiveInfinity : 0;
        }
        // Arte de combate (tabla 0x94FE28): categoría +0x4C: 0 especial (transformaciones, pero también trampas y
        // ataques), 1 ataque, 2 refuerzo, 3 invocación, 4 resucitar (invoca muertos), 5 aura. Se muestran 2-5 y, de la 0,
        // solo las transformaciones «Turn into Vampire» (1022) y «Flip-flop» (1021).
        // Artes sin campo de duración reconocible, cuadradas con su ayuda en el juego (nivel 5): Battle Rage 1084 44 s
        // (39 + 1·nivel, es de categoría 1), War Cry 1089 31 s (26 + 1·nivel, supuesto 1 s por nivel como las auras)
        if (!_hide.Contains(id) && _artFixed.TryGetValue(id, out var fx)) return fx.b + fx.i * lvl;
        int catA = *(int*)(def + 0x4C);
        bool bats = id >= 1035 && id <= 1037; // Bats (Blood Swarm, Confusion, Guard): invocan murciélagos (categoría 0)
        bool show = _show.Contains(id) || catA == 2 || catA == 3 || catA == 4 || catA == 5 || id == 1022 || id == 1021 || bats;
        if (!show) return 0;
        if (_permanent.Contains(id) || bats) return float.PositiveInfinity;
        // Duración: el parámetro +0x50 + 0x54·nivel cuando es un tiempo (crece pocos segundos por nivel): Turn into
        // Vampire 24 + 6·nivel = 30 s a nivel 1 (medido), Pak-Dain 44 + 1, Battle Rage 39 + 1...
        float pb = *(float*)(def + 0x50), pi = *(float*)(def + 0x54);
        if (pb >= 5 && pi >= 0.9f && pi <= 6) return pb + pi * lvl;
        // Si no, la duración está en +0x58 + 0x5C·nivel (guerrero: Awe 3 + 0,35·nivel = ~5 s a nivel 5, como su ayuda;
        // Heroic Courage y Dagger Stare 39,5 + 0,5·nivel). Antes se caía a la recarga (Awe daba 40 s).
        float db = *(float*)(def + 0x58), di = *(float*)(def + 0x5C);
        if (db >= 2 && db <= 600 && di > 0 && di <= 2) return db + di * lvl;
        // O en +0x60 + 0x64·nivel, enteros (Testosterone y Adrenaline: 29 + 1·nivel = 34 s a nivel 5, como su ayuda)
        int ib = *(int*)(def + 0x60), ii = *(int*)(def + 0x64);
        if (ib >= 2 && ib <= 600 && ii > 0 && ii <= 5) return ib + ii * lvl;
        // Invocaciones (Mongoose, Wolf Call): hasta que mueren
        if (catA == 3) return float.PositiveInfinity;
        // Auras sin dato propio: 39 + 1·nivel (Soul Catcher: 44 s a nivel 5, como su ayuda; Battle Rage lleva 39 + 1 en su
        // parámetro). Solo llevan icono las artes con duración: sin dato, no se usa la recarga (Flip-flop daba 255 s).
        if (catA == 5) return 39 + lvl;
        return 0;
    }

    /// <summary>Definición fija: arte (id >= 1000, tabla 0x94FE28) o hechizo (tabla 0x952ACC, 0x94 bytes, 0x4D2310).</summary>
    private static nint Def(short id)
    {
        if (id >= 1000) return ArtDef(id);
        for (nint p = 0x952ACC; p < 0x955344; p += 0x94)
            if (*(short*)p == id) return p;
        return 0;
    }

    private static readonly HashSet<short> _show = new(), _hide = new();
    // Sin duración: hasta que muera el héroe (Companion of the Woods; [Buffs] Permanent= para añadir más)
    private static readonly HashSet<short> _permanent = new();
    private static int _variant = 2;

    private static void ParseIds(string list, HashSet<short> into)
    {
        foreach (var part in list.Split(',', ';', ' '))
            if (short.TryParse(part.Trim(), out short v)) into.Add(v);
    }
    private static readonly HashSet<short> _logged = new();

    public static void Init(Config cfg)
    {
        Enabled = cfg.GetBool("Buffs", "Enabled", true);
        _size = Math.Clamp(cfg.GetInt("Buffs", "Size", 32), 16, 96);
        _crop = Math.Clamp(cfg.GetInt("Buffs", "Crop", 12), 0, 40) / 100f;
        _top = cfg.GetInt("Buffs", "Y", 14);
        _right = cfg.GetInt("Buffs", "Right", 104); // distancia desde el borde derecho de la pantalla al primer icono
        _variant = Math.Clamp(cfg.GetInt("Buffs", "Variant", 2), 0, 2);
        ParseIds(cfg.GetString("Buffs", "Show", ""), _show);
        ParseIds(cfg.GetString("Buffs", "Hide", ""), _hide);
        ParseIds(cfg.GetString("Buffs", "Permanent", ""), _permanent);
    }

    private static nint Hero()
    {
        nint om = *(nint*)0xAD5C40;
        return om != 0 ? ((delegate* unmanaged[Thiscall]<nint, nint>)0x603E30)(om) : 0;
    }

    /// <summary>Definición fija del arte (0x78 bytes) o 0.</summary>
    private static nint ArtDef(short id)
    {
        for (nint p = 0x94FE28; p < 0x952A38; p += 0x78)
            if (*(short*)p == id) return p;
        return 0;
    }

    private static bool _listed;

    /// <summary>Texto del juego por su número: 0x672C90 (thiscall sobre 0x182ED50) devuelve un objeto cuyo primer
    /// campo es el wchar_t* del texto.</summary>
    internal static string Text(int id, int max = 80)
    {
        nint r = ((delegate* unmanaged[Thiscall]<nint, int, nint>)0x672C90)(0x182ED50, id);
        if (r == 0) return "?";
        nint w = *(nint*)r;
        if (w == 0) return "?";
        char* c = (char*)w;
        int n = 0;
        while (n < max && c[n] != 0) n++;
        return new string(c, 0, n);
    }

    /// <summary>Para el log: la descripción de las artes que pueden ser buffs, en una línea.</summary>
    private static string Desc(nint p)
    {
        int cat = *(int*)(p + 0x4C); short id = *(short*)p;
        if (!(cat >= 2 && cat <= 5) && id != 1021 && id != 1022) return "";
        return Text(*(int*)(p + 8), 2000).Replace("\n", " ").Replace("\r", " ");
    }

    /// <summary>¿La descripción del arte (texto +0x08) indica una duración? Solo esas llevan icono.</summary>
    private static bool HasDuration(nint def)
    {
        string d = Text(*(int*)(def + 8), 2000);
        return d.Contains("uration", StringComparison.OrdinalIgnoreCase) || d.Contains("Dauer", StringComparison.OrdinalIgnoreCase)
            || d.Contains("Duraci", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Una vez, en la partida: tabla de todas las artes (para revisar categorías y duraciones).</summary>
    private static void ListArts()
    {
        var sb = new System.Text.StringBuilder("BuffBar: artes de combate (id | personaje (máscara +0x70) | categoría +0x4C | recarga +0x40+0x44·nivel | parámetro +0x50+0x54·nivel | nombre | icono):");
        for (nint p = 0x94FE28; p < 0x952A38; p += 0x78)
        {
            sb.Append($"\n    {*(short*)p,5} | 0x{*(int*)(p + 0x70):X7} | cat {*(int*)(p + 0x4C)} | {*(float*)(p + 0x40):0.##}+{*(float*)(p + 0x44):0.##} | {*(float*)(p + 0x50):0.##}+{*(float*)(p + 0x54):0.##} | {Text(*(int*)(p + 4))} | {new string((sbyte*)(p + 0x0C))} | +0x60 {*(int*)(p + 0x60)}+{*(int*)(p + 0x64)} | {Desc(p)}");
        }
        Log.Info(sb.ToString());
        sb.Clear();
        sb.Append("BuffBar: hechizos (tabla 0x952ACC, 0x94 bytes; id | máscara +0x8C | +0x3C | +0x40 | nombre | icono):");
        for (nint p = 0x952ACC; p < 0x955344; p += 0x94)
            sb.Append($"#    {*(short*)p,5} | 0x{*(int*)(p + 0x8C):X7} | 0x{*(int*)(p + 0x3C):X} | {*(int*)(p + 0x40)} | {Text(*(int*)(p + 4))} | {new string((sbyte*)(p + 0x0C))}");
        Log.Info(sb.ToString().Replace("#    ", Environment.NewLine + "    "));
    }

    /// <summary>Textura registrada en el gestor del juego por su nombre (0 si no está cargada).</summary>
    private static nint Texture(nint name)
    {
        nint mgr = ((delegate* unmanaged[Cdecl]<nint>)0x6569C0)();
        if (mgr == 0) return 0;
        int tid = ((delegate* unmanaged[Thiscall]<nint, nint, int>)0x657360)(mgr, name);
        if (tid == 0) return 0;
        nint obj = ((delegate* unmanaged[Thiscall]<nint, int, int, nint>)0x65ED90)(*(nint*)0x13E7838, tid, 0);
        return obj != 0 ? *(nint*)(obj + 0x14) : 0;
    }

    /// <summary>
    /// Icono del arte o hechizo: el dibujo «normal» de su casilla de la barra (id de dibujo en +0x30 de la definición,
    /// el de color, como cuando está disponible). Tabla de dibujos de la interfaz en 0x9ECED8, 0x54 bytes por id:
    /// rectángulo en píxeles x0,y0,x1,y1 (floats +0x04..+0x10) dentro de la textura cuyo nombre está en +0x34
    /// (p. ej. GUI_spell18.TGA, cuadros de 64x64). Si no está, el icono suelto de +0x0C (GUI_MOVE_*.TGA).
    /// Sin caché: tras Alt+Tab el juego recrea las texturas.
    /// </summary>
    private static nint Icon(short id, out float u0, out float v0, out float u1, out float v1)
    {
        u0 = v0 = 0; u1 = v1 = 1;
        nint def = Def(id);
        if (def == 0) return 0;
        int sid = *(int*)(def + 0x30 + _variant * 4); // +0x30 gris, +0x34 y +0x38: las otras dos variantes
        // Los tres dibujos de un arte suelen estar en la misma imagen (columna con las tres variantes). Si el elegido
        // está en otra imagen que el gris, es el dibujo de otra arte (los murciélagos de la vampiresa: 723, 724 en
        // GUI_spell16 y 725 en GUI_spell17): se usa la variante intermedia si está con el gris, si no el gris.
        int sid0 = *(int*)(def + 0x30), sid1 = *(int*)(def + 0x34);
        static string SpriteTex(int i) => i > 0 && i < 0x800 ? new string((sbyte*)(0x9ECED8 + i * 0x54 + 0x34)) : "";
        if (sid != sid0 && SpriteTex(sid) != SpriteTex(sid0))
            sid = SpriteTex(sid1) == SpriteTex(sid0) ? sid1 : sid0;
        nint t = 0;
        if (sid > 0 && sid < 0x800)
        {
            nint spr = 0x9ECED8 + sid * 0x54;
            t = Texture(spr + 0x34);
            if (t != 0)
            {
                // En el juego en marcha la tabla ya guarda coordenadas normalizadas (0..1); en el ejecutable, píxeles de
                // una textura de 256x256. Se aceptan las dos.
                float x0 = *(float*)(spr + 4), y0 = *(float*)(spr + 8), x1 = *(float*)(spr + 0xC), y1 = *(float*)(spr + 0x10);
                if (x1 > 1.01f || y1 > 1.01f) { x0 /= 256f; y0 /= 256f; x1 = (x1 + 1) / 256f; y1 = (y1 + 1) / 256f; }
                u0 = x0; v0 = y0; u1 = x1; v1 = y1;
            }
        }
        if (t == 0) t = Texture(def + 0x0C);
        if (_logged.Add(id)) Log.Info($"BuffBar: {id}: dibujo {sid} ({(sid > 0 ? new string((sbyte*)(0x9ECED8 + sid * 0x54 + 0x34)) : "-")} uv {u0:0.###},{v0:0.###}-{u1:0.###},{v1:0.###}) -> textura 0x{t:X}");
        return t;
    }

    /// <summary>Dibuja los buffs activos (pantalla completa, al final de la interfaz; lo llama MiniMap.Draw).</summary>
    public static void Draw(nint device, int screenW)
    {
        if (!Enabled) return;
        if (!_listed) { _listed = true; try { ListArts(); } catch (Exception ex) { Log.Error("BuffBar: lista: " + ex.Message); } }
        nint hero = Hero();
        if (hero == 0) return;
        nint st = *(nint*)(hero + 0x4AA), en = *(nint*)(hero + 0x4AE);
        if (st == 0 || en <= st || en - st > 0x16 * 64) return;
        int n = (int)((en - st) / 0x16), shown = 0;
        var rs = new MiniMap.States(device);
        rs.Set(27, 1); rs.Set(19, 5); rs.Set(20, 6); rs.Set(7, 0); rs.Set(14, 0); rs.Set(15, 0); rs.Set(22, 1);
        // Tiempo de juego: solo avanza mientras se dibujan fotogramas (cada uno suma lo transcurrido, como mucho
        // 0,1 s). Con el juego minimizado o en pausa no se dibuja y los buffs no se «gastan».
        long real = _clock.ElapsedMilliseconds;
        _gameMs += Math.Clamp(real - _lastReal, 0, 100);
        _lastReal = real;
        long now = _gameMs;
        // El héroe ha muerto: fuera todos (los permanentes, como el compañero, solo acaban así)
        if (*(int*)(hero + 0x4D8) <= 0 && _active.Count > 0) { foreach (var k in _active.Keys) _ended[k] = now; _active.Clear(); }
        // Pruebas (menú F10): quitar los iconos de los buffs (el efecto lo lleva el juego por dentro)
        if (ClearBuffs)
        {
            ClearBuffs = false;
            int c = _active.Count;
            _active.Clear(); _ended.Clear(); // (sin la espera de 5 s: se puede relanzar y detectar al momento)
            Log.Info($"BuffBar: buffs quitados ({c})");
        }
        // 1) Lanzamientos: la recarga (+0x12) pasa de 0 a casi llena. Al lanzar un arte, el juego pone a recargar
        // también las demás: si saltan varias en el mismo fotograma, la lanzada es la que mejor encaja con su propia
        // recarga (restante / total más cerca de 1).
        short best = 0; float bestErr = float.MaxValue; int bestLevel = 0;
        var cands = new System.Text.StringBuilder();
        int nc = 0;
        for (int i = 0; i < n; i++)
        {
            byte* e = (byte*)(st + i * 0x16);
            short id = *(short*)(e + 4);
            float total = *(float*)(e + 0xA), left = *(float*)(e + 0x12);
            _prevLeft.TryGetValue(id, out float prev);
            _prevLeft[id] = left;
            if (!(prev <= 0 && left > total * 0.8f && left > 0)) continue;
            nc++;
            cands.Append($" {id}:{left:0.##}/{total:0.##}");
            if (_active.ContainsKey(id) && !float.IsPositiveInfinity(_active[id].dur)) continue;
            if (_ended.TryGetValue(id, out long endedAt) && now - endedAt < 5000) continue;
            float err = MathF.Abs(left / total - 1f);
            if (err < bestErr) { bestErr = err; best = id; bestLevel = e[6] + e[7]; }
        }
        if (nc > 0)
        {
            // El arte lanzado es el seleccionado en la barra (el botón derecho lanza ese): en la vampiresa, por ejemplo,
            // todo un grupo de artes recarga a la vez con la recarga llena y no se distinguen por la recarga.
            int sel = SelectedArt(out int selType);
            if (_selLogs++ < 10) Log.Info($"BuffBar: arte seleccionado en la barra: {sel} (tipo {selType})");
            // Turn into Vampire: al saltar la recarga la transformación ya ha ocurrido y la casilla ya muestra Turn into
            // Knight (1023). Si saltan 1022 y 1023 juntos con cualquiera de los dos seleccionado, se lanzó 1022.
            if (sel == 1023 && cands.ToString().Contains(" 1022:")) sel = 1022;
            if (sel != 0 && cands.ToString().Contains($" {sel}:"))
            {
                best = (short)sel;
                for (int i = 0; i < n; i++) { byte* e = (byte*)(st + i * 0x16); if (*(short*)(e + 4) == best) bestLevel = e[6] + e[7]; }
                if (_active.TryGetValue(best, out var a0) && !float.IsPositiveInfinity(a0.dur)) best = 0; // ya activo
                // la segunda fase de la recarga (al acabar el efecto, p. ej. Turn into Vampire) no es un lanzamiento
                else if (_ended.TryGetValue(best, out long endedAt2) && now - endedAt2 < 5000) best = 0;
            }
        }
        // Volver a la forma normal (Turn into Knight, 1023; salta al lanzar de nuevo la transformación) acaba
        // Turn into Vampire (1022)
        // Volver a la forma normal: con la transformación (1022, o su casilla ya cambiada a 1023) seleccionada, salta la
        // recarga de 1023 y no la de 1022. (Al transformarse saltan las dos; al lanzar otras artes del grupo salta
        // 1023 pero el seleccionado es otro.)
        string cs = cands.ToString();
        int selNow = nc > 0 ? SelectedArt(out _) : 0;
        if (nc > 0 && cs.Contains(" 1023:") && !cs.Contains(" 1022:") && (selNow == 1022 || selNow == 1023) && _active.Remove(1022))
        {
            _ended[1022] = now;
            Log.Info("BuffBar: vuelta a la forma normal: termina Turn into Vampire");
            if (best == 1022 || best == 1023) best = 0;
        }
        if (best != 0)
        {
            float dur = Duration(best, bestLevel);
            if (dur > 0) { _active[best] = (now, dur); Log.Info($"BuffBar: arte {best} (nivel {bestLevel}) lanzado: {(float.IsPositiveInfinity(dur) ? "permanente" : dur.ToString("0.#") + " s")} (recargas que saltaron:{cands})"); }
            else if (nc > 0 && _candLogs++ < 20) Log.Info($"BuffBar: lanzado {best} (no es un buff); recargas que saltaron:{cands}");
        }
        // 2) Dibujo
        for (int i = 0; i < n; i++)
        {
            byte* e = (byte*)(st + i * 0x16);
            short id = *(short*)(e + 4);
            float total, left;
            if (!_active.TryGetValue(id, out var act)) continue;
            float elapsed = (now - act.start) / 1000f;
            if (elapsed >= act.dur) { _active.Remove(id); _ended[id] = now; continue; }
            total = act.dur; left = act.dur - elapsed;
            float x = screenW - _right - shown * (_size + _gap) - _size, y = _top;
            shown++;
            nint tex = Icon(id, out float iu0, out float iv0, out float iu1, out float iv1);
            // El dibujo del icono ya es redondo y tiene su propio borde oscuro: se recorta el margen para que su
            // círculo llene el aro ([Buffs] Crop, % de cada lado).
            float cu = (iu1 - iu0) * _crop, cv = (iv1 - iv0) * _crop;
            iu0 += cu; iu1 -= cu; iv0 += cv; iv1 -= cv;
            float cx = x + _size / 2f, cy = y + _size / 2f, r = _size / 2f;
            // 1) círculo en la plantilla: el icono (cuadrado) solo se pinta dentro
            bool stencil = MiniMap.HasStencil(device);
            if (stencil)
            {
                int* rc = stackalloc int[4];
                rc[0] = (int)x - 1; rc[1] = (int)y - 1; rc[2] = (int)(x + _size) + 2; rc[3] = (int)(y + _size) + 2;
                ((delegate* unmanaged[Stdcall]<nint, uint, int*, uint, uint, float, uint, int>)(*(nint**)device)[10])(device, 1, rc, 4, 0, 1f, 0);
                rs.Set(52, 1); rs.Set(56, 8); rs.Set(57, 1); rs.Set(58, 0xFF); rs.Set(59, 0xFF); rs.Set(53, 1); rs.Set(54, 1); rs.Set(55, 3);
                rs.Set(19, 1); rs.Set(20, 2); // ZERO / ONE: no pinta
                rs.Tss(1, 3); rs.Tss(4, 3);
                MiniMap.Disc(device, cx, cy, r, 0xFFFFFFFF);
                rs.Set(19, 5); rs.Set(20, 6);
                rs.Set(56, 3); rs.Set(55, 1); // EQUAL / KEEP
            }
            // 2) icono (o un disco de bronce si no hay textura)
            if (tex != 0)
            {
                rs.Tss(1, 4); rs.Tss(2, 2); rs.Tss(3, 0); // COLOROP MODULATE, ARG1 TEXTURE, ARG2 DIFFUSE
                rs.Tss(4, 4); rs.Tss(5, 2); rs.Tss(6, 0);
                Quad(device, x, y, _size, _size, 0xFFFFFFFF, tex, iu0, iv0, iu1, iv1);
            }
            rs.Tss(1, 3); rs.Tss(4, 3);
            if (tex == 0) MiniMap.Disc(device, cx, cy, r, 0xFF8A6230);
            if (stencil) rs.Set(52, 0);
            // 3) velo en sector: lo que ya ha pasado, en el sentido de las agujas del reloj desde arriba
            float gone = Math.Clamp(1f - left / total, 0f, 1f);
            if (gone > 0) Pie(device, cx, cy, r, gone, 0x99000000);
            // 4) aro metálico fino
            MiniMap.Ring(device, cx, cy, r, 0.4f);
        }
        rs.RestoreAll();
    }

    /// <summary>Sector circular desde arriba, en el sentido de las agujas del reloj, que cubre la fracción dada.</summary>
    private static void Pie(nint device, float cx, float cy, float r, float frac, uint color)
    {
        const int seg = 48;
        int n = Math.Max(1, (int)MathF.Ceiling(seg * frac));
        float* v = stackalloc float[5 * (seg + 2)];
        void P(int i, float px, float py) { float* p = v + i * 5; p[0] = px; p[1] = py; p[2] = 0; p[3] = 1; *(uint*)(p + 4) = color; }
        P(0, cx, cy);
        for (int i = 0; i <= n; i++)
        {
            float a = -MathF.PI / 2 + MathF.PI * 2 * frac * i / n;
            P(i + 1, cx + MathF.Cos(a) * r, cy + MathF.Sin(a) * r);
        }
        ((delegate* unmanaged[Stdcall]<nint, int, uint, nint, uint, uint, int>)(*(nint**)device)[25])(device, 6, 0x44, (nint)v, (uint)(n + 2), 0);
    }

    private static void Quad(nint device, float x, float y, float w, float h, uint color, nint tex, float u0 = 0, float v0 = 0, float u1 = 1, float v1 = 1)
    {
        float* q = stackalloc float[7 * 4];
        void V(int i, float px, float py, float u, float t)
        {
            float* p = q + i * 7;
            p[0] = px - 0.5f; p[1] = py - 0.5f; p[2] = 0; p[3] = 1; *(uint*)(p + 4) = color; p[5] = u; p[6] = t;
        }
        V(0, x, y, u0, v0); V(1, x + w, y, u1, v0); V(2, x, y + h, u0, v1); V(3, x + w, y + h, u1, v1);
        ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)(*(nint**)device)[35])(device, 0, tex);
        ((delegate* unmanaged[Stdcall]<nint, int, uint, nint, uint, uint, int>)(*(nint**)device)[25])(device, 5, 0x144, (nint)q, 4, 0);
    }
}
