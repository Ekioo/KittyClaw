// Dashboard free-position drag-to-move + drag-to-resize.
// Tiles are absolutely positioned. JS moves them directly in the DOM for
// smooth 60 fps, then calls Blazor [JSInvokable] only on mouseup to persist.

const SNAP = 20; // grid cell size in px

let _dotnet = null;

window.dashboardInitDnd = (dotnetRef) => { _dotnet = dotnetRef; };

// ── Move ──────────────────────────────────────────────────────────────────────

let _drag = null;

window.dashboardStartDrag = (fileName, tileId, mouseX, mouseY, tileX, tileY) => {
    const tile = document.getElementById(tileId);
    if (!tile) return;
    tile.classList.add('tile-dragging');
    document.getElementById('dashboard-grid')?.classList.add('grid-active');
    _drag = { fileName, tile, mouseX, mouseY, tileX, tileY, lastX: tileX, lastY: tileY };
};

// ── Resize ────────────────────────────────────────────────────────────────────

let _resize = null;

window.dashboardStartResize = (fileName, tileId, mouseX, mouseY, tileW, tileH) => {
    const tile = document.getElementById(tileId);
    if (!tile) return;
    tile.classList.add('tile-resizing');
    document.getElementById('dashboard-grid')?.classList.add('grid-active');
    _resize = { fileName, tile, mouseX, mouseY, tileW, tileH, lastW: tileW, lastH: tileH };
};

// ── Shared mouse handlers ─────────────────────────────────────────────────────

document.addEventListener('mousemove', (e) => {
    if (_drag) {
        const { tile, mouseX, mouseY, tileX, tileY } = _drag;
        const newX = snap(Math.max(0, tileX + e.clientX - mouseX));
        const newY = snap(Math.max(0, tileY + e.clientY - mouseY));
        tile.style.left = newX + 'px';
        tile.style.top  = newY + 'px';
        _drag.lastX = newX;
        _drag.lastY = newY;
    }

    if (_resize) {
        const { tile, mouseX, mouseY, tileW, tileH } = _resize;
        const newW = snap(Math.max(SNAP * 5, tileW + e.clientX - mouseX));
        const newH = snap(Math.max(SNAP * 3, tileH + e.clientY - mouseY));
        tile.style.width  = newW + 'px';
        tile.style.height = newH + 'px';
        _resize.lastW = newW;
        _resize.lastH = newH;
    }
}, { passive: true });

document.addEventListener('mouseup', () => {
    const grid = document.getElementById('dashboard-grid');

    if (_drag) {
        const { fileName, tile, lastX, lastY } = _drag;
        tile.classList.remove('tile-dragging');
        _drag = null;
        if (!_resize) grid?.classList.remove('grid-active');
        _dotnet?.invokeMethodAsync('OnTileMoveEnd', fileName, lastX, lastY);
    }

    if (_resize) {
        const { fileName, tile, lastW, lastH } = _resize;
        tile.classList.remove('tile-resizing');
        _resize = null;
        if (!_drag) grid?.classList.remove('grid-active');
        _dotnet?.invokeMethodAsync('OnResizeEnd', fileName, lastW, lastH);
    }
});

// Prevent text selection while dragging
document.addEventListener('selectstart', (e) => {
    if (_drag || _resize) e.preventDefault();
});

function snap(v) { return Math.round(v / SNAP) * SNAP; }

window.tileChatScrollToBottom = (el) => {
    if (el) el.scrollTop = el.scrollHeight;
};

// ── Mermaid lazy loader ───────────────────────────────────────────────────────
// Loads mermaid.js from CDN on demand and re-renders any <pre class="mermaid">
// blocks the server emitted. A MutationObserver re-runs after Blazor SSR updates.

let _mermaidLoading = null;

function loadMermaid() {
    if (window.mermaid) return Promise.resolve(window.mermaid);
    if (_mermaidLoading) return _mermaidLoading;
    _mermaidLoading = new Promise((resolve, reject) => {
        const s = document.createElement('script');
        s.src = 'https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js';
        s.onload = () => {
            window.mermaid.initialize({ startOnLoad: false, theme: 'dark', securityLevel: 'strict' });
            resolve(window.mermaid);
        };
        s.onerror = reject;
        document.head.appendChild(s);
    });
    return _mermaidLoading;
}

async function renderPendingMermaid() {
    const nodes = document.querySelectorAll('pre.mermaid:not([data-rendered])');
    if (!nodes.length) return;
    const mermaid = await loadMermaid();
    for (const el of nodes) {
        const src = el.textContent.trim();
        el.setAttribute('data-rendered', '1');
        try {
            const id = 'm' + Math.random().toString(36).slice(2, 10);
            const { svg } = await mermaid.render(id, src);
            el.innerHTML = svg;
        } catch (e) {
            el.innerHTML = '<span class="tile-empty">mermaid error</span>';
        }
    }
}

// Re-scan whenever Blazor mutates the DOM (debounced).
let _mermaidTimer = null;
const _mermaidObserver = new MutationObserver(() => {
    clearTimeout(_mermaidTimer);
    _mermaidTimer = setTimeout(renderPendingMermaid, 50);
});
_mermaidObserver.observe(document.body, { childList: true, subtree: true });
document.addEventListener('DOMContentLoaded', renderPendingMermaid);
