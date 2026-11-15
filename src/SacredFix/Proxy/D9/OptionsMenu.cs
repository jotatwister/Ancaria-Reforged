using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using SacredFix.Core;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Menú de opciones propio ([Menu] Enabled=1), encima del juego. Se abre y se cierra con F10 ([Menu] Key).
/// - Pestañas: Imagen (motor, color, enfoque, luz, MSAA...), Juego (minimapa, buffos, lanzar con una tecla, recargas,
///   contador de FPS) y Teclas (qué tecla física hace cada acción del juego: la traducción de [Keys] Remap).
/// - Cada cambio se guarda al momento en SacredFix.ini (Config.Set, que antes del primer cambio guarda una copia
///   SacredFix.ini.original). Lo que se puede, se aplica en el acto; lo demás lleva «*» y se aplica al reiniciar.
/// - Teclado: Tab / Mayús+Tab (o RePág / AvPág) cambian de pestaña, ↑↓ eligen, ←→ cambian, Intro activa, Esc o F10
///   cierran. En Teclas: Intro y luego la tecla nueva; Supr la deja en la suya.
/// - Ratón: al pasar por encima se resalta; clic en una pestaña la abre; clic en el valor: mitad izquierda baja,
///   mitad derecha sube (Sí/No y teclas: cambia o empieza a esperar la tecla); rueda: desplaza la lista; clic fuera:
///   cierra.
/// - Con el menú abierto, el juego no recibe teclas (salvo Alt y Windows) ni clics ni rueda; el movimiento sí.
/// </summary>
internal static unsafe class OptionsMenu
{
    private enum Kind { Header, Bool, Num, Choice, Key, Action }

    private sealed class Item
    {
        public Kind Kind;
        public string Label = "", Section = "", Key = "";
        public float Min, Max, Step;
        public string[] Values = Array.Empty<string>(), Names = Array.Empty<string>();
        public bool Restart, Changed;
        public string Value = "";
        public Action<string>? Apply;
        public uint GameVk; // (Kind.Key) tecla del juego
        public string Help = ""; // explicación (página izquierda, opción seleccionada)
    }

    private static readonly string[] TabNames = { "Imagen", "Juego", "Teclas" };
    private static readonly List<Item>[] _tabs = { new(), new(), new() };
    private static readonly int[] _selOf = { 1, 1, 1 };
    private static int _tab;
    private static List<Item> Items => _tabs[_tab];
    private static int Sel { get => _selOf[_tab]; set => _selOf[_tab] = value; }

    public static bool IsOpen { get; private set; }
    private static readonly object _lock = new();
    private static uint _toggleVk = 0x79; // F10
    private static bool _enabled;
    private static Item? _capture; // tecla esperando la nueva
    private static int _clickLogs;

    // dibujo y zonas para el ratón (en píxeles del rectángulo de la imagen)
    private static readonly List<float> _v = new();
    private static float _px, _py, _pw, _ph, _rowY0, _rowH, _valL, _valR;
    private static int _firstRow, _visible;

    private static CultureInfo Inv => CultureInfo.InvariantCulture;

