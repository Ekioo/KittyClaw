(() => {
    if (window.top !== window) return;
    const send = command => window.chrome.webview.postMessage(`window:${command}`);
    let toolbar;
    const install = () => {
        document.documentElement.classList.add('windows-desktop');
        document.documentElement.classList.toggle('desktop-standalone', !document.querySelector('.page-header'));
        if (toolbar?.isConnected) return;
        // Overlay window controls in the reserved right edge without moving Blazor-owned nodes.
        toolbar = document.createElement('header');
        toolbar.className = 'desktop-window-toolbar';
        const title = document.createElement('span');
        title.className = 'desktop-window-title';
        title.textContent = 'KittyClaw';
        toolbar.append(title);
        const back = document.createElement('button');
        back.type = 'button';
        back.className = 'desktop-window-return';
        const language = document.documentElement.lang || navigator.language;
        const label = language.startsWith('fr') ? 'Retour' : 'Back';
        back.textContent = `← ${label}`;
        back.title = `${label} (Alt+←)`;
        back.addEventListener('click', () => send('back'));
        toolbar.append(back);
        const controls = document.createElement('div');
        controls.className = 'desktop-window-controls';
        controls.setAttribute('role', 'group');
        controls.setAttribute('aria-label', 'Window controls');
        const items = [
            ['minimize', 'Minimize', '<path d="M1 6h10"/>'],
            ['maximize', 'Maximize or restore', '<rect x="1.5" y="1.5" width="9" height="9"/>'],
            ['close', 'Close to notification area', '<path d="m1 1 10 10M11 1 1 11"/>']
        ];
        for (const [command, label, icon] of items) {
            const button = document.createElement('button');
            button.type = 'button';
            button.title = label;
            button.setAttribute('aria-label', label);
            button.innerHTML = `<svg viewBox="0 0 12 12" aria-hidden="true">${icon}</svg>`;
            button.addEventListener('click', () => send(command));
            controls.append(button);
        }
        toolbar.append(controls);
        document.body.prepend(toolbar);
    };
    document.addEventListener('DOMContentLoaded', () => {
        const style = document.createElement('style');
        style.textContent = /* SHELL_STYLES */;
        document.head.append(style);
        install();
        window.Blazor?.addEventListener('enhancedload', install);
        new MutationObserver(install).observe(document.body, { childList: true, subtree: true });
    });
})();
