using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SacredFix.Core;

namespace SacredFix.Proxy;

/// <summary>
/// Contadores de llamadas (diagnóstico). Se activan con [Debug] CallCounters=1.
///
/// Cada CALL rel32 de la lista se redirige a un trozo de código propio que hace
///     inc dword ptr [contador]
///     jmp destino_original
/// No toca registros ni pila, así que sirve para cualquier convención de llamada. Los totales se
/// escriben en cada resumen de D3DTrace (llamadas desde el resumen anterior). Comparando una partida a
/// 1024x768 con una panorámica se ve en qué eslabón deja el juego de dibujar los personajes.
/// </summary>
internal static unsafe class CallCounters
{
    private static readonly (nint site, string what)[] Sites =
    {
        (0x6313EB, "62FF60: objetos evaluados (44CF20)"),
        (0x631C42, "62FF60: objetos dentro de pantalla (dibujar)"),
        (0x6318B7, "62FF60: objetos fuera de pantalla (sin dibujar)"),
        (0x631CC2, "62FF60 -> 41F7F0 (objeto del mundo)"),
        (0x41F889, "41F7F0 -> 406FF0 (modelo 0x142)"),
        (0x599F29, "599910 -> 44B230"),
        (0x5BEE6F, "5BEE60 -> 44B230"),
        (0x44B7CF, "44B230 -> 44A9D0"),
        (0x44B8A7, "44B230 -> 44A9D0 (2)"),
        (0x44B76F, "44B230 -> 406FF0 (0x142)"),
        (0x44B82D, "44B230 -> 406FF0 (0x142, 2)"),
        (0x44AD71, "44A9D0 -> 40D060 (0x152)"),
        (0x44AEC2, "44A9D0 -> 40D060 (0x152, 2)"),
        (0x44AD46, "44A9D0 -> 40CD30 (0x142)"),
        (0x44ADD7, "44A9D0 -> 406510"),
    };

    /// <summary>Entradas de vtable (métodos virtuales «dibujar» de las criaturas): cuentan las entradas al método.</summary>
    private static readonly (nint slot, string what)[] Slots =
    {
        (0x891198, "entra 599910 (vtable 891198)"),
        (0x891B20, "entra 5BEE60 (vtable 891B20)"),
        (0x891B74, "entra 5BEE60 (vtable 891B74)"),
        (0x891CB4, "entra 5BEE60 (vtable 891CB4)"),
        (0x891D54, "entra 5BEE60 (vtable 891D54)"),
    };

    /// <summary>
    /// cCreature método 10 (0x5559E0, casilla 0x8911AC): cargar (1) / descargar (0) el modelo de Granny.
    /// Cuenta cada caso y guarda la última dirección de retorno (quién lo pide).
    /// </summary>
    private const nint LoadSlot = 0x8911AC;
    private static int* _load; // [0]=cargar, [1]=descargar, [2]=retorno cargar, [3]=retorno descargar
    private static int _lastLoad, _lastUnload;

    /// <summary>
    /// Lista de dibujo del fotograma ([0xAD7848]..[0xAD784C], entradas de 6 dwords; dword 0 = banderas de
    /// tipo). La recorre 0x62E410 (llamado en 0x63288C). Contamos las entradas por tipo, en el mismo
    /// orden en que las distingue el bucle: 4, 8, 1, 2, 0x20 y «objeto» (ninguna de esas).
    /// </summary>
    private static delegate* unmanaged[Thiscall]<nint, int, void> _drawLoop;
    private static readonly long[] _listTypes = new long[6], _listLast = new long[6];
    private static readonly string[] ListNames = { "4", "8", "1", "2", "0x20", "objeto" };
    private static long _listFrames, _listFramesLast;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void DrawLoopHook(nint self, int arg)
    {
        try
        {
            int* p = *(int**)0xAD7848, end = *(int**)0xAD784C;
            int guard = 0;
            for (; p < end && guard++ < 100000; p += 6)
            {
                int f = p[0];
                int k = (f & 4) != 0 ? 0 : (f & 8) != 0 ? 1 : (f & 1) != 0 ? 2 : (f & 2) != 0 ? 3 : (f & 0x20) != 0 ? 4 : 5;
                _listTypes[k]++;
            }
            _listFrames++;
        }
        catch { }
        _drawLoop(self, arg);
    }

