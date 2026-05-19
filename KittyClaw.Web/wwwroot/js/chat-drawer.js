window.chatDrawerScrollToBottom = function (el) {
    if (el) el.scrollTop = el.scrollHeight;
};

// Block the default newline insertion when pressing Enter (without Shift) so the
// browser doesn't append "\n" after our Send() handler clears the textarea — that
// would re-fire oninput and restore the just-cleared text.
window.chatDrawerInstallEnterGuard = function (el) {
    if (!el || el.__enterGuardInstalled) return;
    el.__enterGuardInstalled = true;
    el.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' && !e.shiftKey) e.preventDefault();
    });
};

// Image paste support (#115). Watches the chat textarea for `paste` events carrying
// image clipboard items, validates them client-side, and bridges accepted images back
// to the Blazor component via JSInvokable callbacks. Plain-text pastes pass through
// unchanged because preventDefault() only fires when at least one image item is found.
window.chatDrawerInstallPasteHandler = function (el, dotnetRef) {
    if (!el || el.__pasteHandlerInstalled) return;
    el.__pasteHandlerInstalled = true;

    var ALLOWED = { 'image/jpeg': 1, 'image/png': 1, 'image/gif': 1, 'image/webp': 1 };
    var MAX_BYTES = 5 * 1024 * 1024; // 5 MB per image
    var MAX_IMAGES = 5;

    el.addEventListener('paste', function (e) {
        var cd = e.clipboardData;
        if (!cd || !cd.items) return;
        var imageItems = [];
        for (var i = 0; i < cd.items.length; i++) {
            var it = cd.items[i];
            if (it.kind === 'file' && it.type && it.type.indexOf('image/') === 0) imageItems.push(it);
        }
        if (imageItems.length === 0) return; // let plain-text paste work normally

        e.preventDefault();

        if (imageItems.length > MAX_IMAGES) {
            dotnetRef.invokeMethodAsync('OnImagePasteError', 'too many images pasted at once (max ' + MAX_IMAGES + ')');
            return;
        }

        imageItems.forEach(function (item) {
            var file = item.getAsFile();
            if (!file) return;
            if (!ALLOWED[file.type]) {
                dotnetRef.invokeMethodAsync('OnImagePasteError', 'unsupported image type: ' + file.type);
                return;
            }
            if (file.size > MAX_BYTES) {
                dotnetRef.invokeMethodAsync('OnImagePasteError', 'image too large (max 5 MB)');
                return;
            }
            var reader = new FileReader();
            reader.onload = function () {
                dotnetRef.invokeMethodAsync('OnImagePasted', {
                    dataUrl: reader.result,
                    mime: file.type,
                    name: file.name || 'pasted-image',
                    sizeBytes: file.size
                });
            };
            reader.onerror = function () {
                dotnetRef.invokeMethodAsync('OnImagePasteError', 'failed to read image');
            };
            reader.readAsDataURL(file);
        });
    });
};
