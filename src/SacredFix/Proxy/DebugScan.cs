using System;
using System.IO;
using System.Runtime.InteropServices;
using SacredFix.Core;

namespace SacredFix.Proxy;

/// <summary>
/// Diagnóstico de un solo uso al entrar en la partida (desde el dibujo del minimapa, con el héroe):
///   [Debug] ShowTexts=1070,1073,...: apunta en el registro esos textos del juego. Solo números que el juego usa:
///   la búsqueda por número (0x672C90 -> 0x80E780) construye objetos y con un número que no existe tumba el juego,
///   así que no se puede recorrer a ciegas.
///   [Debug] DumpHero=1: guarda los primeros 0x4000 bytes del héroe en SacredFix_heroe.bin (con ReadProcessMemory,
///   que no falla si se sale de la memoria del objeto: lo que no se pueda leer queda a cero).
/// </summary>
internal static unsafe partial class DebugScan
{
    private static int[] _ids = Array.Empty<int>();
    private static bool _dump, _quests, _done;

    [LibraryImport("kernel32.dll")] private static partial nint GetCurrentProcess();
    [LibraryImport("kernel32.dll")] private static partial int ReadProcessMemory(nint proc, nint addr, byte* buf, nint size, nint* read);

    public static void Init(Config cfg)
    {
        var list = new System.Collections.Generic.List<int>();
        foreach (var p in cfg.GetString("Debug", "ShowTexts", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(p, out int v) && v > 0) list.Add(v);
        _ids = list.ToArray();
        _dump = cfg.GetBool("Debug", "DumpHero", false);
        _quests = cfg.GetBool("Debug", "DumpQuests", false);
    }

    /// <summary>Lee memoria sin fallar (lo que no se pueda leer queda a cero).</summary>
    private static byte[] Read(nint addr, int n)
    {
        byte[] buf = new byte[n];
        fixed (byte* b = buf)
            for (int off = 0; off < n; off += 0x100)
            {
                nint got = 0;
                ReadProcessMemory(GetCurrentProcess(), addr + off, b + off, Math.Min(0x100, n - off), &got);
            }
        return buf;
    }

    /// <summary>
    /// Gestor de misiones (supuesto: [0xAAD3A4]; lista +0x31C..+0x320 de entradas de 0x34 bytes y lista +0x424..+0x428
    /// de 0x174 bytes, la estructura que copia TRIGGER_INSERT_QUEST en 0x7E71D9) y la brújula (0xAB7820..0xAB7860).
    /// Todo a SacredFix_misiones.bin: bloques [etiqueta de 16 bytes][dirección][tamaño][datos].
    /// </summary>
    /// <summary>Desde el menú (Pruebas): guardar ahora las misiones (cada vez, con número).</summary>
    public static void DumpQuestsNow() { try { _n++; DumpQuests(); } catch (Exception ex) { Log.Warn("DebugScan: " + ex.Message); } }
    private static int _n;

    private static void DumpQuests()
    {
        string path = Path.Combine(Path.GetDirectoryName(Config.FilePath) ?? ".", _n > 0 ? $"SacredFix_misiones_{_n}.bin" : "SacredFix_misiones.bin");
        using var f = new BinaryWriter(File.Create(path));
        void Block(string tag, nint addr, int n)
        {
            n = Math.Clamp(n, 0, 0x200000);
            var t = new byte[16]; System.Text.Encoding.ASCII.GetBytes(tag).AsSpan(0, Math.Min(16, tag.Length)).CopyTo(t);
            f.Write(t); f.Write((uint)addr); f.Write(n); f.Write(Read(addr, n));
        }
        // 0xAAD3A4: std::vector de misiones (inicio, fin, capacidad), 0x174 bytes cada una; 0xAAD3B0: otra lista
        byte[] g = Read(0xAAD3A4, 0x18);
        nint qa = BitConverter.ToInt32(g, 0), qb = BitConverter.ToInt32(g, 4), oa = BitConverter.ToInt32(g, 0xC), ob = BitConverter.ToInt32(g, 0x10);
        Block("global", 0xAAD380, 0x80);
        Block("brujula", 0xAB7820, 0x40);
        int nq = qa != 0 && qb > qa ? (int)((qb - qa) / 0x174) : 0;
        Log.Info($"DebugScan: misiones en 0xAAD3A4: {nq}; lista 0xAAD3B0: {(ob > oa ? ob - oa : 0)} bytes");
        if (nq > 0 && nq < 500)
        {
            Block("misiones", qa, nq * 0x174);
            for (int k = 0; k < nq; k++)
            {
                byte[] q = Read(qa + k * 0x174, 0x40);
                var sb = new System.Text.StringBuilder($"DebugScan: misión {k}: ");
                for (int o = 0; o < 0x24; o += 4) sb.Append($"+{o:X2}={BitConverter.ToInt32(q, o)} ");
                Log.Info(sb.ToString());
                for (int o = 0x24; o <= 0x3C; o += 4)
                {
                    int id = BitConverter.ToInt32(q, o);
                    if (id == 0 || id == -1 || (id & 0x80000000) == 0) continue; // solo los de los datos del juego (bit alto)
                    Log.Info($"DebugScan:    +{o:X2} texto 0x{id:X8}: {BuffBar.Text(id, 400).Replace("\n", " | ").Replace("\r", " ")}");
                }
            }
        }
        if (oa != 0 && ob > oa && ob - oa < 0x100000) Block("lista3B0", oa, (int)(ob - oa));
        Log.Info($"DebugScan: misiones guardadas en {path}");
    }

    /// <summary>¿Queda algo por hacer? (para no buscar al héroe en cada fotograma).</summary>
    public static bool Pending => !_done && (_ids.Length > 0 || _dump || _quests);

    public static void Run(nint hero)
    {
        if (_done || hero == 0 || (_ids.Length == 0 && !_dump && !_quests)) return;
        _done = true;
        try
        {
            foreach (int id in _ids)
                Log.Info($"DebugScan: texto {id}: {BuffBar.Text(id, 300).Replace("\n", " ").Replace("\r", " ")}");
            if (_dump)
            {
                const int N = 0x4000;
                byte[] buf = new byte[N];
                fixed (byte* b = buf)
                    for (int off = 0; off < N; off += 0x100)
                    {
                        nint got = 0;
                        ReadProcessMemory(GetCurrentProcess(), hero + off, b + off, 0x100, &got);
                    }
                string path = Path.Combine(Path.GetDirectoryName(Config.FilePath) ?? ".", "SacredFix_heroe.bin");
                File.WriteAllBytes(path, buf);
                Log.Info($"DebugScan: héroe 0x{hero:X8} guardado en {path}");
            }
            if (_quests) DumpQuests();
        }
        catch (Exception ex) { Log.Warn("DebugScan: " + ex.Message); }
    }
}
