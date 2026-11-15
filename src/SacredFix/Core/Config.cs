using System;
using System.Collections.Generic;
using System.IO;

namespace SacredFix.Core;

/// <summary>
/// Lee SacredFix.ini (junto a Sacred.exe). Formato:
///   [Seccion]
///   Clave=Valor      ; comentario
/// Si el archivo no existe se usan los valores por defecto.
/// </summary>
internal sealed class Config
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ruta del SacredFix.ini cargado (para guardar cambios).</summary>
    public static string? FilePath { get; private set; }

    /// <summary>
    /// Cambia el valor de una clave en SacredFix.ini, respetando el resto del archivo: comentarios, orden y la
    /// alineación del «; comentario» de esa línea. Si la clave no existe en la sección, la añade al final de ella.
    /// </summary>
    public static bool Set(string section, string key, string value)
    {
        try
        {
            if (FilePath == null || !File.Exists(FilePath)) return false;
            // antes del primer cambio, una copia del ini tal como estaba (no se sobrescribe nunca)
            string backup = FilePath + ".original";
            if (!File.Exists(backup)) { File.Copy(FilePath, backup); Log.Info($"Config: copia de seguridad del ini en {backup}"); }
            var lines = new List<string>(File.ReadAllLines(FilePath));
            string cur = "";
            int sectionEnd = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith('[') && t.Contains(']'))
                {
                    if (cur.Equals(section, StringComparison.OrdinalIgnoreCase) && sectionEnd < 0) sectionEnd = i;
                    cur = t[1..t.IndexOf(']')].Trim();
                    continue;
                }
                if (!cur.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
                int eq = lines[i].IndexOf('=');
                if (eq <= 0 || !lines[i][..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                string rest = lines[i][(eq + 1)..];
                int c = rest.IndexOfAny(new[] { ';', '#' });
                string newRest = c < 0 ? value : value.PadRight(Math.Max(value.Length + 1, c)) + rest[c..];
                lines[i] = lines[i][..(eq + 1)] + newRest;
                File.WriteAllLines(FilePath, lines);
                return true;
            }
            if (cur.Equals(section, StringComparison.OrdinalIgnoreCase) && sectionEnd < 0) sectionEnd = lines.Count;
            if (sectionEnd < 0) { lines.Add($"[{section}]"); sectionEnd = lines.Count; }
            lines.Insert(sectionEnd, $"{key}={value}");
            File.WriteAllLines(FilePath, lines);
            return true;
        }
        catch (Exception ex) { Log.Warn($"Config: no se pudo guardar {section}.{key}: {ex.Message}"); return false; }
    }

    public static Config Load(string gameDir)
    {
        var cfg = new Config();
        var path = Path.Combine(gameDir, "SacredFix.ini");
        FilePath = path;
        if (!File.Exists(path)) return cfg;

        string section = "";
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw;
            int c = line.IndexOfAny(new[] { ';', '#' });
            if (c >= 0) line = line[..c];
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            cfg._values[$"{section}.{line[..eq].Trim()}"] = line[(eq + 1)..].Trim();
        }
        return cfg;
    }

    public bool GetBool(string section, string key, bool def)
    {
        if (!_values.TryGetValue($"{section}.{key}", out var v)) return def;
        return v is "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                        || v.Equals("si", StringComparison.OrdinalIgnoreCase);
    }

    public string GetString(string section, string key, string def)
        => _values.TryGetValue($"{section}.{key}", out var v) ? v : def;

    public int GetInt(string section, string key, int def)
        => _values.TryGetValue($"{section}.{key}", out var v) && int.TryParse(v, out var n) ? n : def;
}
