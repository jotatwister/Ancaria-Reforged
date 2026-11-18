/*
 * SacredFix: punto de entrada nativo (DllMain).
 *
 * NativeAOT no ejecuta código al cargar la DLL, y algunos parches (FocusFix) deben aplicarse antes
 * de la primera llamada del juego a ddraw. DllMain no puede hacer casi nada (bloqueo del cargador),
 * así que solo lanza un hilo que llama a la exportación gestionada SacredFix_Early.
 *
 * Idioma del juego ([Language] Game en SacredFix.ini): el juego elige el idioma de sus textos buscando un código
 * (US, DE, FR, SP, IT, PL, HU, JP, VC, RU, CZ) entre las palabras de su línea de órdenes (WinMain 0x817B30, bucle
 * 0x817D86) y carga .\SCRIPTS\<código>\global.res. Steam no pasa ninguno. Aquí, antes de que arranque el juego (y sin
 * carrera con su descifrado de SteamStub), se añade el código, en minúsculas («sp»: el juego pasa a minúsculas el
 * código que busca, no la línea), a la línea de órdenes que devuelve GetCommandLineA:
 * en Windows esa función solo devuelve un puntero guardado («mov eax, [puntero] / ret»), que se cambia por uno propio.
 *
 * Eso fija el índice del idioma, pero los textos no salen del archivo: la versión de Steam los lleva dentro del .exe
 * (recurso «BINARY» 107, el global.res inglés cifrado con XOR) y su cargador (0x80E680, thiscall sobre el gestor de
 * textos 0x182ED50) no mira la ruta. Por eso aquí se lee scripts\<código>\global.res y se cambia ese cargador por
 * LoadTexts, que entrega el archivo tal cual (mismo formato que el recurso ya descifrado). El cambio se hace en la
 * primera llamada a CreateMutexA (lo primero de WinMain, 0x817B41), cuando SteamStub ya ha descifrado el código y antes
 * de que se carguen los textos (0x817DDB): CreateMutexA de kernelbase empieza por «mov edi, edi» con 5 bytes libres
 * delante (parche en caliente de Windows), así que se engancha sin trampolín.
 *
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

static int IsCode(const char *c)
{
    static const char *codes[] = { "US", "DE", "FR", "SP", "IT", "PL", "HU", "JP", "VC", "RU", "CZ" };
    int i;
    for (i = 0; i < 11; i++)
        if (c[0] == codes[i][0] && c[1] == codes[i][1] && c[2] == 0) return 1;
    return 0;
}

/* Cambia el puntero que devuelve GetCommandLineA del módulo dado; 1 si lo ha cambiado. */
static int PatchCommandLine(const char *module, char *newLine)
{
    HMODULE m = GetModuleHandleA(module);
    unsigned char *f = m ? (unsigned char *)GetProcAddress(m, "GetCommandLineA") : NULL;
    char **ptr;
    DWORD old;
    if (!f || f[0] != 0xA1 || f[5] != 0xC3) return 0; /* mov eax, [puntero] ; ret */
    ptr = *(char ***)(f + 1);
    if (!VirtualProtect(ptr, sizeof(char *), PAGE_READWRITE, &old)) return 0;
    *ptr = newLine;
    VirtualProtect(ptr, sizeof(char *), old, &old);
    return 1;
}

/* ---- textos del idioma: el global.res del archivo en lugar del recurso inglés del .exe ---- */
static unsigned char *g_res;
static DWORD g_resSize;
static volatile LONG g_textsHooked;
typedef HANDLE (WINAPI *CreateMutexAFn)(LPSECURITY_ATTRIBUTES, BOOL, LPCSTR);
static CreateMutexAFn g_createMutex;

#define TEXTS_LOADER 0x0080E680u
#define GAME_ALLOC   0x00849842u
static const unsigned char g_loaderStart[11] = { 0x6A, 0xFF, 0x68, 0x27, 0xA6, 0x88, 0x00, 0x64, 0xA1, 0x00, 0x00 };

/* thiscall (ecx = gestor de textos, 1 argumento, ret 4) como fastcall: ecx, edx sin usar, el argumento en la pila */
static char __fastcall LoadTexts(unsigned char *mgr, void *unused, const char *path)
{
    typedef void *(__cdecl *AllocFn)(unsigned int);
    unsigned char *buf;
    DWORD i;
    (void)unused; (void)path;
    buf = (unsigned char *)((AllocFn)GAME_ALLOC)(g_resSize);
    if (!buf) return 0;
    for (i = 0; i < g_resSize; i++) ((volatile unsigned char *)buf)[i] = g_res[i];
    *(unsigned char **)mgr = buf;
    *(DWORD *)(mgr + 4) = g_resSize;
    return 1;
}