    /// <summary>
    /// Sonda de colocación 3D: 0x6239C0 (cdecl, (salida, float* px) -> salida) pasa la posición en píxeles
    /// absolutos de un objeto a su posición 3D del mundo. Se guardan muestras (px, mundo) desde las llamadas del
    /// bucle 0x62FF60; D3DTrace las proyecta con la cámara actual y las compara con lo que espera el suelo 2D.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<nint, float*, nint> _toWorld;
    internal static readonly float[] PlaceSamples = new float[4 * 8]; // px, py, mundo_x, mundo_y
    internal static int PlaceCount;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint ToWorldHook(nint outp, float* px)
    {
        nint r = _toWorld(outp, px);
        try
        {
            int i = PlaceCount++ % 8;
            float* w = (float*)r;
            PlaceSamples[i * 4] = px[0]; PlaceSamples[i * 4 + 1] = px[1];
            PlaceSamples[i * 4 + 2] = w[0]; PlaceSamples[i * 4 + 3] = w[1];
        }
        catch { }
        return r;
    }

    private static int* _counts;
    private static int[] _last = Array.Empty<int>();
    private static bool _active;

    public static void Init(Config cfg)
    {
        if (!cfg.GetBool("Debug", "CallCounters", false)) return;
        try
        {
            int total = Sites.Length + Slots.Length;
            _counts = (int*)Mem.AllocCode(new byte[total * 4]);
            _last = new int[total];
            for (int i = 0; i < Sites.Length; i++)
            {
                var (site, what) = Sites[i];
                if (Mem.Read<byte>(site) != 0xE8) { Log.Warn($"CallCounters: en 0x{site:X8} no hay CALL ({what})"); continue; }
                nint target = site + 5 + Mem.Read<int>(site + 1);
                // FF 05 imm32 = inc dword ptr [imm32] ; E9 rel32 = jmp destino
                var code = new byte[11];
                code[0] = 0xFF; code[1] = 0x05;
                BitConverter.TryWriteBytes(code.AsSpan(2), (int)(nint)(_counts + i));
                code[6] = 0xE9;
                nint stub = Mem.AllocCode(code);
                *(int*)(stub + 7) = (int)(target - (stub + 11));
                Mem.RedirectCall(site, stub);
            }
            for (int j = 0; j < Slots.Length; j++)
            {
                var (slot, _) = Slots[j];
                nint target = Mem.Read<nint>(slot);
                nint stub = MakeStub(_counts + Sites.Length + j, target);
                Mem.Write(slot, stub);
            }
            InstallLoadProbe();
            delegate* unmanaged[Cdecl]<nint, float*, nint> tw = &ToWorldHook;
            foreach (var site in new nint[] { 0x631227, 0x631271 })
                _toWorld = (delegate* unmanaged[Cdecl]<nint, float*, nint>)Mem.RedirectCall(site, (nint)tw);
            delegate* unmanaged[Stdcall]<nint, int, void> h = &DrawLoopHook;
            _drawLoop = (delegate* unmanaged[Thiscall]<nint, int, void>)Mem.RedirectCall(0x63288C, Thunks.ThiscallToStdcall((nint)h));
            _active = true;
            Log.Info($"CallCounters: {Sites.Length} llamadas con contador");
        }
        catch (Exception ex) { Log.Error("CallCounters: " + ex.Message); }
    }

