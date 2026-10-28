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
})();
