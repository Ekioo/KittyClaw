(function () {
    const state = { dotNet: null, listening: false };

    function onKeyDown(e) {
        if (e.key !== 'Escape') return;
        if (e.isComposing) return;
        if (!state.dotNet) return;
        try {
            state.dotNet.invokeMethodAsync('HandleEscape');
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
        }
    };
})();
