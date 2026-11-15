using System;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Patches;

/// <summary>
/// Parche: más baldosas con capas de mezcla (hierba, detalles del suelo) por fotograma, para pantallas anchas.
///
/// Al recorrer las filas de baldosas (cWorldView_renderTileRow, 0x62AE90), cada baldosa con capas de mezcla se copia
/// (0x90 bytes) a una tabla de la vista en +0xB64, con una lista paralela de dwords en +0x3E3C4 y el contador en +0x3FF1C
/// (se pone a 0 en 0x632606). La tabla tiene sitio para 1750: al llegar a 0x6D5 (0x62B493) el contador deja de subir y
/// cada baldosa nueva sobrescribe la última. Las que se pierden se dibujan solo con el color base: a más de 1366 de
/// ancho, la hierba de los lados se veía lisa y con bordes en escalera hasta acercarse (lo cuentan también las notas de
/// sacred-bild, que solo se usan como datos). La tabla la recorre 0x62D3C0 al dibujar las capas.
///
/// Solo 4 sitios usan las tablas; se llevan a memoria propia de [Fixes] LayeredTilesMax entradas:
///   0x62B472 lea edi, [ecx+ebp+0xB64]          → lea edi, [ecx+tabla] ; nop
///   0x62B486 mov [ebp+edx*4+0x3E3C4], eax      → mov [edx*4+lista], eax
///   0x62B493 cmp eax, 0x6D5                    → cmp eax, máximo-1
///   0x62D418 lea eax, [edi+0x3E3C4]            → mov eax, lista ; nop
///   0x62D41E lea esi, [edi+0xB7C]              → mov esi, tabla+0x18 ; nop
/// El contador sigue en la vista. Las vistas se dibujan de una en una (se llena y se dibuja en el mismo fotograma), así
/// que basta con una tabla para todas.
/// </summary>
internal sealed unsafe class LayeredTiles : IPatch
{
    public string Name => "Baldosas con capas de mezcla";
    public string IniKey => "LayeredTiles";
    public bool EnabledByDefault => true;

    public void Apply(Config cfg)
    {
        int max = Math.Clamp(cfg.GetInt("Fixes", "LayeredTilesMax", 16384), 0x6D6, 0x10000);
        Check(0x62B472, "8D BC 29 64 0B 00 00");
        Check(0x62B486, "89 84 95 C4 E3 03 00");
        Check(0x62B493, "3D D5 06 00 00");
        Check(0x62D418, "8D 87 C4 E3 03 00");
        Check(0x62D41E, "8D B7 7C 0B 00 00");

        nint table = (nint)NativeMemory.AllocZeroed((nuint)max * 0x90);
        nint list = (nint)NativeMemory.AllocZeroed((nuint)max * 4);
        Span<byte> b = stackalloc byte[7];

        b[0] = 0x8D; b[1] = 0xB9; Imm(b, 2, (int)table); b[6] = 0x90;              // lea edi, [ecx+tabla] ; nop
        Mem.WriteBytes(0x62B472, b[..7]);
        b[0] = 0x89; b[1] = 0x04; b[2] = 0x95; Imm(b, 3, (int)list);               // mov [edx*4+lista], eax
        Mem.WriteBytes(0x62B486, b[..7]);
        Mem.ReplaceImm32(0x62B494, 0x6D5, max - 1);                                // cmp eax, máximo-1
        b[0] = 0xB8; Imm(b, 1, (int)list); b[5] = 0x90;                            // mov eax, lista ; nop
        Mem.WriteBytes(0x62D418, b[..6]);
        b[0] = 0xBE; Imm(b, 1, (int)(table + 0x18)); b[5] = 0x90;                  // mov esi, tabla+0x18 ; nop
        Mem.WriteBytes(0x62D41E, b[..6]);
        Log.Info($"[{Name}] tope de 1750 -> {max} baldosas por fotograma (tabla en 0x{table:X8})");
    }

    private static void Imm(Span<byte> b, int at, int v) => BitConverter.TryWriteBytes(b[at..], v);

    private static void Check(nint at, string hex)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", ""));
        if (!Mem.Matches(at, bytes)) throw new InvalidOperationException($"bytes inesperados en 0x{at:X8}: {Mem.Hex(at, bytes.Length)}");
    }
}
