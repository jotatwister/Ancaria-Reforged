using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SacredFix.Core;

namespace SacredFix.Proxy;

/// <summary>
/// Fase 0 de la capa Direct3D 9 propia ([Debug] ApiInventory=1): inventario de lo que usa el juego de DirectDraw 7 y
/// Direct3D 7. Cada método de IDirectDraw7, IDirectDrawSurface7, IDirect3D7, IDirect3DDevice7 e
/// IDirect3DVertexBuffer7 pasa por un pequeño adaptador (pushad; registrar; popad; jmp al de antes), así se cuentan
/// las llamadas sin cambiar nada. De algunos se guardan además los parámetros distintos (estados de render y de
/// textura, formatos de vértice, banderas de Lock, Blt, Flip, descripciones de superficie...) y desde qué sitio del
/// juego se llaman. Cada 30 s se escribe todo en SacredFix_api.txt (carpeta del juego).
/// </summary>
internal static unsafe class ApiInventory
{
    public static bool Enabled { get; private set; }

    private enum If { DD, Surf, D3D, Dev, VB }

    private static readonly string[][] Names =
    {
        new[] { "QueryInterface", "AddRef", "Release", "Compact", "CreateClipper", "CreatePalette", "CreateSurface",
                "DuplicateSurface", "EnumDisplayModes", "EnumSurfaces", "FlipToGDISurface", "GetCaps", "GetDisplayMode",
                "GetFourCCCodes", "GetGDISurface", "GetMonitorFrequency", "GetScanLine", "GetVerticalBlankStatus",
                "Initialize", "RestoreDisplayMode", "SetCooperativeLevel", "SetDisplayMode", "WaitForVerticalBlank",
                "GetAvailableVidMem", "GetSurfaceFromDC", "RestoreAllSurfaces", "TestCooperativeLevel",
                "GetDeviceIdentifier", "StartModeTest", "EvaluateMode" },
        new[] { "QueryInterface", "AddRef", "Release", "AddAttachedSurface", "AddOverlayDirtyRect", "Blt", "BltBatch",
                "BltFast", "DeleteAttachedSurface", "EnumAttachedSurfaces", "EnumOverlayZOrders", "Flip",
                "GetAttachedSurface", "GetBltStatus", "GetCaps", "GetClipper", "GetColorKey", "GetDC", "GetFlipStatus",
                "GetOverlayPosition", "GetPalette", "GetPixelFormat", "GetSurfaceDesc", "Initialize", "IsLost", "Lock",
                "ReleaseDC", "Restore", "SetClipper", "SetColorKey", "SetOverlayPosition", "SetPalette", "Unlock",
                "UpdateOverlay", "UpdateOverlayDisplay", "UpdateOverlayZOrder", "GetDDInterface", "PageLock",
                "PageUnlock", "SetSurfaceDesc", "SetPrivateData", "GetPrivateData", "FreePrivateData",
                "GetUniquenessValue", "ChangeUniquenessValue", "SetPriority", "GetPriority", "SetLOD", "GetLOD" },
        new[] { "QueryInterface", "AddRef", "Release", "EnumDevices", "CreateDevice", "CreateVertexBuffer",
                "EnumZBufferFormats", "EvictManagedTextures" },
        new[] { "QueryInterface", "AddRef", "Release", "GetCaps", "EnumTextureFormats", "BeginScene", "EndScene",
                "GetDirect3D", "SetRenderTarget", "GetRenderTarget", "Clear", "SetTransform", "GetTransform",
                "SetViewport", "MultiplyTransform", "GetViewport", "SetMaterial", "GetMaterial", "SetLight", "GetLight",
                "SetRenderState", "GetRenderState", "BeginStateBlock", "EndStateBlock", "PreLoad", "DrawPrimitive",
                "DrawIndexedPrimitive", "SetClipStatus", "GetClipStatus", "DrawPrimitiveStrided",
                "DrawIndexedPrimitiveStrided", "DrawPrimitiveVB", "DrawIndexedPrimitiveVB", "ComputeSphereVisibility",
                "GetTexture", "SetTexture", "GetTextureStageState", "SetTextureStageState", "ValidateDevice",
                "ApplyStateBlock", "CaptureStateBlock", "DeleteStateBlock", "CreateStateBlock", "Load", "LightEnable",
                "GetLightEnable", "SetClipPlane", "GetClipPlane", "GetInfo" },
        new[] { "QueryInterface", "AddRef", "Release", "Lock", "Unlock", "ProcessVertices", "GetVertexBufferDesc",
                "Optimize", "ProcessVerticesStrided" },
    };

