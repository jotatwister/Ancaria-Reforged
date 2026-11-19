using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SacredFix.Core;
using SacredFix.Proxy.D9;

namespace SacredFix.Patches;

/// <summary>
/// Parche 3 (EXPERIMENTAL): resolución panorámica.
///
/// Paso A: mantener 768 de alto y ampliar el ancho (1366×768 en 16:9), de modo que la interfaz
/// conserve su tamaño y solo haya que recolocarla en horizontal.
///
/// Se aplica por etapas ([Display] Stage=N aplica de la 1 a la N) para poder probar cada una.
/// Cada sustitución localiza el valor original dentro de la instrucción y lo comprueba antes de escribir.
///
///   Etapa 1: modo de vídeo (DEVMODE de ChangeDisplaySettingsA + find_mode).
///   Etapa 2: dibujo 2D a pantalla completa: preparación de las rutinas de dibujo, copias GDI
///            y recorte en los bordes (1023/767).
///   Etapa 3: proyección del mundo isométrico: centro de pantalla (512/384, entero y decimal)
///            y límites de descarte (1024.0/768.0).
/// </summary>
internal sealed class Widescreen : IPatch
{
    string IPatch.Name => Name;
    public string IniKey => "Widescreen";
    public bool EnabledByDefault => false;

    public static int Width { get; private set; } = 1024;
    public static int Height { get; private set; } = 768;

    /// <summary>
    /// Qué valor nuevo corresponde a cada constante original.
    /// W/H/MaxX/MaxY/CX/CY: valores inmediatos enteros.
    /// FW/FH/FCX/FCY: la instrucción lee un decimal (float) de .rdata; se redirige a uno propio.
    /// </summary>
    private enum K { W, H, MaxX, MaxY, CX, CY, FW, FH, FCX, FCY, DOrthoNeg, DOrthoPos, FBitsHalfW, FBitsNegHalfW, WPlus200, FBitsOrthoPos, FBitsOrthoNeg, NegCX, PickLimit, FBitsW, FUnzX }

    // Decimales compartidos de .rdata (no se modifican: se redirigen las instrucciones)
    private const int RdataF1024 = 0x8906F4, RdataF768 = 0x892040, RdataF512 = 0x8906FC, RdataF384 = 0x8909F0;

    /// <summary>Bloque propio con [W, H, W/2, H/2] como float, en memoria que no se libera nunca.</summary>
    private static nint _floats;

    private static readonly (nint instr, int orig, K kind, string what)[] Stage1 =
    {
        (0x816081, 1024, K.W, "DEVMODE ancho (ChangeDisplaySettingsA)"),
        (0x81608F, 768,  K.H, "DEVMODE alto  (ChangeDisplaySettingsA)"),
        (0x816C6F, 768,  K.H, "find_mode alto"),
        (0x816C74, 1024, K.W, "find_mode ancho"),
    };

    private static readonly (nint instr, int orig, K kind, string what)[] Stage2 =
    {
        // OJO: 0x613670 (pushes 1024/768 en 0x6137B9…0x613838) NO se toca: convierte el formato de color
        // de una IMAGEN de 1024×768 ya cargada (0x660940: 565→555, 0x6609A0: 32→4444, 0x660A60, 0x660AD0).
        // Cambiarlo a 1366 lee y escribe fuera de la imagen (posible origen de la franja de basura).

        // 0x6468A0: BitBlt de GDI a pantalla completa (pantallas de carga, fundidos a negro)
        (0x64698D, 768, K.H, "BitBlt SRCCOPY alto"),    (0x646992, 1024, K.W, "BitBlt SRCCOPY ancho"),
        (0x6469AA, 768, K.H, "BitBlt BLACKNESS alto"),  (0x6469AF, 1024, K.W, "BitBlt BLACKNESS ancho"),

        // Recorte en los bordes de pantalla
        (0x6C78A5, 1023, K.MaxX, "recorte A x"), (0x6C78CC, 1023, K.MaxX, "recorte A x"), (0x6C790C, 1023, K.MaxX, "recorte A x"),
        (0x6C78BD, 767, K.MaxY, "recorte A y"),  (0x6C78D4, 767, K.MaxY, "recorte A y"),  (0x6C78EF, 767, K.MaxY, "recorte A y"),
        (0x6C7959, 1023, K.MaxX, "recorte B x"), (0x6C797E, 1023, K.MaxX, "recorte B x"), (0x6C79BA, 1023, K.MaxX, "recorte B x"),
        (0x6C7970, 767, K.MaxY, "recorte B y"),  (0x6C7986, 767, K.MaxY, "recorte B y"),  (0x6C799F, 767, K.MaxY, "recorte B y"),
        (0x6CA04E, 1023, K.MaxX, "recorte C x"), (0x6CA074, 1023, K.MaxX, "recorte C x"), (0x6CA0B2, 1023, K.MaxX, "recorte C x"),
        (0x6CA065, 767, K.MaxY, "recorte C y"),  (0x6CA07C, 767, K.MaxY, "recorte C y"),  (0x6CA096, 767, K.MaxY, "recorte C y"),
        (0x6C9D23, 1024, K.W, "límite C x"),     (0x6C9D33, 768, K.H, "límite C y"),      (0x6C9D86, 768, K.H, "límite C y"),
        (0x759CBC, 768, K.H, "rect. pantalla D alto"), (0x759CC1, 1024, K.W, "rect. pantalla D ancho"),
        (0x759FA1, 767, K.MaxY, "recorte D y"),  (0x759FA6, 1023, K.MaxX, "recorte D x"),
        (0x759FFA, 767, K.MaxY, "recorte D y"),  (0x759FFF, 1023, K.MaxX, "recorte D x"),
        (0x75A526, 767, K.MaxY, "recorte E y"),  (0x75A52B, 1023, K.MaxX, "recorte E x"),
        (0x75A5B4, 767, K.MaxY, "recorte E y"),  (0x75A5B9, 1023, K.MaxX, "recorte E x"),
        (0x75A60D, 767, K.MaxY, "recorte E y"),  (0x75A612, 1023, K.MaxX, "recorte E x"),
    };

