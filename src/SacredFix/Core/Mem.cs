using System;
using SacredFix.Native;

namespace SacredFix.Core;

/// <summary>
/// Acceso a la memoria del propio proceso (el juego). Como estamos dentro de Sacred.exe,
/// basta con punteros; solo hay que quitar la protección de escritura del código antes de parchear.
/// </summary>
internal static unsafe class Mem
{
    public static T Read<T>(nint address) where T : unmanaged => *(T*)address;

    public static void Write<T>(nint address, T value) where T : unmanaged
        => WriteBytes(address, new ReadOnlySpan<byte>(&value, sizeof(T)));

    /// <summary>Escribe bytes en una zona de código (cambia la protección y la restaura).</summary>
    public static void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
    {
        if (!Win32.VirtualProtect(address, (nuint)bytes.Length, Win32.PAGE_EXECUTE_READWRITE, out uint old))
            throw new InvalidOperationException($"VirtualProtect falló en 0x{address:X8}");
        bytes.CopyTo(new Span<byte>((void*)address, bytes.Length));
        Win32.VirtualProtect(address, (nuint)bytes.Length, old, out _);
        Win32.FlushInstructionCache(Win32.GetCurrentProcess(), address, (nuint)bytes.Length);
    }

    /// <summary>¿Coinciden los bytes de memoria con los esperados? Sirve para confirmar versión y descifrado.</summary>
    public static bool Matches(nint address, ReadOnlySpan<byte> expected)
        => new ReadOnlySpan<byte>((void*)address, expected.Length).SequenceEqual(expected);

    public static string Hex(nint address, int count)
        => Convert.ToHexString(new ReadOnlySpan<byte>((void*)address, count));

    /// <summary>
    /// Redirige una instrucción CALL rel32 (E8 xx xx xx xx) a otra función.
    /// Devuelve el destino original, para poder llamarlo desde nuestro reemplazo.
    /// </summary>
    public static nint RedirectCall(nint callSite, nint newTarget)
    {
        if (Read<byte>(callSite) != 0xE8)
            throw new InvalidOperationException($"En 0x{callSite:X8} no hay un CALL rel32");
        nint original = callSite + 5 + Read<int>(callSite + 1);
        Write(callSite + 1, (int)(newTarget - (callSite + 5)));
        return original;
    }

    /// <summary>
    /// Cambia un valor inmediato de 32 bits dentro de una instrucción (p. ej. el 0x400 de "push 0x400"),
    /// comprobando antes que el valor actual es el esperado.
    /// </summary>
    public static void ReplaceImm32(nint at, int expected, int value)
    {
        int cur = Read<int>(at);
        if (cur != expected)
            throw new InvalidOperationException($"en 0x{at:X8} se esperaba {expected} (0x{expected:X}) y hay {cur} (0x{cur:X})");
        Write(at, value);
    }

    /// <summary>
    /// Cambia un valor inmediato de 32 bits dentro de la instrucción que empieza en <paramref name="instr"/>
    /// (sirve para push/mov/cmp con cualquier codificación). Busca el valor en los primeros 11 bytes
    /// de la instrucción y exige que aparezca una sola vez.
    /// </summary>
    public static void ReplaceImmInInstruction(nint instr, int expected, int value)
    {
        Span<byte> pat = stackalloc byte[4];
        BitConverter.TryWriteBytes(pat, expected);
        var code = new ReadOnlySpan<byte>((void*)instr, 11);
        int at = code.IndexOf(pat);
        if (at < 0)
            throw new InvalidOperationException($"no se encuentra {expected} (0x{expected:X}) en la instrucción 0x{instr:X8} ({Hex(instr, 11)})");
        if (code[(at + 1)..].IndexOf(pat) >= 0)
            throw new InvalidOperationException($"{expected} aparece dos veces en la instrucción 0x{instr:X8}");
        Write(instr + at, value);
    }

    /// <summary>
    /// Sustituye el inicio de una función por un salto (JMP rel32, 5 bytes) a otra.
    /// La función original deja de ejecutarse: úsalo solo cuando el reemplazo hace todo el trabajo.
    /// </summary>
    public static void WriteJump(nint at, nint target)
    {
        Span<byte> jmp = stackalloc byte[5];
        jmp[0] = 0xE9;
        BitConverter.TryWriteBytes(jmp[1..], (int)(target - (at + 5)));
        WriteBytes(at, jmp);
    }

    /// <summary>Reserva memoria ejecutable y copia en ella un trozo de código máquina.</summary>
    public static nint AllocCode(ReadOnlySpan<byte> code)
    {
        nint p = Win32.VirtualAlloc(0, (nuint)Math.Max(code.Length, 16),
                                    Win32.MEM_COMMIT | Win32.MEM_RESERVE, Win32.PAGE_EXECUTE_READWRITE);
        if (p == 0) throw new OutOfMemoryException("VirtualAlloc falló");
        code.CopyTo(new Span<byte>((void*)p, code.Length));
        Win32.FlushInstructionCache(Win32.GetCurrentProcess(), p, (nuint)code.Length);
        return p;
    }
}
