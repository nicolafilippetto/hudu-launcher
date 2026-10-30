// Terminale della scheda SSH/Telnet: xterm.js dialoga con il Launcher tramite WebView2.
(function () {
    "use strict";

    var host = window.chrome && window.chrome.webview;
    var dark = { background: "#0c0c0c", foreground: "#f2f2f2", cursor: "#ffa200", selectionBackground: "#5a3d12" };
    var light = { background: "#ffffff", foreground: "#1a1f26", cursor: "#df6c01", selectionBackground: "#fbd9b5" };

    var term = new Terminal({
        fontFamily: "'Cascadia Mono', 'Cascadia Code', Consolas, monospace",
        fontSize: 14,
        cursorBlink: true,
        scrollback: 10000,
        // necessario per le decorazioni usate dall'evidenziazione
        allowProposedApi: true,
        theme: dark
    });
    var fit = new FitAddon.FitAddon();
    term.loadAddon(fit);
    term.open(document.getElementById("terminal"));
    fit.fit();
    var highlighter = window.createHighlighter ? window.createHighlighter(term) : null;

    function send(message) {
        if (host) { host.postMessage(message); }
    }

    function base64ToBytes(b64) {
        var binary = atob(b64);
        var bytes = new Uint8Array(binary.length);
        for (var i = 0; i < binary.length; i++) { bytes[i] = binary.charCodeAt(i); }
        return bytes;
    }

    term.onData(function (data) { send({ type: "input", data: data }); });
    term.onResize(function (size) { send({ type: "resize", cols: size.cols, rows: size.rows }); });

    // Copia automatica della selezione, incolla con il tasto destro o Ctrl+Shift+V.
    term.onSelectionChange(function () {
        var text = term.getSelection();
        if (text) { send({ type: "copy", text: text }); }
    });
    document.addEventListener("contextmenu", function (event) {
        event.preventDefault();
        send({ type: "paste-request" });
    });
    term.attachCustomKeyEventHandler(function (event) {
        if (event.type === "keydown" && event.ctrlKey && event.shiftKey && (event.key === "V" || event.key === "v")) {
            send({ type: "paste-request" });
            return false;
        }
        if (event.type === "keydown" && event.ctrlKey && event.shiftKey && (event.key === "C" || event.key === "c")) {
            var text = term.getSelection();
            if (text) { send({ type: "copy", text: text }); }
            return false;
        }
        return true;
    });

    if (host) {
        host.addEventListener("message", function (event) {
            var message = event.data;
            switch (message.type) {
                case "data":
                    term.write(base64ToBytes(message.data));
                    break;
                case "text":
                    term.write(message.data);
                    break;
                case "paste":
                    term.paste(message.data);
                    break;
                case "theme":
                    term.options.theme = message.dark ? dark : light;
                    document.body.classList.toggle("light", !message.dark);
                    if (highlighter) { highlighter.setDark(message.dark); }
                    break;
                case "highlight":
                    if (highlighter) { highlighter.setRules(message.rules); }
                    break;
                case "focus":
                    term.focus();
                    break;
                case "copy-selection":
                    var selected = term.getSelection();
                    if (selected) { send({ type: "copy", text: selected }); }
                    term.focus();
                    break;
                case "select-all":
                    term.selectAll();
                    term.focus();
                    break;
                case "clear":
                    term.clear();
                    term.focus();
                    break;
                case "zoom":
                    zoom(message.delta);
                    break;
                case "export":
                    send({ type: "export", text: bufferText() });
                    break;
            }
        });
    }

    // Dimensione del testo: dai pulsanti della barra o con Ctrl+rotellina.
    function zoom(delta) {
        var size = Math.max(9, Math.min(28, term.options.fontSize + delta));
        if (size !== term.options.fontSize) {
            term.options.fontSize = size;
            fit.fit();
        }
        term.focus();
    }
    document.addEventListener("wheel", function (event) {
        if (event.ctrlKey) {
            event.preventDefault();
            zoom(event.deltaY < 0 ? 1 : -1);
        }
    }, { passive: false });

    // Tutto il testo del terminale (cronologia compresa), con le righe spezzate ricomposte.
    function bufferText() {
        var buffer = term.buffer.active;
        var lines = [];
        for (var i = 0; i < buffer.length; i++) {
            var line = buffer.getLine(i);
            if (!line) { continue; }
            var text = line.translateToString(true);
            if (line.isWrapped && lines.length > 0) {
                lines[lines.length - 1] += text;
            } else {
                lines.push(text);
            }
        }
        while (lines.length > 0 && lines[lines.length - 1] === "") { lines.pop(); }
        return lines.join("\n") + "\n";
    }

    window.addEventListener("resize", function () { fit.fit(); });
    term.focus();
    send({ type: "ready", cols: term.cols, rows: term.rows });
})();