    private static readonly long[][] _counts = Array.ConvertAll(Names, n => new long[n.Length]);
    private static readonly HashSet<nint> _patched = new();
    // Detalles por método: «parámetros» distintos y sitios del juego (dirección de vuelta) que lo llaman
    private static readonly Dictionary<string, HashSet<string>> _details = new();
    private static readonly Dictionary<string, HashSet<nint>> _callers = new();
    private static readonly object _lock = new();
    private static long _lastDump;
    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private static nint _recorder;

    public static void Init(Config cfg)
    {
        Enabled = cfg.GetBool("Debug", "ApiInventory", false);
        if (Enabled) Log.Info("ApiInventory: activado: se registran todas las llamadas a DirectDraw / Direct3D 7 (SacredFix_api.txt)");
    }

    public static void OnDirectDraw(nint obj) { if (Enabled) PatchAll(obj, If.DD); }
    public static void OnSurface(nint obj) { if (Enabled) PatchAll(obj, If.Surf); }
    public static void OnDirect3D(nint obj) { if (Enabled) PatchAll(obj, If.D3D); }
    public static void OnDevice(nint obj) { if (Enabled) PatchAll(obj, If.Dev); }

    /// <summary>Engancha todos los métodos de la tabla del objeto (una vez por tabla).</summary>
    private static void PatchAll(nint obj, If kind)
    {
        try
        {
            if (obj == 0) return;
            nint vt = *(nint*)obj;
            lock (_lock) { if (!_patched.Add(vt)) return; }
            if (_recorder == 0) _recorder = (nint)(delegate* unmanaged[Stdcall]<int, nint*, void>)&Record;
            int n = Names[(int)kind].Length;
            for (int i = 0; i < n; i++)
            {
                nint slot = vt + i * sizeof(nint);
                nint orig = *(nint*)slot;
                Mem.Write(slot, MakeStub(((int)kind << 8) | i, orig));
            }
            Log.Info($"ApiInventory: {kind} vtable 0x{vt:X8} enganchada ({n} métodos)");
        }
        catch (Exception ex) { Log.Error("ApiInventory: " + ex.Message); }
    }

