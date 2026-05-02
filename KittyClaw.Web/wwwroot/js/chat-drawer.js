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
