using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Patches;

/// <summary>
/// Nombres de los objetos del suelo fijos: una pulsación corta de Alt los deja visibles (y se pueden pinchar) hasta la
/// siguiente, en vez de tener que mantener Alt.
///
/// El juego no usa sus mensajes de teclado para esto: pregunta GetAsyncKeyState(VK_MENU) en cada sitio.
///   0x6271B9  dibujo del mundo: con Alt, marca 0x4000 en [vista + 0x10] y dibuja las etiquetas de los objetos;
///   0x626F1E  al elegir qué hay bajo el cursor, da prioridad a las etiquetas (tabla 0x95F944 en vez de 0x95F934);
///   0x611F75  cursor sobre el mundo (cEngine_updateWorldCursor): con Alt, busca la etiqueta bajo el ratón (0x6270A0);
///   0x61764C  clic en el mundo: igual, para coger el objeto pinchando su etiqueta;
///   0x60F187  copia el estado de Alt en [0x182EE40] (bits 0x30), que lee 0x599ACC con los objetos del mundo.
/// Cada «call [GetAsyncKeyState]» (FF 15 B0 02 89 00) pasa a «call AltKey / nop»: Alt real o modo fijo activo.
/// 0x6C8330 (interfaz, Alt + otra marca) no se toca.
///
/// La pulsación corta se detecta en el gancho de teclado de bajo nivel (KeyInput): Alt sola, soltada en menos de
/// 300 ms y sin otra tecla en medio (Alt+Tab, Alt+F4... no cuentan). [Items] AltToggle=0 la desactiva.
/// </summary>
internal sealed unsafe class ItemLabels : IPatch
{
    public string Name => "Nombres de objetos con Alt fijo";
    public string IniKey => "ItemLabels";
    public bool EnabledByDefault => true;

    private static readonly nint[] Sites = { 0x6271B9, 0x626F1E, 0x611F75, 0x61764C, 0x60F187 };
    private static ReadOnlySpan<byte> CallIat => new byte[] { 0xFF, 0x15, 0xB0, 0x02, 0x89, 0x00 };

    /// <summary>Pulsación corta de Alt = fijar o quitar los nombres.</summary>
    public static volatile bool TapToggle = true;
    /// <summary>Nombres fijos ahora mismo.</summary>
    public static volatile bool Show;

