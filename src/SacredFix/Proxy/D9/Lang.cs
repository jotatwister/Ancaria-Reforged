using System;
using System.Collections.Generic;
using SacredFix.Core;

namespace SacredFix.Proxy.D9;

/// <summary>
/// Idioma de los textos propios ([Menu] Language = es / en; menú F10 «Idioma / Language»). Los textos se escriben en
/// castellano y aquí está su versión en inglés. La traducción se hace al dibujar (MenuFont.Text / Width), así que el
/// cambio es inmediato. Los textos del propio juego (misiones, objetos) no pasan por aquí: no están en la tabla.
/// Etiquetas con número («Arma 3») y con « *» se traducen por prefijo.
/// </summary>
internal static class Lang
{
    public static volatile bool En;

    public static void Init(Config cfg) => En = cfg.GetString("Menu", "Language", "es").Trim().ToLowerInvariant().StartsWith("en");

    public static string T(string s)
    {
        if (!En || string.IsNullOrEmpty(s)) return s;
        if (Es2En.TryGetValue(s, out var en)) return en;
        if (s.EndsWith(" *")) return T(s[..^2]) + " *";
        foreach (var (p, q) in Prefixes)
            if (s.StartsWith(p, StringComparison.Ordinal)) return q + s[p.Length..];
        return s;
    }

    private static readonly (string, string)[] Prefixes =
    {
        ("Arma ", "Weapon "), ("Arte o combo ", "Art or combo "), ("Tecla del juego ", "Game key "),
        ("Ahora tu tecla para ", "Now your key for "),
    };

