// Applica il tema salvato prima del disegno della pagina (evita il lampo bianco).
(function () {
    try {
        var theme = localStorage.getItem("launcher-theme");
        if (theme === "light" || theme === "dark") {
            document.documentElement.setAttribute("data-theme", theme);
        }
    } catch (e) {
        // localStorage non disponibile: si segue il tema del sistema.
    }

    // Barra del titolo del portale installato come app: stesso colore della barra superiore.
    var dark = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;
    window.updateThemeColor = function () {
        var meta = document.querySelector('meta[name="theme-color"]');
        if (!meta) { return; }
        var chosen = document.documentElement.getAttribute("data-theme");
        var isDark = chosen ? chosen === "dark" : !!(dark && dark.matches);
        meta.setAttribute("content", isDark ? "#121212" : "#ffffff");
    };
    document.addEventListener("DOMContentLoaded", window.updateThemeColor);
    if (dark && dark.addEventListener) { dark.addEventListener("change", window.updateThemeColor); }
})();
