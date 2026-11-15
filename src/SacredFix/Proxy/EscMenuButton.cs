using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Proxy;

/// <summary>
/// Botón «Ancaria Reforged» dentro del menú de Escape del juego (cUI_EscMenu, tabla 0x8964DC), con su mismo aspecto.
///
/// El menú (264x200 en el lienzo; ctor 0x6BB160) crea sus 5 botones en 0x6BBBD0 (llamada en 0x6BB20A, ctor, y en
/// 0x6BC7F7, al volver a la página principal): cada uno es un cUI_Button (0x9C bytes, tabla 0x897700) creado con
/// 0x753B90 thiscall(botón, nombre, rect {x, y, w:16, h:16}, padre, 4, id de texto, orden, 0, 0x80605), ret 0x28,
/// en x 0 e y 8, 44, 80, 116, 152 (32 de alto), y guardado en el vector de +0x158 (push_back 0x4B32A0).
/// El texto: con id de texto 0, su dibujo usa el vector propio de +0x88 (0x672C90 devuelve un objeto {inicio, fin}, no letras).
/// Con la orden (+0x98) a 0, el clic no hace nada (0x754390); nuestro botón lleva una copia de la tabla con su casilla 4
/// (eventos) propia: con el evento de tipo 2 (clic) dentro del botón, abre el menú de opciones (OptionsMenu).
/// Se añade un sexto botón y los seis se reparten cada 32 px desde y 4: el marco se dibuja siempre a 264x200.
/// </summary>
internal static unsafe class EscMenuButton
{
    private const nint BuildFn = 0x6BBBD0, ButtonEventFn = 0x754390;
    private static nint _label, _vt, _name;
    private static int _logs;

    public static void Install()
    {
        try
        {
            foreach (nint s in new nint[] { 0x6BB20A, 0x6BC7F7 })
                if (Mem.Read<byte>(s) != 0xE8 || s + 5 + Mem.Read<int>(s + 1) != BuildFn)
                    throw new InvalidOperationException($"0x{s:X8} no llama a 0x{BuildFn:X8}");
            if (Mem.Read<nint>(0x897700 + 4 * 4) != ButtonEventFn) throw new InvalidOperationException("tabla de cUI_Button inesperada");

            // texto y nombre (memoria propia, para siempre)
            const string label = "Ancaria Reforged";
            _label = (nint)NativeMemory.AllocZeroed((nuint)(label.Length + 1) * 2);
            for (int i = 0; i < label.Length; i++) ((char*)_label)[i] = label[i];
            // (el nombre es el de la definición gráfica del botón: con uno inexistente el juego aborta al dibujarlo;
            // se usa el de «Continuar», que tiene el mismo aspecto)
            byte[] nm = System.Text.Encoding.ASCII.GetBytes("UI_ESC_CONTINUE\0");
            _name = (nint)NativeMemory.Alloc((nuint)nm.Length);
            for (int i = 0; i < nm.Length; i++) ((byte*)_name)[i] = nm[i];

            // copia de la tabla de cUI_Button (con el puntero de RTTI de delante) y casilla 4 propia
            const int slots = 16;
            nint copy = (nint)NativeMemory.Alloc((nuint)(slots + 1) * 4);
            for (int i = 0; i <= slots; i++) ((nint*)copy)[i] = Mem.Read<nint>(0x897700 - 4 + i * 4);
            _vt = copy + 4;
            ((nint*)_vt)[4] = Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, nint, uint>)&ButtonEvent);

