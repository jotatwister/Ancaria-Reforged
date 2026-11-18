using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SacredFix.Core;
using SacredFix.Native;

namespace SacredFix.Patches;

/// <summary>
/// Letra de los textos del juego y del mod ([Fonts] Text, Title, Size).
///
/// El gestor de fuentes del juego (cFontMgr) registra los .TTF de la carpeta font (AddFontResourceA) y crea sus fuentes
/// una vez, al arrancar, con CreateFontIndirectA (IAT 0x89003C) desde su tabla de LOGFONTA (0xA1CD98, 0x44 bytes cada
/// una). Con ellas pinta las letras en texturas y mide los textos (GetTextMetricsA), así que basta con cambiar el
/// LOGFONTA: el juego coloca solo los textos con la letra nueva. Las 7 fuentes (alto en píxeles, negativo):
///   AntiquaSSK 17, 21 y 11 (textos)  ·  Carolingia 16, 27, 21 y 24 (títulos y botones)
/// y una Times New Roman de 20 (consola), que no se toca.
/// - Text / Title: nombre de una fuente instalada en Windows o de un .ttf/.otf de la carpeta font del juego (se registran
///   aquí en privado, solo para este proceso). «juego» o vacío = la original.
/// - Size: 8 a 14; 11 es el tamaño del juego y cada punto escala todas en proporción (alto · Size / 11).
/// Se aplica al arrancar (el juego crea sus fuentes una sola vez). Las letras del mod (MenuFont) usan las mismas.
/// </summary>
internal sealed unsafe class GameFonts : IPatch
{
    public string Name => "Letra del juego";
    public string IniKey => "GameFonts";
    public bool EnabledByDefault => true;

    public const int MinSize = 8, MaxSize = 14, GameSize = 11;
    public const string GameTextFace = "AntiquaSSK", GameTitleFace = "Carolingia";
    /// <summary>Letra elegida para el texto y los títulos (vacío = la del juego) y tamaño (11 = el del juego).</summary>
    public static string TextFace = "", TitleFace = "";
    public static int Size = GameSize;

    private const nint IatCreateFontIndirectA = 0x89003C;
    private static delegate* unmanaged[Stdcall]<byte*, nint> _create;
    private static int _logs;
    private static bool _userFonts;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int AddFontResourceExW(string file, uint flags, nint reserved);

    /// <summary>Lee [Fonts] (también lo usa el menú del mod aunque el parche esté apagado).</summary>
    public static void Load(Config cfg)
    {
        TextFace = Face(cfg.GetString("Fonts", "Text", ""));
        TitleFace = Face(cfg.GetString("Fonts", "Title", ""));
        Size = Math.Clamp(cfg.GetInt("Fonts", "Size", GameSize), MinSize, MaxSize);
        RegisterUserFonts();
    }

    private static string Face(string s)
    {
        s = s.Trim();
        return s.Length == 0 || s.Equals("juego", StringComparison.OrdinalIgnoreCase) || s.Equals("game", StringComparison.OrdinalIgnoreCase) ? "" : s;
    }

    public void Apply(Config cfg)
    {
        Load(cfg);
        nint cur = Mem.Read<nint>(IatCreateFontIndirectA);
        if (cur == 0) throw new InvalidOperationException("IAT de CreateFontIndirectA vacía");
        _create = (delegate* unmanaged[Stdcall]<byte*, nint>)cur;
        Mem.Write<nint>(IatCreateFontIndirectA, (nint)(delegate* unmanaged[Stdcall]<byte*, nint>)&CreateFontIndirectHook);
        Log.Info($"[{Name}] texto «{(TextFace.Length > 0 ? TextFace : GameTextFace)}», títulos «{(TitleFace.Length > 0 ? TitleFace : GameTitleFace)}», tamaño {Size} (juego {GameSize})");
    }

