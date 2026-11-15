using System;
using System.IO;
using SacredFix.Native;

namespace SacredFix.Core;

/// <summary>
/// Registro en SacredFix.log, junto a Sacred.exe. También se envía a OutputDebugString,
/// así que se ve en tools/crashcatch.py (líneas DEBUGSTR) o en DebugView.
/// Nunca lanza excepciones: un fallo del log no debe tumbar el juego.
/// </summary>
internal static class Log
{
    private static readonly object Sync = new();
    private static StreamWriter? _writer;
    private static bool _enabled = true;

    private static bool _initDone;

    /// <summary>Puede llamarse desde varios hilos (arranque temprano y Bootstrap): solo vale la primera.</summary>
    public static void Init(string gameDir, bool enabled)
    {
        lock (Sync)
        {
            if (_initDone) return;
            _initDone = true;
            _enabled = enabled;
            if (!enabled) return;
            try
            {
                var path = Path.Combine(gameDir, "SacredFix.log");
                _writer = new StreamWriter(path, append: false) { AutoFlush = true };
            }
            catch
            {
                _writer = null; // sin archivo: seguimos solo con OutputDebugString
            }
        }
    }

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("AVISO", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        if (!_enabled) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}";
        try
        {
            lock (Sync) _writer?.WriteLine(line);
            Win32.OutputDebugStringW("[SacredFix] " + line);
        }
        catch
        {
            // ignorado a propósito
        }
    }
}
