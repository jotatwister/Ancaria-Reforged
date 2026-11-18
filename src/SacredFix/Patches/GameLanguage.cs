using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SacredFix.Core;
using SacredFix.Native;

namespace SacredFix.Patches;

/// <summary>
/// Idioma de los textos del juego ([Language] Game). El ejecutable inglés 2.0.2.28 ya trae 11 idiomas en su tabla
/// (0x899394, 16 bytes cada uno: US, DE, FR, SP, IT, PL, HU, JP, VC, RU, CZ). En WinMain (0x817B30) busca cada código
/// entre las palabras de su línea de órdenes (bucle 0x817D86, con 0x813A90) y, si está, fija ese idioma ([0x17E7D34]) y
/// carga «.\SCRIPTS\&lt;código&gt;\global.res» (0x817DC3); si no, el de [0x17E7D34]. Steam no pasa ninguno, y la versión de
/// Steam vuelve a escribir LANGUAGE : US en Settings.cfg, así que cambiar ese valor no hace nada.
///
/// El idioma se pone en el DllMain nativo (Native/early.c), antes de que arranque el juego: añade el código a la línea de
/// órdenes que devuelve GetCommandLineA, como si se hubiera lanzado «Sacred.exe SP». (Un parche de bytes en WinMain
/// perdía la carrera con el descifrado de SteamStub: el juego carga sus textos nada más descifrarse.) Eso fija el índice,
/// pero la versión de Steam/GOG no lee los textos del archivo: lleva el global.res inglés dentro del .exe (recurso
/// «BINARY» 107, cifrado con XOR) y su cargador 0x80E680 no mira la ruta; early.c lo cambia por uno que entrega el
/// global.res elegido. Las voces ([Language] Voices) son otro sound.pak, en pak\&lt;código&gt;\; early.c cambia la ruta
/// que usa el juego (0x81720E y el búfer 0x9D760C). Aquí solo se comprueba y se registra. Los global.res de otras versiones (por ejemplo de un language pack) tienen los mismos ids
/// que el de Steam. No se reparte ningún texto del juego: cada jugador pone su global.res en scripts\&lt;código&gt;\.
/// </summary>
internal sealed class GameLanguage : IPatch
{
    public string Name => "Idioma de los textos del juego";
    public string IniKey => "GameLanguage";
    public bool EnabledByDefault => true;

    public static readonly string[] Codes = { "US", "DE", "FR", "SP", "IT", "PL", "HU", "JP", "VC", "RU", "CZ" };
    public static readonly string[] Names = { "English", "Deutsch", "Français", "Español", "Italiano", "Polski", "Magyar", "日本語", "中文", "Русский", "Čeština" };

    /// <summary>Código elegido (vacío = el del juego).</summary>
    public static string Code { get; private set; } = "";

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern nint GetCommandLineA();

    public void Apply(Config cfg)
    {
        // voces ([Language] Voices): las pone Native/early.c cambiando el push de la ruta del sound.pak (0x81720E)
        unsafe
        {
            nint vp = *(nint*)0x81720F;
            if (vp != 0xA1D3D0) Log.Info($"[{Name}] voces: {Marshal.PtrToStringAnsi(vp)}");
            else if (cfg.GetString("Language", "Voices", "juego").Trim().Length is 2) Log.Warn($"[{Name}] voces elegidas, pero no está pak/<código>/sound.pak: se quedan las del juego");
        }
        string c = cfg.GetString("Language", "Game", "juego").Trim().ToUpperInvariant();
        if (c.Length > 2) c = c.Substring(0, 2);
        int idx = Array.IndexOf(Codes, c);
        if (idx < 0) { Code = ""; Log.Info($"[{Name}] el del juego"); return; }
        if (!File.Exists(ResPath(c))) { Log.Warn($"[{Name}] no está {ResPath(c)}: se queda el del juego"); return; }
        Code = c;
        string line = Marshal.PtrToStringAnsi(GetCommandLineA()) ?? "";
        bool ok = (" " + line + " ").Contains(" " + c.ToLowerInvariant() + " ");
        if (ok) Log.Info($"[{Name}] {Names[idx]} ({c}): scripts\\{c}\\global.res (línea de órdenes: «{line}»)");
        else Log.Warn($"[{Name}] {Names[idx]} ({c}) elegido, pero la línea de órdenes no lo lleva («{line}»): el juego sigue en su idioma");
        unsafe
        {
            int gi = *(int*)0x17E7D34;
            bool own = *(byte*)0x80E680 == 0xE9; // cargador de textos cambiado por Native/early.c
            Log.Info($"[{Name}] índice del juego {gi} ({(gi >= 0 && gi < Codes.Length ? Codes[gi] : "?")}); textos {(own ? "del global.res" : "originales (recurso del .exe)")}; «Level» = «{SacredFix.Proxy.GameText.Get(SacredFix.Proxy.GameText.Level, "(sin texto)")}»");
        }
    }

    public static string GameDir => Path.GetDirectoryName(Win32.GetModuleFileName(0)) ?? ".";
    public static string ResPath(string code) => Path.Combine(GameDir, "scripts", code, "global.res");

    /// <summary>Idiomas con voces (pak\&lt;código&gt;\sound.pak) en la carpeta del juego (para el menú).</summary>
    public static List<string> AvailableVoices()
    {
        var l = new List<string>();
        foreach (var c in Codes) if (File.Exists(Path.Combine(GameDir, "pak", c, "sound.pak"))) l.Add(c);
        return l;
    }

    /// <summary>Idiomas con global.res en la carpeta scripts del juego (para el menú).</summary>
    public static List<string> Available()
    {
        var l = new List<string>();
        foreach (var c in Codes) if (File.Exists(ResPath(c))) l.Add(c);
        return l;
    }

    /// <summary>Juego de caracteres de Windows para las letras del idioma elegido (0 = el del juego).</summary>
    public static byte Charset => Code switch
    {
        "CZ" or "PL" or "HU" => 238, // EASTEUROPE_CHARSET
        "RU" => 204,                  // RUSSIAN_CHARSET
        "JP" => 128,                  // SHIFTJIS_CHARSET
        "VC" => 136,                  // CHINESEBIG5_CHARSET
        _ => 0,
    };
}
