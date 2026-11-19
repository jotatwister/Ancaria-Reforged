using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using SacredFix.Core;

namespace SacredFix.Proxy;

/// <summary>
/// Diagnóstico ([Debug] HeroWatch=1): punto de vigilancia de hardware (DR0, escritura de 2 bytes) sobre el estado del
/// héroe (word +0xFC). Cada escritura lanza EXCEPTION_SINGLE_STEP justo después de la instrucción; un manejador
/// vectorizado apunta el valor, la instrucción y las direcciones de vuelta de la pila (dentro de Sacred.exe). Sirve
/// para saber qué código cancela una acción (caso del caballo con el panorámico).
/// </summary>
internal static unsafe class HeroWatch
{
    public static bool Enabled;
    private static nint _hero, _addr;
    private static int _logs, _armed;
    private static ushort _last;

    [DllImport("kernel32.dll")] private static extern nint AddVectoredExceptionHandler(uint first, delegate* unmanaged[Stdcall]<nint*, int> h);
    [DllImport("kernel32.dll")] private static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll")] private static extern int Thread32First(nint snap, int* te);
    [DllImport("kernel32.dll")] private static extern int Thread32Next(nint snap, int* te);
    [DllImport("kernel32.dll")] private static extern nint OpenThread(uint access, int inherit, uint id);
    [DllImport("kernel32.dll")] private static extern uint SuspendThread(nint h);
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(nint h);
    [DllImport("kernel32.dll")] private static extern int GetThreadContext(nint h, byte* ctx);
    [DllImport("kernel32.dll")] private static extern int SetThreadContext(nint h, byte* ctx);
    [DllImport("kernel32.dll")] private static extern int CloseHandle(nint h);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    public static void Init(Config cfg)
    {
        Enabled = cfg.GetBool("Debug", "HeroWatch", false);
        if (!Enabled) return;
        AddVectoredExceptionHandler(1, &Handler);
        Log.Info("HeroWatch: activado (vigilancia del estado del héroe +0xFC)");
    }

    /// <summary>En los fotogramas de la partida (UiCanvas).</summary>
    public static void Tick()
    {
        if (!Enabled) return;
        nint om = *(nint*)0xAD5C40;
        if (om != 0) Arm(((delegate* unmanaged[Thiscall]<nint, nint>)0x603E30)(om));
    }

    /// <summary>Con el héroe de la partida.</summary>
    public static void Arm(nint hero)
    {
        if (!Enabled || hero == 0 || hero == _hero || Interlocked.Exchange(ref _armed, 1) == 1) return;
        _hero = hero; _addr = hero + 0xFC; _last = *(ushort*)_addr;
        // los registros de depuración son por hilo: se ponen en todos desde un hilo aparte (que puede suspender a este)
        var t = new Thread(() => { try { SetAll(_addr); } catch (Exception ex) { Log.Error("HeroWatch: " + ex.Message); } finally { _armed = 0; } });
        t.IsBackground = true; t.Start();
    }

    private static void SetAll(nint addr)
    {
        uint self = GetCurrentThreadId(), pid = GetCurrentProcessId();
        nint snap = CreateToolhelp32Snapshot(4 /* TH32CS_SNAPTHREAD */, 0);
        int* te = stackalloc int[7];
        te[0] = 28;
        byte* ctx = (byte*)NativeMemory.AlignedAlloc(1024, 16);
        int n = 0;
        for (int ok = Thread32First(snap, te); ok != 0; ok = Thread32Next(snap, te))
        {
            if ((uint)te[3] != pid || (uint)te[2] == self) continue;
            nint h = OpenThread(0x0002 | 0x0008 | 0x0010 | 0x0040, 0, (uint)te[2]);
            if (h == 0) continue;
            SuspendThread(h);
            new Span<byte>(ctx, 1024).Clear();
            *(uint*)ctx = 0x00010010; // CONTEXT_DEBUG_REGISTERS
            if (GetThreadContext(h, ctx) != 0)
            {
                *(uint*)(ctx + 4) = (uint)addr;                      // Dr0
                *(uint*)(ctx + 0x18) = 1u | 1u << 16 | 1u << 18;     // Dr7: L0, escritura, 2 bytes
                if (SetThreadContext(h, ctx) != 0) n++;
            }
            ResumeThread(h);
            CloseHandle(h);
        }
        CloseHandle(snap);
        NativeMemory.AlignedFree(ctx);
        Log.Info($"HeroWatch: vigilando 0x{addr:X8} en {n} hilos");
    }

    /// <summary>EXCEPTION_POINTERS: [0] registro (código +0), [1] contexto (Dr6 +0x14, Ebp +0xB4, Eip +0xB8, Esp +0xC4).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Handler(nint* ep)
    {
        try
        {
            if (*(uint*)ep[0] != 0x80000004) return 0; // EXCEPTION_SINGLE_STEP
            byte* c = (byte*)ep[1];
            uint dr6 = *(uint*)(c + 0x14);
            if ((dr6 & 1) == 0) return 0;
            *(uint*)(c + 0x14) = 0;
            ushort v = *(ushort*)_addr;
            if (v != _last && _logs < 200)
            {
                _logs++;
                uint eip = *(uint*)(c + 0xB8), esp = *(uint*)(c + 0xC4);
                var sb = new System.Text.StringBuilder();
                int k = 0;
                for (uint p = esp; p < esp + 0x2000 && k < 16; p += 4)
                {
                    uint r = *(uint*)p;
                    if (r < 0x401000 || r >= 0x88F000) continue;
                    byte* b = (byte*)r;
                    if (b[-5] == 0xE8 || (b[-6] == 0xFF && b[-5] == 0x15) || (b[-2] == 0xFF && (b[-1] & 0x38) == 0x10) || (b[-3] == 0xFF && (b[-2] & 0x38) == 0x10))
                    { sb.Append($" 0x{r:X}"); k++; }
                }
                Log.Info($"HeroWatch: estado {_last} -> {v} en 0x{eip:X} (hilo {GetCurrentThreadId()}); vuelta:{sb}");
                if (eip == 0x52F149)
                {
                    // orden recibida (0x52F0xx): ebx = la orden
                    uint* o = (uint*)*(uint*)(c + 0xA4);
                    Log.Info($"HeroWatch:   orden {o[0]:X} {o[1]:X} {o[2]:X} {o[3]:X} {o[4]:X} {o[5]:X} {o[6]:X} {o[7]:X} {o[8]:X} {o[9]:X}; héroe en {*(int*)(_hero + 0x1C)},{*(int*)(_hero + 0x20)}");
                }
            }
            _last = v;
            return -1; // EXCEPTION_CONTINUE_EXECUTION
        }
        catch { return 0; }
    }
}