    // Etapa 3: proyección del mundo isométrico (pantalla <-> mundo respecto al centro) y descarte
    // de lo que queda fuera de pantalla.
    private static readonly (nint instr, int orig, K kind, string what)[] Stage3 =
    {
        // 0x62E410
        (0x62E7F0, RdataF512, K.FCX, "proy. A centro x"), (0x62E819, RdataF512, K.FCX, "proy. A centro x"),
        (0x62E826, RdataF384, K.FCY, "proy. A centro y"), (0x62E832, RdataF384, K.FCY, "proy. A centro y"),
        (0x62E8BB, RdataF1024, K.FW, "proy. A límite x"), (0x62E8EE, RdataF768, K.FH, "proy. A límite y"),
        (0x62EE68, RdataF512, K.FCX, "proy. A2 centro x"), (0x62EE82, RdataF512, K.FCX, "proy. A2 centro x"),
        (0x62EE92, RdataF384, K.FCY, "proy. A2 centro y"), (0x62EE9E, RdataF384, K.FCY, "proy. A2 centro y"),
        (0x62F19D, RdataF1024, K.FW, "proy. A2 límite x"), (0x62F1D5, RdataF768, K.FH, "proy. A2 límite y"),
        (0x62F7F7, 512, K.CX, "proy. A3 centro x"), (0x62F7FD, 384, K.CY, "proy. A3 centro y"),
        (0x62FCF0, 512, K.CX, "proy. A4 centro x"), (0x62FD0D, 384, K.CY, "proy. A4 centro y"),
        // 0x62FF60
        (0x63041A, RdataF512, K.FCX, "proy. B centro x"), (0x630445, RdataF512, K.FCX, "proy. B centro x"),
        (0x630452, RdataF384, K.FCY, "proy. B centro y"), (0x63045E, RdataF384, K.FCY, "proy. B centro y"),
        (0x6304E6, RdataF1024, K.FW, "proy. B límite x"), (0x63051A, RdataF768, K.FH, "proy. B límite y"),
        (0x630A98, RdataF512, K.FCX, "proy. B2 centro x"), (0x630AB2, RdataF512, K.FCX, "proy. B2 centro x"),
        (0x630ABF, RdataF384, K.FCY, "proy. B2 centro y"), (0x630ACB, RdataF384, K.FCY, "proy. B2 centro y"),
        (0x630DCF, RdataF1024, K.FW, "proy. B2 límite x"), (0x630E07, RdataF768, K.FH, "proy. B2 límite y"),
        (0x63143A, 512, K.CX, "proy. B3 centro x"), (0x631440, 384, K.CY, "proy. B3 centro y"),
        (0x63195A, 512, K.CX, "proy. B4 centro x"), (0x631977, 384, K.CY, "proy. B4 centro y"),
        (0x631AE3, 512, K.CX, "proy. B5 centro x"), (0x631B00, 384, K.CY, "proy. B5 centro y"),
        // 0x62AE90
        (0x62B2CA, RdataF1024, K.FW, "proy. C límite x"),
        (0x62B315, RdataF512, K.FCX, "proy. C centro x"), (0x62B34E, RdataF384, K.FCY, "proy. C centro y"),
        (0x62B356, 512, K.CX, "proy. C centro x"), (0x62B379, 384, K.CY, "proy. C centro y"),
        (0x62CAD0, 512, K.CX, "proy. C2 centro x"), (0x62CAE0, 384, K.CY, "proy. C2 centro y"),
        // 0x62D700
        (0x62DAC2, RdataF1024, K.FW, "proy. D límite x"),
        (0x62DB13, RdataF512, K.FCX, "proy. D centro x"), (0x62DB4E, RdataF384, K.FCY, "proy. D centro y"),
        (0x62DB56, 512, K.CX, "proy. D centro x"), (0x62DB71, 384, K.CY, "proy. D centro y"),
        // 0x62DD00 (sin el 'and eax, 0x3FF' ni el '* 1/1024', que no parecen de pantalla)
        (0x62E20C, RdataF512, K.FCX, "proy. E centro x"), (0x62E220, RdataF384, K.FCY, "proy. E centro y"),
        (0x62E228, 512, K.CX, "proy. E centro x"), (0x62E239, 384, K.CY, "proy. E centro y"),
        // 0x631EB0
        (0x631EC2, RdataF512, K.FCX, "proy. F centro x"), (0x631EEC, RdataF512, K.FCX, "proy. F centro x"),
        (0x631EFA, RdataF384, K.FCY, "proy. F centro y"), (0x631F0A, RdataF384, K.FCY, "proy. F centro y"),
        // 0x41EC20
        (0x41EC27, 384, K.CY, "proy. G centro y"), (0x41EC37, 512, K.CX, "proy. G centro x"),
        (0x41EE5B, RdataF512, K.FCX, "proy. G centro x"), (0x41EE65, RdataF512, K.FCX, "proy. G centro x"),
        (0x41EE71, RdataF384, K.FCY, "proy. G centro y"), (0x41EE7B, RdataF384, K.FCY, "proy. G centro y"),
    };

