/*
 * SacredFix: punto de entrada nativo (DllMain).
 *
 * NativeAOT no ejecuta código al cargar la DLL, y algunos parches (FocusFix) deben aplicarse antes
 * de la primera llamada del juego a ddraw. DllMain no puede hacer casi nada (bloqueo del cargador),
 * así que solo lanza un hilo que llama a la exportación gestionada SacredFix_Early.
 * Se compila con cl /c /Zl (sin CRT) desde build.ps1 y se enlaza con <NativeLibrary>.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

static DWORD WINAPI EarlyThread(LPVOID module)
{
    typedef void (WINAPI *EarlyFn)(void);
    EarlyFn fn = (EarlyFn)GetProcAddress((HMODULE)module, "SacredFix_Early");
    if (fn) fn();
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        HANDLE t = CreateThread(NULL, 0, EarlyThread, instance, 0, NULL);
        if (t) CloseHandle(t);
    }
    return TRUE;
}
