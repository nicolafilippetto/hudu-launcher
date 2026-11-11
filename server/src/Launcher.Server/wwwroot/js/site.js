// Launcher — comportamento del portale. Nessuna dipendenza esterna; nessuno script inline (CSP).
(function () {
    "use strict";

    var $ = function (selector, root) { return (root || document).querySelector(selector); };
    var $$ = function (selector, root) { return Array.prototype.slice.call((root || document).querySelectorAll(selector)); };

    // ------------------------------------------------------------------ utilità

    function antiforgeryToken() {
        var input = $('input[name="__RequestVerificationToken"]');
        return input ? input.value : "";
    }

    function toast(message, kind) {
        var container = $(".toasts");
        if (!container) { return; }
        var el = document.createElement("div");
        el.className = "toast" + (kind ? " " + kind : "");
        el.textContent = message;
        container.appendChild(el);
        setTimeout(function () { el.remove(); }, kind === "error" ? 7000 : 4000);
    }

    async function readError(response) {
        try {
            var data = await response.json();
            return data.detail || data.title || ("Errore " + response.status);
        } catch (e) {
            if (response.status === 401) { return "Sessione scaduta: ricarica la pagina."; }
            if (response.status === 429) { return "Troppe richieste: attendi qualche minuto."; }
            return "Errore " + response.status;
        }
    }

    async function postJson(url, body) {
        var response = await fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: {
                "Content-Type": "application/json",
                "RequestVerificationToken": antiforgeryToken(),
                "X-Requested-With": "fetch"
            },
            body: JSON.stringify(body || {})
        });
        if (!response.ok) { throw new Error(await readError(response)); }
        return response.json();
    }

    function busy(button, on) {
        if (!button) { return; }
        button.classList.toggle("is-busy", on);
    }

    // ------------------------------------------------------------------ tema

    function applyTheme(theme) {
        if (theme === "light" || theme === "dark") {
            document.documentElement.setAttribute("data-theme", theme);
        } else {
            document.documentElement.removeAttribute("data-theme");
        }
        try { localStorage.setItem("launcher-theme", theme); } catch (e) { /* ignorato */ }
        if (window.updateThemeColor) { window.updateThemeColor(); }
    }

    document.addEventListener("click", function (event) {
        var toggle = event.target.closest("[data-theme-toggle]");
        if (!toggle) { return; }
        var current = document.documentElement.getAttribute("data-theme") || "auto";
        var next = current === "auto" ? "light" : current === "light" ? "dark" : "auto";
        applyTheme(next);
        toast(next === "auto" ? "Tema: come il sistema" : next === "light" ? "Tema chiaro" : "Tema scuro");
    });

    // ------------------------------------------------------------------ ricerca

    var searchForm = $("[data-live-search]");
    var searchInput = searchForm ? $("input", searchForm) : null;
    var scopeButton = searchForm ? $("[data-search-scope]", searchForm) : null;
    var content = $("[data-content]");
    var searchTimer = null;
    var searchController = null;
    // Cliente aperto: la ricerca resta nel cliente finché non si sceglie "Cerca ovunque".
    var scopeId = searchForm ? searchForm.getAttribute("data-scope-id") || "" : "";

    function runSearch() {
        if (!content || !searchInput) { return; }
        var q = searchInput.value.trim();
        if (searchController) { searchController.abort(); }
        searchController = new AbortController();
        var params = "q=" + encodeURIComponent(q) + (scopeId ? "&c=" + encodeURIComponent(scopeId) : "");
        fetch("/?handler=Search&" + params, {
            credentials: "same-origin",
            headers: { "X-Requested-With": "fetch" },
            signal: searchController.signal
        }).then(function (response) {
            if (!response.ok) { throw new Error("Ricerca non riuscita"); }
            return response.text();
        }).then(function (html) {
            content.innerHTML = html;
            var url = "/";
            if (scopeId) { url += "?c=" + encodeURIComponent(scopeId) + (q ? "&q=" + encodeURIComponent(q) : ""); }
            else if (q) { url += "?q=" + encodeURIComponent(q); }
            history.replaceState(null, "", url);
            if (!scopeId) {
                $$(".company-item.active").forEach(function (a) { a.classList.remove("active"); });
            }
        }).catch(function (error) {
            if (error.name !== "AbortError") { toast(error.message, "error"); }
        });
    }

    function searchEverywhere() {
        scopeId = "";
        searchForm.classList.remove("has-scope");
        if (scopeButton) { scopeButton.hidden = true; }
        searchInput.placeholder = "Cerca cliente, server, IP…";
        searchInput.focus();
        if (searchInput.value.trim()) { runSearch(); }
    }

    if (searchInput && content) {
        searchInput.addEventListener("input", function () {
            clearTimeout(searchTimer);
            searchTimer = setTimeout(runSearch, 220);
        });
        searchForm.addEventListener("submit", function (event) {
            event.preventDefault();
            clearTimeout(searchTimer);
            runSearch();
        });
        document.addEventListener("click", function (event) {
            if (event.target.closest("[data-search-scope], [data-search-everywhere]")) { searchEverywhere(); }
        });
    }

    document.addEventListener("keydown", function (event) {
        var typing = /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement.tagName);
        if (event.key === "/" && !typing && searchInput) {
            event.preventDefault();
            searchInput.focus();
            searchInput.select();
        } else if (event.key === "Escape" && document.activeElement === searchInput && searchInput.value) {
            searchInput.value = "";
            runSearch();
        }
    });

    // ------------------------------------------------------------------ filtro clienti

    var companyFilter = $("[data-company-filter]");
    if (companyFilter) {
        companyFilter.addEventListener("input", function () {
            var terms = companyFilter.value.toLowerCase().trim().split(/\s+/).filter(Boolean);
            $$("[data-company-list] .company-item").forEach(function (item) {
                var name = item.getAttribute("data-name") || "";
                item.hidden = !terms.every(function (t) { return name.indexOf(t) >= 0; });
            });
        });
    }

    // L'elenco dei clienti resta dov'era tra un clic e l'altro (ogni clic ricarica la pagina). Si
    // sposta solo se il cliente aperto non si vede, e muove solo l'elenco: scrollIntoView faceva
    // scorrere anche la pagina, nascondendo l'intestazione del cliente.
    var companyList = $("[data-company-list]");
    if (companyList) {
        try {
            var savedScroll = sessionStorage.getItem("launcher-company-scroll");
            if (savedScroll !== null) { companyList.scrollTop = parseInt(savedScroll, 10) || 0; }
        } catch (e) { /* sessionStorage non disponibile */ }

        var activeCompany = $(".company-item.active", companyList);
        if (activeCompany) {
            var itemBox = activeCompany.getBoundingClientRect();
            var listBox = companyList.getBoundingClientRect();
            if (itemBox.top < listBox.top || itemBox.bottom > listBox.bottom) {
                companyList.scrollTop += itemBox.top - listBox.top - companyList.clientHeight / 3;
            }
        }

        window.addEventListener("pagehide", function () {
            try { sessionStorage.setItem("launcher-company-scroll", String(companyList.scrollTop)); } catch (e) { /* ignorato */ }
        });
    }

    // ------------------------------------------------------------------ connessione

    // ------------------------------------------------------------------ selezione multipla

    // Asset selezionati (anche di clienti diversi): cache offline, modifica in blocco, nascondi.
    // La selezione resta passando da un cliente all'altro (sessionStorage della scheda).
    var offlineMax = parseInt(document.body.getAttribute("data-offline-max"), 10) || 0;
    var offlineMinutes = document.body.getAttribute("data-offline-minutes") || "60";
    var selectionBar = null;
    var selection = {};
    try { selection = JSON.parse(sessionStorage.getItem("launcher-selection")) || {}; } catch (e) { /* ignorato */ }

    function saveSelection() {
        try { sessionStorage.setItem("launcher-selection", JSON.stringify(selection)); } catch (e) { /* ignorato */ }
    }

    function selectedIds() { return Object.keys(selection); }

    function isSelected(id) { return Object.prototype.hasOwnProperty.call(selection, id); }

    function clearSelection() {
        selection = {};
        saveSelection();
        renderSelection();
    }

    function buildSelectionBar() {
        var bar = document.createElement("div");
        bar.className = "selection-bar";
        var add = function (tag, className, text, attribute) {
            var el = document.createElement(tag);
            el.className = className;
            el.textContent = text;
            if (attribute) { el.type = "button"; el.setAttribute(attribute, ""); }
            bar.appendChild(el);
            return el;
        };
        add("span", "selection-count", "");
        if (offlineMax > 0) { add("button", "btn btn-primary btn-sm", "", "data-offline-prepare"); }
        add("button", "btn btn-secondary btn-sm", "Modifica in blocco", "data-bulk-edit");
        add("button", "btn btn-secondary btn-sm", "Nascondi", "data-bulk-hide");
        add("button", "btn btn-secondary btn-sm", "Mostra", "data-bulk-show");
        add("button", "btn btn-ghost btn-sm", "Deseleziona", "data-selection-clear");
        return bar;
    }

    function renderSelection() {
        $$("[data-pick]").forEach(function (box) { box.checked = isSelected(box.getAttribute("data-pick")); });
        $$("[data-pick-all]").forEach(function (all) {
            var boxes = $$("[data-pick]", all.closest(".section"));
            var picked = boxes.filter(function (b) { return b.checked; }).length;
            all.checked = boxes.length > 0 && picked === boxes.length;
            all.indeterminate = picked > 0 && picked < boxes.length;
        });
        var ids = selectedIds();
        document.body.classList.toggle("has-selection", ids.length > 0);
        if (ids.length === 0) {
            if (selectionBar) { selectionBar.remove(); selectionBar = null; }
            return;
        }
        if (!selectionBar) {
            selectionBar = buildSelectionBar();
            document.body.appendChild(selectionBar);
        }
        $(".selection-count", selectionBar).textContent = ids.length === 1 ? "1 selezionato" : ids.length + " selezionati";
        // "Nascondi" se tra i selezionati c'è un asset visibile, "Mostra" se ce n'è uno nascosto.
        var hiddenCount = ids.filter(function (id) { return selection[id].hidden === true; }).length;
        $("[data-bulk-hide]", selectionBar).hidden = hiddenCount === ids.length;
        $("[data-bulk-show]", selectionBar).hidden = hiddenCount === 0;
        var offlineButton = $("[data-offline-prepare]", selectionBar);
        if (offlineButton) {
            var withoutIp = ids.filter(function (id) { return !selection[id].direct; }).length;
            offlineButton.textContent = "Offline " + ids.length + "/" + offlineMax;
            offlineButton.disabled = ids.length > offlineMax || withoutIp > 0;
            offlineButton.title = ids.length > offlineMax
                ? "Al massimo " + offlineMax + " asset nella cache offline"
                : withoutIp > 0
                    ? withoutIp + " asset senza indirizzo IP: non si possono usare offline"
                    : "Prepara nel Launcher per " + offlineMinutes + " minuti (connessione diretta, anche senza internet)";
        }
    }

    function select(box, on) {
        var id = box.getAttribute("data-pick");
        if (on) {
            selection[id] = {
                name: box.getAttribute("data-pick-name") || id,
                direct: box.getAttribute("data-pick-direct") === "true",
                hidden: box.getAttribute("data-pick-hidden") === "true"
            };
        } else {
            delete selection[id];
        }
    }

    renderSelection();
    // Le righe cambiano con la ricerca e con l'editor: si riallineano le spunte.
    if (content && window.MutationObserver) {
        new MutationObserver(function (records) {
            var rowsChanged = records.some(function (r) { return r.addedNodes.length > 0; });
            if (rowsChanged) { renderSelection(); }
        }).observe(content, { childList: true, subtree: true });
    }

    document.addEventListener("change", function (event) {
        var box = event.target.closest("[data-pick]");
        var all = event.target.closest("[data-pick-all]");
        if (box) {
            select(box, box.checked);
        } else if (all) {
            $$("[data-pick]", all.closest(".section")).forEach(function (b) { select(b, all.checked); });
        } else {
            return;
        }
        saveSelection();
        renderSelection();
    });

    async function postForm(url, values) {
        var data = new FormData();
        data.append("__RequestVerificationToken", antiforgeryToken());
        Object.keys(values).forEach(function (key) { data.append(key, values[key]); });
        var response = await fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: { "X-Requested-With": "fetch" },
            body: data
        });
        if (!response.ok) { throw new Error(await readError(response)); }
        return response.json();
    }

    document.addEventListener("click", async function (event) {
        if (event.target.closest("[data-selection-clear]")) {
            clearSelection();
            return;
        }
        var hide = event.target.closest("[data-bulk-hide]");
        var show = event.target.closest("[data-bulk-show]");
        if (hide || show) {
            var ids = selectedIds();
            if (hide && !window.confirm("Nascondere " + ids.length + " asset? Si possono ritrovare con \"Mostra anche gli asset nascosti\".")) { return; }
            var visibilityButton = hide || show;
            busy(visibilityButton, true);
            try {
                var result = await postForm("/?handler=Bulk", { AssetIds: ids.join(","), Visibility: hide ? "hide" : "show" });
                clearSelection();
                toast(result.message, "ok");
                setTimeout(function () { window.location.reload(); }, 600);
            } catch (error) {
                toast(error.message, "error");
            } finally {
                busy(visibilityButton, false);
            }
            return;
        }
        if (event.target.closest("[data-bulk-edit]")) {
            openDialogFrom("/?handler=BulkEditor&ids=" + encodeURIComponent(selectedIds().join(",")));
            return;
        }
        var button = event.target.closest("[data-offline-prepare]");
        if (!button || button.disabled) { return; }
        busy(button, true);
        try {
            var count = selectedIds().length;
            var offline = await postJson("/api/offline", {
                assetIds: selectedIds().map(function (id) { return parseInt(id, 10); })
            });
            window.location.href = offline.url;
            clearSelection();
            toast(count + " asset inviati al Launcher: li trovi in \"Pronte offline\" per " + offlineMinutes + " minuti. Non chiudere il Launcher.", "ok");
        } catch (error) {
            toast(error.message, "error");
        } finally {
            busy(button, false);
        }
    });

    // Il pulsante di connessione chiede come collegarsi: tramite jump host RustDesk o diretta
    // (tecnico dal cliente o in VPN). L'ultima scelta viene proposta per prima.
    var connectMenu = null;

    function closeConnectMenu() {
        if (connectMenu) { connectMenu.remove(); connectMenu = null; }
    }

    async function startConnection(button, mode, anchor) {
        closeConnectMenu();
        var shown = anchor || button;
        busy(shown, true);
        try { localStorage.setItem("launcher-connect-mode", mode); } catch (e) { /* ignorato */ }
        try {
            var result = await postJson("/api/connect", {
                assetId: parseInt(button.getAttribute("data-connect"), 10),
                key: button.getAttribute("data-key"),
                mode: mode
            });
            window.location.href = result.url;
            toast("Apertura del Launcher… Se non succede nulla, installa Hudu Launcher per Windows.");
        } catch (error) {
            toast(error.message, "error");
        } finally {
            setTimeout(function () { busy(shown, false); }, 1500);
        }
    }

    function connectWith(button, anchor) {
        // RustDesk spento: esiste solo la connessione diretta, niente menu.
        if (document.body.getAttribute("data-rustdesk") === "off") {
            startConnection(button, "direct", anchor);
        } else {
            openConnectMenu(button, anchor);
        }
    }

    function menuItem(title, detail, enabled, icon) {
        var item = document.createElement("button");
        item.type = "button";
        item.className = "connect-option";
        item.setAttribute("role", "menuitem");
        item.disabled = !enabled;
        var strong = document.createElement("strong");
        if (icon) {
            strong.className = "connect-option-row";
            var svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
            var use = document.createElementNS("http://www.w3.org/2000/svg", "use");
            use.setAttribute("href", "#" + icon);
            svg.appendChild(use);
            strong.appendChild(svg);
            strong.appendChild(document.createTextNode(title));
        } else {
            strong.textContent = title;
        }
        var small = document.createElement("span");
        small.textContent = detail;
        item.appendChild(strong);
        item.appendChild(small);
        return item;
    }

    function showMenu(anchor, focusIndex) {
        document.body.appendChild(connectMenu);
        placeConnectMenu(anchor);
        var buttons = $$(".connect-option", connectMenu);
        if (buttons[focusIndex]) { buttons[focusIndex].focus(); }
    }

    // "Altre connessioni": prima si sceglie la connessione, poi (come per le altre) jump host o diretta.
    function openMoreMenu(more) {
        closeConnectMenu();
        var list = more.parentNode.querySelector("[data-connect-more-list]");
        if (!list) { return; }
        connectMenu = document.createElement("div");
        connectMenu.className = "connect-menu";
        connectMenu.setAttribute("role", "menu");
        var first = -1;
        $$("[data-connect]", list).forEach(function (button, index) {
            var item = menuItem(button.textContent.trim(), button.getAttribute("data-target") || "", !button.disabled,
                button.getAttribute("data-icon"));
            item.title = button.title;
            item.addEventListener("click", function (event) {
                event.stopPropagation();
                connectWith(button, more);
            });
            connectMenu.appendChild(item);
            if (first < 0 && !button.disabled) { first = index; }
        });
        showMenu(more, first);
    }

    function openConnectMenu(button, anchor) {
        anchor = anchor || button;
        closeConnectMenu();
        var gateway = button.getAttribute("data-gateway");
        var target = button.getAttribute("data-target") || "";
        var last = "gateway";
        try { last = localStorage.getItem("launcher-connect-mode") || "gateway"; } catch (e) { /* ignorato */ }

        connectMenu = document.createElement("div");
        connectMenu.className = "connect-menu";
        connectMenu.setAttribute("role", "menu");
        var options = [
            { mode: "gateway", title: "Tramite jump host", detail: gateway ? "RustDesk " + gateway : "nessun gateway disponibile", enabled: !!gateway },
            { mode: "direct", title: "Diretta", detail: target + " · dal cliente o in VPN", enabled: true }
        ];
        var title = document.createElement("div");
        title.className = "connect-option-head";
        title.textContent = button.textContent.trim();
        if (anchor !== button) { connectMenu.appendChild(title); }
        options.forEach(function (option) {
            var item = menuItem(option.title, option.detail, option.enabled);
            item.addEventListener("click", function (event) {
                event.stopPropagation();
                startConnection(button, option.mode, anchor);
            });
            connectMenu.appendChild(item);
        });
        var index = options.findIndex(function (o) { return o.mode === last && o.enabled; });
        if (index < 0) { index = options.findIndex(function (o) { return o.enabled; }); }
        showMenu(anchor, index);
    }

    // Il menu resta sempre dentro la finestra: sotto il pulsante se c'è spazio, altrimenti sopra
    // (le ultime righe dell'elenco sono vicine al fondo della pagina).
    function placeConnectMenu(button) {
        var rect = button.getBoundingClientRect();
        var width = connectMenu.offsetWidth;
        var height = connectMenu.offsetHeight;
        var left = Math.min(rect.right - width, window.innerWidth - width - 12);
        var below = rect.bottom + 6;
        var above = rect.top - 6 - height;
        var top = below + height <= window.innerHeight - 8 || above < 8 ? below : above;
        connectMenu.style.left = Math.max(12, left) + "px";
        connectMenu.style.top = Math.max(8, Math.min(top, window.innerHeight - height - 8)) + "px";
    }

    window.addEventListener("resize", closeConnectMenu);
    window.addEventListener("scroll", closeConnectMenu, true);

    document.addEventListener("click", function (event) {
        var more = event.target.closest("[data-connect-more]");
        if (more) {
            event.stopPropagation();
            openMoreMenu(more);
            return;
        }
        var button = event.target.closest("[data-connect]");
        if (button && !button.disabled) {
            event.stopPropagation();
            connectWith(button);
            return;
        }
        if (connectMenu && !event.target.closest(".connect-menu")) { closeConnectMenu(); }
    });

    document.addEventListener("keydown", function (event) {
        if (!connectMenu) { return; }
        if (event.key === "Escape") { closeConnectMenu(); }
        if (event.key === "ArrowDown" || event.key === "ArrowUp") {
            event.preventDefault();
            var items = $$(".connect-option:not([disabled])", connectMenu);
            var index = items.indexOf(document.activeElement);
            var next = items[(index + (event.key === "ArrowDown" ? 1 : items.length - 1)) % items.length];
            if (next) { next.focus(); }
        }
    });
    window.addEventListener("scroll", closeConnectMenu, { passive: true });

    document.addEventListener("click", async function (event) {
        var button = event.target.closest("[data-favorite]");
        if (!button) { return; }
        var id = button.getAttribute("data-favorite");
        try {
            var result = await postJson("/api/favorites/" + encodeURIComponent(id));
            $$('[data-favorite="' + id + '"]').forEach(function (b) {
                b.classList.toggle("on", result.favorite);
                b.setAttribute("aria-pressed", result.favorite ? "true" : "false");
            });
            toast(result.favorite ? "Aggiunto ai preferiti" : "Rimosso dai preferiti");
        } catch (error) {
            toast(error.message, "error");
        }
    });

    // ------------------------------------------------------------------ editor asset

    var dialog = $("[data-editor]");
    var dialogBody = $("[data-editor-body]");
    var peersLoaded = false;

    async function loadPeers() {
        if (peersLoaded) { return; }
        peersLoaded = true;
        try {
            var response = await fetch("/?handler=Peers", { credentials: "same-origin", headers: { "X-Requested-With": "fetch" } });
            if (!response.ok) { throw new Error(); }
            var peers = await response.json();
            var list = $("#peer-list");
            var fragment = document.createDocumentFragment();
            peers.forEach(function (peer) {
                var option = document.createElement("option");
                option.value = peer.label;
                if (peer.book) { option.label = peer.book; }
                fragment.appendChild(option);
            });
            list.appendChild(fragment);
        } catch (e) {
            peersLoaded = false;
        }
    }

    function openEditor(assetId) {
        openDialogFrom("/?handler=Editor&assetId=" + encodeURIComponent(assetId));
    }

    // Editor di un asset o modifica in blocco: HTML generato dal portale (Razor, già codificato).
    async function openDialogFrom(url) {
        if (!dialog) { return; }
        dialogBody.innerHTML = '<div class="modal-loading">Caricamento…</div>';
        dialog.showModal();
        loadPeers();
        try {
            var response = await fetch(url, {
                credentials: "same-origin",
                headers: { "X-Requested-With": "fetch" }
            });
            if (!response.ok) { throw new Error(await readError(response)); }
            dialogBody.innerHTML = await response.text();
            syncEditor();
            var first = $("select, input:not([type=hidden])", dialogBody);
            if (first) { first.focus(); }
        } catch (error) {
            dialog.close();
            toast(error.message || "Impossibile aprire l'editor", "error");
        }
    }

    document.addEventListener("click", function (event) {
        var edit = event.target.closest("[data-edit]");
        if (edit) {
            openEditor(edit.getAttribute("data-edit"));
            return;
        }
        // Chiusura di qualsiasi finestra modale: pulsante di chiusura o clic sullo sfondo.
        var closer = event.target.closest("[data-close-modal]");
        var modal = closer ? closer.closest("dialog") : (event.target.tagName === "DIALOG" ? event.target : null);
        if (modal) { modal.close(); }
    });

    // ------------------------------------------------------------------ finestre e amministratori

    document.addEventListener("click", function (event) {
        var opener = event.target.closest("[data-open-dialog]");
        if (!opener) { return; }
        var target = document.getElementById(opener.getAttribute("data-open-dialog"));
        if (target && target.showModal) {
            target.showModal();
            var first = $("input:not([type=hidden])", target);
            if (first) { first.focus(); }
        }
    });

    function generatePassword() {
        // 20 caratteri senza simboli ambigui: sempre almeno una maiuscola, una minuscola e un numero.
        var sets = ["ABCDEFGHJKLMNPQRSTUVWXYZ", "abcdefghijkmnopqrstuvwxyz", "23456789"];
        var all = sets.join("") + "-_.!?";
        var random = new Uint32Array(20);
        crypto.getRandomValues(random);
        var chars = Array.prototype.map.call(random, function (n, i) {
            var pool = i < sets.length ? sets[i] : all;
            return pool[n % pool.length];
        });
        var order = new Uint32Array(chars.length);
        crypto.getRandomValues(order);
        for (var i = chars.length - 1; i > 0; i--) {
            var j = order[i] % (i + 1);
            var t = chars[i]; chars[i] = chars[j]; chars[j] = t;
        }
        return chars.join("");
    }

    document.addEventListener("click", function (event) {
        var button = event.target.closest("[data-generate-password]");
        if (!button) { return; }
        var input = $("input", button.parentElement);
        input.value = generatePassword();
        input.focus();
        input.select();
    });

    function syncBreakGlass(form) {
        var toggle = $("[data-breakglass-toggle]", form);
        var field = $("[data-breakglass-field]", form);
        var password = $("[data-breakglass-password]", form);
        if (!toggle || !field || !password) { return; }
        field.hidden = !toggle.checked;
        password.disabled = !toggle.checked;
    }

    $$("[data-breakglass-form]").forEach(syncBreakGlass);
    document.addEventListener("change", function (event) {
        if (event.target.matches("[data-breakglass-toggle]")) { syncBreakGlass(event.target.closest("form")); }
    });

    // ------------------------------------------------------------------ editor a schede

    var protocolIcons = { Rdp: "i-monitor", Ssh: "i-terminal", Telnet: "i-terminal", Http: "i-globe", Https: "i-globe",
        Winbox: "i-router", Oracle: "i-database", Tcp: "i-cable" };

    function tabFor(panel) {
        return panel ? $('[data-tab="' + panel.getAttribute("data-panel") + '"]', dialogBody) : null;
    }

    function selectTab(name) {
        $$("[data-tab]", dialogBody).forEach(function (tab) {
            var on = tab.getAttribute("data-tab") === name;
            tab.classList.toggle("is-active", on);
            tab.setAttribute("aria-selected", on ? "true" : "false");
        });
        $$("[data-panel]", dialogBody).forEach(function (panel) {
            panel.hidden = panel.getAttribute("data-panel") !== name;
        });
    }

    // Protocollo effettivo della scheda: quello scelto o, se "automatico", quello dedotto dal portale.
    function syncPanel(panel) {
        var select = panel ? $("[data-protocol]", panel) : null;
        if (!select) { return; }
        var protocol = select.value || select.getAttribute("data-auto-protocol") || "";
        var any = false;
        $$("[data-for]", panel).forEach(function (group) {
            var on = group.getAttribute("data-for").split(" ").indexOf(protocol) >= 0;
            group.hidden = !on;
            any = any || on;
        });
        var none = $("[data-advanced-none]", panel);
        if (none) { none.hidden = any; }
        // Oracle: il nome del database è obbligatorio, quindi le avanzate si aprono da sole.
        var details = $("[data-advanced]", panel);
        if (details && protocol === "Oracle") { details.open = true; }
        var tab = tabFor(panel);
        var use = tab && !tab.classList.contains("is-muted") ? $("use", tab) : null;
        if (use && protocolIcons[protocol]) { use.setAttribute("href", "#" + protocolIcons[protocol]); }
    }

    function syncEditor() {
        $$("[data-panel]", dialogBody).forEach(syncPanel);
    }

    function newTabButton(index) {
        var button = document.createElement("button");
        button.type = "button";
        button.className = "conn-tab";
        button.setAttribute("role", "tab");
        button.setAttribute("aria-selected", "false");
        button.setAttribute("data-tab", "extra-" + index);
        var svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
        var use = document.createElementNS("http://www.w3.org/2000/svg", "use");
        use.setAttribute("href", "#i-globe");
        svg.appendChild(use);
        var label = document.createElement("span");
        label.setAttribute("data-tab-label", "");
        label.textContent = "Nuova connessione";
        button.appendChild(svg);
        button.appendChild(label);
        return button;
    }

    document.addEventListener("click", function (event) {
        if (!dialogBody || !dialogBody.contains(event.target)) { return; }
        var tab = event.target.closest("[data-tab]");
        if (tab) {
            selectTab(tab.getAttribute("data-tab"));
            return;
        }
        var add = event.target.closest("[data-extra-add]");
        if (add) {
            var template = $("[data-extra-template]", dialogBody);
            var index = parseInt(add.getAttribute("data-next-index"), 10) || 0;
            add.setAttribute("data-next-index", String(index + 1));
            var html = template.innerHTML.replace(/__i__/g, String(index));
            // HTML del <template> generato dal server (Razor, già codificato); __i__ è solo un indice numerico.
            // nosemgrep: typescript.react.security.audit.react-unsanitized-method.react-unsanitized-method
            template.insertAdjacentHTML("beforebegin", html);
            add.parentNode.insertBefore(newTabButton(index), $('[data-tab="console"]', dialogBody) || add);
            var panel = $('[data-panel="extra-' + index + '"]', dialogBody);
            syncPanel(panel);
            selectTab("extra-" + index);
            var input = $("[data-label-input]", panel);
            if (input) { input.focus(); }
            return;
        }
        var remove = event.target.closest("[data-extra-delete]");
        if (remove) {
            var row = remove.closest("[data-extra-row]");
            var rowTab = tabFor(row);
            // Si segna da togliere al salvataggio senza toglierla dal modulo: gli indici Extras[n]
            // devono restare consecutivi, altrimenti il portale ignora le schede che seguono.
            $("[data-extra-remove]", row).value = "true";
            row.classList.add("is-removed");
            row.removeAttribute("data-panel");
            if (rowTab) { rowTab.parentNode.removeChild(rowTab); }
            selectTab("primary");
            return;
        }
        var reset = event.target.closest("[data-console-reset]");
        if (reset) {
            var consolePanel = reset.closest("[data-console-panel]");
            $$("input:not([type=hidden]):not([type=checkbox])", consolePanel).forEach(function (field) { field.value = ""; });
            $$("select", consolePanel).forEach(function (field) {
                field.selectedIndex = 0;
                field.classList.remove("is-overridden");
            });
            var hide = $("[data-console-hide]", consolePanel);
            if (hide && hide.checked) {
                hide.checked = false;
                hide.dispatchEvent(new Event("change", { bubbles: true }));
            }
            syncPanel(consolePanel);
            var consoleLabel = $("[data-tab-label]", tabFor(consolePanel));
            if (consoleLabel) { consoleLabel.textContent = "Console"; }
            toast("Console come in Hudu: salva per confermare.");
        }
    });

    document.addEventListener("input", function (event) {
        var input = event.target.closest("[data-label-input]");
        if (!input || !dialogBody || !dialogBody.contains(input)) { return; }
        var label = $("[data-tab-label]", tabFor(input.closest("[data-panel]")));
        if (label) { label.textContent = input.value.trim() || input.getAttribute("data-label-default") || "Nuova connessione"; }
    });

    document.addEventListener("change", function (event) {
        if (!dialogBody || !dialogBody.contains(event.target)) { return; }
        if (event.target.matches("[data-protocol]")) {
            syncPanel(event.target.closest("[data-panel]"));
        }
        if (event.target.matches("[data-advanced] select") && !/DatabaseKind$/.test(event.target.name)) {
            event.target.classList.toggle("is-overridden", event.target.value !== "");
        }
        if (event.target.matches("[data-console-hide]")) {
            var tab = tabFor(event.target.closest("[data-panel]"));
            if (tab) {
                tab.classList.toggle("is-muted", event.target.checked);
                $("use", tab).setAttribute("href", event.target.checked ? "#i-eye-off" : "#i-shield");
            }
        }
    });

    document.addEventListener("submit", async function (event) {
        var form = event.target;
        if (form.matches("[data-confirm]") && !window.confirm(form.getAttribute("data-confirm"))) {
            event.preventDefault();
            return;
        }
        if (form.matches("[data-bulk-form]")) {
            event.preventDefault();
            submitBulk(form);
            return;
        }
        if (!form.matches("[data-editor-form]")) { return; }
        event.preventDefault();
        var submit = $('button[type="submit"]', form);
        busy(submit, true);
        var assetId = $('input[name="AssetId"]', form).value;
        var inList = !!$('article[data-asset-id="' + assetId + '"] .chip-link');
        try {
            var response = await fetch(form.getAttribute("action") + (inList ? "&context=list" : ""), {
                method: "POST",
                credentials: "same-origin",
                headers: { "X-Requested-With": "fetch" },
                body: new FormData(form)
            });
            var html = await response.text();
            if (response.status === 422) {
                dialogBody.innerHTML = html;
                syncEditor();
                return;
            }
            if (!response.ok) { throw new Error("Salvataggio non riuscito (" + response.status + ")"); }
            $$('article[data-asset-id="' + assetId + '"]').forEach(function (article) {
                // Riga restituita dal portale stesso (vista Razor con codifica HTML); la CSP blocca comunque gli script inline.
                // nosemgrep: typescript.react.security.audit.react-unsanitized-method.react-unsanitized-method
                article.insertAdjacentHTML("afterend", html);
                var updated = article.nextElementSibling;
                article.remove();
                if (updated) { updated.classList.add("is-updated"); }
            });
            dialog.close();
            toast("Salvato", "ok");
        } catch (error) {
            toast(error.message, "error");
        } finally {
            busy(submit, false);
        }
    });

    async function submitBulk(form) {
        var submit = $('button[type="submit"]', form);
        busy(submit, true);
        try {
            var response = await fetch(form.getAttribute("action"), {
                method: "POST",
                credentials: "same-origin",
                headers: { "X-Requested-With": "fetch" },
                body: new FormData(form)
            });
            if (response.status === 422) {
                dialogBody.innerHTML = await response.text();
                return;
            }
            if (!response.ok) { throw new Error(await readError(response)); }
            var result = await response.json();
            dialog.close();
            clearSelection();
            toast(result.message, "ok");
            setTimeout(function () { window.location.reload(); }, 600);
        } catch (error) {
            toast(error.message, "error");
        } finally {
            busy(submit, false);
        }
    }

    // Modifica in blocco: scrivere un gateway sceglie "Imposta".
    document.addEventListener("input", function (event) {
        var input = event.target.closest("[data-bulk-gateway]");
        if (!input) { return; }
        var mode = $('select[name="GatewayMode"]', input.closest("form"));
        if (mode && input.value.trim()) { mode.value = "set"; }
    });

    // Rubrica RustDesk per tutti i campi gateway (editor e gateway del cliente): si carica al primo uso.
    document.addEventListener("focusin", function (event) {
        if (event.target.closest("[data-peer-input]")) { loadPeers(); }
    });

    // I campi gateway accettano "ID — nome": al cambio si lascia solo l'ID visibile per chiarezza.
    document.addEventListener("change", function (event) {
        var input = event.target.closest("[data-peer-input]");
        if (input && input.value.indexOf(" — ") > 0) {
            input.value = input.value.split(" — ")[0].trim();
        }
    });
})();
