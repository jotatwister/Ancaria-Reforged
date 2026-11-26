using System;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Patches;

/// <summary>
/// Parche: más baldosas de agua por fotograma, y con tope. Sin él, el juego se cierra con zoom alejado y mucha agua en
/// pantalla con resoluciones anchas (reportado: puente de Silver Creek, costa de «A Tragic Loss»).
///
/// Al recorrer las filas de baldosas (0x62AE90), cada baldosa de agua se apunta (0x98 bytes) en una tabla de la vista en
/// +0x3FF2C, con el contador en +0x80E3C (se pone a 0 en 0x63260C). La tabla tiene sitio para 1750 y, a diferencia de la
/// de capas (ver LayeredTiles), NO tiene tope: la entrada 1750 cae justo encima del contador (su primer campo es un
/// puntero), y la siguiente escribe muy lejos: cierre en 0x62B57C (fstp). Las notas de sacred-bild ya lo señalaban.
///
/// Arreglo: tabla propia de [Fixes] WaterTilesMax entradas y tope en el contador.
///   10 escrituras 0x62B569..0x62B667  [ebp+reg*8+0x3FFxx]  → [reg*8+tabla+desplazamiento] (mod 10 → 00, misma longitud)
///   0x62DDD7 lea esi, [edi+0x3FF44]   → mov esi, tabla+0x18 ; nop   (dibujado del agua, 0x62DD30)
///   0x62E0E2 lea ebp, [edi+0x3FF3C]   → mov ebp, tabla+0x10 ; nop   (segunda pasada)
///   0x62B675 (inc del contador)       → salto a un trozo que no pasa de WaterTilesMax
/// El contador sigue en la vista. Las vistas se llenan y se dibujan de una en una en el mismo fotograma.
/// </summary>
internal sealed unsafe class WaterTiles : IPatch
{
    public string Name => "Baldosas de agua";
    public string IniKey => "WaterTiles";
    public bool EnabledByDefault => true;

    private const int Stride = 0x98, Base = 0x3FF2C;

    // instrucción, posición del byte ModRM, desplazamiento original
    private static readonly (nint at, int modrm, int disp)[] Writes =
    {
        (0x62B569, 1, 0x3FF2C), (0x62B57C, 1, 0x3FF30), (0x62B599, 1, 0x3FF34), (0x62B5C0, 1, 0x3FF38),
        (0x62B5DB, 1, 0x3FF3C), (0x62B5F3, 2, 0x3FF40), (0x62B607, 1, 0x3FF44), (0x62B627, 1, 0x3FF64),
        (0x62B647, 1, 0x3FF84), (0x62B667, 1, 0x3FFA4),
    };

    public void Apply(Config cfg)
    {
        int max = Math.Clamp(cfg.GetInt("Fixes", "WaterTilesMax", 16384), 1750, 0x10000);

        foreach (var (at, modrm, disp) in Writes)
        {
            byte m = *(byte*)(at + modrm);
            if ((m & 0xC7) != 0x84 || (*(byte*)(at + modrm + 1) & 7) != 5 || *(int*)(at + modrm + 2) != disp)
                throw new InvalidOperationException($"bytes inesperados en 0x{at:X8}: {Mem.Hex(at, modrm + 6)}");
        }
        Check(0x62DDD7, "8D B7 44 FF 03 00");
        Check(0x62E0E2, "8D AF 3C FF 03 00");
        Check(0x62B675, "8B 95 3C 0E 08 00 8B 4C 24 20 42 83 F9 04 89 95 3C 0E 08 00");

        // una entrada de sobra: con el contador en el tope, la siguiente se escribe en ella y no se dibuja
        nint table = (nint)NativeMemory.AllocZeroed((nuint)(max + 1) * Stride);

        Span<byte> b = stackalloc byte[8];
        foreach (var (at, modrm, disp) in Writes)
        {
            int n = modrm + 6;
            new ReadOnlySpan<byte>((void*)at, n).CopyTo(b);
            b[modrm] = (byte)(b[modrm] & 0x3F);                                    // mod 10 -> 00: [índice*8 + disp32]
            BitConverter.TryWriteBytes(b[(modrm + 2)..], (int)table + (disp - Base));
            Mem.WriteBytes(at, b[..n]);
        }
        b[0] = 0xBE; BitConverter.TryWriteBytes(b[1..], (int)table + 0x18); b[5] = 0x90;   // mov esi, tabla+0x18 ; nop
        Mem.WriteBytes(0x62DDD7, b[..6]);
        b[0] = 0xBD; BitConverter.TryWriteBytes(b[1..], (int)table + 0x10); b[5] = 0x90;   // mov ebp, tabla+0x10 ; nop
        Mem.WriteBytes(0x62E0E2, b[..6]);

        // contador con tope (lo mismo que el original más «cmp edx, máximo / jae»)
        byte[] stub =
        {
            0x8B, 0x95, 0x3C, 0x0E, 0x08, 0x00,         // mov edx, [ebp+0x80E3C]
            0x8B, 0x4C, 0x24, 0x20,                     // mov ecx, [esp+0x20]
            0x81, 0xFA, 0, 0, 0, 0,                     // cmp edx, máximo
            0x73, 0x01,                                 // jae (sin subir)
            0x42,                                       // inc edx
            0x89, 0x95, 0x3C, 0x0E, 0x08, 0x00,         // mov [ebp+0x80E3C], edx
            0x83, 0xF9, 0x04,                           // cmp ecx, 4
            0xE9, 0, 0, 0, 0,                           // jmp 0x62B689 (con las banderas del cmp)
        };
        BitConverter.TryWriteBytes(stub.AsSpan(12), max);
        nint code = Mem.AllocCode(stub);
        int back = (int)(0x62B689 - (code + stub.Length));
        Mem.Write(code + stub.Length - 4, back);
        Mem.WriteJump(0x62B675, code);
        Span<byte> nops = stackalloc byte[15];
        nops.Fill(0x90);
        Mem.WriteBytes(0x62B67A, nops);

        Log.Info($"[{Name}] tope de 1750 (sin comprobar) -> {max} baldosas por fotograma (tabla en 0x{table:X8})");
    }

    private static int _logged = 1000, _errors;

    /// <summary>
    /// Diagnóstico: apunta en el registro cuántas baldosas de agua se ven cuando pasa de 1000 y cada 100 más (también con
    /// WaterTiles=0, para saber cuánto falta para el desbordamiento de 1750). Vista = la de TrackEnemy.
    /// </summary>
    public static void Tick()
    {
        try
        {
            nint eng = ((delegate* unmanaged[Cdecl]<int, nint>)0x60D6C0)(-1);
            if (eng == 0) return;
            nint view = *(nint*)(eng + 8 + *(ushort*)(eng + 0x48) * 4);
            if (view == 0) return;
            int n = *(int*)(view + 0x80E3C);
            if (n < _logged || n > 0x10000) return;
            _logged = n / 100 * 100 + 100;
            Log.Info($"[Baldosas de agua] {n} baldosas de agua en pantalla (zoom {*(float*)(view + 0xB60):0.##}; el juego original aguanta 1750)");
        }
        catch (Exception ex) { if (_errors++ < 3) Log.Error("WaterTiles: " + ex.Message); }
    }

    private static void Check(nint at, string hex)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", ""));
        if (!Mem.Matches(at, bytes)) throw new InvalidOperationException($"bytes inesperados en 0x{at:X8}: {Mem.Hex(at, bytes.Length)}");
    }
}
