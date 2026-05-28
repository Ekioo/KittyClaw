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