    /// <summary>Los .ttf/.otf de la carpeta font del juego, en privado (para poder elegirlos aunque no estén instalados).</summary>
    public static void RegisterUserFonts()
    {
        if (_userFonts) return;
        _userFonts = true;
        try
        {
            string dir = Path.Combine(Path.GetDirectoryName(Win32.GetModuleFileName(0)) ?? ".", "font");
            if (!Directory.Exists(dir)) return;
            int n = 0;
            foreach (string f in Directory.GetFiles(dir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".ttf" or ".otf" or ".ttc" && AddFontResourceExW(f, 0x10 /* FR_PRIVATE */, 0) > 0) n++;
            }
            Log.Info($"[Letra del juego] {n} fuentes de la carpeta font registradas para el juego y el menú");
        }
        catch (Exception ex) { Log.Warn("[Letra del juego] carpeta font: " + ex.Message); }
    }

    /// <summary>Alto ajustado al tamaño elegido (el del juego · Size / 11).</summary>
    public static int Scale(int h) => Size == GameSize ? h : (int)Math.Round(h * Size / (double)GameSize, MidpointRounding.AwayFromZero);

    /// <summary>LOGFONTA: alto +0, ancho +4, peso +16, juego de caracteres +23, calidad +26, nombre +28 (32 bytes, ANSI).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint CreateFontIndirectHook(byte* lf)
    {
        try
        {
            if (lf == null) return _create(lf);
            string face = Marshal.PtrToStringAnsi((nint)(lf + 28)) ?? "";
            bool text = face == GameTextFace, title = face == GameTitleFace;
            if (!text && !title) return _create(lf);
            string nf = text ? TextFace : TitleFace;
            byte cs = GameLanguage.Charset;
            if (nf.Length == 0 && Size == GameSize && cs == 0) return _create(lf);
            byte* copy = stackalloc byte[60];
            Buffer.MemoryCopy(lf, copy, 60, 60);
            *(int*)copy = Scale(*(int*)lf);
            if (cs != 0) copy[23] = cs; // (juego de caracteres del idioma del juego: checo, polaco, ruso...)
            if (nf.Length > 0)
            {
                byte[] name = System.Text.Encoding.Default.GetBytes(nf);
                int len = Math.Min(name.Length, 31);
                for (int i = 0; i < 32; i++) copy[28 + i] = i < len ? name[i] : (byte)0;
                copy[23] = 1; // DEFAULT_CHARSET: que Windows coja la fuente aunque su juego de caracteres sea otro
            }
            if (_logs++ < 12)
                Log.Info($"[Letra del juego] «{face}» {*(int*)lf} -> «{(nf.Length > 0 ? nf : face)}» {*(int*)copy}");
            return _create(copy);
        }
        catch { return _create(lf); }
    }

    // ---- Lista de letras para el menú ----
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int EnumFontFamiliesExW(nint dc, byte* lf, delegate* unmanaged[Stdcall]<byte*, byte*, uint, nint, int> proc, nint param, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    private static List<string>? _families;

    /// <summary>Familias TrueType/OpenType disponibles (instaladas y de la carpeta font), por orden alfabético.</summary>
    public static List<string> Families()
    {
        if (_families != null) return _families;
        RegisterUserFonts();
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        _collect = set;
        try
        {
            nint dc = GetDC(0);
            byte* lf = stackalloc byte[92]; // LOGFONTW
            new Span<byte>(lf, 92).Clear();
            lf[23] = 1; // DEFAULT_CHARSET: todas
            EnumFontFamiliesExW(dc, lf, &EnumProc, 0, 0);
            ReleaseDC(0, dc);
        }
        catch (Exception ex) { Log.Warn("[Letra del juego] lista de fuentes: " + ex.Message); }
        _collect = null;
        _families = new List<string>(set);
        return _families;
    }

    private static SortedSet<string>? _collect;

    /// <summary>ENUMLOGFONTEXW: LOGFONTW (nombre en +28, 32 wchar); tipo 4 = TrueType (las OpenType también lo marcan).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int EnumProc(byte* elf, byte* tm, uint type, nint param)
    {
        try
        {
            if ((type & 4) == 0) return 1;           // solo TrueType / OpenType (escalables)
            if (elf[23] == 2) return 1;              // SYMBOL_CHARSET (Wingdings...)
            string name = new string((char*)(elf + 28));
            if (name.Length == 0 || name[0] == '@') return 1; // verticales
            _collect?.Add(name);
        }
        catch { }
        return 1;
    }
}
