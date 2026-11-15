using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Proxy;

/// <summary>
/// Opción «Ancaria Reforged» en el menú principal del juego (cUI_MainMenu, tabla 0x897004), en la fila libre entre
/// «Extras» y «Quit».
///
/// Las opciones las crea 0x713670 (llamada solo desde el ctor, 0x711B68): cada una es una etiqueta (0x8C bytes, tabla
/// 0x897758) creada con 0x754490 thiscall(etiqueta, nombre, rect {x, y, w:16, h:16}, padre, estilo 4, id de texto,
/// orden, 0x80605), ret 0x24 (id de texto → +0x78, estilo → +0x7C, orden → +0x88), en x 0x180, 256x30, una fila cada
/// 32 px; «Extras» (texto 0x414) y «Quit» (0x416) dejan una fila vacía entre ellas. Se guardan en las listas del menú
/// +0x158 y +0x90 (std::vector) y se les llama a vtable[0x28].
/// La orden es un objeto con tabla {destructor, ejecutar} como la de «Quit» (0x897068): la nuestra abre el menú de
/// opciones. El texto lo busca el dibujo (0x754900) con 0x672C90 (thiscall [0x182ED50], id), que devuelve un objeto
/// cuyo +0 / +4 son el principio y el final de las letras: para nuestro id se devuelve uno propio.
/// </summary>
internal static unsafe class MainMenuButton
{
    private const int TextId = 0x5AC0DE;
    private const nint BuildFn = 0x713670, BuildSite = 0x711B68, TextFn = 0x672C90, LabelCtor = 0x754490;
    private static nint _textObj, _cmdVt, _name;
    private static delegate* unmanaged[Thiscall]<nint, int, nint> _text;
    private static int _logs;

    public static void Install()
    {
        try
        {
            if (Mem.Read<byte>(BuildSite) != 0xE8 || BuildSite + 5 + Mem.Read<int>(BuildSite + 1) != BuildFn)
                throw new InvalidOperationException($"0x{BuildSite:X8} no llama a 0x{BuildFn:X8}");
            if (!Mem.Matches(TextFn, new byte[] { 0x8B, 0x44, 0x24, 0x04, 0x56 }))
                throw new InvalidOperationException($"0x{TextFn:X8} inesperado: {Mem.Hex(TextFn, 5)}");

            // texto: objeto {principio, final} de las letras
            const string label = "Ancaria Reforged";
            char* letters = (char*)NativeMemory.AllocZeroed((nuint)(label.Length + 1) * 2);
            for (int i = 0; i < label.Length; i++) letters[i] = label[i];
            _textObj = (nint)NativeMemory.AllocZeroed(16);
            ((nint*)_textObj)[0] = (nint)letters; ((nint*)_textObj)[1] = (nint)(letters + label.Length); ((nint*)_textObj)[2] = (nint)(letters + label.Length);
            // (nombre de la etiqueta: el de «Extras», definición gráfica existente)
            byte[] nm = System.Text.Encoding.ASCII.GetBytes("UI_LABEL_EXTRAS\0");
            _name = (nint)NativeMemory.Alloc((nuint)nm.Length);
            for (int i = 0; i < nm.Length; i++) ((byte*)_name)[i] = nm[i];

            // tabla de la orden: {destructor, ejecutar}
            _cmdVt = (nint)NativeMemory.AllocZeroed(4 * 4);
            ((nint*)_cmdVt)[0] = Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, int, nint>)&CmdDtor);
            ((nint*)_cmdVt)[1] = Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, void>)&CmdRun);

            // búsqueda de textos: desvío en la entrada (mov eax, [esp+4] / push esi)
            var c = new byte[10];
            for (int i = 0; i < 5; i++) c[i] = Mem.Read<byte>(TextFn + i);
            c[5] = 0xE9;
            nint tramp = Mem.AllocCode(c);
            *(int*)(tramp + 6) = (int)((TextFn + 5) - (tramp + 10));
            _text = (delegate* unmanaged[Thiscall]<nint, int, nint>)tramp;
            nint th = Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, int, nint>)&TextHook);
            var j = new byte[5];
            j[0] = 0xE9; BitConverter.TryWriteBytes(j.AsSpan(1), (int)(th - (TextFn + 5)));
            Mem.WriteBytes(TextFn, j);

            Mem.RedirectCall(BuildSite, Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, void>)&BuildHook));
            Log.Info("Menú principal: opción de Ancaria Reforged añadida");
        }
        catch (Exception ex) { Log.Error("Menú principal: no se pudo añadir la opción: " + ex.Message); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint TextHook(nint self, int id) => id == TextId ? _textObj : _text(self, id);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint CmdDtor(nint self, int flags)
    {
        if ((flags & 1) != 0) ((delegate* unmanaged[Cdecl]<nint, void>)0x849410)(self); // operator delete
        return self;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void CmdRun(nint self)
    {
        D9.OptionsMenu.OpenFromGame();
        Log.Info("Menú: abierto con la opción del menú principal");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void BuildHook(nint self)
    {
        ((delegate* unmanaged[Thiscall]<nint, void>)BuildFn)(self);
        try { AddLabel(self); }
        catch (Exception ex) { if (_logs++ < 3) Log.Error("Menú principal: " + ex.Message); }
    }

    private static void AddLabel(nint menu)
    {
        nint* first = *(nint**)(menu + 0x158), last = *(nint**)(menu + 0x15C);
        nint extras = 0, quit = 0;
        for (nint* p = first; p != null && p < last; p++)
        {
            if (*p == 0 || *(nint*)*p != 0x897758) continue;
            int id = *(int*)(*p + 0x78);
            if (id == 0x414) extras = *p; else if (id == 0x416) quit = *p;
        }
        if (_logs++ < 3)
            Log.Info($"Menú principal: {(last - first)} etiquetas; Extras {(extras != 0 ? $"{*(int*)(extras + 0x24)},{*(int*)(extras + 0x28)} ({*(short*)(extras + 0x2C)}x{*(short*)(extras + 0x2E)})" : "no")}, Quit y {(quit != 0 ? *(int*)(quit + 0x28) : -1)}");
        if (extras == 0 || quit == 0) return;
        int x = *(int*)(extras + 0x24), y = *(int*)(extras + 0x28) + 32, w = *(short*)(extras + 0x2C), h = *(short*)(extras + 0x2E);
        if (y + h > *(int*)(quit + 0x28)) return; // (sin fila libre)

        nint cmd = ((delegate* unmanaged[Cdecl]<int, nint>)0x849842)(4);
        if (cmd == 0) return;
        *(nint*)cmd = _cmdVt;
        nint mem = ((delegate* unmanaged[Cdecl]<int, nint>)0x849842)(0x8C);
        if (mem == 0) return;
        int wh = (w & 0xFFFF) | (h << 16);
        nint l = ((delegate* unmanaged[Thiscall]<nint, nint, int, int, int, nint, int, int, nint, int, nint>)LabelCtor)(
            mem, _name, x, y, wh, menu, *(ushort*)(extras + 0x7C), TextId, cmd, 0x80605);
        if (l == 0) return;
        var push = (delegate* unmanaged[Thiscall]<nint, nint*, void>)0x4B32A0;
        push(menu + 0x158, &l);
        push(menu + 0x90, &l);
        ((delegate* unmanaged[Thiscall]<nint, void>)(*(nint**)l)[0x28 / 4])(l);
    }
}
