// Collapse-state persistence for the unified multi-project board (/board).
// One localStorage key per project slug so each browser remembers which lanes
// were collapsed independently of any other client.
const UNIFIED_BOARD_PREFIX = 'unified-board-collapsed-';

window.unifiedBoardStorage = {
    // Returns { [slug]: bool } for every slug that HAS a stored preference — slugs with no
    // entry are omitted so the caller can apply its own default (paused lanes start collapsed).
    getCollapsed: function (slugs) {
        const result = {};
        (slugs || []).forEach(function (slug) {
            const stored = localStorage.getItem(UNIFIED_BOARD_PREFIX + slug);
            if (stored !== null) result[slug] = stored === '1';
        });
        return result;
    },

    setCollapsed: function (slug, collapsed) {
        try {
            localStorage.setItem(UNIFIED_BOARD_PREFIX + slug, collapsed ? '1' : '0');
        } catch {
            /* localStorage unavailable (private browsing, quota, ...) — collapse state
               simply won't persist across reloads; the in-memory state still works. */
        }
    }
};

// Native drag gestures can emit a click after dragend. The ticket card also owns
// the click that opens its project board, so intercept that one synthetic click
// before Blazor sees it. A genuine click always starts with a fresh pointerdown,
// which clears the suppression candidate.
(function installUnifiedBoardDragClickGuard() {
    let draggedTicketKey = null;
    let suppressTicketKey = null;

    function ticketKey(card) {
        if (!card) return null;
        return `${card.dataset.projectSlug || ''}:${card.dataset.ticketId || ''}`;
    }

    document.addEventListener('pointerdown', function () {
        suppressTicketKey = null;
    }, true);

    document.addEventListener('dragstart', function (event) {
        const card = event.target instanceof Element
            ? event.target.closest('[data-unified-ticket="true"]')
            : null;
        draggedTicketKey = ticketKey(card);
    }, true);

    document.addEventListener('dragend', function () {
        suppressTicketKey = draggedTicketKey;
        draggedTicketKey = null;
    }, true);

    document.addEventListener('click', function (event) {
        if (!suppressTicketKey || !(event.target instanceof Element)) return;
        const clickedCard = event.target.closest('[data-unified-ticket="true"]');
        if (ticketKey(clickedCard) !== suppressTicketKey) return;

        event.preventDefault();
        event.stopImmediatePropagation();
        suppressTicketKey = null;
    }, true);
})();