    // Grupo 4 (candidata a motor de baldosas): 0x52AB70 recorre x hasta 1024 con centro (512, 384)
    private static readonly (nint instr, int orig, K kind, string what)[] Group4 =
    {
        (0x52BB2A, 1024, K.W, "baldosas? ancho (mov esi)"),
        (0x52BC47, 512, K.CX, "baldosas? centro x"), (0x52BC4C, 384, K.CY, "baldosas? centro y"),
        (0x52C850, 512, K.CX, "baldosas? centro x"), (0x52C855, 384, K.CY, "baldosas? centro y"),
        (0x52C953, 1024, K.W, "baldosas? ancho (cmp esi)"),
    };

    // Grupo 8: tamaño del área del mundo en la inicialización del motor de baldosas (0x624A00).
    // [obj+0x96AB0] = zoom * 1024.0 y [obj+0x96AB4] = zoom * 768.0; de ahí salen las filas
    // ([+0x96ACC] = alto/48 + ...) y columnas (ancho/192 + ...) que recorre 0x632140.
    // La traza D3D muestra las baldosas en x = -96..1120.
    private static readonly (nint instr, int orig, K kind, string what)[] Group8 =
    {
        (0x624A12, RdataF1024, K.FW, "mundo: ancho (fmul zoom*1024.0)"),
        (0x624A23, RdataF768, K.FH, "mundo: alto (fmul zoom*768.0)"),
    };

    // Grupo 9: descarte por polígono contra la pantalla [0,1024) x [0,768) al añadirlo al lote de D3D.
    // Es lo que corta el suelo en x≈1120 (baldosa de 96 px cuyo borde izquierdo es < 1024).
    // 0x629180 = añadir baldosa al lote (FVF 0x244), 0x628E60 = el otro lote (0x1C4), 0x61E540 = ídem.
    private static readonly (nint instr, int orig, K kind, string what)[] Group9 =
    {
        (0x6291B4, RdataF1024, K.FW, "lote 629180: descarte x"), (0x6291DC, RdataF768, K.FH, "lote 629180: descarte y"),
        (0x628E85, RdataF1024, K.FW, "lote 628E60: descarte x"), (0x628EAF, RdataF768, K.FH, "lote 628E60: descarte y"),
        (0x61E5B2, RdataF1024, K.FW, "lote 61E540: descarte x"), (0x61E5DC, RdataF768, K.FH, "lote 61E540: descarte y"),
    };

    // Grupo 10: cámara y proyección 3D de la partida (modelos de Granny, D3D con matrices propias).
    // a) La proyección es ortográfica de ±267 x ±200 unidades (4:3): ortho(-267s, 267s, -200s, 200s).
    //    Para W x H hace falta ±200·W/H (355,7 a 1366x768). Se redirigen los fmul qword [-267.0/+267.0]
    //    de las 4 funciones del motor del mundo (0x4B16FD/0x4B170E, fuera del motor, se dejan).
    // b) La cámara convierte su posición de píxeles a unidades del mundo con (ancho de la proyección) /
    //    (ancho de la superficie, [dispositivo 0xCDCA1C]+0x20). Sin a), 534/1366: la cámara queda a 0,75x
    //    (los modelos, ~16 000 px a la derecha). Con a) queda en 711/1366 = 534/1024, igual que a 1024,
    //    así que NO hay que tocar el ancho de la cámara. Forzar 1024 ahí (grupo 11) la deja a 1,34x
    //    (probado en partida). Se deja solo como referencia.
    // Requiere el grupo 8 (el suelo centrado en W/2, igual que el ratón en 0x629F4D).
    private const int RdataDNeg267 = 0x890E40, RdataDPos267 = 0x890E48;
    private static readonly (nint instr, int orig, K kind, string what)[] Group10 =
    {
        (0x624F21, RdataDPos267, K.DOrthoPos, "proyección 624F: +267"), (0x624F2F, RdataDNeg267, K.DOrthoNeg, "proyección 624F: -267"),
        (0x62829B, RdataDPos267, K.DOrthoPos, "proyección 6282: +267"), (0x6282A9, RdataDNeg267, K.DOrthoNeg, "proyección 6282: -267"),
        (0x628475, RdataDPos267, K.DOrthoPos, "proyección 6284: +267"), (0x628483, RdataDNeg267, K.DOrthoNeg, "proyección 6284: -267"),
        (0x628C20, RdataDPos267, K.DOrthoPos, "proyección 628C: +267"), (0x628C31, RdataDNeg267, K.DOrthoNeg, "proyección 628C: -267"),
    };
    /// <summary>mov cx, word ptr [eax+0x20] (ancho de la superficie) -> mov cx, 0x400</summary>
    private static readonly nint[] CamWidthReads = { 0x6285A2, 0x6287D9 };
    private static ReadOnlySpan<byte> CamWidthOrig => new byte[] { 0x66, 0x8B, 0x48, 0x20 };
    private static ReadOnlySpan<byte> CamWidthNew => new byte[] { 0x66, 0xB9, 0x00, 0x04 };

