using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using SacredFix.Core;

namespace SacredFix.Patches;

/// <summary>
/// Parche 2: forzar color de 32 bits aunque Settings.cfg diga GFX32 : 0.
///
/// En Windows 10 el modo de 16 bits no funciona: o el juego se cierra (sin modos de 16 bits) o se
/// cuelga al cargar las fuentes (con modos emulados).
///
/// El juego lee la opción con un getter, bool Config::GetGFX32() en 0x81C510 (thiscall, sin
/// argumentos). Sustituimos su inicio por un salto a nuestra función, que devuelve siempre 1.
/// No se toca Settings.cfg: el valor del archivo se ignora mientras el parche esté activo.
/// </summary>
internal sealed unsafe class Force32Bit : IPatch
{
    public string Name => "Forzar 32 bits";
    public string IniKey => "Force32Bit";
    public bool EnabledByDefault => true;

    /// <summary>Prólogo original de GetGFX32: push -1 / push 0x88CBF2 (manejador SEH)</summary>
    private static ReadOnlySpan<byte> ExpectedPrologue => new byte[] { 0x6A, 0xFF, 0x68, 0xF2, 0xCB, 0x88, 0x00 };

    private static int _calls;

    public void Apply(Config cfg)
    {
        if (!Mem.Matches(Addr.CfgGetGfx32, ExpectedPrologue))
            throw new InvalidOperationException($"bytes inesperados en 0x{Addr.CfgGetGfx32:X8}: {Mem.Hex(Addr.CfgGetGfx32, 7)}");

        delegate* unmanaged[Stdcall]<nint, int> hook = &GetGfx32Hook;
        nint thunk = Thunks.ThiscallToStdcall((nint)hook);
        Mem.WriteJump(Addr.CfgGetGfx32, thunk);
        Log.Info($"[{Name}] 0x{Addr.CfgGetGfx32:X8} -> JMP adaptador 0x{thunk:X8}");
    }

    /// <summary>Reemplazo de Config::GetGFX32(). 'self' es el objeto de configuración (ECX).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetGfx32Hook(nint self)
    {
        int n = Interlocked.Increment(ref _calls);
        if (n <= 4) Log.Info($"GetGFX32() consultado (#{n}) -> forzado a 1 (32 bits)");
        return 1; // AL = 1
    }
}
