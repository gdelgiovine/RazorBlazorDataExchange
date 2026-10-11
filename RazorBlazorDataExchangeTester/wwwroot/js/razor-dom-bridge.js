(function (global) {
    "use strict";

    // Generic Razor DOM-command executor. The application decides which DOM
    // changes to make from C#; this file has no application-specific selectors.
    // The operations below intentionally exclude arbitrary HTML and script eval.
    function apply(command) {
        if (!command || typeof command.id !== "string") return false;

        const element = document.getElementById(command.id);
        if (!element) return false;

        switch (command.op) {
            case "setText":
                element.textContent = String(command.value ?? "");
                return true;
            case "setValue":
                if (!("value" in element)) return false;
                element.value = String(command.value ?? "");
                return true;
            case "setChecked":
                if (!("checked" in element)) return false;
                element.checked = command.value === true;
                return true;
            case "setVisible":
                element.hidden = command.value !== true;
                return true;
            case "setClass":
                if (typeof command.name !== "string" ||
                    !/^[a-zA-Z_][a-zA-Z0-9_-]*$/.test(command.name)) return false;
                element.classList.toggle(command.name, command.value === true);
                return true;
            case "setAttribute":
                if (typeof command.name !== "string") return false;
                // Avoid event handlers, style, URL and raw-HTML attributes.
                if (!(/^(title|role|alt|placeholder)$/.test(command.name) ||
                    /^(aria|data)-[a-z0-9_-]+$/.test(command.name))) return false;
                if (command.value == null) element.removeAttribute(command.name);
                else element.setAttribute(command.name, String(command.value));
                return true;
            default:
                return false;
        }
    }

    function connect(url, options) {
        const handlers = options || {};
        const source = new EventSource(url);
        let lastSequence = 0;

        source.addEventListener("ready", function () {
            lastSequence = 0; // New stream (including a reconnect).
            if (typeof handlers.onReady === "function") handlers.onReady();
        });

        source.addEventListener("dom", function (event) {
            try {
                const frame = JSON.parse(event.data);
                if (!frame || !Array.isArray(frame.commands) ||
                    !Number.isSafeInteger(frame.sequence) ||
                    frame.sequence <= lastSequence) return;

                lastSequence = frame.sequence;
                for (const command of frame.commands) apply(command);
                if (typeof handlers.onApplied === "function") handlers.onApplied(frame);
            } catch (error) {
                console.error("Invalid RazorDomBridge SSE command.", error);
            }
        });

        source.onerror = function () {
            if (typeof handlers.onError === "function") handlers.onError();
        };

        global.addEventListener("pagehide", function () { source.close(); }, { once: true });
        return source;
    }

    global.RazorDomBridge = Object.freeze({ connect: connect });
})(window);