    // Grupo 12: personajes, cofres y PNJ (modelos de Granny, 0x40EA40, 0x40F4xx, 0x40FDxx). Cada uno
    // monta su propia cámara en píxeles de una pantalla de 1024x768: ortho(±512, ±384, -400, 2000) con
    // «push 512.0 / push -512.0», y coloca el modelo en x_cámara = 512 - x_pantalla («mov ecx, 0x200»),
    // y_cámara = y_pantalla - 384. Para W: ortho ±W/2 y centro W/2. (0x6F2215 tiene la misma proyección
    // pero no ese centro: probablemente la interfaz; se deja.)
    private const int F512Bits = 0x44000000, FNeg512Bits = unchecked((int)0xC4000000);
    private static readonly (nint instr, int orig, K kind, string what)[] Group12 =
    {
        (0x40EB93, F512Bits, K.FBitsHalfW, "modelos 40EA40: ortho +512"), (0x40EB98, FNeg512Bits, K.FBitsNegHalfW, "modelos 40EA40: ortho -512"),
        (0x40EBC1, 512, K.CX, "modelos 40EA40: centro x"),
        (0x40F599, F512Bits, K.FBitsHalfW, "modelos 40F4xx: ortho +512"), (0x40F59E, FNeg512Bits, K.FBitsNegHalfW, "modelos 40F4xx: ortho -512"),
        (0x40F5CE, 512, K.CX, "modelos 40F4xx: centro x"),
        (0x40FEC9, F512Bits, K.FBitsHalfW, "modelos 40FDxx: ortho +512"), (0x40FECE, FNeg512Bits, K.FBitsNegHalfW, "modelos 40FDxx: ortho -512"),
        (0x40FEFE, 512, K.CX, "modelos 40FDxx: centro x"),
    };

    // Grupo 13: descarte de objetos del mundo por posición en pantalla (bucles de 0x62FF60 y 0x62E410).
    // x = (objeto - cámara) + 512 («add edi, 0x200» en 0x63143A); si x no está en (-200, 1224) o y en
    // (-200, 968), el objeto se actualiza pero NO se dibuja (personajes invisibles con colisiones
    // correctas). Para W: centro W/2 y límite W+200. 0x63195A y 0x631AE3 colocan la sombra y otra
    // pasada con el mismo centro.
    private static readonly (nint instr, int orig, K kind, string what)[] Group13 =
    {
        (0x63143A, 512, K.CX, "objetos: centro x (descarte)"),
        (0x63145B, 0x4C8, K.WPlus200, "objetos: límite x 1024+200"),
        (0x63195A, 512, K.CX, "objetos: centro x (2.ª pasada)"),
        (0x631AE3, 512, K.CX, "objetos: centro x (sombra)"),
        // Segundo bucle de objetos, 0x62E410 (se llama antes, desde 0x63288C): aquí van los personajes.
        // Encontrado con el pseudocódigo de Ghidra de 0x62E410.
        (0x62F7F7, 512, K.CX, "objetos 62E410: centro x (descarte)"),
        (0x62F818, 0x4C8, K.WPlus200, "objetos 62E410: límite x 1024+200"),
        (0x62FCF0, 512, K.CX, "objetos 62E410: centro x (sombra)"),
    };

    // Va con la mitad 14 del grupo 10. 0x623B60 pasa un punto 3D a píxeles con
    // x = vista_x · 1024 · PROJ[0][0] / 2 (y con 768). Con la proyección ancha (2/711) la escala baja de 1,92
    // a 1,44 px/unidad. En 0x44CF20 (caja en pantalla de cada objeto 3D) eso coloca a los personajes al 75 %
    // de su posición, a miles de píxeles de la cámara, y el bucle 0x62FF60 los salta: personajes invisibles.
    // No se puede cambiar la función entera: la cámara y el suelo también la usan y ya cuadran (probado: se
    // descoloca todo, panoramica_42). Se copia la función (126 bytes, sin saltos ni llamadas) con W/H y solo
    // las dos llamadas de 0x44CF20 apuntan a la copia.
    private const nint ProjectToPixels = 0x623B60;
    private static readonly nint[] ProjectToPixelsCallsForVisibility = { 0x44D30C, 0x44D33E };
    private const int ProjectToPixelsSize = 126;

    private static unsafe void InstallVisibilityScale()
    {
        var code = new ReadOnlySpan<byte>((void*)ProjectToPixels, ProjectToPixelsSize).ToArray();
        if (code[0] != 0x83 || code[^1] != 0xC3)
            throw new InvalidOperationException($"0x623B60 no es lo esperado: {Mem.Hex(ProjectToPixels, 8)}");
        int Replace(int from, int to)
        {
            int n = 0;
            for (int k = 0; k + 4 <= code.Length; k++)
                if (BitConverter.ToInt32(code, k) == from) { BitConverter.TryWriteBytes(code.AsSpan(k), to); n++; }
            return n;
        }
        if (Replace(RdataF1024, (int)_floats) != 1 || Replace(RdataF768, (int)(_floats + 4)) != 1)
            throw new InvalidOperationException("0x623B60: no se encuentran 1024.0/768.0 una sola vez");
        nint copy = Mem.AllocCode(code);
        foreach (var site in ProjectToPixelsCallsForVisibility)
        {
            nint old = Mem.RedirectCall(site, copy);
            if (old != ProjectToPixels)
            {
                Mem.RedirectCall(site, old);
                throw new InvalidOperationException($"0x{site:X8} no llama a 0x623B60 (llama a 0x{old:X8})");
            }
        }
        Log.Info($"[{Name}]   0x44CF20 (visibilidad de objetos 3D): punto 3D a píxeles con {Width}x{Height} (copia en 0x{copy:X8})");
    }

