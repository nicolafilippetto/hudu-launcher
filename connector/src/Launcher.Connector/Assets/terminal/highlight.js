// Evidenziazione del terminale con regole regex (come Royal TS/Rebex). Il testo non viene toccato:
// i colori sono decorazioni di xterm.js sulle righe visibili, ricalcolate quando cambiano.
(function () {
    "use strict";

    var palettes = {
        dark: { red: "#ff6b6b", green: "#4ade80", yellow: "#facc15", blue: "#60a5fa", magenta: "#e879f9", cyan: "#22d3ee", orange: "#fb923c", gray: "#9ca3af" },
        light: { red: "#c62828", green: "#2e7d32", yellow: "#a16207", blue: "#1d4ed8", magenta: "#a21caf", cyan: "#0e7490", orange: "#c2410c", gray: "#6b7280" }
    };

    window.createHighlighter = function (term) {
        var rules = [];
        var dark = true;
        var cache = new Map(); // prima riga (assoluta) della riga logica -> { key, markers, decorations }
        var scheduled = false;

        function colorOf(name) {
            if (/^#[0-9a-f]{6}$/i.test(name)) { return name; }
            return (dark ? palettes.dark : palettes.light)[name] || null;
        }

        function setRules(list) {
            rules = [];
            (list || []).forEach(function (rule) {
                try {
                    var color = colorOf(rule.color);
                    if (color) { rules.push({ re: new RegExp(rule.pattern, "gi"), color: rule.color }); }
                } catch (e) {
                    // espressione non valida in JavaScript: si salta
                }
            });
            clear();
            schedule();
        }

        function setDark(value) {
            dark = value;
            clear();
            schedule();
        }

        function disposeEntry(entry) {
            entry.decorations.forEach(function (d) { d.dispose(); });
            entry.markers.forEach(function (m) { m.marker.dispose(); });
        }

        function clear() {
            cache.forEach(disposeEntry);
            cache.clear();
        }

        function schedule() {
            if (scheduled || rules.length === 0) { return; }
            scheduled = true;
            requestAnimationFrame(refresh);
        }

        // Testo della riga logica (righe a capo automatico unite) con la colonna di ogni carattere.
        function readLine(buffer, rows) {
            var text = "";
            var cells = [];
            var cell = null;
            rows.forEach(function (y) {
                var line = buffer.getLine(y);
                if (!line) { return; }
                for (var x = 0; x < line.length; x++) {
                    cell = line.getCell(x, cell || undefined);
                    if (!cell) { break; }
                    var width = cell.getWidth();
                    if (width === 0) { continue; }
                    var chars = cell.getChars() || " ";
                    var plain = cell.isFgDefault() && !cell.isInverse();
                    for (var i = 0; i < chars.length; i++) {
                        cells.push({ y: y, x: x, width: width, plain: plain });
                    }
                    text += chars;
                }
            });
            return { text: text, cells: cells };
        }

        function decorate(buffer, start, rows, line) {
            var colors = new Array(line.text.length);
            rules.forEach(function (rule) {
                rule.re.lastIndex = 0;
                var match;
                var guard = 0;
                while ((match = rule.re.exec(line.text)) !== null && guard++ < 500) {
                    if (match[0].length === 0) { rule.re.lastIndex++; continue; }
                    for (var i = match.index; i < match.index + match[0].length; i++) {
                        // vale la prima regola; il testo già colorato dal server resta com'è
                        if (colors[i] === undefined && line.cells[i].plain) { colors[i] = rule.color; }
                    }
                }
            });

            var cursorLine = buffer.baseY + buffer.cursorY;
            var markers = {};
            var decorations = [];
            var segment = null;

            function flush() {
                if (!segment) { return; }
                var marker = markers[segment.y];
                if (!marker) {
                    marker = term.registerMarker(segment.y - cursorLine);
                    if (!marker) { segment = null; return; }
                    markers[segment.y] = marker;
                }
                var decoration = term.registerDecoration({
                    marker: marker,
                    x: segment.x,
                    width: segment.end - segment.x,
                    foregroundColor: colorOf(segment.color),
                    layer: "top"
                });
                if (decoration) { decorations.push(decoration); }
                segment = null;
            }

            for (var i = 0; i < line.cells.length; i++) {
                var c = line.cells[i];
                var color = colors[i];
                if (segment && (color !== segment.color || c.y !== segment.y)) { flush(); }
                if (color === undefined) { continue; }
                if (!segment) { segment = { y: c.y, x: c.x, end: c.x + c.width, color: color }; }
                else { segment.end = Math.max(segment.end, c.x + c.width); }
            }
            flush();

            return {
                key: line.text,
                start: start,
                // con la riga prevista: se il buffer scorre via le righe vecchie, il marker si sposta
                markers: Object.keys(markers).map(function (k) { return { marker: markers[k], y: Number(k) }; }),
                decorations: decorations
            };
        }

        function refresh() {
            scheduled = false;
            var buffer = term.buffer.active;
            // Programmi a schermo intero (vim, top…): niente evidenziazione, hanno già i loro colori.
            if (buffer.type !== "normal" || rules.length === 0) { clear(); return; }

            var top = buffer.viewportY;
            var bottom = Math.min(buffer.length, top + term.rows);
            var y = top;
            while (y > 0 && buffer.getLine(y) && buffer.getLine(y).isWrapped) { y--; }

            var seen = new Set();
            while (y < bottom) {
                var start = y;
                var rows = [];
                do { rows.push(y); y++; } while (y < buffer.length && buffer.getLine(y) && buffer.getLine(y).isWrapped);
                seen.add(start);

                var line = readLine(buffer, rows);
                var entry = cache.get(start);
                var valid = entry && entry.key === line.text
                    && entry.markers.every(function (m) { return !m.marker.isDisposed && m.marker.line === m.y; });
                if (valid) { continue; }
                if (entry) { disposeEntry(entry); }
                cache.set(start, decorate(buffer, start, rows, line));
            }

            cache.forEach(function (entry, key) {
                if (!seen.has(key)) { disposeEntry(entry); cache.delete(key); }
            });
        }

        term.onWriteParsed(schedule);
        term.onScroll(schedule);
        term.onResize(function () { clear(); schedule(); });
        term.onRender(schedule);
        term.buffer.onBufferChange(function () { clear(); schedule(); });

        return { setRules: setRules, setDark: setDark };
    };
})();
