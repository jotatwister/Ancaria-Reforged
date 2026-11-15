using System;

namespace SacredFix.Core;

/// <summary>
/// Pequeños "adaptadores" en código máquina entre convenciones de llamada.
///
/// El juego (C++ de Visual C++ 6) llama a sus métodos con <b>thiscall</b>: el objeto va en el
/// registro ECX y los argumentos en la pila. Desde C# es más sencillo y fiable exportar
/// funciones <b>stdcall</b> (todo en la pila). Este adaptador convierte una en otra:
///
///     pop  eax        ; 58           saca la dirección de retorno
///     push ecx        ; 51           mete 'this' como primer argumento
///     push eax        ; 50           vuelve a poner la dirección de retorno
///     jmp  destino    ; E9 rel32     salta a nuestra función stdcall
///
/// Nuestra función recibe (this, arg1, arg2, ...) y al terminar limpia un argumento más que el
/// original (el 'this' que hemos metido), así que la pila queda exactamente como el juego espera.
/// </summary>
internal static class Thunks
{
    public static nint ThiscallToStdcall(nint stdcallTarget)
    {
        Span<byte> code = stackalloc byte[8];
        code[0] = 0x58;            // pop eax
        code[1] = 0x51;            // push ecx
        code[2] = 0x50;            // push eax
        code[3] = 0xE9;            // jmp rel32
        nint thunk = Mem.AllocCode(code);
        Mem.Write(thunk + 4, (int)(stdcallTarget - (thunk + 8)));
        return thunk;
    }
}
