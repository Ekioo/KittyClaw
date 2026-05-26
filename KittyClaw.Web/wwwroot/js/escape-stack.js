(function () {
    const state = { dotNet: null, listening: false };
    const focusStack = [];

    function onKeyDown(e) {
        if (e.key !== 'Escape') return;
        if (e.isComposing) return;
        if (!state.dotNet) return;
        const target = focusStack.length > 0 ? focusStack[focusStack.length - 1] : null;
        try {
            const result = state.dotNet.invokeMethodAsync('HandleEscape');
            if (result && typeof result.then === 'function') {
                result.then(function (handled) {
                    if (handled && target && document.body.contains(target)) {
                        try { target.focus(); } catch (_) { /* ignore */ }
                    }
                }).catch(function () { /* circuit gone */ });
            }
        } catch (_) { /* circuit gone */ }
    }

    window.escapeStack = {
        init: function (dotNetRef) {
            state.dotNet = dotNetRef;
            if (!state.listening) {
                document.addEventListener('keydown', onKeyDown, true);
                state.listening = true;
            }
        },
        dispose: function () {
            state.dotNet = null;
        },
        pushFocus: function () {
            focusStack.push(document.activeElement);
        },
        popFocus: function () {
            focusStack.pop();
        }
    };
})();
