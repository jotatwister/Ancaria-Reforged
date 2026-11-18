using System;
using SacredFix.Core;

namespace SacredFix.Proxy;

/// <summary>
/// Comparar objetos: al pasar el ratón por un objeto (mochila, cofre, comerciante, herrero...), junto a su ficha se
/// muestra la del objeto equipado en el mismo hueco, hecha por el juego (mismo formato e idioma).
///
/// Las fichas de objetos (cUI_Tooltip) se piden en cada fotograma con cUI_Manager::showItemTooltip (0x75B390, 33
/// llamadores; la compartida es la de índice [gestor+0x54]): 0x6E8890(-1) limpia indicadores, 0x6E8860(0xB) los pone
/// (con el 1 guarda la hora [0xCD7A50] en +0x21D4), 0x6E69E0(1), posición +0x24/+0x28, setText 0x6E6EA0(0, &texto, 0),
/// 0x6E81C0(id) (id en +0x21D0, indicador 0x40 = recolocar) y 0x732550(1). Su dibujo (0x6E76C0, enganchado en
/// UiCanvas.TipRenderHook) la oculta si +0x21D4 no es la hora actual, la recoloca con 0x6E7AF0 (tamaño en los words
/// +0x2C/+0x2E) y, con el indicador 1, se oculta sola al acabar: solo se ve los fotogramas en que se pide.
///
/// Aquí, después de dibujar una ficha con objeto pedida en este fotograma, se prepara la nuestra igual, se calcula su
/// tamaño con 0x6E7AF0, se pone al lado y se dibuja en ese momento. Texto: objeto->vtable[0x44](&vector, 0, 0, 0x5F)
/// (vector de wchar {primero, último, fin}; 0x8321A0 reserva, 0x41D3B0(ptr, n) libera), como cUI_Equipment (0x6B7D1A).
/// Ficha propia: tooltipCreate 0x75AF90 (thiscall gestor, 1, 0xB) → índice; 0x75B1E0 (gestor, índice) → ficha.
/// Equipo del héroe: 19 huecos, id en héroe+0x1A4 + hueco·4. Categoría: 0x426220 (thiscall [0xAAB5E4], tipo).
/// </summary>
internal static unsafe class ItemCompare
{
    public static volatile bool Enabled = true;
    private const int Max = 2;
    private static readonly int[] _idx = { -1, -1 };
    private static readonly nint[] _tip = new nint[Max];
    private static readonly int[] _textItem = new int[Max];
    private static readonly nint[] _text = new nint[Max * 3]; // vector de texto de cada ficha (primero, último, fin)
    private static int _lastHover, _errors, _logs;

    public static void Init(Config cfg) => Enabled = cfg.GetBool("Items", "Compare", true);

    /// <summary>Antes de dibujar una ficha: ¿es de un objeto y está pedida en este fotograma? (no una nuestra)</summary>
    public static bool IsCandidate(nint t, out int id)
    {
        id = 0;
        if (!Enabled || t == 0 || t == _tip[0] || t == _tip[1]) return false;
        try
        {
            if ((*(uint*)(t + 0x10) & 1) == 0 || (*(uint*)(t + 0x154) & 1) == 0) return false;
            if (*(int*)(t + 0x21D4) != *(int*)0xCD7A50) return false;
            id = *(int*)(t + 0x21D0);
            return id != 0;
        }
        catch { return false; }
    }

    /// <summary>Después de dibujar la ficha del objeto id: dibuja al lado la del equipado (ya con su posición final).</summary>
    public static void DrawFor(nint hover, int item, nint device)
    {
        try
        {
            nint mgr = *(nint*)0x17EEBBC, om = *(nint*)0xAD5C40, db = *(nint*)0xAAB5E4;
            if (mgr == 0 || om == 0 || db == 0) return;
            nint hero = ((delegate* unmanaged[Thiscall]<nint, nint>)0x603E30)(om);
            if (hero == 0) return;
            var getObj = (delegate* unmanaged[Thiscall]<nint, int, nint>)0x5FE000;
            var category = (delegate* unmanaged[Thiscall]<nint, int, int>)0x426220;
            nint o = getObj(om, item);
            if (o == 0 || Cast(o) == 0) return;
            int cat = category(db, *(int*)(o + 0x10));
            int* eq = stackalloc int[Max];
            int n = 0;
            for (int s = 0; s < 19; s++)
            {
                int id = *(int*)(hero + 0x1A4 + s * 4);
                if (id == 0) continue;
                if (id == item) return; // es uno de los equipados
                nint e = getObj(om, id);
                if (e == 0 || Cast(e) == 0) continue;
                if (n < Max && category(db, *(int*)(e + 0x10)) == cat) eq[n++] = id;
            }
            if (item != _lastHover)
            {
                _lastHover = item;
                if (_logs++ < 10) Log.Info($"Comparar: objeto {item} (categoría {cat}), equipados {n}{(n > 0 ? $": {eq[0]}" : "")}{(n > 1 ? $", {eq[1]}" : "")}");
            }
            if (n == 0) return;

            int hx = *(int*)(hover + 0x24), hy = *(int*)(hover + 0x28), hw = *(short*)(hover + 0x2C);
            int left = hx, right = hx + hw;
            for (int k = 0; k < n; k++)
            {
                nint t = Ensure(mgr, k);
                if (t == 0) return;
                // como showItemTooltip (0x75B390), sin el indicador 0x40: la colocación la decidimos aquí
                ((delegate* unmanaged[Thiscall]<nint, int, void>)0x6E8890)(t, -1);
                ((delegate* unmanaged[Thiscall]<nint, int, void>)0x6E8860)(t, 0xB);
                ((delegate* unmanaged[Thiscall]<nint, int, void>)0x6E69E0)(t, 1);
                if (_textItem[k] != eq[k]) { MakeText(k, getObj(om, eq[k])); _textItem[k] = eq[k]; }
                if (_text[k * 3] == 0) return;
                fixed (nint* v = &_text[k * 3])
                    ((delegate* unmanaged[Thiscall]<nint, int, nint*, int, void>)0x6E6EA0)(t, 0, v, 0);
                *(int*)(t + 0x21D0) = eq[k]; *(int*)(t + 0x21DC) = 0;
                *(short*)(t + 0x2C) = 1024; *(short*)(t + 0x2E) = 768; // (la colocación mide el texto dentro de este tamaño)
                *(int*)(t + 0x24) = hx; *(int*)(t + 0x28) = hy;
                ((delegate* unmanaged[Thiscall]<nint, void>)0x6E7AF0)(t);
                int w = *(short*)(t + 0x2C);
                int x = left - w - 4;
                if (x < 16) { x = right + 4; right = x + w; } else left = x;
                *(int*)(t + 0x24) = x; *(int*)(t + 0x28) = hy;
                ((delegate* unmanaged[Thiscall]<nint, int, void>)0x732550)(t, 1);
                ((delegate* unmanaged[Thiscall]<nint, nint, uint>)0x6E76C0)(t, device); // se oculta sola al acabar (indicador 1)
            }
        }
        catch (Exception ex) { if (_errors++ < 3) Log.Error("Comparar: " + ex.Message); }
    }