    public static void Init(Config cfg)
    {
        Lang.Init(cfg);
        _enabled = cfg.GetBool("Menu", "Enabled", true);
        if (!_enabled) return;
        string key = cfg.GetString("Menu", "Key", "F10").Trim().ToUpperInvariant();
        if (key.StartsWith('F') && int.TryParse(key[1..], out int fn) && fn >= 1 && fn <= 24) _toggleVk = (uint)(0x6F + fn);

        // ---- Imagen ----
        var t = _tabs[0];
        Header(t, "Color y nitidez");
        Num(t, cfg, "Enfoque", "Display", "Sharpen", 0.4f, 0, 1, 0.1f, v => Layer.Sharpen = F(v));
        Help(t, "Realza los bordes de la imagen al escalarla a tu pantalla. 0 lo quita; valores altos pueden marcar demasiado los contornos.");
        Num(t, cfg, "Viveza del color", "Display", "Saturation", 1, 0, 2, 0.05f, v => Layer.Saturation = F(v));
        Help(t, "Intensidad de los colores. 1 es el original; menos apaga los colores y más los aviva.");
        Num(t, cfg, "Contraste", "Display", "Contrast", 1, 0.5f, 2, 0.05f, v => Layer.Contrast = F(v));
        Help(t, "Diferencia entre zonas claras y oscuras. 1 es el original.");
        Num(t, cfg, "Gamma", "Display", "Gamma", 1, 0.5f, 2, 0.05f, v => Layer.Gamma = F(v));
        Help(t, "Aclara u oscurece los tonos medios sin quemar los blancos. 1 es el original; más alto aclara.");
        Num(t, cfg, "Brillo", "Display", "Brightness", 0, -0.3f, 0.3f, 0.02f, v => Layer.Brightness = F(v));
        Help(t, "Sube o baja el brillo de toda la imagen. 0 es el original.");
        Header(t, "Calidad");
        Bool(t, cfg, "Luz por píxel", "Display", "PixelLighting", true, v => Layer.PixelLighting = v == "1");
        Help(t, "Calcula la luz de personajes y objetos 3D en cada píxel, más suave que el original (por vértice).");
        Bool(t, cfg, "Bordes suaves en lo 3D", "Display", "AlphaToCoverage", true, v => Layer.AlphaToCoverage = v == "1");
        Help(t, "Suaviza los bordes de pelo, hojas y telas de los modelos 3D. Necesita el antialiasing activado.");
        Choice(t, cfg, "Antialiasing (MSAA)", "Display", "MultiSample", "4", new[] { "0", "2", "4", "8" }, new[] { "No", "2x", "4x", "8x" }, null);
        Help(t, "Suaviza los dientes de sierra de los modelos 3D. Más alto se ve mejor pero exige más a la tarjeta.");
        Choice(t, cfg, "Filtrado anisótropo", "Display", "Anisotropy", "16", new[] { "0", "2", "4", "8", "16" }, new[] { "No", "2x", "4x", "8x", "16x" }, null);
        Help(t, "Mantiene nítidas las texturas vistas en ángulo, como el suelo de los modelos 3D. Apenas cuesta rendimiento.");
        Choice(t, cfg, "Resolución interna", "Display", "RenderScale", "auto", new[] { "auto", "1", "1.5", "2" }, new[] { "Pantalla", "Juego", "1,5x", "2x" }, null);
        Help(t, "A qué tamaño se dibuja. Pantalla: a la de tu monitor, más nítido. Juego: a la del juego y se estira. 1,5x y 2x: más nítido, más coste.");
        Choice(t, cfg, "Límite de FPS", "Display", "MaxFps", "0", new[] { "0", "30", "60", "120", "144" }, new[] { "Monitor", "30", "60", "120", "144" }, v => FrameLimit.Init(int.Parse(v, Inv)));
        Help(t, "Fotogramas por segundo máximos. Monitor: los de tu pantalla, sin cortes de imagen.");
        Header(t, "Ventana y motor");
        Choice(t, cfg, "Modo de pantalla", "Display", "D3D9Window", "borderless", new[] { "borderless", "window", "fullscreen" }, new[] { "Sin bordes", "Ventana", "Completa" }, null);
        Help(t, "Sin bordes: ventana a pantalla completa, cambia de programa al instante. Ventana: con marco. Completa: pantalla exclusiva.");
        Bool(t, cfg, "Mantener la proporción", "Display", "KeepAspect", true, null);
        Help(t, "Deja bandas negras para que la imagen no se deforme si tu monitor tiene otra forma que el juego.");
        Choice(t, cfg, "Motor gráfico", "Display", "Renderer", "d3d11", new[] { "d3d11", "d3d9" }, new[] { "Direct3D 11", "Direct3D 9" }, null);
        Help(t, "Direct3D 11 es el recomendado. Direct3D 9 es una alternativa por si da problemas en tu equipo.");
        Choice(t, cfg, "Ancho del juego", "Display", "Width", "1366", new[] { "auto", "1366", "1229", "1024" }, new[] { "Monitor", "1366", "1229", "1024" }, null);
        Help(t, "Anchura de la vista del juego (alto 768). Monitor: según la forma de tu pantalla. 1024 es el original; 1366 es el máximo seguro.");
        Bool(t, cfg, "Escalado de Windows", "Display", "DpiAware", true, null);
        Help(t, "Ignora el escalado de Windows (125 %, 150 %…) para que el juego no se vea borroso.");

        // ---- Juego ----
        t = _tabs[1];
        Header(t, "General");
        Choice(t, cfg, "Idioma / Language", "Menu", "Language", "es", new[] { "es", "en" }, new[] { "Castellano", "English" }, v => Lang.En = v == "en");
        Help(t, "Idioma de los textos de SacredFix. Los textos del juego (misiones, objetos) siguen el idioma del juego.");
        Header(t, "Mapa y estado");
        Bool(t, cfg, "Minimapa siempre visible", "MiniMap", "Enabled", true, v => MiniMap.Enabled = v == "1");
        Help(t, "Muestra un minimapa en la esquina durante la partida, sin abrir el mapa con Tab.");
        Num(t, cfg, "Tamaño del minimapa", "MiniMap", "Size", 120, 80, 240, 10, null);
        Help(t, "Tamaño del minimapa en pantalla, en píxeles.");
        Num(t, cfg, "Zoom del minimapa", "MiniMap", "Zoom", 50, 20, 150, 10, null);
        Help(t, "Cuánto terreno abarca el minimapa. Más bajo, más cerca.");
        Bool(t, cfg, "Buffos junto al retrato", "Buffs", "Enabled", true, v => BuffBar.Enabled = v == "1");
        Help(t, "Muestra junto al retrato los iconos de los efectos activos (artes y hechizos con duración) y el tiempo que les queda.");
        Num(t, cfg, "Tamaño de los buffos", "Buffs", "Size", 32, 20, 64, 2, v => BuffBar.SetSize((int)F(v)));
        Help(t, "Tamaño de los iconos de efectos activos.");
        Bool(t, cfg, "Nivel y supervivencia", "Hud", "HeroInfo", true, v => Overlay.SetHeroInfo(v == "1"));
        Help(t, "Muestra junto al retrato del héroe su nivel y el bonus de supervivencia, sin abrir la ficha del personaje.");
        Bool(t, cfg, "Misiones a la izquierda", "Hud", "Quests", true, v => QuestTracker.Enabled = v == "1");
        Help(t, "Lista de misiones con su título y el objetivo actual, a la izquierda, debajo del minimapa.");
        Header(t, "Nombres de objetos (Alt)");
        Bool(t, cfg, "Fijar con un toque de Alt", "Items", "AltToggle", true, v => { Patches.ItemLabels.TapToggle = v == "1"; if (v != "1") Patches.ItemLabels.Show = false; });
        Help(t, "Un toque corto de Alt deja los nombres del botín fijos; otro toque los quita. Mantener Alt invierte el modo.");
        Choice(t, cfg, "Nombres fijos: mostrar", "Items", "MinQuality", "0", new[] { "0", "1", "2", "3" }, new[] { "Todo", "Desde azules", "Desde amarillos", "Verdes y dorados" }, v => Patches.ItemLabels.MinQuality = int.Parse(v));
        Help(t, "Qué se ve con los nombres fijos. Blanco: normal; azul: mágico; amarillo: raro; verde: set; dorado: único. El oro se ve siempre.");
        Header(t, "Combate");
        Bool(t, cfg, "Lanzar artes con una tecla", "Keys", "QuickCast", true, v => KeyInput.QuickCast = v == "1");
        Help(t, "Las teclas de artes (6 a 0) lanzan el arte al momento, sin tener que seleccionarlo y pulsar el botón derecho.");
        Bool(t, cfg, "Segundos de recarga", "Keys", "CooldownSeconds", true, v => Overlay.SetCooldowns(v == "1"));
        Help(t, "Muestra encima de cada arte los segundos que faltan para poder volver a usarla.");
        Header(t, "Rendimiento");
        Choice(t, cfg, "Caché de objetos", "Fixes", "ObjectCacheSize", "16384", new[] { "4096", "8192", "16384", "32768" }, new[] { "4096", "8192", "16384", "32768" }, null);
        Help(t, "Cuántos objetos del mundo guarda el juego en memoria. Más alto evita bajones de FPS en ciudades con el zoom alejado.");
        Header(t, "Pruebas");
        t.Add(new Item { Kind = Kind.Action, Label = "Quitar buffos activos", Apply = _ => BuffBar.ClearBuffs = true });
        Help(t, "Pruebas: borra los iconos de efectos activos.");
        t.Add(new Item { Kind = Kind.Action, Label = "Guardar datos de misiones", Apply = _ => DebugScan.DumpQuestsNow() });
        Help(t, "Pruebas: guarda las misiones del juego en SacredFix_misiones_N.bin (junto al juego) para estudiarlas.");
        Header(t, "Contador de FPS");
        Bool(t, cfg, "Contador de FPS", "Fps", "Enabled", false, v => Overlay.SetFps(v == "1", null));
        Help(t, "Muestra los fotogramas por segundo en una esquina.");
        Num(t, cfg, "Tamaño del contador", "Fps", "Size", 16, 10, 32, 2, v => Overlay.SetFps(null, (int)F(v)));
        Help(t, "Tamaño del texto del contador de FPS.");

        // ---- Teclas ----
        t = _tabs[2];
        // atajos de la partida sin conexión (los del manual del juego; sin los de multijugador, el ratón, Ctrl+B ni Esc)
        Header(t, "Ventanas y partida");
        foreach (var (lab, vk) in new (string, uint)[] {
            ("Minimapa", 0x09), ("Acercar", 0x6B), ("Alejar", 0x6D), ("Recoger todo lo visible", 'A'), ("Inventario", 'I'),
            ("Artes de combate", 'F'), ("Combos", 'C'), ("Diario", 'L'), ("Mapa del mundo", 'M'), ("Opciones", 'O'),
            ("Guardar", 'S'), ("Carga rápida", 0x77), ("Guardado rápido", 0x78), ("Pausa", 'P'), ("Ayuda", 'H') })
            t.Add(new Item { Kind = Kind.Key, Label = lab, GameVk = vk });
        Header(t, "Pociones");
        foreach (var (lab, vk) in new (string, uint)[] {
            ("Poción de curación", 0x20), ("Muerte de no muertos", 'Q'), ("Poción del mentor", 'W'), ("Antídoto de víbora", 'E'),
            ("Poción de concentración", 'R'), ("Curar a los mercenarios", 'B') })
            t.Add(new Item { Kind = Kind.Key, Label = lab, GameVk = vk });
        Header(t, "Armas");
        for (int i = 0; i < 5; i++) t.Add(new Item { Kind = Kind.Key, Label = $"Arma {i + 1}", GameVk = (uint)('1' + i) });
        Header(t, "Artes y combos");
        for (int i = 0; i < 5; i++) t.Add(new Item { Kind = Kind.Key, Label = $"Arte o combo {i + 1}", GameVk = "67890"[i] });
        Header(t, "Este menú");
        t.Add(new Item { Kind = Kind.Key, Label = "Abrir y cerrar el menú", GameVk = MenuKeyId });
        Help(t, "La tecla que abre y cierra este menú en la partida.");

        StartHooks();
        Log.Info($"Menú: opciones propias en {_tabs.Length} pestañas (tecla 0x{_toggleVk:X2})");
    }

