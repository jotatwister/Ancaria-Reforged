using System;
using SacredFix.Core;

namespace SacredFix.Patches;

/// <summary>
/// Cada parche es una clase con Name, IniKey y Apply(). Para añadir uno nuevo:
///   1. Crea la clase en esta carpeta implementando IPatch.
///   2. Añádela a la lista de abajo.
///   3. Documenta en el propio parche las direcciones y los bytes esperados.
/// Cada parche debe comprobar los bytes que va a modificar antes de tocarlos.
/// </summary>
internal interface IPatch
{
    string Name { get; }
    /// <summary>Clave en la sección [Fixes] de SacredFix.ini.</summary>
    string IniKey { get; }
    bool EnabledByDefault { get; }
    void Apply(Config cfg);
}

internal static class PatchManager
{
    private static readonly IPatch[] All =
    {
        new Force32Bit(),
        new VideoModeFallback(),
        new FocusFix(),
        new Widescreen(),
        new ObjectCache(),
        new ItemLabels(),
    };

    public static void ApplyAll(Config cfg)
    {
        foreach (var p in All)
        {
            if (!cfg.GetBool("Fixes", p.IniKey, p.EnabledByDefault))
            {
                Log.Info($"[{p.Name}] desactivado en SacredFix.ini");
                continue;
            }
            try
            {
                p.Apply(cfg);
                Log.Info($"[{p.Name}] aplicado");
            }
            catch (Exception ex)
            {
                Log.Error($"[{p.Name}] no se pudo aplicar: {ex.Message}");
            }
        }
    }
}