    /// <summary>
    /// pushad; pushfd; lea eax, [esp+0x24] (dirección de vuelta); push eax; push id; call Record; popfd; popad;
    /// jmp [orig]. Record es stdcall(id, pila) y limpia sus 8 bytes.
    /// </summary>
    private static nint MakeStub(int id, nint orig)
    {
        var c = new List<byte>();
        c.Add(0x60); c.Add(0x9C);
        c.AddRange(new byte[] { 0x8D, 0x44, 0x24, 0x24 });
        c.Add(0x50);
        c.Add(0x68); c.AddRange(BitConverter.GetBytes(id));
        int callAt = c.Count;
        c.Add(0xE8); c.AddRange(new byte[4]);
        c.Add(0x9D); c.Add(0x61);
        int jmpAt = c.Count;
        c.Add(0xFF); c.Add(0x25); c.AddRange(new byte[4]);
        int dataAt = c.Count;
        c.AddRange(BitConverter.GetBytes((int)orig));
        nint code = Mem.AllocCode(c.ToArray());
        *(int*)(code + callAt + 1) = (int)(_recorder - (code + callAt + 5));
        *(int*)(code + jmpAt + 2) = (int)(code + dataAt);
        return code;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void Record(int id, nint* st)
    {
        try
        {
            int kind = id >> 8, m = id & 0xFF;
            _counts[kind][m]++;
            // st[0] = vuelta, st[1] = this, st[2...] = argumentos
            string name = Names[kind][m];
            nint ret = st[0];
            if (ret >= 0x401000 && ret < 0x890000) AddCaller(kind, name, ret);
            string? d = Detail((If)kind, m, st);
            if (d != null) AddDetail(kind, name, d);
            // objetos nuevos que llegan como argumento: texturas, superficies de Blt, vertex buffers
            if (kind == (int)If.Dev && m == 35 && st[3] != 0) PatchAll(st[3], If.Surf);           // SetTexture
            if (kind == (int)If.Dev && (m == 31 || m == 32) && st[3] != 0) PatchAll(st[3], If.VB); // Draw*VB
            if (kind == (int)If.Surf && m == 5 && st[3] != 0) PatchAll(st[3], If.Surf);           // Blt (origen)
            if (kind == (int)If.Surf && m == 7 && st[4] != 0) PatchAll(st[4], If.Surf);           // BltFast (origen)
            if (_clock.ElapsedMilliseconds - _lastDump > 30000) { _lastDump = _clock.ElapsedMilliseconds; Dump(); }
        }
        catch { }
    }

    private static string? Detail(If kind, int m, nint* st)
    {
        switch (kind)
        {
            case If.DD:
                if (m == 20) return $"flags 0x{(uint)st[3]:X}";                                        // SetCooperativeLevel
                if (m == 21) return $"{st[2]}x{st[3]}x{st[4]} hz {st[5]} flags 0x{(uint)st[6]:X}";       // SetDisplayMode
                if (m == 6 && st[2] != 0) return SurfDesc(st[2]);                                        // CreateSurface
                if (m == 0 && st[2] != 0) return (*(Guid*)st[2]).ToString();                             // QueryInterface
                return null;
            case If.Surf:
                if (m == 5) return $"flags 0x{(uint)st[5]:X}{(st[2] != 0 ? " con rect destino" : "")}{(st[3] != 0 ? " con origen" : " sin origen (relleno)")}"; // Blt
                if (m == 7) return $"trans 0x{(uint)st[6]:X}";                                         // BltFast
                if (m == 11) return $"flags 0x{(uint)st[3]:X}";                                        // Flip
                if (m == 25) return $"flags 0x{(uint)st[4]:X}{(st[2] != 0 ? " con rect" : "")}";        // Lock
                if (m == 29) return $"flags 0x{(uint)st[2]:X}";                                        // SetColorKey
                if (m == 12 && st[2] != 0) return $"caps 0x{*(uint*)st[2]:X}";                          // GetAttachedSurface
                if (m == 0 && st[2] != 0) return (*(Guid*)st[2]).ToString();
                return null;
            case If.D3D:
                if (m == 4 && st[2] != 0) return (*(Guid*)st[2]).ToString();                             // CreateDevice
                if (m == 5 && st[2] != 0) return $"fvf 0x{*(uint*)(st[2] + 8):X} vértices {*(uint*)(st[2] + 12)} caps 0x{*(uint*)(st[2] + 4):X}"; // CreateVertexBuffer
                return null;
            case If.Dev:
                switch (m)
                {
                    case 20: return $"{RsName((int)st[2])} = 0x{(uint)st[3]:X}";                       // SetRenderState
                    case 37: return $"etapa {st[2]} {TssName((int)st[3])} = 0x{(uint)st[4]:X}";        // SetTextureStageState
                    case 25: return $"tipo {st[2]} fvf 0x{(uint)st[3]:X} flags 0x{(uint)st[6]:X}";    // DrawPrimitive
                    case 26: return $"tipo {st[2]} fvf 0x{(uint)st[3]:X} flags 0x{(uint)st[8]:X}";    // DrawIndexedPrimitive
                    case 29: return $"tipo {st[2]} fvf 0x{(uint)st[3]:X}";                             // DrawPrimitiveStrided
                    case 30: return $"tipo {st[2]} fvf 0x{(uint)st[3]:X}";                             // DrawIndexedPrimitiveStrided
                    case 31: case 32: return $"tipo {st[2]}";
                    case 10: return $"flags 0x{(uint)st[4]:X} rects {st[2]}";                          // Clear
                    case 11: case 12: case 14: return $"estado {st[2]}";                               // *Transform
                    case 35: return $"etapa {st[2]}{(st[3] == 0 ? " (nada)" : "")}";                   // SetTexture
                    case 18: case 44: return $"luz {st[2]}";
                    case 8: return $"flags 0x{(uint)st[3]:X}";                                         // SetRenderTarget
                    default: return null;
                }
            case If.VB:
                if (m == 3) return $"flags 0x{(uint)st[2]:X}";                                         // Lock
                return null;
        }
        return null;
    }

    /// <summary>DDSURFACEDESC2: dwFlags +4, alto +8, ancho +0xC, formato de píxel +0x48, caps +0x68 (y caps2 +0x6C).</summary>
    private static string SurfDesc(nint d)
    {
        uint flags = *(uint*)(d + 4), caps = *(uint*)(d + 0x68), caps2 = *(uint*)(d + 0x6C);
        string size = (flags & 0x6) != 0 ? $"{*(uint*)(d + 0xC)}x{*(uint*)(d + 8)}" : "?x?";
        string pf = (flags & 0x1000) != 0
            ? $" formato flags 0x{*(uint*)(d + 0x4C):X} bits {*(uint*)(d + 0x54)} R 0x{*(uint*)(d + 0x58):X} G 0x{*(uint*)(d + 0x5C):X} B 0x{*(uint*)(d + 0x60):X} A 0x{*(uint*)(d + 0x64):X}"
            : "";
        string back = (flags & 0x20) != 0 ? $" backbuffers {*(uint*)(d + 0x14)}" : "";
        return $"flags 0x{flags:X} {size} caps 0x{caps:X} caps2 0x{caps2:X}{back}{pf}";
    }

    private static string RsName(int s) => s switch
    {
        1 => "TEXTUREHANDLE", 2 => "ANTIALIAS", 4 => "TEXTUREPERSPECTIVE", 7 => "ZENABLE", 8 => "FILLMODE", 9 => "SHADEMODE",
        10 => "LINEPATTERN", 14 => "ZWRITEENABLE", 15 => "ALPHATESTENABLE", 16 => "LASTPIXEL", 19 => "SRCBLEND",
        20 => "DESTBLEND", 22 => "CULLMODE", 23 => "ZFUNC", 24 => "ALPHAREF", 25 => "ALPHAFUNC", 26 => "DITHERENABLE",
        27 => "ALPHABLENDENABLE", 28 => "FOGENABLE", 29 => "SPECULARENABLE", 30 => "ZVISIBLE", 33 => "STIPPLEDALPHA",
        34 => "FOGCOLOR", 35 => "FOGTABLEMODE", 36 => "FOGSTART", 37 => "FOGEND", 38 => "FOGDENSITY", 40 => "EDGEANTIALIAS",
        41 => "COLORKEYENABLE", 46 => "ZBIAS", 47 => "RANGEFOGENABLE", 52 => "STENCILENABLE", 53 => "STENCILFAIL",
        54 => "STENCILZFAIL", 55 => "STENCILPASS", 56 => "STENCILFUNC", 57 => "STENCILREF", 58 => "STENCILMASK",
        59 => "STENCILWRITEMASK", 60 => "TEXTUREFACTOR", 128 => "WRAP0", 136 => "CLIPPING", 137 => "LIGHTING",
        138 => "EXTENTS", 139 => "AMBIENT", 140 => "FOGVERTEXMODE", 141 => "COLORVERTEX", 142 => "LOCALVIEWER",
        143 => "NORMALIZENORMALS", 144 => "COLORKEYBLENDENABLE", 145 => "DIFFUSEMATERIALSOURCE",
        146 => "SPECULARMATERIALSOURCE", 147 => "AMBIENTMATERIALSOURCE", 148 => "EMISSIVEMATERIALSOURCE",
        151 => "VERTEXBLEND", 152 => "CLIPPLANEENABLE",
        _ => $"estado {s}",
    };

    private static string TssName(int t) => t switch
    {
        1 => "COLOROP", 2 => "COLORARG1", 3 => "COLORARG2", 4 => "ALPHAOP", 5 => "ALPHAARG1", 6 => "ALPHAARG2",
        7 => "BUMPENVMAT00", 11 => "TEXCOORDINDEX", 12 => "ADDRESS", 13 => "ADDRESSU", 14 => "ADDRESSV",
        15 => "BORDERCOLOR", 16 => "MAGFILTER", 17 => "MINFILTER", 18 => "MIPFILTER", 19 => "MIPMAPLODBIAS",
        20 => "MAXMIPLEVEL", 21 => "MAXANISOTROPY", 22 => "BUMPENVLSCALE", 23 => "BUMPENVLOFFSET",
        24 => "TEXTURETRANSFORMFLAGS",
        _ => $"tipo {t}",
    };

    private static void AddDetail(int kind, string name, string d)
    {
        string key = $"{(If)kind}::{name}";
        lock (_lock)
        {
            if (!_details.TryGetValue(key, out var set)) _details[key] = set = new HashSet<string>();
            if (set.Count < 300) set.Add(d);
        }
    }

    private static void AddCaller(int kind, string name, nint ret)
    {
        string key = $"{(If)kind}::{name}";
        lock (_lock)
        {
            if (!_callers.TryGetValue(key, out var set)) _callers[key] = set = new HashSet<nint>();
            if (set.Count < 64) set.Add(ret);
        }
    }

    private static void Dump()
    {
        try
        {
            var sb = new StringBuilder($"SacredFix: inventario de DirectDraw / Direct3D 7 ({DateTime.Now:HH:mm:ss}, {_clock.Elapsed.TotalMinutes:0.0} min de juego)\r\n");
            lock (_lock)
            {
                for (int k = 0; k < Names.Length; k++)
                {
                    sb.Append($"\r\n== {(If)k} ==\r\n");
                    for (int m = 0; m < Names[k].Length; m++)
                    {
                        long c = _counts[k][m];
                        if (c == 0) continue;
                        string key = $"{(If)k}::{Names[k][m]}";
                        sb.Append($"  {Names[k][m],-28} {c,12:N0}");
                        if (_callers.TryGetValue(key, out var cs))
                        {
                            sb.Append("   desde:");
                            foreach (var r in cs) sb.Append($" {r:X}");
                        }
                        sb.Append("\r\n");
                        if (_details.TryGetValue(key, out var ds))
                        {
                            var list = new List<string>(ds);
                            list.Sort(StringComparer.Ordinal);
                            foreach (var d in list) sb.Append($"      {d}\r\n");
                        }
                    }
                }
            }
            string path = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "SacredFix_api.txt");
            File.WriteAllText(path, sb.ToString());
        }
        catch (Exception ex) { Log.Error("ApiInventory: " + ex.Message); }
    }
}
