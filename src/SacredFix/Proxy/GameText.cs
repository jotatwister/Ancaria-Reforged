using System;
using System.Collections.Generic;

namespace SacredFix.Proxy;

/// <summary>
/// Textos del propio juego (scripts\&lt;idioma&gt;\global.res), para que salgan en el idioma del juego y con sus traducciones.
/// Se piden como lo hace su interfaz: gestor de textos 0x6725E0() y 0x672C90 (thiscall gestor, id), que devuelve un objeto
/// cuyo +0 / +4 son el principio y el final de las letras (wchar). Los ids de global.res van con el bit alto
/// (id | 0x80000000); con un id que no exista devuelve un texto vacío. Ids usados:
///   0x84404628 «Level» y 0x84407730 «Survival Bonus» (ficha del personaje), 0x844074FB «Current».
/// </summary>
internal static unsafe class GameText
{
    public const uint Level = 0x84404628, SurvivalBonus = 0x84407730, Current = 0x844074FB;
    private static readonly Dictionary<uint, string> _cache = new();

    /// <summary>El texto del juego con ese id; si no está (o aún no se han cargado los textos), fallback.</summary>
    public static string Get(uint id, string fallback)
    {
        if (_cache.TryGetValue(id, out var s)) return s;
        try
        {
            nint mgr = ((delegate* unmanaged[Cdecl]<nint>)0x6725E0)();
            if (mgr == 0) return fallback;
            nint p = ((delegate* unmanaged[Thiscall]<nint, uint, nint>)0x672C90)(mgr, id);
            if (p == 0) return fallback;
            char* b = *(char**)p, e = *(char**)(p + 4);
            if (b == null || e <= b || e - b > 256) return fallback;
            s = new string(b, 0, (int)(e - b)).TrimEnd('\0').Trim();
        }
        catch { return fallback; }
        if (s.Length == 0) return fallback;
        _cache[id] = s;
        return s;
    }
}