    private const uint MenuKeyId = 0x10000, AddId = 0x10001;
    private const string OtherHeader = "Otras teclas del juego";
    private static uint _addGame; // «Añadir»: la tecla del juego ya elegida (0: aún no)

    /// <summary>Teclas: una fila por cada traducción que no sea de habilidades ni armas, y «Añadir traducción».</summary>
    private static void RebuildOther()
    {
        var t = _tabs[2];
        int h = t.FindIndex(x => x.Kind == Kind.Header && x.Label == OtherHeader);
        if (h < 0) return;
        t.RemoveRange(h + 1, t.Count - h - 1);
        var seen = new HashSet<uint>();
        foreach (var pair in KeyInput.RemapText().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split(':');
            if (kv.Length != 2 || !TryVk(kv[1], out uint g) || t.Exists(x => x.Kind == Kind.Key && x.GameVk == g && x.Label != $"Tecla del juego {KeyInput.KeyName(g)}") || !seen.Add(g)) continue;
            t.Add(new Item { Kind = Kind.Key, Label = $"Tecla del juego {KeyInput.KeyName(g)}", GameVk = g });
        }
        t.Add(new Item { Kind = Kind.Key, Label = "Añadir traducción", GameVk = AddId });
        if (Sel >= t.Count) Sel = t.Count - 1;
    }

    /// <summary>La tecla pulsada mientras se espera una (Esc cancela antes de llegar aquí).</summary>
    private static void Captured(Item it, uint vk)
    {
        if (it.GameVk == MenuKeyId)
        {
            _toggleVk = vk;
            Config.Set("Menu", "Key", KeyInput.KeyName(vk));
            Log.Info($"Menú: se abre con {KeyInput.KeyName(vk)}");
        }
        else if (it.GameVk == AddId)
        {
            if (_addGame == 0) { _addGame = vk; return; } // ahora, la tecla propia
            Assign(new Item { Label = $"Tecla del juego {KeyInput.KeyName(_addGame)}", GameVk = _addGame }, vk);
            _addGame = 0;
            RebuildOther();
        }
        else { Assign(it, vk); RebuildOther(); }
        _capture = null;
    }

    private static float F(string v) => float.Parse(v, Inv);

    private static void Header(List<Item> t, string s) => t.Add(new Item { Kind = Kind.Header, Label = s });
    /// <summary>Explicación de la última opción añadida.</summary>
    private static void Help(List<Item> t, string text) => t[^1].Help = text;

    private static void Bool(List<Item> t, Config cfg, string label, string sec, string key, bool def, Action<string>? apply) =>
        t.Add(new Item { Kind = Kind.Bool, Label = label, Section = sec, Key = key, Value = cfg.GetBool(sec, key, def) ? "1" : "0", Apply = apply, Restart = apply == null });

    private static void Num(List<Item> t, Config cfg, string label, string sec, string key, float def, float min, float max, float step, Action<string>? apply)
    {
        float v = float.TryParse(cfg.GetString(sec, key, def.ToString(Inv)).Trim(), NumberStyles.Float, Inv, out float f) ? f : def;
        t.Add(new Item { Kind = Kind.Num, Label = label, Section = sec, Key = key, Min = min, Max = max, Step = step, Value = Fmt(Math.Clamp(v, min, max)), Apply = apply, Restart = apply == null });
    }