    private static void InstallLoadProbe()
    {
        _load = (int*)Mem.AllocCode(new byte[16]);
        nint target = Mem.Read<nint>(LoadSlot);
        // thiscall: [esp]=retorno, [esp+4]=parámetro (bool cargar). Solo usa eax, que guarda y restaura.
        var c = new byte[42];
        int i = 0;
        c[i++] = 0x50;                                              // push eax
        c[i++] = 0x8B; c[i++] = 0x44; c[i++] = 0x24; c[i++] = 0x04; // mov eax,[esp+4] (retorno)
        c[i++] = 0x80; c[i++] = 0x7C; c[i++] = 0x24; c[i++] = 0x08; c[i++] = 0x00; // cmp byte [esp+8],0
        c[i++] = 0x74; c[i++] = 0x0D;                               // je descargar
        c[i++] = 0xA3; BitConverter.TryWriteBytes(c.AsSpan(i), (int)(nint)(_load + 2)); i += 4; // mov [ret_cargar],eax
        c[i++] = 0xFF; c[i++] = 0x05; BitConverter.TryWriteBytes(c.AsSpan(i), (int)(nint)(_load + 0)); i += 4; // inc [cargar]
        c[i++] = 0xEB; c[i++] = 0x0B;                               // jmp fin
        c[i++] = 0xA3; BitConverter.TryWriteBytes(c.AsSpan(i), (int)(nint)(_load + 3)); i += 4; // descargar: mov [ret_descargar],eax
        c[i++] = 0xFF; c[i++] = 0x05; BitConverter.TryWriteBytes(c.AsSpan(i), (int)(nint)(_load + 1)); i += 4; // inc [descargar]
        c[i++] = 0x58;                                              // fin: pop eax
        c[i++] = 0xE9;                                              // jmp original
        nint stub = Mem.AllocCode(c);
        *(int*)(stub + i) = (int)(target - (stub + i + 4));
        Mem.Write(LoadSlot, stub);
        Log.Info($"CallCounters: sonda de carga de modelos en la casilla 0x{LoadSlot:X8} (destino 0x{target:X8})");
    }

    private static nint MakeStub(int* counter, nint target)
    {
        var code = new byte[11];
        code[0] = 0xFF; code[1] = 0x05;
        BitConverter.TryWriteBytes(code.AsSpan(2), (int)(nint)counter);
        code[6] = 0xE9;
        nint stub = Mem.AllocCode(code);
        *(int*)(stub + 7) = (int)(target - (stub + 11));
        return stub;
    }

    /// <summary>Llamadas desde el último informe, una por línea.</summary>
    public static void AppendReport(StringBuilder sb)
    {
        if (!_active) return;
        if (_listFrames != _listFramesLast)
        {
            long fr = _listFrames - _listFramesLast;
            sb.Append($" [lista de dibujo por fotograma:");
            for (int k = 0; k < 6; k++) { sb.Append($" {ListNames[k]}={(_listTypes[k] - _listLast[k]) / (double)fr:0.#}"); _listLast[k] = _listTypes[k]; }
            sb.Append(']');
            _listFramesLast = _listFrames;
        }
        if (_load != null)
        {
            sb.Append($" [cCreature cargar modelo: {_load[0] - _lastLoad} (último desde 0x{_load[2]:X8})]");
            sb.Append($" [cCreature descargar modelo: {_load[1] - _lastUnload} (último desde 0x{_load[3]:X8})]");
            _lastLoad = _load[0]; _lastUnload = _load[1];
        }
        sb.Append("\n    contadores:");
        for (int i = 0; i < Sites.Length; i++)
        {
            int now = _counts[i];
            sb.Append($" [{Sites[i].what}: {now - _last[i]}]");
            _last[i] = now;
        }
        for (int j = 0; j < Slots.Length; j++)
        {
            int i = Sites.Length + j;
            int now = _counts[i];
            sb.Append($" [{Slots[j].what}: {now - _last[i]}]");
            _last[i] = now;
        }
    }
}
