using System;
using System.IO;
using SacredFix.Core;
using SacredFix.Native;

namespace SacredFix.Proxy;

/// <summary>
/// Carga el ddraw.dll de Windows (en un proceso de 32 bits, System32 se redirige a SysWOW64)
/// y obtiene la dirección de cada función para reenviarle las llamadas.
/// </summary>
internal static class RealDDraw
{
    private static nint _module;
    // Ojo: los campos estáticos se inicializan en orden de aparición; por eso se usa el enum
    // y no Names.Length (Names aún sería null aquí).
    private static readonly nint[] _fn = new nint[Enum.GetValues<Fn>().Length];

    /// <summary>Las 22 funciones que exporta ddraw.dll (mismo orden que el enum Fn).</summary>
    public static readonly string[] Names =
    {
        "AcquireDDThreadLock", "CompleteCreateSysmemSurface", "D3DParseUnknownCommand",
        "DDGetAttachedSurfaceLcl", "DDInternalLock", "DDInternalUnlock", "DSoundHelp",
        "DirectDrawCreate", "DirectDrawCreateClipper", "DirectDrawCreateEx",
        "DirectDrawEnumerateA", "DirectDrawEnumerateExA", "DirectDrawEnumerateExW", "DirectDrawEnumerateW",
        "DllCanUnloadNow", "DllGetClassObject", "GetDDSurfaceLocal", "GetOLEThunkData",
        "GetSurfaceFromDC", "RegisterSpecialCase", "ReleaseDDThreadLock", "SetAppCompatData",
    };

    public static nint Get(Fn f)
    {
        nint p = _fn[(int)f];
        if (p != 0) return p;

        if (_module == 0)
        {
            string path = Path.Combine(Win32.GetSystemDirectory(), "ddraw.dll");
            _module = Win32.LoadLibraryW(path);
            if (_module == 0)
            {
                Win32.MessageBoxW(0, $"SacredFix no ha podido cargar {path}", "Sacred — SacredFix",
                                  Win32.MB_OK | Win32.MB_ICONERROR);
                Win32.ExitProcess(1);
            }
        }
        p = Win32.GetProcAddress(_module, Names[(int)f]);
        if (p == 0) Log.Error($"el ddraw.dll cargado no exporta {Names[(int)f]}");
        _fn[(int)f] = p;
        return p;
    }
}

internal enum Fn
{
    AcquireDDThreadLock, CompleteCreateSysmemSurface, D3DParseUnknownCommand,
    DDGetAttachedSurfaceLcl, DDInternalLock, DDInternalUnlock, DSoundHelp,
    DirectDrawCreate, DirectDrawCreateClipper, DirectDrawCreateEx,
    DirectDrawEnumerateA, DirectDrawEnumerateExA, DirectDrawEnumerateExW, DirectDrawEnumerateW,
    DllCanUnloadNow, DllGetClassObject, GetDDSurfaceLocal, GetOLEThunkData,
    GetSurfaceFromDC, RegisterSpecialCase, ReleaseDDThreadLock, SetAppCompatData,
}