static void HookTextsLoader(void)
{
    unsigned char *f = (unsigned char *)TEXTS_LOADER;
    DWORD old;
    int i;
    for (i = 0; i < 11; i++) if (f[i] != g_loaderStart[i]) return; /* aún cifrado (o no es la 2.0.2.28) */
    if (InterlockedExchange(&g_textsHooked, 1)) return;
    if (!VirtualProtect(f, 5, PAGE_EXECUTE_READWRITE, &old)) return;
    f[0] = 0xE9;
    *(LONG *)(f + 1) = (LONG)((unsigned char *)LoadTexts - (f + 5));
    VirtualProtect(f, 5, old, &old);
    FlushInstructionCache(GetCurrentProcess(), f, 5);
}

/* ---- voces: otro sound.pak ([Language] Voices) ----
   WinMain fija la ruta del sonido con «push 0xA1D3D0» («.\PAK\SOUND.PAK») en 0x81720E y la copia (0x677160) al búfer
   0x9D760C, del que la abre el sonido (0x67661A). Se cambia ese push por la ruta del idioma elegido. */
#define VOICE_PUSH 0x0081720Eu
#define VOICE_BUFFER 0x009D760Cu
static char g_voicePath[32];
static volatile LONG g_voiceHooked;

static void HookVoices(void)
{
    unsigned char *f = (unsigned char *)VOICE_PUSH;
    DWORD old;
    if (f[0] != 0x68 || *(DWORD *)(f + 1) != 0x00A1D3D0u) return; /* aún cifrado (o no es la 2.0.2.28) */
    if (InterlockedExchange(&g_voiceHooked, 1)) return;
    if (!VirtualProtect(f, 5, PAGE_EXECUTE_READWRITE, &old)) return;
    *(DWORD *)(f + 1) = (DWORD)(ULONG_PTR)g_voicePath;
    VirtualProtect(f, 5, old, &old);
    FlushInstructionCache(GetCurrentProcess(), f, 5);
    /* el búfer de la ruta ya viene con «.\PAK\SOUND.PAK» y el sonido puede abrirlo antes de ese push: también aquí */
    {
        char *buf = (char *)VOICE_BUFFER;
        const char *def = ".\\PAK\\SOUND.PAK";
        int i;
        for (i = 0; def[i]; i++) if (buf[i] != def[i]) return;
        if (buf[i] != 0 || !VirtualProtect(buf, 32, PAGE_READWRITE, &old)) return;
        lstrcpyA(buf, g_voicePath);
        VirtualProtect(buf, 32, old, &old);
    }
}

static HANDLE WINAPI CreateMutexAHook(LPSECURITY_ATTRIBUTES sa, BOOL owner, LPCSTR name)
{
    if (g_res && !g_textsHooked) HookTextsLoader();
    if (g_voicePath[0] && !g_voiceHooked) HookVoices();
    return g_createMutex(sa, owner, name);
}

/* Parche en caliente: «jmp gancho» en los 5 bytes libres de delante y «jmp $-5» sobre «mov edi, edi».
   La original se llama en función+2 (tras el «mov edi, edi», que no hace nada). */
static int HotPatch(const char *module, const char *name, void *hook, void **orig)
{
    HMODULE m = GetModuleHandleA(module);
    unsigned char *f = m ? (unsigned char *)GetProcAddress(m, name) : NULL;
    DWORD old;
    int i;
    if (!f || f[0] != 0x8B || f[1] != 0xFF) return 0;
    for (i = 1; i <= 5; i++) if (f[-i] != 0xCC && f[-i] != 0x90) return 0;
    if (!VirtualProtect(f - 5, 7, PAGE_EXECUTE_READWRITE, &old)) return 0;
    *orig = f + 2;
    f[-5] = 0xE9;
    *(LONG *)(f - 4) = (LONG)((unsigned char *)hook - f);
    *(volatile unsigned short *)f = 0xF9EB; /* jmp -7 */
    VirtualProtect(f - 5, 7, old, &old);
    FlushInstructionCache(GetCurrentProcess(), f - 5, 7);
    return 1;
}

