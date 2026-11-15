using System;
using SacredFix.Core;

namespace SacredFix.Proxy;

/// <summary>
/// Recogida automática del oro al pasar por encima, sin agacharse ni hacer clic (solo partida individual).
///
/// El juego no la tiene: PICKUPAUTO de Settings.cfg solo es el filtro de la tecla «recoger todo» (0 todo, 1 filtro,
/// 2 solo oro; 0x4EC3AC), que hace andar al héroe hasta cada objeto. Al llegar, el juego recorre las 3×3 casillas
/// de alrededor (0x4ECED5) y llama a cCreature::pickupItem (0x558080) con lo que pasa el filtro 0x4EC330; con
/// PICKUPANIM=0 llama a pickupItem directamente sin animación (0x53CEB0). Aquí se hace lo mismo en cada fotograma
/// de la partida, solo con el oro:
///   - héroe: 0x603E30 (thiscall [0xAD5C40]); +0xC id, +0x18 posición, +0x2C capa.
///   - casilla de la posición: 0x6224E0 (thiscall struct de 16 bytes, &pos); 53,67 unidades por casilla.
///   - coordenada de casilla: 0x6224B0 (thiscall struct, 0, x, y, 0).
///   - objetos de la casilla: 0x634C50 (thiscall [[0xAD5C40]], &coord, capa) → +8 id del primero; objeto+0x30 el siguiente.
///   - objeto por id: 0x5FE000 (thiscall [0xAD5C40], id); +0xC id, +0x10 tipo.
///   - ¿es oro?: 0x43AF50 (thiscall [0xAAB5E4], tipo). Filtro de recogida: 0x4EC330 (thiscall héroe, objeto).
/// En red ([0x182EBEC] != 0) no hace nada, como la recogida del juego (UI_NET_NO_AUTOPICKUP).
/// </summary>
internal static unsafe class AutoGold
{
    public static volatile bool Enabled = true;
    private static int _errors, _picked;

    public static void Init(Config cfg) => Enabled = cfg.GetBool("Items", "AutoGold", true);

    /// <summary>Llamado en los fotogramas de la partida (UiCanvas, con MiniMap.InGameView()).</summary>
    public static void Tick()
    {
        if (!Enabled || *(byte*)0x182EBEC != 0) return;
        try
        {
            nint om = *(nint*)0xAD5C40;
            if (om == 0) return;
            nint world = *(nint*)om;
            nint goldTab = *(nint*)0xAAB5E4;
            if (world == 0 || goldTab == 0) return;
            nint hero = ((delegate* unmanaged[Thiscall]<nint, nint>)0x603E30)(om);
            if (hero == 0) return;

            var toCell = (delegate* unmanaged[Thiscall]<nint, nint, nint>)0x6224E0;
            var mkCell = (delegate* unmanaged[Thiscall]<nint, int, int, int, int, nint>)0x6224B0;
            var cellObjs = (delegate* unmanaged[Thiscall]<nint, nint, int, nint>)0x634C50;
            var getObj = (delegate* unmanaged[Thiscall]<nint, int, nint>)0x5FE000;
            var isGold = (delegate* unmanaged[Thiscall]<nint, int, byte>)0x43AF50;
            var filter = (delegate* unmanaged[Thiscall]<nint, nint, byte>)0x4EC330;
            var pickup = (delegate* unmanaged[Thiscall]<nint, int, byte>)0x558080;

            byte* pos = stackalloc byte[32];
            byte* cell = stackalloc byte[32];
            toCell((nint)pos, hero + 0x18);
            int cx = *(int*)(pos + 4), cy = *(int*)(pos + 8);
            int layer = *(int*)(hero + 0x2C);

            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    mkCell((nint)cell, 0, cx + dx, cy + dy, 0);
                    nint c = cellObjs(world, (nint)cell, layer);
                    if (c == 0) continue;
                    int id = *(int*)(c + 8);
                    for (int guard = 0; id != 0 && guard < 64; guard++)
                    {
                        nint o = getObj(om, id);
                        if (o == 0) break;
                        int next = *(int*)(o + 0x30);
                        if (isGold(goldTab, *(int*)(o + 0x10)) != 0 && filter(hero, o) != 0)
                        {
                            pickup(hero, *(int*)(o + 0xC));
                            if (_picked++ < 5) Log.Info($"AutoGold: oro recogido (objeto {id})");
                            break; // la lista de la casilla ha cambiado: el resto, en el fotograma siguiente
                        }
                        id = next;
                    }
                }
        }
        catch (Exception ex) { if (_errors++ < 3) Log.Error("AutoGold: " + ex.Message); }
    }
}