    // Grupo 16: las matrices ortográficas con ±267 fijos («push 267.0 / push -267.0» como inmediatos):
    // las 4 funciones del grupo 10 construyen una segunda matriz tras fijar la de D3D, y 0x60C14D/0x60D950
    // fijan otra proyección de D3D (0x60C187) que el grupo 10 no tocaba. Para W: ±200·W/H.
    private const int F267Bits = 0x43858000, FNeg267Bits = unchecked((int)0xC3858000);
    private static readonly (nint instr, int orig, K kind, string what)[] Group16 =
    {
        (0x60C161, F267Bits, K.FBitsOrthoPos, "ortho fija 60C1: +267"), (0x60C166, FNeg267Bits, K.FBitsOrthoNeg, "ortho fija 60C1: -267"),
        (0x60D9BE, F267Bits, K.FBitsOrthoPos, "ortho fija 60D9: +267"), (0x60D9C3, FNeg267Bits, K.FBitsOrthoNeg, "ortho fija 60D9: -267"),
        (0x624F63, F267Bits, K.FBitsOrthoPos, "ortho fija 624F: +267"), (0x624F68, FNeg267Bits, K.FBitsOrthoNeg, "ortho fija 624F: -267"),
        (0x6282DD, F267Bits, K.FBitsOrthoPos, "ortho fija 6282: +267"), (0x6282E2, FNeg267Bits, K.FBitsOrthoNeg, "ortho fija 6282: -267"),
        (0x6284B7, F267Bits, K.FBitsOrthoPos, "ortho fija 6284: +267"), (0x6284BC, FNeg267Bits, K.FBitsOrthoNeg, "ortho fija 6284: -267"),
        (0x628C65, F267Bits, K.FBitsOrthoPos, "ortho fija 628C: +267"), (0x628C6A, FNeg267Bits, K.FBitsOrthoNeg, "ortho fija 628C: -267"),
    };

    // Grupo 17: centro de la cámara 3D del mundo. 0x6284E0 (thiscall(this, int), ret 4) calcula el ojo y el
    // objetivo de la cámara a partir de la posición en píxeles [this+0x14] (x) y el zoom [this+0xB60]. Esa
    // posición corresponde al centro de una pantalla de 1024; el centro de la de W está (W/2 - 512)·zoom px más
    // a la derecha. Sin esto, los objetos 3D salen ~171 px a la derecha del suelo (medido; el experimento
    // CamShift=171 lo confirmó). Se envuelven sus 3 llamadas: antes, [this+0x14] += (W/2 - 512)·zoom; después,
    // se restaura. Solo cambia la cámara (VIEW), que también usan el ratón y la visibilidad.
    private static readonly nint[] CameraCalls = { 0x60C1BA, 0x626395, 0x62AC25 };
    private const nint CameraFunc = 0x6284E0;
    private static unsafe delegate* unmanaged[Thiscall]<nint, int, void> _cameraOrig;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void CameraHook(nint self, int arg)
    {
        int* x = (int*)(self + 0x14);
        float zoom = *(float*)(self + 0xB60);
        int add = (int)Math.Round((Width / 2.0 - 512.0) * zoom);
        *x += add;
        _cameraOrig(self, arg);
        *x -= add;
    }

    private static unsafe void InstallCameraCenter()
    {
        delegate* unmanaged[Stdcall]<nint, int, void> h = &CameraHook;
        nint thunk = Thunks.ThiscallToStdcall((nint)h);
        foreach (var site in CameraCalls)
        {
            nint old = Mem.RedirectCall(site, thunk);
            if (old != CameraFunc) { Mem.RedirectCall(site, old); throw new InvalidOperationException($"0x{site:X8} no llama a 0x6284E0"); }
        }
        _cameraOrig = (delegate* unmanaged[Thiscall]<nint, int, void>)CameraFunc;
        Log.Info($"[{Name}]   cámara 3D: centro desplazado (W/2 - 512)·zoom = {Width / 2 - 512}·zoom px en sus 3 llamadas");
    }

