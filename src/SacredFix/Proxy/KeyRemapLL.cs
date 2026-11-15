using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using SacredFix.Core;

namespace SacredFix.Proxy;

/// <summary>
/// Traducción de teclas ([Keys] Remap) con un gancho de teclado de bajo nivel (WH_KEYBOARD_LL), como AutoHotkey: en
/// la partida, la pulsación física de una tecla traducida se descarta y se inyecta (SendInput) la tecla destino.
/// Así el juego la ve igual la lea como la lea (mensajes, GetKeyboardState o GetAsyncKeyState); los ganchos de
/// mensajes no servían porque el bucle de mensajes del juego guarda las funciones en registros y su procedimiento
/// de ventana no recibe las teclas en la partida. Las teclas inyectadas (LLKHF_INJECTED) no se traducen, así «F1:1» y
/// «1:6» no se encadenan. Fuera de la partida, con el chat abierto o con otra ventana delante, no se toca nada.
/// </summary>
internal static unsafe partial class KeyInput
{
    private static bool _llActive; // gancho de bajo nivel instalado
    private static readonly HashSet<uint> _llDown = new(); // teclas físicas traducidas que siguen pulsadas
    private static int _llLogs;
    private const nint Marker = 0x5AC4ED;

    [LibraryImport("user32.dll")] private static partial nint SetWindowsHookExW(int id, nint proc, nint mod, uint thread);
    [LibraryImport("user32.dll")] private static partial nint CallNextHookEx(nint hook, int code, nint w, nint l);
    [LibraryImport("user32.dll")] private static partial int GetMessageW(byte* msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")] private static partial uint SendInput(uint count, byte* inputs, int size);
    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);

    private static void StartLowLevelRemap()
    {
        _llActive = true;
        Log.Info($"KeyInput: traducción de teclas con gancho de bajo nivel ({_remap.Count} teclas, activo {_llActive})");
        var t = new Thread(() =>
        {
            nint h = SetWindowsHookExW(13 /* WH_KEYBOARD_LL */, (nint)(delegate* unmanaged[Stdcall]<int, nint, nint, nint>)&LowLevelProc,
                                       GetModuleHandleW(null), 0);
            if (h == 0) { _llActive = false; Log.Warn("KeyInput: no se pudo instalar el gancho de teclado"); return; }
            Log.Info("KeyInput: gancho de teclado de bajo nivel instalado (traducción de teclas)");
            byte* msg = stackalloc byte[28];
            while (GetMessageW(msg, 0, 0, 0) > 0) { }
        }) { IsBackground = true, Name = "SacredFix teclado" };
        t.Start();
    }

    private static bool LowLevelActive()
    {
        nint fg = GetForegroundWindow();
        uint pid = 0;
        if (fg == 0) return false;
        GetWindowThreadProcessId(fg, &pid);
        return pid == GetCurrentProcessId() && InGame() && !ChatOpen();
    }

    /// <summary>KBDLLHOOKSTRUCT: vkCode +0, scanCode +4, flags +8 (0x10 inyectada, 0x80 soltada), time +12, extra +16.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint LowLevelProc(int code, nint w, nint l)
    {
        try
        {
            if (code == 0 && l != 0)
            {
                uint vk = *(uint*)l, flags = *(uint*)(l + 8);
                bool up = (flags & 0x80) != 0;
                if ((flags & 0x10) == 0) AltTap(vk, up, *(uint*)(l + 12));
                uint to = 0; bool mapped;
                lock (_remap) mapped = _remap.TryGetValue(vk, out to);
                if ((flags & 0x10) == 0 && mapped)
                {
                    bool translate = up ? _llDown.Remove(vk) : (_llDown.Contains(vk) || LowLevelActive());
                    if (translate)
                    {
                        if (!up) _llDown.Add(vk);
                        Send(to, up);
                        if (!up && _llLogs++ < 10) Log.Info($"KeyInput: tecla 0x{vk:X2} -> 0x{to:X2}");
                        return 1; // la física no llega al juego
                    }
                }
            }
        }
        catch { }
        return CallNextHookEx(0, code, w, l);
    }

    // Pulsación corta de Alt (sola, < 300 ms, sin otra tecla ni botón del ratón en medio): fija o quita los nombres de
    // los objetos del suelo (Patches.ItemLabels). Alt+Tab, Alt+F4 o Alt+clic no cuentan.
    private static bool _altDown, _altTap;
    private static uint _altTime;
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int vk);
    private static void AltTap(uint vk, bool up, uint time)
    {
        bool alt = vk == 0x12 || vk == 0xA4 || vk == 0xA5;
        if (!alt) { if (!up) _altTap = false; return; }
        if (!up) { if (!_altDown) { _altDown = true; _altTap = true; _altTime = time; } return; }
        bool tap = _altDown && _altTap && time - _altTime < 300;
        _altDown = false;
        if (!tap || !Patches.ItemLabels.TapToggle) return;
        if (GetAsyncKeyState(1) < 0 || GetAsyncKeyState(2) < 0) return;
        if (LowLevelActive()) Patches.ItemLabels.Toggle();
    }

    private static void Send(uint vk, bool up)
    {
        // INPUT de 32 bits: type +0, KEYBDINPUT { wVk +4, wScan +6, dwFlags +8, time +12, dwExtraInfo +16 }, 28 bytes
        byte* inp = stackalloc byte[28];
        new Span<byte>(inp, 28).Clear();
        *(uint*)inp = 1; // INPUT_KEYBOARD
        *(ushort*)(inp + 4) = (ushort)vk;
        *(ushort*)(inp + 6) = (ushort)MapVirtualKeyA(vk, 0);
        *(uint*)(inp + 8) = up ? 2u : 0u; // KEYEVENTF_KEYUP
        *(nint*)(inp + 16) = Marker;
        SendInput(1, inp, 28);
    }
}
