using System.Runtime.InteropServices;
using SacredFix.Core;
using static SacredFix.Proxy.D9.Display;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Motor Direct3D 11: pantalla completa exclusiva ([Display] D3D9Window=fullscreen).
/// - La ventana se prepara igual que sin bordes (del tamaño del monitor) y la cadena se pone en exclusiva con
///   SetFullscreenState. El modo de vídeo es el del escritorio, así que no hay cambio de resolución: la imagen del juego
///   se estira como en sin bordes.
/// - Al perder el foco (Alt+Tab, pinchar fuera), DXGI sale solo de la exclusiva. Al volver la ventana al frente, el
///   motor vuelve a entrar, como mucho una vez cada 30 fotogramas, y rehace los búferes con ResizeBuffers.
/// - Si no se puede entrar (por ejemplo, porque la ventana no está al frente al arrancar), sigue funcionando sin bordes
///   y lo reintenta.
/// </summary>
internal sealed unsafe partial class Backend11
{
    private const int ScSetFullscreen = 10, ScGetFullscreen = 11;
    private bool _exclusive, _isExclusive;
    private int _exclTry, _exclErrors;

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();

    private void EnterExclusive()
    {
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, nint, int>)Fn(_sc, ScSetFullscreen))(_sc, 1, 0);
        if (hr != 0)
        {
            if (_exclErrors++ < 5) Log.Warn($"D11: no se pudo entrar en pantalla completa exclusiva (0x{hr:X8}): sin bordes por ahora");
            return;
        }
        _isExclusive = true;
        CheckResize(true);
        Log.Info("D11: pantalla completa exclusiva");
    }

    /// <summary>Antes de presentar: si se salió de la exclusiva y la ventana vuelve a estar al frente, se vuelve a entrar.</summary>
    private void KeepExclusive()
    {
        int fs = 0; nint target = 0;
        ((delegate* unmanaged[Stdcall]<nint, int*, nint*, int>)Fn(_sc, ScGetFullscreen))(_sc, &fs, &target);
        Layer.ReleaseCom(target);
        if (fs != 0) { _isExclusive = true; return; }
        if (_isExclusive)
        {
            _isExclusive = false;
            _exclTry = 0;
            Log.Info("D11: fuera de la pantalla completa exclusiva (la ventana ha perdido el foco)");
            CheckResize(true);
        }
        if (GetForegroundWindow() == Hwnd && IsIconic(Hwnd) == 0 && _exclTry++ % 30 == 0) EnterExclusive();
    }
}