    private static void Choice(List<Item> t, Config cfg, string label, string sec, string key, string def, string[] values, string[] names, Action<string>? apply)
    {
        string v = cfg.GetString(sec, key, def).Trim().ToLowerInvariant();
        int i = Array.IndexOf(values, v);
        t.Add(new Item { Kind = Kind.Choice, Label = label, Section = sec, Key = key, Values = values, Names = names, Value = i >= 0 ? values[i] : v, Apply = apply, Restart = apply == null });
    }

    private static string Fmt(float f) => MathF.Round(f, 2).ToString("0.##", Inv);

    // ---- cambios ----

    private static void Change(Item it, int dir)
    {
        if (it.Kind == Kind.Key) { _capture = it; return; }
        if (it.Kind == Kind.Action) { it.Apply?.Invoke(""); Log.Info($"Menú: {it.Label}"); return; }
        string nv = it.Value;
        switch (it.Kind)
        {
            case Kind.Bool: nv = it.Value == "1" ? "0" : "1"; break;
            case Kind.Num: nv = Fmt(Math.Clamp(F(it.Value) + dir * it.Step, it.Min, it.Max)); break;
            case Kind.Choice:
                int i = Array.IndexOf(it.Values, it.Value);
                i = i < 0 ? 0 : (i + dir + it.Values.Length) % it.Values.Length;
                nv = it.Values[i]; break;
            default: return;
        }
        if (nv == it.Value) return;
        it.Value = nv;
        try { it.Apply?.Invoke(nv); } catch (Exception ex) { Log.Warn($"Menú: {it.Key}: {ex.Message}"); }
        if (it.Restart) it.Changed = true;
        Config.Set(it.Section, it.Key, nv);
        Log.Info($"Menú: [{it.Section}] {it.Key} = {nv}" + (it.Restart ? " (al reiniciar)" : ""));
    }

    /// <summary>Teclas: la tecla física <paramref name="phys"/> hace la acción del juego de <paramref name="it"/> (0 = la suya).</summary>
    private static void Assign(Item it, uint phys)
    {
        var map = new Dictionary<uint, uint>();
        foreach (var pair in KeyInput.RemapText().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split(':');
            if (kv.Length == 2 && TryVk(kv[0], out uint a) && TryVk(kv[1], out uint b)) map[a] = b;
        }
        // quien hacía esta acción deja de hacerla; la tecla nueva deja lo que hiciera
        foreach (var k in new List<uint>(map.Keys)) if (map[k] == it.GameVk) map.Remove(k);
        if (phys != 0) { map.Remove(phys); if (phys != it.GameVk) map[phys] = it.GameVk; }
        var parts = new List<string>();
        foreach (var kv in map) parts.Add($"{KeyInput.KeyName(kv.Key)}:{KeyInput.KeyName(kv.Value)}");
        string text = string.Join(",", parts);
        KeyInput.SetRemap(text);
        Config.Set("Keys", "Remap", text);
        Log.Info($"Menú: {it.Label} -> {(phys == 0 ? "su tecla" : KeyInput.KeyName(phys))} (Remap={text})");
    }

    private static bool TryVk(string s, out uint vk)
    {
        s = s.Trim().ToUpperInvariant(); vk = 0;
        if (s.Length == 1) { vk = s[0]; return true; }
        if (s.StartsWith("F") && int.TryParse(s[1..], out int f)) { vk = (uint)(0x6F + f); return true; }
        if (s.StartsWith("NUM") && int.TryParse(s[3..], out int n)) { vk = (uint)(0x60 + n); return true; }
        if (s == "SPACE") { vk = 0x20; return true; }
        if (s == "NUM+") { vk = 0x6B; return true; }
        if (s == "NUM-") { vk = 0x6D; return true; }
        if (s == "ENTER") { vk = 0x0D; return true; }
        if (s == "TAB") { vk = 0x09; return true; }
        return s.StartsWith("0X") && uint.TryParse(s[2..], NumberStyles.HexNumber, Inv, out vk);
    }

    private static void Move(int dir)
    {
        var items = Items;
        int n = items.Count, i = Sel;
        for (int k = 0; k < n; k++)
        {
            i = (i + dir + n) % n;
            if (items[i].Kind != Kind.Header) { Sel = i; return; }
        }
    }

    /// <summary>Desplaza la lista (la selección acompaña para que no la devuelva a su sitio).</summary>
    private static void Scroll(int rows)
    {
        var items = Items;
        _firstRow = Math.Clamp(_firstRow + rows, 0, Math.Max(0, items.Count - _visible));
        if (Sel < _firstRow || Sel >= _firstRow + _visible)
        {
            Sel = rows > 0 ? Math.Min(items.Count - 1, _firstRow + _visible - 1) : _firstRow;
            if (items[Sel].Kind == Kind.Header) Move(rows > 0 ? -1 : 1);
        }
    }