    // Grupo 18: sitios que faltaban, tomados de kirides/sacred-bild (GPL-3, src/game/resolution_sites.inc;
    // https://github.com/kirides/sacred-bild). Solo se usan los datos (direcciones de operando y valores de
    // sustitución), no su código. Sus notas: «los centros float e int deben cambiar juntos: si solo se parchea
    // uno, todo se desplaza W/2 - 512» y «faltar la forma -0x200 desplazaba todos los edificios (512 - W/2)/zoom».
    // Las direcciones son las del OPERANDO (ReplaceImmInInstruction encuentra el valor en la posición 0).
    // Se omiten los de alto (384/768/968/818/888), que no cambian con H = 768.
    private const int RdataUnzX = 0x182ED70;
    private static readonly (nint instr, int orig, K kind, string what)[] Group18 =
    {
        (0x41B1A6, 0x200, K.CX, "sacred-bild: 41B170 add esi, 0x200"),
        (0x41B27E, 0x200, K.CX, "sacred-bild: 41B200 add edx, 0x200"),
        (0x41B417, 0x200, K.CX, "sacred-bild: 41B3C0 add eax, 0x200"),
        (0x41B4F7, 0x200, K.CX, "sacred-bild: 41B4A0 add eax, 0x200"),
        (0x41E691, 0x200, K.CX, "sacred-bild: 41E590 add eax, 0x200"),
        (0x62B94D, unchecked((int)0xFFFFFE00), K.NegCX, "sacred-bild: fila de baldosas add edx, -0x200"),
        (0x4FB8B2, unchecked((int)0xFFFFFE00), K.NegCX, "sacred-bild: 4FB7E0 lea -0x200 (andar manteniendo)"),
        (0x626CA4, 1000, K.PickLimit, "sacred-bild: límite de selección en el mundo 1000"),
        (0x60E19A, 0x44800000, K.FBitsW, "sacred-bild: overlay 1024.0"),
        (0x60E22D, 0x44800000, K.FBitsW, "sacred-bild: overlay 1024.0 (2)"),
        (0x60E8A6, 0x400, K.W, "sacred-bild: cEngine_renderThreadRun push 0x400"),
        (0x62721E, 0x400, K.W, "sacred-bild: etiquetas push 0x400"),
        (0x6287FE, RdataUnzX, K.FUnzX, "sacred-bild: cámara, escala sin zoom ·1024/W"),
    };

    // Grupo 5 (candidata): 0x6CCBD0, push 1024/768 y centro (512, 384)
    private static readonly (nint instr, int orig, K kind, string what)[] Group5 =
    {
        (0x6CE2F1, 768, K.H, "6CCBD0 alto"), (0x6CE2F6, 1024, K.W, "6CCBD0 ancho"),
        (0x6CF002, 512, K.CX, "6CCBD0 centro x"), (0x6CF022, 384, K.CY, "6CCBD0 centro y"),
        (0x6CF030, 512, K.CX, "6CCBD0 centro x"), (0x6CF04F, 384, K.CY, "6CCBD0 centro y"),
        (0x6CF6FA, 512, K.CX, "6CCBD0 centro x"), (0x6CF710, 384, K.CY, "6CCBD0 centro y"),
        (0x6CF72C, 512, K.CX, "6CCBD0 centro x"),
        (0x6CF864, 512, K.CX, "6CCBD0 centro x"), (0x6CF891, 384, K.CY, "6CCBD0 centro y"),
    };

