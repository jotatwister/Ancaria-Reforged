using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Con la imagen escalada al monitor (sin bordes), la ventana mide como el monitor pero el juego piensa en su
/// resolución (Display.Width x Height). El juego lee el ratón en 0x66E280 (GetCursorPos - ClientToScreen(0,0), con
/// GetClientRect) y lo mueve con SetCursorPos: se enganchan esas importaciones para que vea su resolución.
///   GetClientRect (IAT 0x8902C4) de la ventana del juego -> (0, 0, Width, Height);
///   GetCursorPos (IAT 0x8902B8) -> origen + (pos - origen) · juego / ventana;
///   SetCursorPos (IAT 0x8902B4) -> al revés.
/// </summary>
internal static unsafe class MouseScale
{
    private static delegate* unmanaged[Stdcall]<int*, int> _getCursorPos;
    private static delegate* unmanaged[Stdcall]<nint, int*, int> _getClientRect;
    private static delegate* unmanaged[Stdcall]<int, int, int> _setCursorPos;
    private static bool _installed;

    [DllImport("user32.dll")] private static extern int ClientToScreen(nint hwnd, int* pt);
    [DllImport("user32.dll", EntryPoint = "GetClientRect")] private static extern int RealClientRect(nint hwnd, int* r);

    public static void Install()
    {
        if (_installed || Display.Hwnd == 0 || (Display.Fullscreen && !Display.Scaling)) return;
        _installed = true;
        _getCursorPos = (delegate* unmanaged[Stdcall]<int*, int>)Mem.Read<nint>(0x8902B8);
        _getClientRect = (delegate* unmanaged[Stdcall]<nint, int*, int>)Mem.Read<nint>(0x8902C4);
        _setCursorPos = (delegate* unmanaged[Stdcall]<int, int, int>)Mem.Read<nint>(0x8902B4);
        Mem.Write<nint>(0x8902B8, (nint)(delegate* unmanaged[Stdcall]<int*, int>)&GetCursorPosHook);
        Mem.Write<nint>(0x8902C4, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&GetClientRectHook);
        Mem.Write<nint>(0x8902B4, (nint)(delegate* unmanaged[Stdcall]<int, int, int>)&SetCursorPosHook);
        Log.Info($"D9: ratón convertido de la ventana ({Display.BBW}x{Display.BBH}) a la imagen del juego ({Display.Width}x{Display.Height})");
    }

    /// <summary>
    /// Origen del área de la ventana en pantalla (cx, cy) y rectángulo de la imagen del juego dentro de ella (ix, iy, iw, ih,
    /// en pantalla; con KeepAspect deja bandas). El juego (0x66E280) resta a GetCursorPos el origen de la ventana
    /// (ClientToScreen(0, 0)), no el de la imagen: el punto que se le devuelve es origen + posición en su resolución.
    /// </summary>
    private static bool Client(out int cx, out int cy, out int ix, out int iy, out int iw, out int ih)
    {
        int* p = stackalloc int[2];
        int* r = stackalloc int[4];
        cx = cy = ix = iy = iw = ih = 0;
        if (RealClientRect(Display.Hwnd, r) == 0) return false;
        p[0] = 0; p[1] = 0;
        ClientToScreen(Display.Hwnd, p);
        cx = p[0]; cy = p[1];
        ix = cx; iy = cy; iw = r[2] - r[0]; ih = r[3] - r[1];
        if (Display.ImgW > 0 && Display.ImgH > 0) { ix += Display.ImgX; iy += Display.ImgY; iw = Display.ImgW; ih = Display.ImgH; } // (con bandas)
        return iw > 0 && ih > 0;
    }

    // Última lectura: posición real en pantalla y la que vio el juego. SetCursorPos (0x61078D, scrollViewAndWarpCursor:
    // lee el cursor, desplaza la vista y lo devuelve a su sitio) aplica solo la diferencia sobre la posición real, sin
    // volver a redondear la posición entera: si no, cada ida y vuelta (escala ~1,4) movía el cursor un píxel.
    private static int _realX, _realY, _gameX, _gameY;
    private static bool _haveLast;
    // Posición real con decimales tras la última recolocación (el cursor de Windows solo guarda enteros): si la siguiente
    // lectura lo encuentra donde lo dejamos, se parte de ella y el ida y vuelta no acumula redondeos.
    private static double _accX, _accY; private static int _setX = int.MinValue, _setY;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetCursorPosHook(int* pt)
    {
        int ok = _getCursorPos(pt);
        if (ok != 0 && pt != null && Client(out int cx, out int cy, out int ix, out int iy, out int iw, out int ih))
        {
            int rx = pt[0], ry = pt[1];
            double ax = rx, ay = ry;
            if (rx == _setX && ry == _setY) { ax = _accX; ay = _accY; } else _setX = int.MinValue;
            pt[0] = cx + (int)Math.Floor((ax - ix + 0.5) * Display.Width / iw);
            pt[1] = cy + (int)Math.Floor((ay - iy + 0.5) * Display.Height / ih);
            _realX = rx; _realY = ry; _gameX = pt[0]; _gameY = pt[1]; _haveLast = true;
        }
        return ok;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetClientRectHook(nint hwnd, int* r)
    {
        if (hwnd == Display.Hwnd && r != null) { r[0] = 0; r[1] = 0; r[2] = Display.Width; r[3] = Display.Height; return 1; }
        return _getClientRect(hwnd, r);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetCursorPosHook(int x, int y)
    {
        if (Client(out int cx, out int cy, out int ix, out int iy, out int iw, out int ih))
        {
            double sx = iw / (double)Display.Width, sy = ih / (double)Display.Height;
            if (_haveLast)
            {
                double bx = _realX == _setX && _realY == _setY ? _accX : _realX, by = _realX == _setX && _realY == _setY ? _accY : _realY;
                _accX = bx + (x - _gameX) * sx; _accY = by + (y - _gameY) * sy;
                x = (int)Math.Round(_accX); y = (int)Math.Round(_accY);
                _setX = x; _setY = y;
            }
            else
            {
                x = ix + (int)Math.Round((x - cx + 0.5) * sx);
                y = iy + (int)Math.Round((y - cy + 0.5) * sy);
            }
        }
        return _setCursorPos(x, y);
    }
}
