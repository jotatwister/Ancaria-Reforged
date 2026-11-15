using System;
using System.Collections.Generic;
using SacredFix.Core;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Seguimiento de misiones ([Hud] Quests=1): lista en el lado izquierdo, debajo del minimapa, con el título y el
/// objetivo actual de cada misión.
///
/// Misiones: std::vector en 0xAAD3A4 (inicio, fin, capacidad), entradas de 0x174 bytes (la estructura que copia
/// TRIGGER_INSERT_QUEST en 0x7E71D9). Campos: +0x04 estado (2 activa, 100 completada: visto al hablar con Ensign
/// William), +0x0C 1 recién recibida (luego 0), +0x14 un id, +0x18/+0x1C coordenadas, +0x20 radio; textos (números con
/// el bit alto, de los datos del juego: 0x672C90 los resuelve): +0x24 título, +0x28 objetivo actual, +0x2C descripción,
/// +0x30 pista. Se muestran las de estado < 100; el registro apunta los campos cuando la lista cambia.
/// Se dibuja dentro de la escena con la letra del juego (MenuFont), solo en fotogramas de la partida (Overlay).
/// </summary>
internal static unsafe class QuestTracker
{
    public static volatile bool Enabled = true;
    private static readonly Dictionary<int, string> _texts = new();
    private static readonly List<float> _v = new();
    private static string _lastSig = "";
    private static long _cacheKey;
    private static float[]? _cache;

    public static void Init(Config cfg) => Enabled = cfg.GetBool("Hud", "Quests", true);

    private static string Text(int id)
    {
        if (id == 0 || id == -1 || (id & 0x80000000) == 0) return "";
        if (_texts.TryGetValue(id, out var t)) return t;
        t = BuffBar.Text(id, 300).Replace('\r', ' ').Replace('\n', ' ')
            .Replace("…", "...").Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"').Trim();
        if (t == "?") t = "";
        _texts[id] = t;
        return t;
    }

    /// <summary>Corta el texto para que quepa en «width» (con «...»).</summary>
    private static string Fit(string s, float width, float k)
    {
        if (MenuFont.Width(s, k) <= width) return s;
        while (s.Length > 4 && MenuFont.Width(s + "...", k) > width) s = s[..^1];
        return s.TrimEnd() + "...";
    }

    /// <summary>Dentro de la escena (desde Overlay, una vez por fotograma de la partida). true si ha dibujado.</summary>
    public static bool DrawInScene()
    {
        if (!Enabled || !Backend.Current.Ready || !MenuFont.Build()) return false;
        if (MiniMap.SidePanelOpen()) return false; // (taparía el inventario, el comerciante...)
        var (w, h) = Backend.Current.SceneSize;
        if (w <= 0 || h <= 0) return false;
        nint qa = *(nint*)0xAAD3A4, qb = *(nint*)0xAAD3A8;
        if (qa == 0 || qb <= qa) return false;
        int n = (int)((qb - qa) / 0x174);
        if (n <= 0 || n > 400) return false;

        // Lo preparado se reutiliza mientras no cambien las misiones (tipo, estado y textos de cada una) ni el tamaño:
        // medir y recortar los textos en cada fotograma costaba tiempo del procesador en la ciudad.
        long key = n * 31L + w * 7919L + h;
        for (int i = 0; i < n; i++)
        {
            nint q = qa + i * 0x174;
            key = key * 1000003 ^ (*(int*)q * 131L + *(int*)(q + 4)) ^ ((long)*(int*)(q + 0x24) << 20) ^ *(int*)(q + 0x28);
        }
        if (key == _cacheKey && _cache != null)
        {
            if (_cache.Length == 0) return true;
            fixed (float* p = _cache) return Backend.Current.DrawOverlayInScene(MenuFont.Texture, p, _cache.Length / 28);
        }
        _cacheKey = key;

        float sx = w / (float)Display.Width, sy = h / (float)Display.Height;
        float kT = 15 * sy / MenuFont.CellH, kO = 13 * sy / MenuFont.CellH;
        float x0 = 14 * sx, y = 196 * sy, maxW = 270 * sx;
        _v.Clear();
        var sig = new System.Text.StringBuilder();
        // Estados (+0x04) como el diario (0x6B090F, tabla 0x6B0D90): 1 y 2 en curso, 100 completada, 101 fallida; el
        // resto sin etiqueta. +0x00 == 3: principal. Primero las principales.
        var rows = new List<(string title, string obj, bool main)>();
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < n && rows.Count < 8; i++)
            {
                nint q = qa + i * 0x174;
                int state = *(int*)(q + 4);
                bool main = *(int*)q == 3;
                if (pass == 0)
                    sig.Append($"[{i}: {*(int*)q},{state},{*(int*)(q + 8)},{*(int*)(q + 0xC)},{*(int*)(q + 0x10)} {Text(*(int*)(q + 0x24))} / {Text(*(int*)(q + 0x28))}] ");
                if ((state != 1 && state != 2) || main != (pass == 0)) continue;
                string title = Text(*(int*)(q + 0x24));
                if (title.Length == 0) continue;
                rows.Add((title, Text(*(int*)(q + 0x28)), main));
            }
        string sg = sig.ToString();
        if (sg != _lastSig) { _lastSig = sg; Log.Info($"Misiones: {n}: {sg}"); }
        if (rows.Count == 0) { _cache = Array.Empty<float>(); return true; }

        // fondo
        float lineT = 18 * sy, lineO = 16 * sy, gap = 6 * sy;
        float totalH = 0;
        foreach (var r in rows) totalH += lineT + (r.obj.Length > 0 ? lineO : 0) + gap;
        MenuFont.Rect(_v, x0 - 8 * sx, y - 6 * sy, maxW + 16 * sx, totalH + 6 * sy, 0x70000000);
        foreach (var (title, obj, main) in rows)
        {
            string t = Fit(title, maxW, kT);
            MenuFont.Text(_v, t, x0 + 1.5f * sx, y + 1.5f * sy, kT, 0xC0000000);
            MenuFont.Text(_v, t, x0, y, kT, main ? 0xFFE8C878 : 0xFFC8D8E8); // principal dorada, secundaria azulada
            y += lineT;
            if (obj.Length > 0)
            {
                string o = Fit("· " + obj, maxW - 8 * sx, kO);
                MenuFont.Text(_v, o, x0 + 8 * sx + 1.5f * sx, y + 1.5f * sy, kO, 0xC0000000);
                MenuFont.Text(_v, o, x0 + 8 * sx, y, kO, 0xFFE0D8C8);
                y += lineO;
            }
            y += gap;
        }
        float[] arr = _cache = _v.ToArray();
        fixed (float* p = arr) return Backend.Current.DrawOverlayInScene(MenuFont.Texture, p, arr.Length / 28);
    }
}
