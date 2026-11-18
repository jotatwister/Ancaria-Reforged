using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Proxy;

/// <summary>
/// Las 22 funciones que exporta nuestra ddraw.dll. Cada una reenvía la llamada, con los mismos
/// argumentos, a la función homónima del ddraw.dll de Windows.
///
/// Son stdcall (convención de las API de Windows en 32 bits): el número de argumentos debe ser
/// EXACTO, porque la función llamada es la que limpia la pila. Firmas tomadas de la documentación
/// de DirectX y, para las internas no documentadas, de Wine (dlls/ddraw/ddraw.spec).
///
/// Las que usa el propio juego (DirectDrawCreate/CreateEx/EnumerateExA) llaman antes a
/// Bootstrap.EnsureInitialized() para aplicar los parches. Las internas las usa d3dim700.dll
/// (Direct3D 7 de Windows), que al encontrar un módulo "ddraw.dll" ya cargado usa el nuestro.
/// </summary>
internal static unsafe class DDrawExports
{
    // ---- Usadas por Sacred.exe ----

    // Traza de las primeras llamadas (para localizar cuelgues durante el arranque gráfico)
    private static int _traceBudget = 40;
    private static void Trace(string msg)
    {
        if (System.Threading.Interlocked.Decrement(ref _traceBudget) >= 0) Log.Info("ddraw: " + msg);
    }

    [UnmanagedCallersOnly(EntryPoint = "DirectDrawCreate", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int DirectDrawCreate(nint guid, nint dd, nint outer)
    {
        Bootstrap.EnsureInitialized();
        Trace($"DirectDrawCreate(guid=0x{guid:X}) ...");
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)RealDDraw.Get(Fn.DirectDrawCreate))(guid, dd, outer);
        Trace($"DirectDrawCreate -> 0x{hr:X8}");
        return hr;
    }

    [UnmanagedCallersOnly(EntryPoint = "DirectDrawCreateEx", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int DirectDrawCreateEx(nint guid, nint dd, nint iid, nint outer)
    {
        Bootstrap.EnsureInitialized();
        Trace($"DirectDrawCreateEx(guid=0x{guid:X}) ...");
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)RealDDraw.Get(Fn.DirectDrawCreateEx))(guid, dd, iid, outer);
        Trace($"DirectDrawCreateEx -> 0x{hr:X8}");
        // Capa propia D3D9: el juego recibe nuestro IDirectDraw7, que guarda el real solo para las consultas
        if (hr == 0 && dd != 0 && D9.Layer.Enabled) *(nint*)dd = D9.DirectDraw7.Create(*(nint*)dd);
        // Solo el objeto de la pantalla principal (guid NULL), que es el que crea el dispositivo 3D
        if (hr == 0 && guid == 0 && dd != 0) D3DTrace.OnDirectDrawCreated(*(nint*)dd);
        return hr;
    }

    [UnmanagedCallersOnly(EntryPoint = "DirectDrawEnumerateExA", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int DirectDrawEnumerateExA(nint callback, nint context, uint flags)
    {
        Bootstrap.EnsureInitialized();
        Trace($"DirectDrawEnumerateExA(flags=0x{flags:X}) ...");
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)RealDDraw.Get(Fn.DirectDrawEnumerateExA))(callback, context, flags);
        Trace($"DirectDrawEnumerateExA -> 0x{hr:X8}");
        return hr;
    }

    // ---- Resto de funciones públicas ----