    public void Apply(Config cfg)
    {
        TapToggle = cfg.GetBool("Items", "AltToggle", true);
        foreach (var s in Sites)
            if (!Mem.Matches(s, CallIat))
                throw new InvalidOperationException($"bytes inesperados en 0x{s:X8}: {Mem.Hex(s, 6)}");
        nint fn = (nint)(delegate* unmanaged[Stdcall]<int, int>)&AltKey;
        foreach (var s in Sites)
        {
            byte[] code = new byte[6];
            code[0] = 0xE8;
            BitConverter.TryWriteBytes(code.AsSpan(1), (int)(fn - (s + 5)));
            code[5] = 0x90;
            Mem.WriteBytes(s, code);
        }
        InstallFilter(cfg);
        Log.Info($"[{Name}] {Sites.Length} consultas de Alt redirigidas; pulsación corta de Alt {(TapToggle ? "activa" : "desactivada")}");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int AltKey(int vk)
    {
        // a través de la IAT (0x8902B0), que puede tener otro gancho (KeyInput: clic derecho de lanzar con una tecla)
        int r = (short)((delegate* unmanaged[Stdcall]<int, int>)*(nint*)0x8902B0)(vk);
        if (vk != 0x12) return r;
        // Alt invierte el modo: sin nombres fijos, mantenerla los muestra; con nombres fijos, mantenerla los oculta
        bool down = ((r & 0x8000) != 0) ^ Show;
        r = down ? r | unchecked((short)0x8000) : r & 0x7FFF;
        // Copia del estado de Alt en [0x182EE40] (bits 0x30): el juego solo la actualiza en 0x60F187, mientras se
        // mantiene el botón, y la lee 0x599ACC con los objetos del mundo. Sin esto, tras un clic se quedaba con el
        // valor antiguo y no seguía al modo fijo. Se actualiza en cada consulta (0x6271B9: una por fotograma).
        int* copy = (int*)0x182EE40;
        *copy = (r & 0x8000) != 0 ? *copy | 0x30 : *copy & ~0x30;
        return r;
    }

    // ---- Filtro por calidad (color del nombre) ----
    // Primer bucle del dibujo de etiquetas (0x627264): por cada entrada de la lista [vista + 0x96B30] (0x1C bytes),
    // 0x5FE000 (thiscall sobre [0xAD5C40], id) da el objeto; en 0x627285 «mov ebx, eax / test ebx, ebx / je 0x627615»
    // (0x627615 = siguiente entrada, sin etiqueta). El color del nombre es objeto->vtable[+0x24](2, 0) (0x627964; con el
    // ratón encima pasa a 0xFFFFFF80); con la propiedad 0x43 el juego usa otro objeto (contenido), igual que aquí.
    // Solo filtra en el modo fijo sin Alt real: manteniendo Alt con el modo apagado se ve todo, como en el original.
    private const nint FilterSite = 0x627285, FilterNext = 0x62728F, FilterSkip = 0x627615;
    private static readonly System.Collections.Generic.HashSet<uint> _hide = new();
    public static volatile bool FilterOn = true;
    /// <summary>[Items] MinQuality: 0 todos, 1 mágicos (azul) y mejores, 2 raros (amarillo) y mejores, 3 solo set y únicos.</summary>
    public static volatile int MinQuality;

    /// <summary>
    /// Calidad del objeto, como la calcula el juego antes de elegir el color del nombre (0x5C13C0, propiedad 2):
    ///   set: entrada del tipo en la base de datos (0x434030 sobre [0xAAB5E4], tipo [obj + 0x10]) con +0x23 != 0 (verde 0xFF15FF25);
    ///   si no, bits 0xF0000 de [obj + 0x118 + 4]: hasta 0x40000 normal (0xFFEFEFEF), hasta 0xC0000 mágico (0xFFA0A0FF),
    ///   hasta 0xF0000 raro (0xFFF0E040), 0xF0000 único (0xFFC19A33).
    /// Los objetos de una clase llevan el color de la clase (0x5C12F0 / 0x43C250), por eso no se usa el color.
    /// Devuelve 0 normal, 1 mágico, 2 raro, 3 set o único; -1 si no es equipo (oro, etc.: se muestra siempre).
    /// Es equipo si el color es uno de los que pone 0x5C13C0 (ItemColors).
    /// </summary>
    // Colores que pone 0x5C13C0: los cinco de calidad y los de clase (0x43C250). Si el nombre lleva uno de ellos, el objeto
    // es equipo (cItemBase y derivadas, con el dato en +0x118); los demás (oro, 0xFFFFFFFF...) se muestran siempre.
    private static readonly uint[] ItemColors =
    {
        0xFFEFEFEF, 0xFFA0A0FF, 0xFFF0E040, 0xFFC19A33, 0xFF15FF25,
        0xFF87CEFA, 0xFFE38217, 0xFF6C4CE5, 0xFFA020F0, 0xFF228B22, 0xFFB75555, 0xFF00FFFF, 0xFFFF6C00,
    };

    private static int QualityOf(nint o, uint color) => QualityOf(o, color, out _);

    private static int QualityOf(nint o, uint color, out bool isSet)
    {
        isSet = false;
        if (Array.IndexOf(ItemColors, color) < 0) return -1;
        nint db = *(nint*)0xAAB5E4;
        if (db != 0)
        {
            nint e = ((delegate* unmanaged[Thiscall]<nint, int, nint>)0x434030)(db, *(int*)(o + 0x10));
            if (e != 0 && *(byte*)(e + 0x23) != 0) { isSet = true; return 3; }
        }
        int q = *(int*)(o + 0x118 + 4) & 0xF0000;
        return q <= 0x40000 ? 0 : q < 0xC0000 ? 1 : q < 0xF0000 ? 2 : 3;
    }

    private static void InstallFilter(Config cfg)
    {
        FilterOn = cfg.GetBool("Items", "Filter", true);
        MinQuality = Math.Clamp(cfg.GetInt("Items", "MinQuality", 0), 0, 3);
        foreach (var part in cfg.GetString("Items", "HideColors", "").Split(',', ';', ' '))
            if (uint.TryParse(part.Trim().Replace("0x", "").Replace("0X", ""), System.Globalization.NumberStyles.HexNumber, null, out uint c)) _hide.Add(c);
        if (!Mem.Matches(FilterSite, new byte[] { 0x8B, 0xD8, 0x85, 0xDB, 0x0F, 0x84 }))
        { Log.Warn($"[{Name0}] filtro: bytes inesperados en 0x{FilterSite:X8}: {Mem.Hex(FilterSite, 10)}"); return; }
        byte[] st =
        {
            0x8B, 0xD8,                   // mov ebx, eax
            0x85, 0xDB,                   // test ebx, ebx
            0x0F, 0x84, 0, 0, 0, 0,       // je siguiente
            0x60,                         // pushad
            0x53,                         // push ebx
            0xE8, 0, 0, 0, 0,             // call Filter (stdcall)
            0x85, 0xC0,                   // test eax, eax
            0x61,                         // popad (no toca las banderas)
            0x0F, 0x84, 0, 0, 0, 0,       // je siguiente (oculto)
            0xE9, 0, 0, 0, 0,             // jmp 0x62728F
        };
        nint stub = Mem.AllocCode(st);
        nint fn = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Filter;
        Mem.Write(stub + 6, (int)(FilterSkip - (stub + 10)));
        Mem.Write(stub + 13, (int)(fn - (stub + 17)));
        Mem.Write(stub + 22, (int)(FilterSkip - (stub + 26)));
        Mem.Write(stub + 27, (int)(FilterNext - (stub + 31)));
        Mem.WriteJump(FilterSite, stub);
        Log.Info($"[{Name0}] filtro por color en 0x{FilterSite:X8} ({(FilterOn ? "activo" : "desactivado")}, calidad mínima {MinQuality}, colores ocultos aparte: {_hide.Count})");
    }
    private const string Name0 = "Nombres de objetos con Alt fijo";

    /// <summary>
    /// Calidad de un objeto del suelo por su id de la lista de etiquetas (con la redirección 0x43 del contenido, como el
    /// juego): -1 si no es equipo o no existe.
    /// </summary>
    public static int Inspect(int id, out bool isSet)
    {
        isSet = false;
        nint mgr = *(nint*)0xAD5C40;
        if (mgr == 0 || id < 0) return -1;
        var get = (delegate* unmanaged[Thiscall]<nint, int, nint>)0x5FE000;
        nint o = get(mgr, id);
        if (o == 0) return -1;
        var prop = (delegate* unmanaged[Thiscall]<nint, int, int, int>)(*(nint**)o)[0x24 / 4];
        int inner = prop(o, 0x43, 0);
        if (inner != 0)
        {
            nint o2 = get(mgr, inner);
            if (o2 != 0) { o = o2; prop = (delegate* unmanaged[Thiscall]<nint, int, int, int>)(*(nint**)o)[0x24 / 4]; }
        }
        return QualityOf(o, (uint)prop(o, 2, 0), out isSet);
    }

    /// <summary>1 = dibujar la etiqueta, 0 = ocultarla.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Filter(nint obj)
    {
        try
        {
            nint o = obj;
            var prop = (delegate* unmanaged[Thiscall]<nint, int, int, int>)(*(nint**)o)[0x24 / 4];
            int inner = prop(o, 0x43, 0);
            if (inner != 0)
            {
                nint mgr = *(nint*)0xAD5C40;
                nint o2 = mgr != 0 ? ((delegate* unmanaged[Thiscall]<nint, int, nint>)0x5FE000)(mgr, inner) : 0;
                if (o2 != 0) { o = o2; prop = (delegate* unmanaged[Thiscall]<nint, int, int, int>)(*(nint**)o)[0x24 / 4]; }
            }
            uint color = (uint)prop(o, 2, 0);
            int quality = QualityOf(o, color);
            if (!FilterOn || !Show) return 1;
            if ((((delegate* unmanaged[Stdcall]<int, int>)*(nint*)0x8902B0)(0x12) & 0x8000) != 0) return 1;
            if (_hide.Contains(color) || _hide.Contains(color & 0xFFFFFF)) return 0;
            return quality < 0 || quality >= MinQuality ? 1 : 0;
        }
        catch { return 1; }
    }

    /// <summary>Cambia el modo fijo (desde el gancho de teclado).</summary>
    public static void Toggle()
    {
        Show = !Show;
        Log.Info($"ItemLabels: nombres de objetos {(Show ? "fijos" : "solo con Alt")}");
    }
}
