const _savedScrolls = {};

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
