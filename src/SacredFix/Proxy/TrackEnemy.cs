using System;
using System.Runtime.InteropServices;
using SacredFix.Core;
using SacredFix.Proxy.D9;

namespace SacredFix.Proxy;

/// <summary>
/// El cursor sigue al enemigo atacado (opción del juego AUTOTRACKENEMY, lector 0x81A330 sobre 0x182EE78).
///
/// El original (scrollViewAndWarpCursor, 0x6105B0) solo compensa el desplazamiento de la cámara mientras el héroe
/// va hacia su objetivo: como el enemigo también se mueve, el cursor se queda atrás. Aquí, en cada fotograma de la
/// partida y con las mismas condiciones que el juego, el cursor se coloca sobre la posición en pantalla del objetivo:
///   - héroe 0x603E30; objetivo: [héroe+0x104] si word[+0xFC] es 4 u 8; [héroe+0x158] si es 6 u 0xB; y word[+0x150] == 1.
///   - objetivo: 0x5FE000 y que sea cCreature (__RTDynamicCast 0x84A961, cObject 0x8EB648 → cCreature 0x8EB660).
///   - posición del mundo: obj+0x1C, obj+0x20 (enteros) → píxeles isométricos con 0x622620 (cdecl, salida, entrada vec3).
///   - motor 0x60D6C0(-1); vista = [motor + 8 + word[motor+0x48]·4]: cámara +0x14/+0x18 (px), zoom +0xB60.
///   - pantalla = (W/2, H/2) + (iso − cámara) / zoom (la misma que el suelo 2D), y se sube hasta el cuerpo.
/// </summary>
internal static unsafe class TrackEnemy
{
    public static volatile bool Enabled = true;
    /// <summary>Píxeles (sin zoom) desde los pies hasta el punto del cuerpo donde se pone el cursor.</summary>
    public static int BodyLift = 40;
    private static int _errors, _logs, _optFrames;
    private static bool _opt;
    private static int _lastTarget;

    [DllImport("user32.dll")] private static extern int ClientToScreen(nint hwnd, int* pt);

    public static void Init(Config cfg)
    {
        Enabled = cfg.GetBool("Fixes", "TrackEnemy", true);
        BodyLift = cfg.GetInt("Fixes", "TrackEnemyLift", 40);
    }

    /// <summary>Llamado en los fotogramas de la partida (UiCanvas, con MiniMap.InGameView()).</summary>
    public static void Tick()
    {
        if (!Enabled) return;
        try
        {
            // la opción del juego (lee Settings en cada llamada: se consulta cada 2 s)
            if (_optFrames-- <= 0) { _optFrames = 120; _opt = ((delegate* unmanaged[Thiscall]<nint, byte>)0x81A330)(0x182EE78) != 0; }
            if (!_opt) return;

            nint om = *(nint*)0xAD5C40;
            if (om == 0) return;
            nint hero = ((delegate* unmanaged[Thiscall]<nint, nint>)0x603E30)(om);
            if (hero == 0) { _lastTarget = 0; return; }
            ushort fc = *(ushort*)(hero + 0xFC), st = *(ushort*)(hero + 0x150);
            int id = fc is 4 or 8 ? *(int*)(hero + 0x104) : fc is 6 or 0xB ? *(int*)(hero + 0x158) : 0;
            if (id == 0 || st != 1) { _lastTarget = 0; return; }

            nint obj = ((delegate* unmanaged[Thiscall]<nint, int, nint>)0x5FE000)(om, id);
            if (obj == 0) return;
            var cast = (delegate* unmanaged[Cdecl]<nint, int, nint, nint, int, nint>)0x84A961;
            if (cast(obj, 0, 0x8EB648, 0x8EB660, 0) == 0) return; // solo criaturas (no objetos del suelo)

            nint eng = ((delegate* unmanaged[Cdecl]<int, nint>)0x60D6C0)(-1);
            if (eng == 0) return;
            nint view = *(nint*)(eng + 8 + *(ushort*)(eng + 0x48) * 4);
            if (view == 0) return;
            float zoom = *(float*)(view + 0xB60);
            if (!(zoom > 0.05f && zoom < 20f)) return;

            float* w = stackalloc float[3];
            float* iso = stackalloc float[3];
            w[0] = *(int*)(obj + 0x1C); w[1] = *(int*)(obj + 0x20); w[2] = 0;
            ((delegate* unmanaged[Cdecl]<float*, float*, void>)0x622620)(iso, w);
            int camX = *(int*)(view + 0x14), camY = *(int*)(view + 0x18);
            float gx = Display.Width / 2f + (iso[0] - camX) / zoom;
            float gy = Display.Height / 2f + (iso[1] - camY) / zoom - BodyLift / zoom;

            // cursor actual en coordenadas del juego (GetCursorPos de la IAT: con MouseScale ya convertido)
            int* p = stackalloc int[2];
            int* o = stackalloc int[2];
            o[0] = 0; o[1] = 0;
            ClientToScreen(Display.Hwnd, o);
            ((delegate* unmanaged[Stdcall]<int*, int>)*(nint*)0x8902B8)(p);
            int curX = p[0] - o[0], curY = p[1] - o[1];

            if (id != _lastTarget)
            {
                // primer fotograma con este objetivo: el cursor acaba de hacer clic sobre él (sirve de calibración)
                _lastTarget = id;
                if (_logs++ < 12)
                {
                    int alt = *(int*)0xAD7998, altY = *(int*)0xAD799C;
                    Log.Info($"TrackEnemy: objetivo {id} (estado {fc}), cursor {curX},{curY}; calculado {gx:0},{gy:0} " +
                             $"(iso {iso[0]:0},{iso[1]:0}, cámara {camX},{camY}, AD7998 {alt},{altY}, zoom {zoom:0.###}, pies y {gy + BodyLift / zoom:0})");
                }
                return;
            }
            if (gx < 0 || gy < 0 || gx >= Display.Width || gy >= Display.Height) return;
            int tx = (int)MathF.Round(gx), ty = (int)MathF.Round(gy);
            if (tx == curX && ty == curY) return;
            ((delegate* unmanaged[Stdcall]<int, int, int>)*(nint*)0x8902B4)(o[0] + tx, o[1] + ty);
        }
        catch (Exception ex) { if (_errors++ < 3) Log.Error("TrackEnemy: " + ex.Message); }
    }
}