/* Lee el global.res elegido y prepara el cambio de cargador; 1 si queda listo. */
static int PrepareTexts(const char *res)
{
    HANDLE h = CreateFileA(res, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    DWORD size, got = 0;
    if (h == INVALID_HANDLE_VALUE) return 0;
    size = GetFileSize(h, NULL);
    if (size < 8 || size == INVALID_FILE_SIZE || size > 64u * 1024 * 1024) { CloseHandle(h); return 0; }
    g_res = (unsigned char *)HeapAlloc(GetProcessHeap(), 0, size);
    if (g_res && (!ReadFile(h, g_res, size, &got, NULL) || got != size)) { HeapFree(GetProcessHeap(), 0, g_res); g_res = NULL; }
    CloseHandle(h);
    if (!g_res) return 0;
    g_resSize = size;
    return 1;
}

/* CreateMutexA (lo primero de WinMain) engancha una sola vez; 1 si queda enganchado. */
static int HookCreateMutex(void)
{
    if (g_createMutex) return 1;
    if (HotPatch("kernelbase.dll", "CreateMutexA", (void *)CreateMutexAHook, (void **)&g_createMutex)) return 1;
    return HotPatch("kernel32.dll", "CreateMutexA", (void *)CreateMutexAHook, (void **)&g_createMutex);
}

/* Primera palabra de [Language] <key>, en mayúsculas, si es un código válido (code: 3 bytes). */
static int ReadCode(const char *ini, const char *key, char *code)
{
    char val[64], *p;
    int i;
    GetPrivateProfileStringA("Language", key, "", val, sizeof(val), ini);
    for (p = val; *p == ' ' || *p == '\t'; p++) {}
    for (i = 0; i < 2 && p[i] && p[i] != ' ' && p[i] != '\t' && p[i] != ';'; i++)
        code[i] = (p[i] >= 'a' && p[i] <= 'z') ? (char)(p[i] - 32) : p[i];
    code[i] = 0;
    return i == 2 && (!p[2] || p[2] == ' ' || p[2] == '\t' || p[2] == ';') && IsCode(code);
}

static void Voices(const char *dir, const char *ini)
{
    char code[3], pak[MAX_PATH];
    if (!ReadCode(ini, "Voices", code)) return;
    lstrcpynA(pak, dir, MAX_PATH);
    if (lstrlenA(pak) + 20 >= MAX_PATH) return;
    lstrcatA(pak, "pak\\"); lstrcatA(pak, code); lstrcatA(pak, "\\sound.pak");
    if (GetFileAttributesA(pak) == INVALID_FILE_ATTRIBUTES) return;
    lstrcpyA(g_voicePath, ".\\PAK\\"); lstrcatA(g_voicePath, code); lstrcatA(g_voicePath, "\\SOUND.PAK");
    if (!HookCreateMutex()) g_voicePath[0] = 0;
}

static void GameLanguage(void)
{
    char dir[MAX_PATH], ini[MAX_PATH], val[64], code[3], res[MAX_PATH];
    char *p, *src, *dst, *line;
    int n, i, len;
    n = (int)GetModuleFileNameA(NULL, dir, MAX_PATH);
    if (n <= 0 || n >= MAX_PATH) return;
    for (p = dir + n; p > dir && p[-1] != '\\' && p[-1] != '/'; p--) {}
    *p = 0;
    lstrcpynA(ini, dir, MAX_PATH);
    if (lstrlenA(ini) + 14 >= MAX_PATH) return;
    lstrcatA(ini, "SacredFix.ini");
    Voices(dir, ini);
    GetPrivateProfileStringA("Language", "Game", "", val, sizeof(val), ini);
    /* primera palabra, en mayúsculas (el ini lleva comentarios tras «;») */
    for (p = val; *p == ' ' || *p == '\t'; p++) {}
    for (i = 0; i < 2 && p[i] && p[i] != ' ' && p[i] != '\t' && p[i] != ';'; i++)
        code[i] = (p[i] >= 'a' && p[i] <= 'z') ? (char)(p[i] - 32) : p[i];
    code[i] = 0;
    if (i != 2 || (p[2] && p[2] != ' ' && p[2] != '\t' && p[2] != ';') || !IsCode(code)) return;
    /* solo si está su global.res */
    lstrcpynA(res, dir, MAX_PATH);
    if (lstrlenA(res) + 24 >= MAX_PATH) return;
    lstrcatA(res, "scripts\\"); lstrcatA(res, code); lstrcatA(res, "\\global.res");
    if (GetFileAttributesA(res) == INVALID_FILE_ATTRIBUTES) return;
    if ((code[0] != 'U' || code[1] != 'S') && PrepareTexts(res) && !HookCreateMutex()) /* el inglés ya va dentro del .exe */
    {
        HeapFree(GetProcessHeap(), 0, g_res); g_res = NULL; g_resSize = 0;
    }
    src = GetCommandLineA();
    if (!src) return;
    len = lstrlenA(src);
    line = (char *)HeapAlloc(GetProcessHeap(), 0, (SIZE_T)len + 8);
    if (!line) return;
    dst = line;
    for (i = 0; i < len; i++) *dst++ = src[i];
    /* en minúsculas: el juego pasa a minúsculas el código que busca (0x813A90), no la línea de órdenes */
    *dst++ = ' '; *dst++ = (char)(code[0] + 32); *dst++ = (char)(code[1] + 32); *dst = 0;
    if (!PatchCommandLine("kernelbase.dll", line)) PatchCommandLine("kernel32.dll", line);
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        GameLanguage(); /* antes de que el juego lea su línea de órdenes */
        HANDLE t = CreateThread(NULL, 0, EarlyThread, instance, 0, NULL);
        if (t) CloseHandle(t);
    }
    return TRUE;
}
