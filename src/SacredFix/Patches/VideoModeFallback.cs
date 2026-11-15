using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SacredFix.Core;
using SacredFix.Native;

namespace SacredFix.Patches;

/// <summary>
/// Parche 1: que el juego no se cierre si no encuentra el modo de vídeo.
///
/// Problema: al iniciar los gráficos, el juego hace
///     entrada = find_mode(1024, 768, GFX32 ? 32 : 16);
/// y usa 'entrada' sin comprobar si es NULL. En Windows 8+ no hay modos de 16 bits, así que con
/// GFX32 : 0 devuelve NULL y el juego se cierra con un acceso a 0x00000000.
///
/// Solución: redirigimos ese CALL (0x816C79) a nuestra función. Llama al find_mode original y,
/// si devuelve NULL, elige el modo más parecido de la tabla (preferiendo la misma profundidad
/// de color). Si la tabla está vacía, explica el problema con un mensaje y cierra limpiamente.
/// </summary>
internal sealed unsafe class VideoModeFallback : IPatch
{
    public string Name => "Modo de vídeo de reserva";
    public string IniKey => "VideoModeFallback";
    public bool EnabledByDefault => true;

    /// <summary>Bytes esperados en 0x816C79: CALL 0x644130</summary>
    private static ReadOnlySpan<byte> ExpectedCall => new byte[] { 0xE8, 0xB2, 0xD4, 0xE2, 0xFF };

    // find_mode original (thiscall: this en ECX, 4 argumentos en la pila)
    private static delegate* unmanaged[Thiscall]<nint, int, int, int, int, nint> _original;
    private static bool _listed;

    public void Apply(Config cfg)
    {
        if (!Mem.Matches(Addr.CallFindModeInit, ExpectedCall))
            throw new InvalidOperationException($"bytes inesperados en 0x{Addr.CallFindModeInit:X8}: {Mem.Hex(Addr.CallFindModeInit, 5)}");

        delegate* unmanaged[Stdcall]<nint, int, int, int, int, nint> hook = &FindModeHook;
        nint thunk = Thunks.ThiscallToStdcall((nint)hook);
        nint orig = Mem.RedirectCall(Addr.CallFindModeInit, thunk);
        _original = (delegate* unmanaged[Thiscall]<nint, int, int, int, int, nint>)orig;
        Log.Info($"[{Name}] CALL 0x{Addr.CallFindModeInit:X8} -> adaptador 0x{thunk:X8} (original 0x{orig:X8})");
    }

    /// <summary>Reemplazo de find_mode. Recibe 'this' como primer argumento gracias al adaptador.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint FindModeHook(nint self, int width, int height, int bpp, int extra)
    {
        nint found = _original(self, width, height, bpp, extra);
        try
        {
            int count = Mem.Read<ushort>(self + Addr.DevModeCount);
            if (!_listed)
            {
                _listed = true;
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < count; i++)
                {
                    nint e = self + Addr.DevModeTable + i * Addr.ModeEntrySize;
                    sb.Append($"{Mem.Read<int>(e + Addr.ModeWidth)}x{Mem.Read<int>(e + Addr.ModeHeight)}x{Mem.Read<int>(e + Addr.ModeBpp)} ");
                }
                Log.Info($"Modos disponibles ({count}): {sb}");
            }
            if (found != 0)
            {
                Log.Info($"find_mode({width}x{height}x{bpp}) = OK ({count} modos en la tabla)");
                return found;
            }

            Log.Warn($"find_mode({width}x{height}x{bpp}) = NULL ({count} modos en la tabla). Buscando el más parecido…");
            nint best = 0;
            long bestScore = long.MaxValue;
            for (int i = 0; i < count; i++)
            {
                nint e = self + Addr.DevModeTable + i * Addr.ModeEntrySize;
                int w = Mem.Read<int>(e + Addr.ModeWidth);
                int h = Mem.Read<int>(e + Addr.ModeHeight);
                int b = Mem.Read<int>(e + Addr.ModeBpp);
                // Penaliza mucho otra profundidad de color; después, la diferencia de tamaño
                long score = (b == bpp ? 0 : 1_000_000) + Math.Abs(w - width) + Math.Abs(h - height);
                if (score < bestScore) { bestScore = score; best = e; }
            }

            if (best == 0)
            {
                Log.Error("La tabla de modos está vacía: no hay ningún modo de vídeo utilizable.");
                Win32.MessageBoxW(0,
                    "Sacred no ha encontrado ningún modo de vídeo compatible.\n\n" +
                    (bpp == 16
                        ? "Windows 8 y posteriores ya no ofrecen modos de 16 bits.\nAbre Settings.cfg y pon:  GFX32 : 1"
                        : "Revisa SacredFix.log en la carpeta del juego.") +
                    "\n\n(SacredFix)",
                    "Sacred — SacredFix", Win32.MB_OK | Win32.MB_ICONERROR);
                Win32.ExitProcess(1);
                return 0;
            }

            // Igual que el original al encontrar un modo: guarda 'extra' en la entrada
            Mem.Write(best + Addr.ModeExtra, extra);
            Log.Warn($"Usando en su lugar {Mem.Read<int>(best + Addr.ModeWidth)}x{Mem.Read<int>(best + Addr.ModeHeight)}x{Mem.Read<int>(best + Addr.ModeBpp)}");
            return best;
        }
        catch (Exception ex)
        {
            Log.Error("FindModeHook: " + ex.Message);
            return found;
        }
    }
}
