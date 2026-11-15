using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SacredFix.Core;
using SacredFix.Native;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Texturas del propio juego para el menú de opciones (el libro de misiones): marco de cuero y madera, pergamino de
/// las páginas y botón de cerrar. Se reconocen por la huella de su contenido (FNV-1a de 64 bits, la de la captura
/// F11) entre las texturas que el juego tiene cargadas. La primera vez se guarda una copia local en
/// SacredFix_cache (solo en este equipo; son datos del juego, no se distribuyen), así están desde el arranque.
/// </summary>
internal static unsafe class SkinTextures
{
    // marco del libro: 3x2 piezas de 256 (en el libro: x 0/256/512, y 0/256)
    public static readonly ulong[] Frame = { 0xF58B3568C82F3C58, 0x6D398A1EBD17B6B4, 0xE0809E382CF9DF5D, 0x442591EBBFC30655, 0x1634C4B3E4AEAD39, 0x5A198F89014987FB };
    // pergamino: izquierda (0,0) y (0,256); mitades (256..384); derecha (384,0) y (384,256); mitades (640..768)
    public const ulong PageL1 = 0xEFEB1663F0DA45AA, PageL2 = 0x71A7D64355E806B5, PageMidL = 0xDB73FBC426214708;
    public const ulong PageR1 = 0x2494944434B219EE, PageR2 = 0x4F69EA6A06E7DF37, PageMidR = 0x533BCA6ECB0AEA76;
    public const ulong Close = 0x407F4A5C519ABC89; // botón de cerrar: uv (0.0039, 0.582)-(0.1387, 0.7168)

    private static readonly Dictionary<ulong, nint> _tex = new();
    private static bool _diskLoaded;
    private static string Dir => Path.Combine(Path.GetDirectoryName(Win32.GetModuleFileName(0)) ?? ".", "SacredFix_cache");

    public static IEnumerable<ulong> All()
    {
        foreach (var h in Frame) yield return h;
        foreach (var h in new[] { PageL1, PageL2, PageMidL, PageR1, PageR2, PageMidR, Close }) yield return h;
    }

    public static nint Get(ulong h) => _tex.TryGetValue(h, out var t) ? t : 0;

    /// <summary>
    /// ¿Están las imprescindibles (marco, página derecha y cerrar)? Si falta alguna, las busca (copia del disco y
    /// texturas cargadas del juego). La página izquierda, si aún no está, se dibuja con la derecha en espejo.
    /// </summary>
    public static bool Ready()
    {
        if (Missing() > 0)
        {
            if (!_diskLoaded) { _diskLoaded = true; LoadDisk(); }
            if (Missing() > 0 && Environment.TickCount64 - _lastScan > 10000) { _lastScan = Environment.TickCount64; ScanGame(); }
        }
        foreach (var h in Frame) if (!_tex.ContainsKey(h)) return false;
        return _tex.ContainsKey(PageR1) && _tex.ContainsKey(PageR2) && _tex.ContainsKey(PageMidR) && _tex.ContainsKey(Close);
    }
    private static long _lastScan = -10000;

    private static int Missing() { int n = 0; foreach (var h in All()) if (!_tex.ContainsKey(h)) n++; return n; }

    private static void LoadDisk()
    {
        try
        {
            foreach (var h in All())
            {
                string f = Path.Combine(Dir, $"{h:X16}.bgra");
                if (!File.Exists(f)) continue;
                byte[] b = File.ReadAllBytes(f);
                int w = BitConverter.ToInt32(b, 0), hh = BitConverter.ToInt32(b, 4);
                if (w <= 0 || hh <= 0 || b.Length != 8 + w * hh * 4) continue;
                fixed (byte* p = &b[8]) { nint t = Backend.Current.CreateRgbaTexture(w, hh, (uint*)p); if (t != 0) _tex[h] = t; }
            }
        }
        catch (Exception ex) { Log.Warn("Menú: copia de las texturas del libro: " + ex.Message); }
    }

    /// <summary>Entre las texturas cargadas del juego (256x256), las que faltan, por su huella.</summary>
    private static void ScanGame()
    {
        var want = new HashSet<ulong>();
        foreach (var h in All()) if (!_tex.ContainsKey(h)) want.Add(h);
        List<object?> objs;
        lock (Layer.Objects) objs = new List<object?>(Layer.Objects);
        foreach (var o in objs)
        {
            if (want.Count == 0) break;
            if (o is not Surface7 s || s.Type != Surface7.Kind.Texture || s.W != 256 || s.H != 256 || s.Cpu == 0) continue;
            if (s.Fmt != 21 && s.Fmt != 26) continue;
            ulong h = Backend11.TexHash(s);
            if (!want.Remove(h)) continue;
            uint* px = (uint*)NativeMemory.Alloc((nuint)(s.W * s.H * 4));
            for (int i = 0; i < s.W * s.H; i++)
            {
                if (s.Fmt == 21) px[i] = ((uint*)s.Cpu)[i];
                else { uint p = ((ushort*)s.Cpu)[i]; px[i] = ((p >> 12) & 15) * 0x11000000u | ((p >> 8) & 15) * 0x110000u | ((p >> 4) & 15) * 0x1100u | (p & 15) * 0x11u; }
            }
            nint t = Backend.Current.CreateRgbaTexture(s.W, s.H, px);
            if (t != 0) _tex[h] = t;
            try
            {
                Directory.CreateDirectory(Dir);
                byte[] b = new byte[8 + s.W * s.H * 4];
                BitConverter.TryWriteBytes(new Span<byte>(b, 0, 4), s.W); BitConverter.TryWriteBytes(new Span<byte>(b, 4, 4), s.H);
                Marshal.Copy((nint)px, b, 8, s.W * s.H * 4);
                File.WriteAllBytes(Path.Combine(Dir, $"{h:X16}.bgra"), b);
            }
            catch { }
            NativeMemory.Free(px);
        }
        if (Missing() == 0) Log.Info("Menú: texturas del libro del juego listas (con copia local en SacredFix_cache)");
    }
}