    [UnmanagedCallersOnly(EntryPoint = "DirectDrawCreateClipper", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int DirectDrawCreateClipper(uint flags, nint clipper, nint outer)
        => ((delegate* unmanaged[Stdcall]<uint, nint, nint, int>)RealDDraw.Get(Fn.DirectDrawCreateClipper))(flags, clipper, outer);

    [UnmanagedCallersOnly(EntryPoint = "DirectDrawEnumerateA", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int DirectDrawEnumerateA(nint callback, nint context)
        => ((delegate* unmanaged[Stdcall]<nint, nint, int>)RealDDraw.Get(Fn.DirectDrawEnumerateA))(callback, context);

    [UnmanagedCallersOnly(EntryPoint = "DirectDrawEnumerateW", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int DirectDrawEnumerateW(nint callback, nint context)
        => ((delegate* unmanaged[Stdcall]<nint, nint, int>)RealDDraw.Get(Fn.DirectDrawEnumerateW))(callback, context);

    [UnmanagedCallersOnly(EntryPoint = "DirectDrawEnumerateExW", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int DirectDrawEnumerateExW(nint callback, nint context, uint flags)
        => ((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)RealDDraw.Get(Fn.DirectDrawEnumerateExW))(callback, context, flags);

    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int DllCanUnloadNow()
        => ((delegate* unmanaged[Stdcall]<int>)RealDDraw.Get(Fn.DllCanUnloadNow))();

    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int DllGetClassObject(nint clsid, nint iid, nint ppv)
        => ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)RealDDraw.Get(Fn.DllGetClassObject))(clsid, iid, ppv);

    // ---- Internas (las usa d3dim700.dll) ----

    [UnmanagedCallersOnly(EntryPoint = "AcquireDDThreadLock", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static void AcquireDDThreadLock()
        => ((delegate* unmanaged[Stdcall]<void>)RealDDraw.Get(Fn.AcquireDDThreadLock))();

    [UnmanagedCallersOnly(EntryPoint = "ReleaseDDThreadLock", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static void ReleaseDDThreadLock()
        => ((delegate* unmanaged[Stdcall]<void>)RealDDraw.Get(Fn.ReleaseDDThreadLock))();

    [UnmanagedCallersOnly(EntryPoint = "CompleteCreateSysmemSurface", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int CompleteCreateSysmemSurface(nint surface)
        => ((delegate* unmanaged[Stdcall]<nint, int>)RealDDraw.Get(Fn.CompleteCreateSysmemSurface))(surface);

    [UnmanagedCallersOnly(EntryPoint = "D3DParseUnknownCommand", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int D3DParseUnknownCommand(nint command, nint returnCommand)
        => ((delegate* unmanaged[Stdcall]<nint, nint, int>)RealDDraw.Get(Fn.D3DParseUnknownCommand))(command, returnCommand);

    [UnmanagedCallersOnly(EntryPoint = "DDGetAttachedSurfaceLcl", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int DDGetAttachedSurfaceLcl(nint a, nint b, nint c)
        => ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)RealDDraw.Get(Fn.DDGetAttachedSurfaceLcl))(a, b, c);

    [UnmanagedCallersOnly(EntryPoint = "DDInternalLock", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int DDInternalLock(nint a, nint b)
        => ((delegate* unmanaged[Stdcall]<nint, nint, int>)RealDDraw.Get(Fn.DDInternalLock))(a, b);

    [UnmanagedCallersOnly(EntryPoint = "DDInternalUnlock", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int DDInternalUnlock(nint a)
        => ((delegate* unmanaged[Stdcall]<nint, int>)RealDDraw.Get(Fn.DDInternalUnlock))(a);

    [UnmanagedCallersOnly(EntryPoint = "DSoundHelp", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int DSoundHelp(nint a, nint b, nint c)
        => ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)RealDDraw.Get(Fn.DSoundHelp))(a, b, c);

    [UnmanagedCallersOnly(EntryPoint = "GetDDSurfaceLocal", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int GetDDSurfaceLocal(nint a, nint b, nint c)
        => ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)RealDDraw.Get(Fn.GetDDSurfaceLocal))(a, b, c);

    [UnmanagedCallersOnly(EntryPoint = "GetOLEThunkData", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int GetOLEThunkData(nint a)
        => ((delegate* unmanaged[Stdcall]<nint, int>)RealDDraw.Get(Fn.GetOLEThunkData))(a);

    [UnmanagedCallersOnly(EntryPoint = "GetSurfaceFromDC", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int GetSurfaceFromDC(nint hdc, nint surface, nint ddraw)
        => ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)RealDDraw.Get(Fn.GetSurfaceFromDC))(hdc, surface, ddraw);

    [UnmanagedCallersOnly(EntryPoint = "RegisterSpecialCase", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int RegisterSpecialCase(nint a, nint b, nint c, nint d)
        => ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)RealDDraw.Get(Fn.RegisterSpecialCase))(a, b, c, d);

    [UnmanagedCallersOnly(EntryPoint = "SetAppCompatData", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int SetAppCompatData(nint a, nint b)
        => ((delegate* unmanaged[Stdcall]<nint, nint, int>)RealDDraw.Get(Fn.SetAppCompatData))(a, b);

    // ---- Propia: permite a los scripts reconocer que la ddraw.dll instalada es SacredFix ----

    [UnmanagedCallersOnly(EntryPoint = "SacredFix_Version", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int SacredFixVersion() => 0x000902; // 0.9.2
}