    /// <summary>Parte un texto en líneas que caben en «width» con la escala «k».</summary>
    private static List<string> Wrap(string text, float width, float k)
    {
        var lines = new List<string>();
        string cur = "";
        foreach (var w in text.Split(' '))
        {
            string tryS = cur.Length == 0 ? w : cur + " " + w;
            if (cur.Length > 0 && MenuFont.Width(tryS, k) > width) { lines.Add(cur); cur = w; }
            else cur = tryS;
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }

    private static void SetTab(int t)
    {
        _tab = (t + _tabs.Length) % _tabs.Length;
        _capture = null; _firstRow = 0;
        if (Items[Sel].Kind == Kind.Header) Move(1);
    }

    private static string Shown(Item it)
    {
        if (it == _capture) return it.GameVk == AddId ? (_addGame == 0 ? "Pulsa la tecla del juego…" : $"Ahora tu tecla para {KeyInput.KeyName(_addGame)}…") : "Pulsa una tecla…";
        return it.Kind switch
        {
            Kind.Bool => it.Value == "1" ? "Sí" : "No",
            Kind.Action => "Pulsar",
            Kind.Choice => Array.IndexOf(it.Values, it.Value) is int i and >= 0 ? it.Names[i] : it.Value,
            Kind.Key when it.GameVk == MenuKeyId => KeyInput.KeyName(_toggleVk),
            Kind.Key when it.GameVk == AddId => "Pulsa aquí",
            Kind.Key => KeyInput.PhysicalFor(it.GameVk) is uint p && p != 0 ? KeyInput.KeyName(p) : "Ninguna",
            _ => it.Value.Replace('.', ','),
        };
    }

    // ---- dibujo (al final de cada fotograma, desde el motor) ----

    private static bool _inSceneThisFrame;
    private static float _hit = 1; // de píxeles de la imagen en pantalla a píxeles del menú

    // lo que se dibuja: lotes por textura (las del libro y la letra), y las zonas que atiende el ratón
    private readonly record struct Batch(nint Tex, float[] V);
    private static readonly List<Batch> _batches = new();
    private enum HitKind { Tab, Row, Close, Up, Down }
    private static readonly List<(float x, float y, float w, float h, HitKind kind, int arg)> _hits = new();

    /// <summary>Desde el dibujo del cursor del juego: el menú, dentro de su imagen, para que el cursor quede encima.</summary>
    public static void DrawInScene()
    {
        if (!IsOpen || !Backend.Current.Ready || !MenuFont.Build()) return;
        var (w, h) = Backend.Current.SceneSize;
        if (w <= 0 || h <= 0) return;
        lock (_lock)
        {
            Build(w, h);
            bool ok = true;
            foreach (var b in _batches)
                fixed (float* p = b.V) ok &= Backend.Current.DrawOverlayInScene(b.Tex, p, b.V.Length / 28);
            if (!ok) return;
            _inSceneThisFrame = true;
            float iw = Display.ImgW > 0 ? Display.ImgW : w;
            _hit = w / iw;
        }
    }

    /// <summary>Al presentar: si el juego no ha dibujado su cursor en este fotograma, el menú se dibuja aquí.</summary>
    public static void Draw()
    {
        bool done = _inSceneThisFrame;
        _inSceneThisFrame = false;
        if (done || !IsOpen || !Backend.Current.Ready || !MenuFont.Build()) return;
        lock (_lock)
        {
            float iw = Display.ImgW > 0 ? Display.ImgW : (Display.OutW > 0 ? Display.OutW : Display.BBW);
            float ih = Display.ImgH > 0 ? Display.ImgH : (Display.OutH > 0 ? Display.OutH : Display.BBH);
            _hit = 1;
            Build(iw, ih);
            foreach (var b in _batches)
                fixed (float* p = b.V) Backend.Current.DrawOverlay(b.Tex, p, b.V.Length / 28);
        }
    }

    /// <summary>El menú en un espacio de iw x ih píxeles: con el libro del juego si están sus texturas; si no, el panel.</summary>
    private static void Build(float iw, float ih)
    {
        _batches.Clear(); _hits.Clear(); _v.Clear();
        if (SkinTextures.Ready()) BuildBook(iw, ih); else BuildPanel(iw, ih);
        if (_v.Count > 0) _batches.Add(new Batch(MenuFont.Texture, _v.ToArray()));
    }

    private static void Piece(ulong tex, float x, float y, float w, float h, float u0, float v0, float u1, float v1)
    {
        nint t = SkinTextures.Get(tex);
        if (t == 0) return;
        // medio texel hacia dentro: el filtro no lee la pieza de al lado en las texturas con varias (la tira de las
        // mitades del pergamino daba una línea en la junta)
        const float d = 0.5f / 256;
        float du = u1 > u0 ? d : -d, dv = v1 > v0 ? d : -d;
        u0 += du; u1 -= du; v0 += dv; v1 -= dv;
        var q = new List<float>(28);
        float c = BitConverter.Int32BitsToSingle(-1);
        x -= 0.5f; y -= 0.5f;
        q.AddRange(new[] { x, y, 0f, 1f, c, u0, v0, x + w, y, 0f, 1f, c, u1, v0, x, y + h, 0f, 1f, c, u0, v1, x + w, y + h, 0f, 1f, c, u1, v1 });
        _batches.Add(new Batch(t, q.ToArray()));
    }

    /// <summary>
    /// Como el libro de misiones: el marco y el pergamino del juego (768x512 a la escala del alto). Página izquierda:
    /// «SacredFix» en rojo y las secciones como la lista de misiones; página derecha: las opciones de la sección.
    /// </summary>
    private static void BuildBook(float iw, float ih)
    {
        float s = ih / 768f, bw = 768 * s, bh = 512 * s, bx = (iw - bw) / 2, by = (ih - bh) / 2, S = 256 * s, H = 128 * s;
        _px = bx; _py = by; _pw = bw; _ph = bh;
        for (int i = 0; i < 6; i++) Piece(SkinTextures.Frame[i], bx + (i % 3) * S, by + (i / 3) * S, S, S, 0, 0, 1, 1);
        if (SkinTextures.Get(SkinTextures.PageL1) != 0 && SkinTextures.Get(SkinTextures.PageL2) != 0 && SkinTextures.Get(SkinTextures.PageMidL) != 0)
        {
            Piece(SkinTextures.PageL1, bx, by, S, S, 0, 0, 1, 1);
            Piece(SkinTextures.PageL2, bx, by + S, S, S, 0, 0, 1, 1);
            Piece(SkinTextures.PageMidL, bx + S, by, H, S, 0, 0, 0.5f, 1);
            Piece(SkinTextures.PageMidL, bx + S, by + S, H, S, 0.5f, 0, 1, 1);
        }
        else
        {
            // (página izquierda aún sin cargar: la derecha en espejo)
            Piece(SkinTextures.PageMidR, bx, by, H, S, 0.5f, 0, 0, 1);
            Piece(SkinTextures.PageMidR, bx, by + S, H, S, 1, 0, 0.5f, 1);
            Piece(SkinTextures.PageR1, bx + H, by, S, S, 1, 0, 0, 1);
            Piece(SkinTextures.PageR2, bx + H, by + S, S, S, 1, 0, 0, 1);
        }
        Piece(SkinTextures.PageR1, bx + 384 * s, by, S, S, 0, 0, 1, 1);
        Piece(SkinTextures.PageR2, bx + 384 * s, by + S, S, S, 0, 0, 1, 1);
        Piece(SkinTextures.PageMidR, bx + 640 * s, by, H, S, 0, 0, 0.5f, 1);
        Piece(SkinTextures.PageMidR, bx + 640 * s, by + S, H, S, 0.5f, 0, 1, 1);
        Piece(SkinTextures.Close, bx + 733 * s, by + 1 * s, 34 * s, 34 * s, 0.0039f, 0.582f, 0.1387f, 0.7168f);
        _hits.Add((bx + 733 * s, by + 1 * s, 34 * s, 34 * s, HitKind.Close, 0));

        uint red = 0xFF8E1A12, ink = 0xFF2E2416, blue = 0xFF1F2F70, dim = 0xFF6E6250, band = 0x38704818;
        float kt = 30 * s / MenuFont.CellH, k = 17 * s / MenuFont.CellH, ks = 13 * s / MenuFont.CellH;
        // página izquierda: título y secciones
        string title = "SacredFix";
        MenuFont.Text(_v, title, bx + (384 * s - MenuFont.Width(title, kt, true)) / 2, by + 32 * s, kt, red, true);
        string sub = "Opciones";
        MenuFont.Text(_v, sub, bx + (384 * s - MenuFont.Width(sub, k)) / 2, by + 72 * s, k, dim);
        MenuFont.Rect(_v, bx + 60 * s, by + 98 * s, 264 * s, 1.5f * s, 0x80704818);
        for (int i = 0; i < TabNames.Length; i++)
        {
            float y = by + (116 + i * 36) * s;
            bool on = i == _tab;
            if (on) MenuFont.Rect(_v, bx + 56 * s, y - 2 * s, 272 * s, 30 * s, band);
            MenuFont.Text(_v, on ? "›" : "·", bx + 66 * s, y + 2 * s, k, on ? red : dim);
            MenuFont.Text(_v, TabNames[i], bx + 86 * s, y + 1 * s, k * 1.2f, on ? red : ink, true);
            _hits.Add((bx + 56 * s, y - 2 * s, 272 * s, 30 * s, HitKind.Tab, i));
        }
        // explicación de la opción seleccionada (entre las secciones y los controles)
        if (Sel >= 0 && Sel < Items.Count && Items[Sel].Help.Length > 0)
        {
            var cur = Items[Sel];
            MenuFont.Text(_v, cur.Label, bx + 66 * s, by + 238 * s, ks * 1.15f, red);
            int line = 0;
            foreach (var l in Wrap(Lang.T(cur.Help), 256 * s, ks * 1.1f))
            {
                if (line >= 5) break;
                MenuFont.Text(_v, l, bx + 66 * s, by + (260 + line * 20) * s, ks * 1.1f, ink);
                line++;
            }
        }
        string[] help = _tab == 2
            ? new[] { "Intro o clic: cambiar la tecla", "Supr: dejar la suya", "Tab: otra sección", "Esc / F10: cerrar" }
            : new[] { "Flechas: elegir y cambiar", "Clic: cambiar · rueda: bajar", "Tab: otra sección · Esc: cerrar", "Los cambios se guardan solos" };
        for (int i = 0; i < help.Length; i++) MenuFont.Text(_v, help[i], bx + 66 * s, by + (372 + i * 24) * s, ks * 1.2f, 0xFF4A3C28);

        // página derecha: la sección
        var items = Items;
        string head = TabNames[_tab];
        MenuFont.Text(_v, head, bx + 384 * s + (384 * s - MenuFont.Width(head, kt * 0.85f, true)) / 2, by + 32 * s, kt * 0.85f, red, true);
        _rowH = 22 * s; _rowY0 = by + 74 * s;
        _visible = Math.Max(1, (int)((bh - 74 * s - 62 * s) / _rowH));
        if (Sel < _firstRow) _firstRow = Sel;
        if (Sel >= _firstRow + _visible) _firstRow = Sel - _visible + 1;
        _firstRow = Math.Clamp(_firstRow, 0, Math.Max(0, items.Count - _visible));
        _valL = bx + 612 * s; _valR = bx + 738 * s;
        bool pending = false;
        for (int r = 0; r < _visible && _firstRow + r < items.Count; r++)
        {
            var it = items[_firstRow + r];
            float y = _rowY0 + r * _rowH;
            if (it.Kind == Kind.Header)
            {
                MenuFont.Text(_v, it.Label, bx + 418 * s, y + 4 * s, ks * 1.1f, red);
                MenuFont.Rect(_v, bx + 418 * s, y + _rowH - 3 * s, 318 * s, 1 * s, 0x60704818);
                continue;
            }
            bool sel = _firstRow + r == Sel;
            if (sel) MenuFont.Rect(_v, bx + 412 * s, y, 330 * s, _rowH, it == _capture ? 0x50A04020u : band);
            MenuFont.Text(_v, it.Label + (it.Restart ? " *" : ""), bx + 424 * s, y + 3 * s, k * 0.92f, ink);
            string val = Shown(it);
            float vk = k * 0.92f, cx = (_valL + _valR) / 2;
            if (MenuFont.Width(val, vk) > _valR - _valL - 24 * s) vk *= (_valR - _valL - 24 * s) / MenuFont.Width(val, vk);
            if (it.Kind is Kind.Num or Kind.Choice)
            {
                MenuFont.Text(_v, "‹", _valL, y + 2 * s, k, sel ? red : dim);
                MenuFont.Text(_v, "›", _valR - MenuFont.Width("›", k), y + 2 * s, k, sel ? red : dim);
            }
            MenuFont.Text(_v, val, cx - MenuFont.Width(val, vk) / 2, y + 3 * s, vk, it.Changed ? 0xFF9A4A00 : blue);
            _hits.Add((bx + 412 * s, y, 330 * s, _rowH, HitKind.Row, _firstRow + r));
        }
        // más opciones arriba o abajo: flechas (se pueden pulsar)
        if (_firstRow > 0)
        {
            string up = "más arriba";
            MenuFont.Text(_v, up, bx + 742 * s - MenuFont.Width(up, ks * 1.1f), by + 54 * s, ks * 1.1f, red);
            _hits.Add((bx + 650 * s, by + 50 * s, 96 * s, 22 * s, HitKind.Up, 0));
        }
        if (_firstRow + _visible < items.Count)
        {
            float ay = _rowY0 + _visible * _rowH;
            string more = "más opciones abajo";
            MenuFont.Text(_v, more, bx + 384 * s + (384 * s - MenuFont.Width(more, ks * 1.1f)) / 2, ay + 2 * s, ks * 1.1f, red);
            _hits.Add((bx + 412 * s, ay, 330 * s, 22 * s, HitKind.Down, 0));
        }
        foreach (var tb in _tabs) foreach (var it in tb) if (it.Changed) pending = true;
        string note = pending ? "Hay cambios que se aplican al reiniciar" : "* se aplica al reiniciar el juego";
        MenuFont.Text(_v, note, bx + 384 * s + (384 * s - MenuFont.Width(note, ks * 1.1f)) / 2, by + bh - 34 * s, ks * 1.1f, pending ? 0xFF9A4A00 : 0xFF4A3C28);
    }

    /// <summary>Sin las texturas del libro: el panel oscuro propio.</summary>
    private static void BuildPanel(float iw, float ih)
    {
        float s = ih / 768f;
        float k = 17 * s / MenuFont.CellH, kt = 22 * s / MenuFont.CellH, ks = 14 * s / MenuFont.CellH;
        uint gold = 0xFFD8B860, dim = 0xFF908A80;
        var items = Items;
        int maxRows = 0; foreach (var tb in _tabs) maxRows = Math.Max(maxRows, tb.Count);
        _rowH = 25 * s;
        _pw = 640 * s; _ph = Math.Min(ih - 20 * s, (maxRows * 25 + 160) * s);
        _px = (iw - _pw) / 2; _py = (ih - _ph) / 2;
        MenuFont.Rect(_v, _px, _py, _pw, _ph, 0xEC141210);
        MenuFont.Rect(_v, _px, _py, _pw, 2 * s, gold); MenuFont.Rect(_v, _px, _py + _ph - 2 * s, _pw, 2 * s, gold);
        MenuFont.Rect(_v, _px, _py, 2 * s, _ph, gold); MenuFont.Rect(_v, _px + _pw - 2 * s, _py, 2 * s, _ph, gold);
        string title = "SacredFix · Opciones";
        MenuFont.Text(_v, title, _px + (_pw - MenuFont.Width(title, kt)) / 2, _py + 10 * s, kt, gold);
        float tabY = _py + 46 * s, tabH = 28 * s, tw = (_pw - 40 * s) / TabNames.Length;
        for (int i = 0; i < TabNames.Length; i++)
        {
            float tx = _px + 20 * s + i * tw;
            bool on = i == _tab;
            MenuFont.Rect(_v, tx + 2 * s, tabY, tw - 4 * s, tabH, on ? 0x70D8B860u : 0x30FFFFFFu);
            MenuFont.Text(_v, TabNames[i], tx + (tw - MenuFont.Width(TabNames[i], k)) / 2, tabY + 4 * s, k, on ? 0xFFFFFFFF : 0xFFC8C2B6);
            _hits.Add((tx, tabY, tw, tabH, HitKind.Tab, i));
        }
        _rowY0 = tabY + tabH + 12 * s;
        _visible = Math.Max(1, (int)((_py + _ph - 64 * s - _rowY0) / _rowH));
        if (Sel < _firstRow) _firstRow = Sel;
        if (Sel >= _firstRow + _visible) _firstRow = Sel - _visible + 1;
        _firstRow = Math.Clamp(_firstRow, 0, Math.Max(0, items.Count - _visible));
        _valL = _px + _pw - 260 * s; _valR = _px + _pw - 24 * s;
        bool pending = false;
        for (int r = 0; r < _visible && _firstRow + r < items.Count; r++)
        {
            var it = items[_firstRow + r];
            float y = _rowY0 + r * _rowH;
            if (it.Kind == Kind.Header)
            {
                MenuFont.Text(_v, it.Label, _px + 20 * s, y + 4 * s, k, gold);
                MenuFont.Rect(_v, _px + 20 * s, y + _rowH - 3 * s, _pw - 40 * s, 1 * s, 0x80D8B860);
                continue;
            }
            bool sel = _firstRow + r == Sel;
            if (sel) MenuFont.Rect(_v, _px + 12 * s, y, _pw - 24 * s, _rowH, it == _capture ? 0x70E07040u : 0x50D8B860u);
            uint col = sel ? 0xFFFFFFFF : 0xFFD0CCC4;
            MenuFont.Text(_v, it.Label + (it.Restart ? " *" : ""), _px + 32 * s, y + 3 * s, k, col);
            string val = Shown(it);
            if (it.Kind is Kind.Num or Kind.Choice)
            {
                MenuFont.Text(_v, "‹", _valL, y + 2 * s, k, sel ? gold : dim);
                MenuFont.Text(_v, "›", _valR - MenuFont.Width("›", k), y + 2 * s, k, sel ? gold : dim);
            }
            MenuFont.Text(_v, val, (_valL + _valR) / 2 - MenuFont.Width(val, k) / 2, y + 3 * s, k, it.Changed ? 0xFFFFC060 : col);
            _hits.Add((_px + 12 * s, y, _pw - 24 * s, _rowH, HitKind.Row, _firstRow + r));
        }
        foreach (var tb in _tabs) foreach (var it in tb) if (it.Changed) pending = true;
        string note = pending ? "Hay cambios (en naranja) que se aplican al reiniciar el juego" : "* se aplica al reiniciar el juego";
        MenuFont.Text(_v, note, _px + (_pw - MenuFont.Width(note, ks)) / 2, _py + _ph - 28 * s, ks, pending ? 0xFFFFC060 : dim);
    }

    // ---- entrada (ganchos de bajo nivel en su propio hilo) ----

    [DllImport("user32.dll")] private static extern nint SetWindowsHookExW(int id, nint proc, nint mod, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint w, nint l);
    [DllImport("user32.dll")] private static extern int GetMessageW(byte* msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, uint* pid);
    [DllImport("user32.dll")] private static extern int ScreenToClient(nint hwnd, int* pt);
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);

