namespace SacredFix.Proxy.D9;

/// <summary>
/// Datos de la pantalla que no dependen del motor de dibujo: ventana del juego, tamaño de la imagen del juego (la que
/// pide con SetDisplayMode), tamaño del back buffer y del área de salida, y modo (exclusiva o ventana).
/// </summary>
internal static class Display
{
    public static nint Hwnd;
    public static int Width = 1024, Height = 768; // lo que dibuja el juego
    public static int BBW, BBH;                     // back buffer (en sin bordes y ventana: el monitor)
    public static int OutW, OutH;                   // área de la ventana a la que se estira la imagen
    public static bool Fullscreen = true;
    /// <summary>Rectángulo de la imagen dentro del área de la ventana (con [Display] KeepAspect, con bandas negras).
    /// ImgW = 0: la imagen llena toda el área.</summary>
    public static int ImgX, ImgY, ImgW, ImgH;

    /// <summary>La imagen del juego se escala al presentar (el back buffer no mide como ella).</summary>
    public static bool Scaling => BBW != Width || BBH != Height;
}
