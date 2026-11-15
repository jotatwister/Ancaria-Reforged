using System;
using SacredFix.Core;

namespace SacredFix.Patches;

/// <summary>
/// Parche: caché de objetos del mundo más grande (bajón de FPS con el zoom alejado en las ciudades).
///
/// En el bucle de dibujo (0x632869 -> 0x62D444 -> 0x6360xx), el juego guarda en un std::map (tamaño en
/// [ebx+0xDE60], cabeza en [ebx+0xDE5C]) los datos de cada objeto que dibuja. Si un objeto no está en el mapa, lo
/// lee del disco (fseek/fread) y lo añade. Cada entrada lleva en +0x24 el último fotograma en que se usó. Al añadir
/// con el mapa lleno (0x636101: cmp ecx, 0x1000), recorre las 4096 entradas buscando la más antigua para echarla.
///
/// Con el panorámico (1366x768) y el zoom alejado, en una ciudad se ven más de 4096 objetos distintos. Entonces casi
/// todos fallan en cada fotograma, y cada fallo recorre el mapa entero y lee del disco: los fotogramas pasan de
/// 16 ms a 60–100 ms (perfil del procesador: 0x833DE0, el «siguiente» del árbol, cerca del 25%).
/// Pasa con cualquier motor.
///
/// Arreglo: el tope pasa a [Fixes] ObjectCacheSize (16384 por defecto). Las entradas se reservan en memoria
/// dinámica, no en una tabla fija (el contador solo se lee en 0x634091 y 0x6360F7), así que el tope se puede subir sin
/// más. La caché solo crece hasta lo que de verdad se ve; lo viejo se sigue echando igual al llegar al tope.
/// </summary>
internal sealed class ObjectCache : IPatch
{
    public string Name => "Caché de objetos del mundo";
    public string IniKey => "ObjectCache";
    public bool EnabledByDefault => true;

    private const nint LimitImm = 0x636103; // cmp ecx, 0x1000 (81 F9 00 10 00 00) en 0x636101

    public void Apply(Config cfg)
    {
        int size = Math.Clamp(cfg.GetInt("Fixes", "ObjectCacheSize", 16384), 0x1000, 0x10000);
        if (!Mem.Matches(LimitImm - 2, new byte[] { 0x81, 0xF9 }))
            throw new InvalidOperationException($"bytes inesperados en 0x{LimitImm - 2:X8}: {Mem.Hex(LimitImm - 2, 6)}");
        Mem.ReplaceImm32(LimitImm, 0x1000, size);
        Log.Info($"[{Name}] tope de 4096 -> {size} objetos (0x{LimitImm - 2:X8})");
    }
}
