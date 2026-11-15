using System;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Motor de dibujo de la capa: lo que necesita la parte común (DirectDraw7, Surface7, Device7, Overlay), que entiende a
/// DirectX 7, para dibujar con una API concreta. Todo se expresa con la semántica de DirectX 7: números de estado de
/// render y de etapa de textura de D3D7, matrices D3D7 (WORLD = 1), FVF de D3D7, formatos D3DFORMAT. Cada motor
/// traduce a lo suyo. Hoy solo hay uno: Backend9 (Direct3D 9).
/// </summary>
internal abstract unsafe class Backend
{
    public static Backend Current = null!; // lo elige Layer.Init según [Display] Renderer

    /// <summary>Tras perder y recuperar el dispositivo: la parte común vuelve a poner sus estados.</summary>
    public static event Action? AfterReset;
    protected static void RaiseAfterReset() => AfterReset?.Invoke();

    // ---- dispositivo ----
    public abstract bool Create();               // con los datos de Display; idempotente
    public abstract bool Ready { get; }
    public abstract void Evict();

    // ---- superficies (Surface7 guarda los recursos del motor en Tex / Surf) ----
    public abstract bool CreateTexture(Surface7 s);   // textura gestionada de un nivel con el formato s.Fmt
    public abstract bool CreateSysMem(Surface7 s);    // superficie fuera de pantalla en memoria del sistema
    public abstract void Release(Surface7 s);
    public abstract bool Lock(Surface7 s, int* rect, uint flags, out byte* bits, out int pitch);
    public abstract void Unlock(Surface7 s);
    public abstract int GetDC(Surface7 s, nint* dc);
    public abstract int ReleaseDC(Surface7 s, nint dc);
    /// <summary>Relleno de color de la pantalla del juego (primaria / back buffer).</summary>
    public abstract int FillScreen(int* rect, uint color);
    /// <summary>Presenta lo dibujado. gameFrame = fotograma normal del juego (Flip); false = tras GDI o Lock.</summary>
    public abstract void Present(bool gameFrame);

    // ---- dispositivo 3D (semántica D3D7) ----
    public abstract bool BeginFrame();           // recupera el dispositivo si hace falta
    public abstract int BeginScene();
    public abstract int EndScene();
    public abstract int Clear(uint count, nint rects, uint flags, uint color, float z, uint stencil);
    public abstract uint DefaultRenderState(int s);
    public abstract uint DefaultTextureStageState(uint stage, int t);
    public abstract int SetRenderState(int s, uint v);
    public abstract int SetTextureStageState(uint stage, int t, uint v);
    public abstract int SetTransform(int s, float* m);
    public abstract int GetTransform(int s, float* m);
    public abstract int MultiplyTransform(int s, float* m);
    public abstract int SetViewport(nint vp);
    public abstract int GetViewport(nint vp);
    public abstract int SetMaterial(nint m);
    public abstract int GetMaterial(nint m);
    public abstract int SetLight(uint i, nint l);
    public abstract int GetLight(uint i, nint l);
    public abstract int LightEnable(uint i, int on);
    public abstract int GetLightEnable(uint i, int* on);
    public abstract int SetTexture(uint stage, Surface7? s);
    public abstract int Draw(int type, uint fvf, nint verts, uint vcount, int stride);
    public abstract int DrawIndexed(int type, uint fvf, nint verts, uint vcount, ushort* idx, uint icount, int stride);

    // ---- superposición (contador de FPS, segundos de recarga) ----
    public abstract nint CreateRgbaTexture(int w, int h, uint* pixels);
    /// <summary>Cuadrados texturizados sobre la imagen final, en píxeles de pantalla: 4 vértices por cuadrado (tira),
    /// 7 floats cada uno (x, y, z, rhw, color, u, v).</summary>
    public abstract void DrawOverlay(nint tex, float* verts, int quads);
    /// <summary>Lo mismo, pero dentro de la imagen del juego (antes de que el juego pinte su cursor), en píxeles de
    /// SceneSize. false si el motor no sabe hacerlo (entonces se dibuja al presentar).</summary>
    public virtual bool DrawOverlayInScene(nint tex, float* verts, int quads) => false;
    public virtual (int w, int h) SceneSize => (0, 0);
}
