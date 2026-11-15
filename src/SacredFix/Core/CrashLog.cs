using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SacredFix.Core;

/// <summary>
/// Registro de fallos ([Debug] CrashLog=1, activo por defecto): un manejador de excepciones vectorizado que, ante un
/// acceso indebido a memoria (0xC0000005), escribe en el log la instrucción que falla (módulo + desplazamiento),
/// los registros y las direcciones de retorno candidatas de la pila (valores que apuntan dentro de un módulo).
/// No trata la excepción: el juego sigue como siempre (sus propios manejadores o el cierre).
/// Solo se registran las 4 primeras, para no llenar el log con excepciones que el juego maneje a propósito.
/// </summary>
internal static unsafe partial class CrashLog
{
    private static int _count;

    [LibraryImport("kernel32.dll")]
    private static partial nint AddVectoredExceptionHandler(uint first, nint handler);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetModuleHandleExW(uint flags, nint address, nint* module);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetModuleFileNameW(nint module, char* buffer, uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsBadReadPtr(nint p, nuint size);

    public static void Init(Config cfg)
    {
        if (!cfg.GetBool("Debug", "CrashLog", false)) return;
        AddVectoredExceptionHandler(1, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Handler);
        // Salidas del proceso por ExitProcess / TerminateProcess del juego (IAT 0x8900EC / 0x890178): quién las llama
        _exitProcess = (delegate* unmanaged[Stdcall]<uint, void>)Mem.Read<nint>(0x8900EC);
        Mem.Write<nint>(0x8900EC, (nint)(delegate* unmanaged[Stdcall]<uint, void>)&ExitProcessHook);
        _terminate = (delegate* unmanaged[Stdcall]<nint, uint, int>)Mem.Read<nint>(0x890178);
        Mem.Write<nint>(0x890178, (nint)(delegate* unmanaged[Stdcall]<nint, uint, int>)&TerminateHook);
    }

    private static delegate* unmanaged[Stdcall]<uint, void> _exitProcess;
    private static delegate* unmanaged[Stdcall]<nint, uint, int> _terminate;

    private static string Stack(nint esp)
    {
        var sb = new StringBuilder();
        int found = 0;
        for (int i = 0; i < 256 && found < 16; i++)
        {
            nint p = esp + i * 4;
            if (IsBadReadPtr(p, 4)) break;
            string? w = Where(*(nint*)p);
            if (w == null) continue;
            sb.Append($" {w}");
            found++;
        }
        return sb.ToString();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void ExitProcessHook(uint code)
    {
        try { int x; Log.Info($"CrashLog: ExitProcess({code}) llamado; pila:{Stack((nint)(&x))}"); } catch { }
        _exitProcess(code);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TerminateHook(nint process, uint code)
    {
        try { int x; Log.Info($"CrashLog: TerminateProcess(0x{process:X}, {code}) llamado; pila:{Stack((nint)(&x))}"); } catch { }
        return _terminate(process, code);
    }

    /// <summary>"modulo.dll+0x1234" o null si la dirección no está en ningún módulo.</summary>
    private static string? Where(nint a)
    {
        nint mod = 0;
        if (a < 0x10000 || !GetModuleHandleExW(6 /* FROM_ADDRESS | UNCHANGED_REFCOUNT */, a, &mod) || mod == 0) return null;
        char* buf = stackalloc char[260];
        uint n = GetModuleFileNameW(mod, buf, 260);
        string path = new string(buf, 0, (int)n);
        int slash = path.LastIndexOf('\\');
        return $"{path[(slash + 1)..]}+0x{a - mod:X}";
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Handler(nint pointers)
    {
        try
        {
            nint rec = *(nint*)pointers, ctx = *(nint*)(pointers + 4);
            uint code = *(uint*)rec;
            // accesos indebidos y los fallos graves que no se recuperan (pila, seguridad de pila, fail-fast, instrucción)
            bool fatal = code == 0xC0000409 || code == 0xC00000FD || code == 0xC0000602 || code == 0xC000001D || code == 0xC0000374;
            if ((code != 0xC0000005 && !fatal) || _count >= 8) return 0;
            _count++;
            nint eip = *(nint*)(ctx + 0xB8), esp = *(nint*)(ctx + 0xC4);
            var sb = new StringBuilder();
            sb.Append(code == 0xC0000005 ? $"CrashLog: acceso indebido a memoria n.º {_count} en 0x{eip:X} ({Where(eip) ?? "?"})"
                                         : $"CrashLog: excepción 0x{code:X8} en 0x{eip:X} ({Where(eip) ?? "?"})");
            if (*(uint*)(rec + 0x10) >= 2) sb.Append($", {(*(nint*)(rec + 0x14) == 0 ? "lectura" : "escritura")} de 0x{*(nint*)(rec + 0x18):X}");
            sb.Append($"\n    eax={*(uint*)(ctx + 0xB0):X8} ebx={*(uint*)(ctx + 0xA4):X8} ecx={*(uint*)(ctx + 0xAC):X8} edx={*(uint*)(ctx + 0xA8):X8} esi={*(uint*)(ctx + 0xA0):X8} edi={*(uint*)(ctx + 0x9C):X8} ebp={*(uint*)(ctx + 0xB4):X8} esp={esp:X8}");
            sb.Append("\n    pila (posibles retornos):");
            int found = 0;
            for (int i = 0; i < 256 && found < 24; i++)
            {
                nint p = esp + i * 4;
                if (IsBadReadPtr(p, 4)) break;
                nint v = *(nint*)p;
                string? w = Where(v);
                if (w == null) continue;
                sb.Append($" [esp+0x{i * 4:X}] {w}");
                found++;
            }
            Log.Error(sb.ToString());
        }
        catch { }
        return 0; // EXCEPTION_CONTINUE_SEARCH
    }
}