    private static int ValueFor(K k) => k switch
    {
        K.W => Width,
        K.H => Height,
        K.MaxX => Width - 1,
        K.MaxY => Height - 1,
        K.CX => Width / 2,
        K.CY => Height / 2,
        K.FW => (int)_floats,
        K.FH => (int)(_floats + 4),
        K.FCX => (int)(_floats + 8),
        K.FCY => (int)(_floats + 12),
        K.DOrthoNeg => (int)(_floats + 16),
        K.DOrthoPos => (int)(_floats + 24),
        K.WPlus200 => Width + 200,
        K.NegCX => -(Width / 2),
        K.PickLimit => 8000,
        K.FBitsW => BitConverter.SingleToInt32Bits((float)Width),
        K.FUnzX => (int)(_floats + 32),
        K.FBitsOrthoPos => BitConverter.SingleToInt32Bits((float)(OrthoHalfWidth() + Shift3DUnits())),
        K.FBitsOrthoNeg => BitConverter.SingleToInt32Bits((float)(-OrthoHalfWidth() + Shift3DUnits())),
        K.FBitsHalfW => BitConverter.SingleToInt32Bits(Width / 2f),
        K.FBitsNegHalfW => BitConverter.SingleToInt32Bits(-Width / 2f),
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    private static unsafe void CreateFloats()
    {
        Span<byte> block = stackalloc byte[36];
        BitConverter.TryWriteBytes(block[32..], (float)(2.0 / 534.0 * 1024.0 / Width)); // MemUnzX (sacred-bild)
        double ortho = OrthoHalfWidth();       // semiancho de la proyección ortográfica (267 a 1024)
        double shift = Shift3DUnits();          // desplazamiento horizontal de la vista 3D (ver Shift3D)
        BitConverter.TryWriteBytes(block[16..], -ortho + shift);
        BitConverter.TryWriteBytes(block[24..], ortho + shift);
        BitConverter.TryWriteBytes(block[0..], (float)Width);
        BitConverter.TryWriteBytes(block[4..], (float)Height);
        BitConverter.TryWriteBytes(block[8..], Width / 2f);
        BitConverter.TryWriteBytes(block[12..], Height / 2f);
        _floats = Mem.AllocCode(block); // memoria propia; el juego solo la lee
        Log.Info($"[{Name}] decimales propios en 0x{_floats:X8}: {Width}.0, {Height}.0, {Width / 2f}, {Height / 2f}; ortho ±{OrthoHalfWidth():0.###}");
    }

    private static string Name => "Panorámico (experimental)";

    /// <summary>
    /// [Display] Shift3D (píxeles): desplaza la vista de la proyección ortográfica 3D (bordes izquierdo y
    /// derecho a la vez). Positivo = los objetos 3D se dibujan más a la izquierda. Diagnóstico del desfase
    /// constante de ~171 px = (W - 1024) / 2 entre lo 3D y el suelo. En unidades: píxeles · (2·200/H)
    /// (0,52 a 768 de alto); los bordes se multiplican por el zoom, así que el desfase en píxeles no cambia.
    /// </summary>
    private static int Shift3D;
    private static double Shift3DUnits() => Shift3D * (2.0 * OrthoHalfWidth() / Width);

    /// <summary>
    /// Semiancho de la proyección ortográfica 3D para el ancho W. El juego usa ±267 × ±200, que NO es 4:3
    /// exacto (267/200 = 1,335): en horizontal trabaja con 267/512 unidades por píxel. Hay que conservar esa
    /// escala: 267 · W / 1024 (356,18 a 1366), no 200 · W / H (355,73). La diferencia es de un 0,12 %, pero
    /// las coordenadas del mundo son de ~25 000 unidades: ~32 unidades = ~61 px de desfase entre lo 3D y el
    /// suelo, que además cambia con la posición y el zoom (medido en panoramica_53/54).
    /// </summary>
    private static double OrthoHalfWidth() => 267.0 * Width / 1024.0;

    public void Apply(Config cfg)
    {
        Width = Resolution.Width(cfg);
        Height = Resolution.Height(cfg);
        Shift3D = cfg.GetInt("Display", "Shift3D", 0);
        if (Shift3D != 0) Log.Info($"[{Name}] Shift3D = {Shift3D} px");
        // Groups=1,2 : grupos de sustituciones a aplicar (desarrollo)
        var groups = new HashSet<int>();
        foreach (var g in cfg.GetString("Display", "Groups", "1").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(g.Trim(), out int n)) groups.Add(n);
        Log.Info($"[{Name}] objetivo {Width}x{Height}, grupos {string.Join(",", groups)}");

        var all = new List<(nint, int, K, string)>();
        if (groups.Contains(1)) all.AddRange(Stage1);
        // Con el lienzo de la interfaz (UiCenter), los recortes de la interfaz (1023/767) y la copia de la imagen de
        // carga (SRCCOPY) deben quedarse en 1024: la interfaz sigue siendo de 1024 de ancho (sacred-bild: «correctos
        // dentro del lienzo, no necesitan parches»). Solo se ensancha el fondo negro (BLACKNESS).
        bool uiCanvas = cfg.GetBool("Display", "UiCenter", false);
        if (groups.Contains(2))
            foreach (var e in Stage2)
                if (!uiCanvas || !(e.what.StartsWith("recorte") || e.what.StartsWith("BitBlt SRCCOPY"))) all.Add(e);
        if (groups.Contains(3)) all.AddRange(Stage3);
        // 6 = solo los límites de descarte de la etapa 3 (fcomp 1024.0 / 768.0)
        if (groups.Contains(6) && !groups.Contains(3))
            foreach (var e in Stage3) if (e.kind is K.FW or K.FH) all.Add(e);
        // 7 = solo los centros de la etapa 3 (512/384, enteros y decimales)
        // 7 = 71 + 72 + 73 (bisección, 2026-11-07): 71 = funciones del suelo y vértices (0x62AE90…0x62E40F),
        // 72 = los dos bucles de objetos (0x62E410…0x631E9F), 73 = el resto (0x631EB0, 0x41EC20).
        if (!groups.Contains(3))
            foreach (var e in Stage3)
            {
                if (e.kind is not (K.FCX or K.FCY or K.CX or K.CY)) continue;
                int sub = e.instr >= 0x62AE90 && e.instr < 0x62E410 ? 71 : e.instr >= 0x62E410 && e.instr < 0x631EA0 ? 72 : 73;
                if (groups.Contains(7) || groups.Contains(sub)) all.Add(e);
            }
        if (groups.Contains(4)) all.AddRange(Group4);
        if (groups.Contains(5)) all.AddRange(Group5);
        if (groups.Contains(8)) all.AddRange(Group8);
        if (groups.Contains(9)) all.AddRange(Group9);
        // 10 = 14 + 15. 14: proyección del dibujado del mundo (0x624E30) y de la cámara (0x628130);
        // 15: las del cambio de zoom (0x628310) y la carga (0x6288F0). Separados para la bisección.
        // 14 = 141 (proyección del mundo, 0x624F21/2F) + 142 (cámara, 0x62829B/A9) + 143 (visibilidad, 0x44CF20)
        bool g14 = groups.Contains(10) || groups.Contains(14), g15 = groups.Contains(10) || groups.Contains(15);
        bool g141 = g14 || groups.Contains(141), g142 = g14 || groups.Contains(142), g143 = g14 || groups.Contains(143);
        if (!g14 && (g141 || g142)) g14 = true; // (el grupo 8 y el bucle de abajo)
        // Con nuestra capa D3D la proyección del mundo se ensancha al dibujar (Device7.WidenProjection) y el juego sigue
        // leyendo y calculando con la de 4:3: no se tocan sus proyecciones (grupos 10 y 16) ni hace falta la copia de
        // visibilidad (0x44CF20 ya sale bien con la original). Igual que sacred-bild, que no ensancha la del juego.
        // ([Debug] LayerProjection=0 vuelve al sistema anterior: proyección ensanchada en el código del juego, grupos 10 y 16;
        // con él no se puede montar a caballo. Sin nuestra capa, Renderer=ddraw, también se usa el anterior.)
        bool layerProj = Layer.Enabled && cfg.GetBool("Debug", "LayerProjection", true)
                         && (g14 || g15 || groups.Contains(16) || groups.Contains(161) || groups.Contains(162));
        if (layerProj)
        {
            Device7.WidenProjection = true;
            // El ojo de la cámara 3D (0x6284E0): X = 2·px / (ancho · PROJ[0][0] · zoom). Con la proyección de 4:3 que lee
            // el juego, el ancho tiene que ser el de 1024 (como en el original; sacred-bild igual): 0x6285A2
            // «mov cx, [eax+0x20]» -> «mov cx, 0x400». (La otra lectura, 0x6287D9, ya la compensa 0x6287FE del grupo 18.)
            if (!Mem.Matches(CamWidthReads[0], CamWidthOrig))
                throw new InvalidOperationException($"cámara: bytes inesperados en 0x{CamWidthReads[0]:X8}: {Mem.Hex(CamWidthReads[0], 4)}");
            Mem.WriteBytes(CamWidthReads[0], CamWidthNew);
            if (!groups.Contains(8)) { groups.Add(8); all.AddRange(Group8); }
            Log.Info($"[{Name}] proyección del mundo ensanchada en la capa D3D (el juego lee la de 4:3)");
        }
        if ((g14 || g15) && !layerProj)
        {
            if (!groups.Contains(8)) { groups.Add(8); all.AddRange(Group8); Log.Info($"[{Name}] la proyección ancha necesita el grupo 8: se añade"); }
            foreach (var e in Group10)
            {
                bool first = e.instr < 0x628300; // 0x624F21/2F y 0x62829B/A9
                if (first ? (e.instr < 0x625000 ? g141 : g142) : g15) all.Add(e);
            }
        }
        if (groups.Contains(12)) all.AddRange(Group12);
        if (groups.Contains(13)) all.AddRange(Group13);
        if (groups.Contains(18)) all.AddRange(Group18);
        // 16 = 161 (proyección de D3D de 0x60C14D/0x60D950) + 162 (matrices secundarias de las 4 del grupo 10)
        if (!layerProj)
            foreach (var e in Group16)
                if (groups.Contains(16) || groups.Contains(e.instr < 0x610000 ? 161 : 162)) all.Add(e);
        if (groups.Contains(11))
        {
            foreach (var at in CamWidthReads)
                if (!Mem.Matches(at, CamWidthOrig))
                    throw new InvalidOperationException($"cámara: bytes inesperados en 0x{at:X8}: {Mem.Hex(at, 4)}");
        }
        // Varios grupos pueden incluir la misma instrucción (p. ej. 7/72 y 13): se aplica una sola vez.
        // Sin esto, la segunda vez ya no encuentra el valor original y el parche entero falla a medias.
        {
            var seen = new HashSet<nint>();
            int before = all.Count;
            all.RemoveAll(e => !seen.Add(e.Item1));
            if (all.Count != before) Log.Info($"[{Name}] {before - all.Count} instrucciones repetidas entre grupos (se aplican una vez)");
        }
        CreateFloats();

        // Primero comprobar TODO; si algo no cuadra no se toca nada (evita dejar el juego a medias)
        Span<byte> pat = stackalloc byte[4];
        foreach (var (instr, orig, _, what) in all)
        {
            BitConverter.TryWriteBytes(pat, orig);
            if (!ContainsOnce(instr, pat))
                throw new InvalidOperationException($"{what}: no se encuentra {orig} en la instrucción 0x{instr:X8} ({Mem.Hex(instr, 11)})");
        }

        int changed = 0;
        foreach (var (instr, orig, kind, what) in all)
        {
            int value = ValueFor(kind);
            // Los decimales de alto (768.0, 384.0) también se redirigen aunque el valor sea igual,
            // para que cambiar Height en el .ini funcione sin tocar código.
            if (value == orig) continue;
            Mem.ReplaceImmInInstruction(instr, orig, value);
            Log.Info($"[{Name}]   0x{instr:X8} {what}: {orig} -> {value}");
            changed++;
        }
        // (prueba descartada: leer la proyección como 4:3 corta el bucle del caballo, pero descoloca la cámara y oculta los personajes)
        if (!layerProj && (groups.Contains(10) || groups.Contains(14) || groups.Contains(143))) { InstallVisibilityScale(); changed += 2; }
        // prueba (caballo): sin el seguimiento suave de la cámara de 0x6103F0 (el «jg» de 0x610473 pasa a «jmp»)
        if (cfg.GetBool("Debug", "NoCamFollow", false) && Mem.Matches(0x610473, new byte[] { 0x7F, 0x2D }))
        {
            Mem.WriteBytes(0x610473, new byte[] { 0xEB, 0x2D });
            Log.Info($"[{Name}] prueba: seguimiento suave de la cámara (0x610473) desactivado");
        }
        if (groups.Contains(17)) { InstallCameraCenter(); changed += 3; }
        if (groups.Contains(11))
            foreach (var at in CamWidthReads)
            {
                Mem.WriteBytes(at, CamWidthNew);
                Log.Info($"[{Name}]   0x{at:X8} cámara: ancho de la superficie -> 1024 fijo");
                changed++;
            }
        Log.Info($"[{Name}] {changed} valores cambiados ({all.Count} comprobados)");
    }

    private static unsafe bool ContainsOnce(nint instr, ReadOnlySpan<byte> pat)
    {
        var code = new ReadOnlySpan<byte>((void*)instr, 11);
        int at = code.IndexOf(pat);
        return at >= 0 && code[(at + 1)..].IndexOf(pat) < 0;
    }
}