    private static readonly Dictionary<string, string> Es2En = new()
    {
        // secciones y títulos
        ["Imagen"] = "Display", ["Juego"] = "Game", ["Teclas"] = "Keys", ["Opciones"] = "Options",
        ["SacredFix · Opciones"] = "SacredFix · Options",
        ["Color y nitidez"] = "Colour and sharpness", ["Calidad"] = "Quality", ["Ventana y motor"] = "Window and renderer",
        ["Mapa y estado"] = "Map and status", ["Nombres de objetos (Alt)"] = "Item names (Alt)", ["Combate"] = "Combat",
        ["Rendimiento"] = "Performance", ["Pruebas"] = "Testing", ["Contador de FPS"] = "FPS counter",
        ["Ventanas y partida"] = "Windows and game", ["Pociones"] = "Potions", ["Armas"] = "Weapons",
        ["Artes y combos"] = "Arts and combos", ["Este menú"] = "This menu", ["Otras teclas del juego"] = "Other game keys",
        ["General"] = "General",
        ["Idioma de los textos de SacredFix. Los textos del juego (misiones, objetos) siguen el idioma del juego."] =
            "Language of SacredFix's own texts. The game's texts (quests, items) follow the game's language.",

        // Imagen
        ["Enfoque"] = "Sharpen",
        ["Realza los bordes de la imagen al escalarla a tu pantalla. 0 lo quita; valores altos pueden marcar demasiado los contornos."] =
            "Sharpens the image when it is scaled to your screen. 0 turns it off; high values can over-outline edges.",
        ["Viveza del color"] = "Saturation",
        ["Intensidad de los colores. 1 es el original; menos apaga los colores y más los aviva."] =
            "Colour intensity. 1 is the original; lower mutes the colours and higher makes them vivid.",
        ["Contraste"] = "Contrast",
        ["Diferencia entre zonas claras y oscuras. 1 es el original."] = "Difference between light and dark areas. 1 is the original.",
        ["Gamma"] = "Gamma",
        ["Aclara u oscurece los tonos medios sin quemar los blancos. 1 es el original; más alto aclara."] =
            "Lightens or darkens the mid-tones without burning the whites. 1 is the original; higher is lighter.",
        ["Brillo"] = "Brightness",
        ["Sube o baja el brillo de toda la imagen. 0 es el original."] = "Raises or lowers the brightness of the whole image. 0 is the original.",
        ["Luz por píxel"] = "Per-pixel lighting",
        ["Calcula la luz de personajes y objetos 3D en cada píxel, más suave que el original (por vértice)."] =
            "Lights characters and 3D objects per pixel, smoother than the original (per vertex).",
        ["Bordes suaves en lo 3D"] = "Smooth 3D edges",
        ["Suaviza los bordes de pelo, hojas y telas de los modelos 3D. Necesita el antialiasing activado."] =
            "Smooths the edges of hair, leaves and cloth on 3D models. Needs antialiasing on.",
        ["Antialiasing (MSAA)"] = "Antialiasing (MSAA)",
        ["Suaviza los dientes de sierra de los modelos 3D. Más alto se ve mejor pero exige más a la tarjeta."] =
            "Smooths jagged edges on 3D models. Higher looks better but costs more GPU.",
        ["Filtrado anisótropo"] = "Anisotropic filtering",
        ["Mantiene nítidas las texturas vistas en ángulo, como el suelo de los modelos 3D. Apenas cuesta rendimiento."] =
            "Keeps textures seen at an angle sharp, like the floor of 3D models. Almost no performance cost.",
        ["Resolución interna"] = "Internal resolution", ["Pantalla"] = "Screen",
        ["A qué tamaño se dibuja. Pantalla: a la de tu monitor, más nítido. Juego: a la del juego y se estira. 1,5x y 2x: más nítido, más coste."] =
            "Rendering size. Screen: your monitor's, sharper. Game: the game's, then stretched. 1.5x and 2x: sharper, more cost.",
        ["Límite de FPS"] = "FPS limit", ["Monitor"] = "Monitor",
        ["Fotogramas por segundo máximos. Monitor: los de tu pantalla, sin cortes de imagen."] =
            "Maximum frames per second. Monitor: your screen's rate, without tearing.",
        ["Modo de pantalla"] = "Display mode", ["Sin bordes"] = "Borderless", ["Ventana"] = "Window", ["Completa"] = "Fullscreen",
        ["Sin bordes: ventana a pantalla completa, cambia de programa al instante. Ventana: con marco. Completa: pantalla exclusiva."] =
            "Borderless: a full-screen window, instant app switching. Window: with a frame. Fullscreen: exclusive mode.",
        ["Mantener la proporción"] = "Keep aspect ratio",
        ["Deja bandas negras para que la imagen no se deforme si tu monitor tiene otra forma que el juego."] =
            "Adds black bars so the image is not distorted if your monitor's shape differs from the game's.",
        ["Motor gráfico"] = "Renderer",
        ["Direct3D 11 es el recomendado. Direct3D 9 es una alternativa por si da problemas en tu equipo."] =
            "Direct3D 11 is recommended. Direct3D 9 is a fallback in case it causes problems on your PC.",
        ["Ancho del juego"] = "Game width",
        ["Anchura de la vista del juego (alto 768). Monitor: según la forma de tu pantalla (hasta 21:9). 1024 es el original."] =
            "Width of the game view (height 768). Monitor: from your screen's shape (up to 21:9). 1024 is the original.",
        ["Escalado de Windows"] = "Windows scaling",
        ["Ignora el escalado de Windows (125 %, 150 %…) para que el juego no se vea borroso."] =
            "Ignores Windows scaling (125 %, 150 %...) so the game does not look blurry.",

        // Juego
        ["Minimapa siempre visible"] = "Always-on minimap",
        ["Muestra un minimapa en la esquina durante la partida, sin abrir el mapa con Tab."] =
            "Shows a minimap in the corner while playing, without opening the map with Tab.",
        ["Tamaño del minimapa"] = "Minimap size", ["Tamaño del minimapa en pantalla, en píxeles."] = "On-screen minimap size, in pixels.",
        ["Zoom del minimapa"] = "Minimap zoom", ["Cuánto terreno abarca el minimapa. Más bajo, más cerca."] = "How much ground the minimap covers. Lower is closer.",
        ["Buffos junto al retrato"] = "Buffs by the portrait",
        ["Muestra junto al retrato los iconos de los efectos activos (artes y hechizos con duración) y el tiempo que les queda."] =
            "Shows active effects (arts and spells with a duration) by the portrait, with their remaining time.",
        ["Tamaño de los buffos"] = "Buff icon size", ["Tamaño de los iconos de efectos activos."] = "Size of the active effect icons.",
        ["Nivel y supervivencia"] = "Level and survival",
        ["Muestra junto al retrato del héroe su nivel y el bonus de supervivencia, sin abrir la ficha del personaje."] =
            "Shows the hero's level and survival bonus by the portrait, without opening the character sheet.",
        ["Misiones a la izquierda"] = "Quests on the left",
        ["Lista de misiones con su título y el objetivo actual, a la izquierda, debajo del minimapa."] =
            "List of quests with their title and current objective, on the left, below the minimap.",
        ["Fijar con un toque de Alt"] = "Tap Alt to pin",
        ["Recoger el oro al pasar"] = "Pick up gold on the way",
        ["Comparar con lo equipado"] = "Compare with equipped",
        ["Equipado"] = "Equipped",
        ["Idioma del juego"] = "Game language", ["Voces del juego"] = "Game voices",
        ["Idioma de las voces del juego. Pon el sound.pak del idioma en pak/<código> (SP, DE, IT...) dentro de la carpeta del juego. Se aplica al reiniciar el juego."] = "Language of the game voices. Put that language's sound.pak in pak/<code> (SP, DE, IT...) inside the game folder. Applies after restarting the game.", ["Del juego"] = "Game default",
        ["Idioma de los textos del juego (no del mod). Pon el global.res del idioma en scripts/<código> (SP, CZ, DE, PL...) dentro de la carpeta del juego."] =
            "Language of the game's texts (not the mod's). Put that language's global.res in scripts/<code> (SP, CZ, DE, PL...) inside the game folder.",
        ["Letra del texto"] = "Text font", ["Letra de los títulos"] = "Title font", ["Tamaño de la letra"] = "Font size",
        ["AntiquaSSK (juego)"] = "AntiquaSSK (game)", ["Carolingia (juego)"] = "Carolingia (game)",
        ["Tipo de letra de los textos del juego y del mod. Puedes elegir cualquiera instalada en Windows o copiar un .ttf u .otf en la carpeta font del juego."] =
            "Font for the game's and the mod's texts. Pick any font installed in Windows, or copy a .ttf or .otf file into the game's font folder.",
        ["Tipo de letra de títulos, botones y menús del juego."] = "Font for the game's titles, buttons and menus.",
        ["Tamaño de todas las letras del juego: 11 es el original. Más grande se lee mejor, pero algunos textos pueden no caber en sus recuadros."] =
            "Size of all the game's fonts: 11 is the original. Bigger is easier to read, but some texts may not fit their boxes.",
        ["Al pasar el ratón por un objeto, junto a su ficha se ve la del objeto que llevas equipado en ese hueco."] =
            "When you hover over an item, the tooltip of the item you have equipped in that slot is shown next to it.",
        ["El oro se recoge solo al pasar a su lado, sin agacharse ni hacer clic. Solo en partida individual: en red el juego no lo permite."] =
            "Gold is picked up just by walking past it, without bending down or clicking. Single player only: the game does not allow it online.",
        ["Un toque corto de Alt deja los nombres del botín fijos; otro toque los quita. Mantener Alt invierte el modo."] =
            "A short tap of Alt pins the loot names; another tap removes them. Holding Alt inverts the mode.",
        ["Nombres fijos: mostrar"] = "Pinned names: show", ["Todo"] = "All", ["Desde azules"] = "Blue and up",
        ["Desde amarillos"] = "Yellow and up", ["Verdes y dorados"] = "Green and gold",
        ["Qué se ve con los nombres fijos. Blanco: normal; azul: mágico; amarillo: raro; verde: set; dorado: único. El oro se ve siempre."] =
            "What pinned names show. White: normal; blue: magic; yellow: rare; green: set; gold: unique. Gold coins always show.",
        ["Lanzar artes con una tecla"] = "One-key casting",
        ["Las teclas de artes (6 a 0) lanzan el arte al momento, sin tener que seleccionarlo y pulsar el botón derecho."] =
            "The art keys (6 to 0) cast the art at once, without selecting it and pressing the right button.",
        ["Segundos de recarga"] = "Cooldown seconds",
        ["Muestra encima de cada arte los segundos que faltan para poder volver a usarla."] =
            "Shows over each art the seconds left until it can be used again.",
        ["Caché de objetos"] = "Object cache",
        ["Cuántos objetos del mundo guarda el juego en memoria. Más alto evita bajones de FPS en ciudades con el zoom alejado."] =
            "How many world objects the game keeps in memory. Higher avoids FPS drops in towns when zoomed out.",
        ["Quitar buffos activos"] = "Clear active buffs", ["Pruebas: borra los iconos de efectos activos."] = "Testing: clears the active effect icons.",
        ["Guardar datos de misiones"] = "Dump quest data",
        ["Pruebas: guarda las misiones del juego en SacredFix_misiones_N.bin (junto al juego) para estudiarlas."] =
            "Testing: saves the game's quests to SacredFix_misiones_N.bin (next to the game) for study.",
        ["Muestra los fotogramas por segundo en una esquina."] = "Shows the frames per second in a corner.",
        ["Tamaño del contador"] = "Counter size", ["Tamaño del texto del contador de FPS."] = "Text size of the FPS counter.",

        // Teclas
        ["Minimapa"] = "Minimap", ["Acercar"] = "Zoom in", ["Alejar"] = "Zoom out", ["Recoger todo lo visible"] = "Pick up all visible",
        ["Inventario"] = "Inventory", ["Artes de combate"] = "Combat arts", ["Combos"] = "Combos", ["Diario"] = "Journal",
        ["Mapa del mundo"] = "World map", ["Guardar"] = "Save", ["Carga rápida"] = "Quick load", ["Guardado rápido"] = "Quick save",
        ["Pausa"] = "Pause", ["Ayuda"] = "Help", ["Poción de curación"] = "Healing potion", ["Muerte de no muertos"] = "Undead bane",
        ["Poción del mentor"] = "Mentor potion", ["Antídoto de víbora"] = "Viper antidote", ["Poción de concentración"] = "Concentration potion",
        ["Curar a los mercenarios"] = "Heal mercenaries", ["Abrir y cerrar el menú"] = "Open and close the menu",
        ["La tecla que abre y cierra este menú en la partida."] = "The key that opens and closes this menu in game.",
        ["Añadir traducción"] = "Add mapping",

        // valores, avisos y controles
        ["Sí"] = "Yes", ["No"] = "No", ["Pulsar"] = "Press", ["Ninguna"] = "None", ["Pulsa aquí"] = "Click here",
        ["Pulsa una tecla…"] = "Press a key…", ["Pulsa la tecla del juego…"] = "Press the game key…",
        ["Intro o clic: cambiar la tecla"] = "Enter or click: change the key", ["Supr: dejar la suya"] = "Del: restore the default",
        ["Tab: otra sección"] = "Tab: next section", ["Esc: cerrar"] = "Esc: close", ["cerrar"] = "close", ["Ninguna"] = "None",
        ["Flechas: elegir y cambiar"] = "Arrows: choose and change", ["Clic: cambiar · rueda: bajar"] = "Click: change · wheel: scroll",
        ["Tab: otra sección · Esc: cerrar"] = "Tab: next section · Esc: close", ["Los cambios se guardan solos"] = "Changes are saved automatically",
        ["más arriba"] = "more above", ["más opciones abajo"] = "more options below",
        ["Hay cambios que se aplican al reiniciar"] = "Some changes apply after a restart",
        ["* se aplica al reiniciar el juego"] = "* applies after restarting the game",
        ["Hay cambios (en naranja) que se aplican al reiniciar el juego"] = "Changes in orange apply after restarting the game",

        // junto al retrato (Overlay)
        ["Nivel"] = "Level", ["Supervivencia"] = "Survival",
    };
}