    private static nint Cast(nint o) =>
        ((delegate* unmanaged[Cdecl]<nint, int, nint, nint, int, nint>)0x84A961)(o, 0, 0x8EB648, 0x94EBC0, 0); // cObject → cItemBase

    /// <summary>Nuestra ficha k (la crea si hace falta o si el juego la ha quitado).</summary>
    private static nint Ensure(nint mgr, int k)
    {
        var get = (delegate* unmanaged[Thiscall]<nint, int, nint>)0x75B1E0;
        if (_idx[k] >= 0 && _tip[k] != 0 && get(mgr, _idx[k]) == _tip[k]) return _tip[k];
        int idx = ((delegate* unmanaged[Thiscall]<nint, int, int, int>)0x75AF90)(mgr, 1, 0xB) & 0xFFFF;
        nint t = get(mgr, idx);
        _idx[k] = t != 0 ? idx : -1; _tip[k] = t; _textItem[k] = 0;
        if (t != 0) { ((delegate* unmanaged[Thiscall]<nint, int, void>)0x7325C0)(t, 1); Log.Info($"Comparar: ficha {k} creada (índice {idx})"); }
        return t;
    }

    /// <summary>Texto de la ficha del objeto (el mismo que pone el juego); se guarda hasta que cambie el objeto.</summary>
    private static void MakeText(int k, nint obj)
    {
        FreeText(k);
        nint item = obj != 0 ? Cast(obj) : 0;
        if (item == 0) return;
        nint buf = ((delegate* unmanaged[Cdecl]<int, nint>)0x8321A0)(0x10);
        if (buf == 0) return;
        *(ushort*)buf = 0;
        fixed (nint* v = &_text[k * 3])
        {
            v[0] = buf; v[1] = buf; v[2] = buf + 0x10;
            var desc = (delegate* unmanaged[Thiscall]<nint, nint*, int, int, int, void>)(*(nint**)item)[0x44 / 4];
            desc(item, v, 0, 0, 0x5F);
            AddHeader(v);
        }
    }

    private static int _textLogs;

    /// <summary>
    /// Pone una primera línea «Equipado» para distinguir esta ficha de la del objeto. El vector se cambia por uno
    /// reservado con operator new (0x849842) de más de 0x80 bytes, que 0x41D3B0 libera con operator delete.
    /// </summary>
    private static void AddHeader(nint* v)
    {
        ushort* a = (ushort*)v[0], last = (ushort*)v[1];
        if (a == null || last < a) return;
        int len = 0;
        while (a + len < last && a[len] != 0) len++;
        bool withNul = a + len < last; // ¿el vector incluye el 0 final?
        if (_textLogs++ < 2)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Math.Min(len, 300); i++) { ushort c = a[i]; sb.Append(c < 32 || c > 126 ? $"<{c:X}>" : ((char)c).ToString()); }
            Log.Info($"Comparar: texto ({len} car., con 0 final: {withNul}): {sb}");
        }
        // el texto del juego lleva códigos: \cAARRGGBB color, \g icono, <A> = 0x0A salto de línea
        string head = "\\cffe8c878" + GameText.Get(GameText.Current, D9.Lang.T("Equipado")) + (char)10; // («Current» del juego)
        int total = head.Length + len + 1;
        int cap = Math.Max(total, 0x48);
        ushort* b = (ushort*)((delegate* unmanaged[Cdecl]<nint, nint>)0x849842)(cap * 2);
        if (b == null) return;
        for (int i = 0; i < head.Length; i++) b[i] = head[i];
        for (int i = 0; i < len; i++) b[head.Length + i] = a[i];
        b[head.Length + len] = 0;
        ((delegate* unmanaged[Thiscall]<nint, nint, int, void>)0x41D3B0)(0, v[0], (int)((v[2] - v[0]) / 2));
        v[0] = (nint)b;
        v[1] = (nint)(b + head.Length + len + (withNul ? 1 : 0));
        v[2] = (nint)(b + cap);
    }

    private static void FreeText(int k)
    {
        nint a = _text[k * 3], end = _text[k * 3 + 2];
        if (a != 0) ((delegate* unmanaged[Thiscall]<nint, nint, int, void>)0x41D3B0)(0, a, (int)((end - a) / 2));
        _text[k * 3] = _text[k * 3 + 1] = _text[k * 3 + 2] = 0;
    }
}