            nint bh = Thunks.ThiscallToStdcall((nint)(delegate* unmanaged[Stdcall]<nint, void>)&BuildHook);
            Mem.RedirectCall(0x6BB20A, bh);
            Mem.RedirectCall(0x6BC7F7, bh);
            Log.Info("Menú de Escape: botón de Ancaria Reforged añadido");
        }
        catch (Exception ex) { Log.Error("Menú de Escape: no se pudo añadir el botón: " + ex.Message); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void BuildHook(nint self)
    {
        ((delegate* unmanaged[Thiscall]<nint, void>)BuildFn)(self);
        try { AddButton(self); }
        catch (Exception ex) { if (_logs++ < 3) Log.Error("Menú de Escape: " + ex.Message); }
    }

    private static void AddButton(nint menu)
    {
        nint* first = *(nint**)(menu + 0x158), last = *(nint**)(menu + 0x15C);
        int n = first != null && last > first ? (int)(last - first) : 0;
        if (n < 5) return;
        nint b0 = first[0], bl = first[n - 1];
        int x = *(int*)(b0 + 0x24), w = *(short*)(b0 + 0x2C), h = *(short*)(b0 + 0x2E);
        int y = *(int*)(bl + 0x28) + 36;
        if (_logs++ < 3)
            Log.Info($"Menú de Escape: {n} botones, el primero en {x},{*(int*)(b0 + 0x28)} ({w}x{h}), el último en y {*(int*)(bl + 0x28)}; menú {*(int*)(menu + 0x24)},{*(int*)(menu + 0x28)} ({*(short*)(menu + 0x2C)}x{*(short*)(menu + 0x2E)})");

        nint mem = ((delegate* unmanaged[Cdecl]<int, nint>)0x849842)(0x9C);
        if (mem == 0) return;
        int wh = (w & 0xFFFF) | (h << 16);
        nint b = ((delegate* unmanaged[Thiscall]<nint, nint, int, int, int, nint, int, int, nint, int, int, nint>)0x753B90)(
            mem, _name, x, y, wh, menu, 4, 0, 0, 0, 0x80605);
        if (b == 0) return;
        *(nint*)b = _vt;
        SetText(b);
        ((delegate* unmanaged[Thiscall]<nint, nint*, void>)0x4B32A0)(menu + 0x158, &b);

        // El marco del menú se dibuja siempre a 264x200 (no sigue al tamaño de la ventana): en vez de agrandarlo, los
        // 6 botones se reparten en el mismo alto, cada 32 px desde y 4 (el último acaba en 196). Posiciones relativas al menú.
        first = *(nint**)(menu + 0x158); last = *(nint**)(menu + 0x15C);
        int k = 0;
        for (nint* p = first; p < last; p++, k++)
            if (*p != 0) *(int*)(*p + 0x28) = 4 + k * 32;
    }

    /// <summary>
    /// Con el id de texto a 0, el dibujo (0x754140) usa el texto propio del botón: vector de wchar {primero, último, fin}
    /// en +0x88 (0x592F80(primero, último)). El constructor deja uno de 0x10 bytes (0x8321A0); se cambia por uno de más de
    /// 0x80 bytes con operator new (0x849842), que se libera con operator delete como hace el juego con los grandes.
    /// </summary>
    private static void SetText(nint b)
    {
        int len = 0;
        while (((char*)_label)[len] != 0) len++;
        const int cap = 0x48;
        char* buf = (char*)((delegate* unmanaged[Cdecl]<int, nint>)0x849842)(cap * 2);
        if (buf == null) return;
        for (int i = 0; i < len; i++) buf[i] = ((char*)_label)[i];
        buf[len] = (char)0;
        nint* v = (nint*)(b + 0x88);
        nint old = v[0], oldEnd = v[2];
        v[0] = (nint)buf; v[1] = (nint)(buf + len); v[2] = (nint)(buf + cap);
        if (old != 0) ((delegate* unmanaged[Cdecl]<nint, int, void>)0x832350)(old, (int)(oldEnd - old)); // (pequeño: 0x10 bytes)
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint ButtonEvent(nint self, nint ev)
    {
        uint r = ((delegate* unmanaged[Thiscall]<nint, nint, uint>)ButtonEventFn)(self, ev);
        try
        {
            if ((r & 0xFF) != 0 || ev == 0 || (*(uint*)(self + 0x10) & 4) == 0) return r;
            int type = ((delegate* unmanaged[Thiscall]<nint, int>)(*(nint**)ev)[1])(ev);
            if (type != 2) return r;
            int ex = *(int*)(ev + 8), ey = *(int*)(ev + 0xC);
            bool hit = ((delegate* unmanaged[Thiscall]<nint, int, int, byte>)(*(nint**)self)[0x1C / 4])(self, ex, ey) != 0;
            if (!hit) return r;
            D9.OptionsMenu.OpenFromGame();
            Log.Info("Menú: abierto con el botón del menú de Escape");
            return 1;
        }
        catch { return r; }
    }
}
