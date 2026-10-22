(() => {
    const selector = "time[data-local-date-time]";
    const formatters = new Map();

    function formatterFor(locale) {
        if (!formatters.has(locale)) {
            formatters.set(locale, new Intl.DateTimeFormat(locale, {
                dateStyle: "short",
                timeStyle: "short"
            }));
        }
        return formatters.get(locale);
    }

    function format(element) {
        const raw = element.getAttribute("datetime");
        if (!raw) return;

        const value = new Date(raw);
        if (Number.isNaN(value.getTime())) return;

        const locale = element.dataset.localDateTimeLocale || document.documentElement.lang || undefined;
        // Omitting timeZone is intentional: Intl then uses the browser/system zone.
        const localized = formatterFor(locale).format(value);
        if (element.textContent !== localized) element.textContent = localized;
        element.title = value.toLocaleString(locale, { timeZoneName: "long" });
    }

    function scan(root) {
        if (!(root instanceof Element)) return;
        if (root.matches(selector)) format(root);
        root.querySelectorAll(selector).forEach(format);
    }

    function start() {
        scan(document.body);
        new MutationObserver(mutations => {
            for (const mutation of mutations) {
                if (mutation.type === "attributes") {
                    format(mutation.target);
                    continue;
                }

                scan(mutation.target);
                mutation.addedNodes.forEach(node => scan(node));
            }
        }).observe(document.body, {
            attributes: true,
            attributeFilter: ["datetime", "data-local-date-time-locale"],
            childList: true,
            subtree: true
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", start, { once: true });
    } else {
        start();
    }
})();
