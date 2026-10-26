const _savedScrolls = {};

// Measure rendered menus, including destinations loaded after opening them.
window.fitBoardContextMenus = function () {
    const margin = 8;
    document.querySelectorAll('.column-context-menu').forEach(menu => {
        const rect = menu.getBoundingClientRect();
        const left = Math.max(margin, Math.min(rect.left, document.documentElement.clientWidth - rect.width - margin));
        const top = Math.max(margin, Math.min(rect.top, window.innerHeight - rect.height - margin));
        menu.style.left = `${left}px`;
        menu.style.top = `${top}px`;
    });
};
window.addEventListener('resize', window.fitBoardContextMenus);

window.saveColumnScrollPositions = function () {
    document.querySelectorAll('.column-body').forEach((el, i) => {
        _savedScrolls[i] = el.scrollTop;
    });
};

window.restoreColumnScrollPositions = function () {
    document.querySelectorAll('.column-body').forEach((el, i) => {
        if (_savedScrolls[i] !== undefined) el.scrollTop = _savedScrolls[i];
    });
};

// Ticket drawers are state inside the already-loaded board. Keep their deep-link URL in
// sync without asking Blazor's router to tear down and rebuild the whole board component.
window.boardReplaceUrl = function (url) {
    window.history.replaceState(window.history.state, "", url);
};

// Reveal a linked column inside the horizontal board without moving the page header.
window.focusBoardTicket = function (boardId, ticketId) {
    const board = document.getElementById(boardId);
    const card = board && board.querySelector(`[data-ticket-id="${Number(ticketId)}"]`);
    if (!card) return;
    card.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
    card.setAttribute('tabindex', '-1');
    card.focus({ preventScroll: true });
};

window.focusBoardColumn = function (boardId, columnId) {
    const board = document.getElementById(boardId);
    const column = board && [...board.children].find(child =>
        child.classList.contains('column') && child.dataset.columnId === String(columnId));
    if (!column) return;
    const inset = parseFloat(getComputedStyle(board).paddingLeft) || 0;
    const offset = column.getBoundingClientRect().left - board.getBoundingClientRect().left
        - board.clientLeft - inset;
    board.scrollTo({ left: board.scrollLeft + offset, behavior: 'instant' });
    column.focus({ preventScroll: true });
};

// Static assets are fingerprinted at publish time so repaired files bypass stale browser caches.
window.kittyClaw = window.kittyClaw || {};
window.kittyClaw.downloadBase64 = (fileName, contentType, base64) => {
    const link = document.createElement('a');
    link.href = `data:${contentType};base64,${base64}`;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
};

// Keep swipe feedback local so it follows native scrolling without a server round trip.
window.mobileColumnNavigation = (() => {
    const bindings = new WeakMap();
    function detach(nav) {
        bindings.get(nav)?.dispose();
        bindings.delete(nav);
    }
    function attach(nav, boardId) {
        const board = document.getElementById(boardId);
        if (!board) return;
        const previousBinding = bindings.get(nav);
        if (previousBinding?.board === board) {
            previousBinding.update();
            return;
        }
        detach(nav);
        const previous = nav.querySelector('.mobile-column-previous');
        const next = nav.querySelector('.mobile-column-next');
        const position = nav.querySelector('.mobile-column-position');
        let columns = [], current = 0;
        function update() {
            columns = [...board.children].filter(child => child.classList.contains('column'));
            nav.hidden = columns.length < 2;
            if (!columns.length || !board.clientWidth) return;
            const left = board.getBoundingClientRect().left;
            current = columns.reduce((closest, column, index) =>
                Math.abs(column.getBoundingClientRect().left - left) <
                Math.abs(columns[closest].getBoundingClientRect().left - left) ? index : closest, 0);
            const text = `${current + 1} / ${columns.length} · ${columns[current].dataset.columnStatus}`;
            if (position.textContent !== text) position.textContent = text;
            previous.disabled = current === 0;
            next.disabled = current === columns.length - 1;
        }
        function move(direction) {
            update();
            const target = columns[current + direction];
            if (target) board.scrollBy({
                left: target.getBoundingClientRect().left - board.getBoundingClientRect().left,
                behavior: 'instant'
            });
            update();
        }
        const back = () => move(-1), forward = () => move(1);
        previous.addEventListener('click', back);
        next.addEventListener('click', forward);
        board.addEventListener('scroll', update, { passive: true });
        const resize = new ResizeObserver(update);
        resize.observe(board);
        const mutations = new MutationObserver(update);
        mutations.observe(board, { childList: true });
        bindings.set(nav, { board, update, dispose() {
            previous.removeEventListener('click', back);
            next.removeEventListener('click', forward);
            board.removeEventListener('scroll', update);
            resize.disconnect();
            mutations.disconnect();
        }});
        update();
    }
    return { attach, detach };
})();
