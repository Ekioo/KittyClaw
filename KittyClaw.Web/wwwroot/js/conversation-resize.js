// Resize locally so dragging does not wait for a Blazor Server round trip.
(() => {
    const desktop = window.matchMedia('(min-width: 768px)');
    const storageKey = 'kc-conversation-width';
    const applyWidth = width => document.documentElement.style.setProperty('--conversation-width', `${width}px`);
    try {
        const savedWidth = Number(localStorage.getItem(storageKey));
        if (Number.isFinite(savedWidth) && savedWidth >= 360) applyWidth(savedWidth);
    } catch { /* Keep resizing available when browser storage is unavailable. */ }
    const persistWidth = () => {
        const width = document.documentElement.style.getPropertyValue('--conversation-width');
        if (!width) return;
        try { localStorage.setItem(storageKey, String(parseFloat(width))); }
        catch { /* The width still survives reopening within this page. */ }
    };
    const setWidth = width => {
        const next = Math.round(Math.min(window.innerWidth * .95, Math.max(360, width)));
        applyWidth(next);
    };

    document.addEventListener('pointerdown', event => {
        const handle = event.target.closest('.conversation-resize-handle');
        if (!handle || !desktop.matches || event.button !== 0) return;
        const drawer = handle.closest('.agent-conversation');
        if (!drawer) return;
        event.preventDefault();
        const startX = event.clientX;
        const startWidth = drawer.getBoundingClientRect().width;
        const pointerId = event.pointerId;
        const previousCursor = document.body.style.cursor;
        const previousSelect = document.body.style.userSelect;
        handle.setPointerCapture(pointerId);
        handle.dataset.resizing = '';
        document.body.style.cursor = 'ew-resize';
        document.body.style.userSelect = 'none';

        const move = e => {
            if (e.pointerId !== pointerId) return;
            if (!desktop.matches || !drawer.isConnected) { finish(); return; }
            setWidth(startWidth + startX - e.clientX);
        };
        const end = e => { if (e.pointerId === pointerId) finish(); };
        const finish = () => {
            persistWidth();
            handle.removeEventListener('pointermove', move);
            handle.removeEventListener('pointerup', end);
            handle.removeEventListener('pointercancel', end);
            handle.removeEventListener('lostpointercapture', end);
            window.removeEventListener('blur', finish);
            desktop.removeEventListener('change', finish);
            if (handle.hasPointerCapture(pointerId)) handle.releasePointerCapture(pointerId);
            delete handle.dataset.resizing;
            document.body.style.cursor = previousCursor;
            document.body.style.userSelect = previousSelect;
        };
        handle.addEventListener('pointermove', move);
        handle.addEventListener('pointerup', end);
        handle.addEventListener('pointercancel', end);
        handle.addEventListener('lostpointercapture', end);
        window.addEventListener('blur', finish);
        desktop.addEventListener('change', finish);
    });

    document.addEventListener('dblclick', event => {
        if (!desktop.matches || !event.target.closest('.conversation-resize-handle')) return;
        event.preventDefault();
        document.documentElement.style.removeProperty('--conversation-width');
        try { localStorage.removeItem(storageKey); }
        catch { /* The default width still applies for the current page. */ }
    });

    document.addEventListener('keydown', event => {
        if (!desktop.matches || !event.target.matches('.conversation-resize-handle')) return;
        if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
        const drawer = event.target.closest('.agent-conversation');
        if (!drawer) return;
        event.preventDefault();
        setWidth(drawer.getBoundingClientRect().width + (event.key === 'ArrowLeft' ? 24 : -24));
        persistWidth();
    });
})();
