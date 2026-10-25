(() => {
    window.kittyLocalTime = {
        scheduleInput(utc) {
            const date = utc ? new Date(utc) : new Date();
            if (!utc) { date.setDate(date.getDate() + 1); date.setHours(9, 0, 0, 0); }
            const pad = n => String(n).padStart(2, "0");
            return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
        },
        inputToUtc(local) { return new Date(local).toISOString(); }
    };
    const selector = "time[data-local-date-time], [data-local-schedule]";
    const formatters = new Map();

    function formatterFor(locale, mode) {
        const key = `${locale}:${mode}`;
        if (!formatters.has(key)) {
            formatters.set(key, new Intl.DateTimeFormat(locale, {
                ...(mode === "time" ? { timeStyle: "medium" }
                    : mode === "date" ? { dateStyle: "short" }
                    : { dateStyle: "short", timeStyle: "short" })
            }));
        }
        return formatters.get(key);
    }

    function format(element) {
        if (!element.matches(selector)) return;
        const schedule = element.dataset.localSchedule;
        const raw = schedule || element.getAttribute("datetime");
        if (!raw) return;

        const value = new Date(raw);
        if (Number.isNaN(value.getTime())) return;

        const locale = element.dataset.localDateTimeLocale || document.documentElement.lang || undefined;
        // Omitting timeZone is intentional: Intl then uses the browser/system zone.
        const localized = formatterFor(locale, element.dataset.localDateTimeFormat).format(value);
        if (schedule) {
            const now = new Date();
            // Compare calendar dates, not elapsed hours, across daylight-saving changes.
            const day = date => Date.UTC(date.getFullYear(), date.getMonth(), date.getDate());
            const days = Math.round((day(value) - day(now)) / 86400000);
            const badge = days <= 0 ? "J" : `J-${days}`;
            if (element.textContent !== badge) element.textContent = badge;
            element.title = `${value.toLocaleString(locale, { timeZoneName: "long" })} \u2192 ${element.dataset.localScheduleTarget}`;
            return;
        }
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
        setInterval(() => document.querySelectorAll("[data-local-schedule]").forEach(format), 60000);
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
            attributeFilter: ["datetime", "data-local-date-time-locale", "data-local-date-time-format", "data-local-schedule", "data-local-schedule-target"],
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
