export function readCollapsed() {
    try {
        const saved = localStorage.getItem('kittyclaw.sidebar.collapsed');
        if (saved !== null) return saved === 'true';
    } catch { /* Storage can be disabled by browser preferences. */ }
    return window.matchMedia('(max-width: 767px)').matches;
}
export function saveCollapsed(collapsed) {
    try { localStorage.setItem('kittyclaw.sidebar.collapsed', String(collapsed)); }
    catch { /* The sidebar remains usable without persistent storage. */ }
}

const resizers = new WeakMap();
export function initializeResize(sidebar) {
    disposeResize(sidebar);
    const handle = sidebar.querySelector('.sidebar-resize-handle');
    const desktop = window.matchMedia('(min-width: 768px)');
    const controller = new AbortController();
    const storageKey = 'kittyclaw.sidebar.width';
    let finishDrag = null;
    const setWidth = width => {
        const next = Math.round(Math.min(480, innerWidth * .45, Math.max(200, width)));
        document.documentElement.style.setProperty('--project-sidebar-width', `${next}px`);
        handle.setAttribute('aria-valuenow', String(next));
    };
    const persist = () => {
        try { localStorage.setItem(storageKey, document.documentElement.style.getPropertyValue('--project-sidebar-width')); }
        catch { /* Resizing remains available without browser storage. */ }
    };
    try {
        const saved = parseFloat(localStorage.getItem(storageKey));
        if (Number.isFinite(saved) && saved >= 200) setWidth(saved);
    } catch { /* Retain the default width. */ }
    handle.addEventListener('pointerdown', event => {
        if (!desktop.matches || sidebar.classList.contains('is-collapsed') || event.button !== 0) return;
        event.preventDefault();
        finishDrag?.();
        const startX = event.clientX;
        const startWidth = sidebar.getBoundingClientRect().width;
        const pointerId = event.pointerId;
        const cursor = document.body.style.cursor;
        const userSelect = document.body.style.userSelect;
        const drag = new AbortController();
        handle.setPointerCapture(pointerId);
        handle.dataset.resizing = '';
        document.body.style.cursor = 'ew-resize';
        document.body.style.userSelect = 'none';
        finishDrag = () => {
            drag.abort();
            if (handle.hasPointerCapture(pointerId)) handle.releasePointerCapture(pointerId);
            delete handle.dataset.resizing;
            document.body.style.cursor = cursor;
            document.body.style.userSelect = userSelect;
            persist();
            finishDrag = null;
        };
        handle.addEventListener('pointermove', e => {
            if (e.pointerId !== pointerId) return;
            if (!desktop.matches || !sidebar.isConnected || sidebar.classList.contains('is-collapsed')) { finishDrag?.(); return; }
            setWidth(startWidth + e.clientX - startX);
        }, { signal: drag.signal });
        for (const type of ['pointerup', 'pointercancel', 'lostpointercapture']) {
            handle.addEventListener(type, e => { if (e.pointerId === pointerId) finishDrag?.(); }, { signal: drag.signal });
        }
        window.addEventListener('blur', () => finishDrag?.(), { signal: drag.signal });
        desktop.addEventListener('change', () => finishDrag?.(), { signal: drag.signal });
    }, { signal: controller.signal });
    handle.addEventListener('dblclick', event => {
        event.preventDefault();
        document.documentElement.style.removeProperty('--project-sidebar-width');
        handle.setAttribute('aria-valuenow', '232');
        try { localStorage.removeItem(storageKey); } catch { /* Retain the default in this page. */ }
    }, { signal: controller.signal });
    handle.addEventListener('keydown', event => {
        if (!desktop.matches || !['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
        event.preventDefault();
        setWidth(sidebar.getBoundingClientRect().width + (event.key === 'ArrowRight' ? 24 : -24));
        persist();
    }, { signal: controller.signal });
    resizers.set(sidebar, () => { finishDrag?.(); controller.abort(); });
}
export function disposeResize(sidebar) {
    resizers.get(sidebar)?.();
    resizers.delete(sidebar);
}
