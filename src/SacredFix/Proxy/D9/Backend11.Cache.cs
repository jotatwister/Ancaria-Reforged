using System;
using System.IO;
using System.Runtime.InteropServices;
using SacredFix.Core;
using SacredFix.Native;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Motor Direct3D 11: caché de shaders compilados en disco (carpeta SacredFix_shaders junto al juego).
/// - Compilar un shader nuevo con D3DCompile cuesta varios ms. Al entrar en partida aparecen varias combinaciones de
///   estados a la vez y se notaba un tirón de hasta 50 ms. Ahora cada shader compilado se guarda en un archivo
///   (&lt;huella&gt;.cso) y las siguientes veces se lee de ahí (D3DCreateBlob + copia).
/// - La huella es FNV-1a de 128 bits del código HLSL y del perfil (vs_4_0 / ps_4_0). Si cambia el generador de
///   shaders, cambia el código y con él el nombre, así que los viejos simplemente dejan de usarse. Se pueden borrar sin
///   problema: se vuelven a crear.
/// - Si la carpeta no se puede escribir, se compila como antes.
/// </summary>
internal sealed unsafe partial class Backend11
{
    private static string? _cacheDir;
    private static bool _cacheBroken, _cacheLogged;
    private static nint _createBlob;
    private static int _cacheHits, _cacheNew;

    private static string CacheDir()
    {
        if (_cacheDir == null)
        {
            string dir = Path.GetDirectoryName(Win32.GetModuleFileName(0)) ?? ".";
            _cacheDir = Path.Combine(dir, "SacredFix_shaders");
        }
        return _cacheDir;
    }

    private static string Fingerprint(string src, string target)
    {
        ulong h1 = 0xCBF29CE484222325, h2 = 0x84222325CBF29CE4;
        void Mix(char c)
        {
            h1 = (h1 ^ c) * 0x100000001B3;
            h2 = (h2 ^ (uint)(c * 31 + 7)) * 0x100000001B3;
        }
        foreach (char c in "v1|") Mix(c);
        foreach (char c in target) Mix(c);
        Mix('\n');
        foreach (char c in src) Mix(c);
        return $"{h1:x16}{h2:x16}";
    }

    /// <summary>ID3DBlob con estos bytes (D3DCreateBlob de d3dcompiler_47).</summary>
    private static nint MakeBlob(byte[] bytes)
    {
        if (_createBlob == 0)
        {
            nint m = Win32.LoadLibraryW(Path.Combine(Win32.GetSystemDirectory(), "d3dcompiler_47.dll"));
            _createBlob = m != 0 ? Win32.GetProcAddress(m, "D3DCreateBlob") : 0;
            if (_createBlob == 0) return 0;
        }
        nint blob = 0;
        if (((delegate* unmanaged[Stdcall]<nuint, nint*, int>)_createBlob)((nuint)bytes.Length, &blob) != 0 || blob == 0) return 0;
        Marshal.Copy(bytes, 0, BlobPtr(blob), bytes.Length);
        return blob;
    }

    private static nint CachedCompile(string src, string target)
    {
        if (_cacheBroken) return CompileCore(src, target);
        string file = Path.Combine(CacheDir(), Fingerprint(src, target) + ".cso");
        try
        {
            if (File.Exists(file))
            {
                nint blob = MakeBlob(File.ReadAllBytes(file));
                if (blob != 0) { _cacheHits++; return blob; }
            }
        }
        catch (Exception ex) { Log.Warn($"D11: no se pudo leer {file}: {ex.Message}"); }
        nint code = CompileCore(src, target);
        if (code == 0) return 0;
        try
        {
            Directory.CreateDirectory(CacheDir());
            var bytes = new byte[(int)BlobSize(code)];
            Marshal.Copy(BlobPtr(code), bytes, 0, bytes.Length);
            File.WriteAllBytes(file, bytes);
            _cacheNew++;
            if (!_cacheLogged) { _cacheLogged = true; Log.Info($"D11: caché de shaders compilados en {CacheDir()}"); }
        }
        catch (Exception ex)
        {
            _cacheBroken = true;
            Log.Warn($"D11: no se puede guardar la caché de shaders ({ex.Message}): se compilan cada vez");
        }
        return code;
    }
}