    private static void StartHooks()
    {
        var t = new Thread(() =>
        {
            nint mod = GetModuleHandleW(null);
            nint hk = SetWindowsHookExW(13 /* WH_KEYBOARD_LL */, (nint)(delegate* unmanaged[Stdcall]<int, nint, nint, nint>)&KeyProc, mod, 0);
            nint hm = SetWindowsHookExW(14 /* WH_MOUSE_LL */, (nint)(delegate* unmanaged[Stdcall]<int, nint, nint, nint>)&MouseProc, mod, 0);
            if (hk == 0 || hm == 0) Log.Warn($"Menú: no se pudieron instalar los ganchos (teclado {hk != 0}, ratón {hm != 0})");
            else Log.Info("Menú: ganchos de teclado y ratón instalados");
            byte* msg = stackalloc byte[28];
            while (GetMessageW(msg, 0, 0, 0) > 0) { }
        }) { IsBackground = true, Name = "SacredFix menú" };
        t.Start();
    }

    private static bool GameInFront()
    {
        nint fg = GetForegroundWindow();
        uint pid = 0;
        if (fg == 0) return false;
        GetWindowThreadProcessId(fg, &pid);
        return pid == GetCurrentProcessId();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint KeyProc(int code, nint w, nint l)
    {
        try
        {
            if (code == 0 && l != 0 && _enabled && GameInFront())
            {
                uint vk = *(uint*)l, flags = *(uint*)(l + 8);
                bool up = (flags & 0x80) != 0, alt = (flags & 0x20) != 0, injected = (flags & 0x10) != 0;
                // las capturas de Windows no deben quedar bloqueadas por el menú: Impr Pant y todo lo que se pulse con la
                // tecla de Windows (Win+Mayús+S...) pasan siempre
                if (vk == 0x2C) return CallNextHookEx(0, code, w, l); // Impr Pant: para Windows, siempre
                if ((GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0) return CallNextHookEx(0, code, w, l);
                if (injected && !IsOpen) return CallNextHookEx(0, code, w, l);
                if (IsOpen && _capture != null)
                {
                    // esperando la tecla nueva: Esc cancela; cualquier otra (salvo Alt / Windows) se asigna
                    if (vk is 0x12 or 0xA4 or 0xA5 or 0x5B or 0x5C || alt) return CallNextHookEx(0, code, w, l);
                    if (!up) lock (_lock) { if (vk == 0x1B) { _capture = null; _addGame = 0; } else Captured(_capture, vk); }
                    return 1;
                }
                if (vk == _toggleVk)
                {
                    if (!up) lock (_lock) { IsOpen = !IsOpen; _capture = null; if (IsOpen && Items[Sel].Kind == Kind.Header) Move(1); }
                    return 1;
                }
                if (IsOpen)
                {
                    if (alt || vk is 0x12 or 0xA4 or 0xA5 or 0x5B or 0x5C) return CallNextHookEx(0, code, w, l); // Alt+Tab, Windows
                    if (!up)
                        lock (_lock)
                            switch (vk)
                            {
                                case 0x26: Move(-1); break;                                         // ↑
                                case 0x28: Move(1); break;                                          // ↓
                                case 0x25: Change(Items[Sel], -1); break;                           // ←
                                case 0x27: case 0x0D: case 0x20: Change(Items[Sel], 1); break;      // →, Intro, espacio
                                case 0x09: SetTab(_tab + ((GetKeyState(0x10) & 0x8000) != 0 ? -1 : 1)); break; // Tab / Mayús+Tab
                                case 0x21: SetTab(_tab - 1); break;                                 // RePág
                                case 0x22: SetTab(_tab + 1); break;                                 // AvPág
                                case 0x2E: case 0x08: // Supr / Retroceso: la tecla de siempre
                                    var ki = Items[Sel];
                                    if (ki.Kind != Kind.Key || ki.GameVk == AddId) break;
                                    if (ki.GameVk == MenuKeyId) Captured(ki, 0x79);
                                    else { Assign(ki, 0); RebuildOther(); }
                                    break;
                                case 0x1B: IsOpen = false; break;                                   // Esc
                            }
                    return 1; // con el menú abierto, el juego no recibe teclas
                }
            }
        }
        catch { }
        return CallNextHookEx(0, code, w, l);
    }

    /// <summary>MSLLHOOKSTRUCT: pt.x +0, pt.y +4, mouseData +8 (rueda: palabra alta), flags +12.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint MouseProc(int code, nint w, nint l)
    {
        try
        {
            if (code == 0 && l != 0 && IsOpen && GameInFront() && Display.Hwnd != 0)
            {
                int msg = (int)w;
                int* pt = stackalloc int[2];
                pt[0] = *(int*)l; pt[1] = *(int*)(l + 4);
                int sx = pt[0], sy = pt[1];
                ScreenToClient(Display.Hwnd, pt);
                float x = (pt[0] - Display.ImgX) * _hit, y = (pt[1] - Display.ImgY) * _hit;
                lock (_lock)
                {
                    var items = Items;
                    bool inside = x >= _px && x < _px + _pw && y >= _py && y < _py + _ph;
                    int hi = -1;
                    for (int i = _hits.Count - 1; i >= 0; i--)
                    {
                        var h = _hits[i];
                        if (x >= h.x && x < h.x + h.w && y >= h.y && y < h.y + h.h) { hi = i; break; }
                    }
                    var hit = hi >= 0 ? _hits[hi] : default;
                    bool onRow = hi >= 0 && hit.kind == HitKind.Row && hit.arg < items.Count && items[hit.arg].Kind != Kind.Header;
                    if (msg == 0x200) // moverse: resaltar (y el juego mueve su cursor)
                    {
                        if (onRow && _capture == null) Sel = hit.arg;
                        return CallNextHookEx(0, code, w, l);
                    }
                    if (msg == 0x201) // botón izquierdo
                    {
                        if (_clickLogs++ < 6)
                            Log.Info($"Menú: clic en pantalla ({sx},{sy}) -> menú ({x:0},{y:0}); dentro {inside}, zona {(hi >= 0 ? hit.kind + " " + hit.arg : "ninguna")}");
                        if (!inside || (hi >= 0 && hit.kind == HitKind.Close)) { IsOpen = false; _capture = null; }
                        else if (hi >= 0 && hit.kind == HitKind.Tab) SetTab(hit.arg);
                        else if (hi >= 0 && hit.kind == HitKind.Up) Scroll(-1);
                        else if (hi >= 0 && hit.kind == HitKind.Down) Scroll(1);
                        else if (onRow)
                        {
                            Sel = hit.arg;
                            var it = items[hit.arg];
                            if (it.Kind is Kind.Bool or Kind.Key or Kind.Action) Change(it, 1);
                            else if (x >= _valL - 10 && x <= _valR + 10) Change(it, x < (_valL + _valR) / 2 ? -1 : 1);
                        }
                    }
                    else if (msg == 0x20A && inside) // rueda: solo desplaza la lista (no cambia valores)
                    {
                        short delta = (short)(*(uint*)(l + 8) >> 16);
                        Scroll(delta > 0 ? -3 : 3);
                    }
                }
                return 1; // con el menú abierto, el juego no recibe clics ni rueda
            }
        }
        catch { }
        return CallNextHookEx(0, code, w, l);
    }
}
