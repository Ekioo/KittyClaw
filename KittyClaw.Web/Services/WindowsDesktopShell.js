(() => {
    if (window.top !== window) return;
    const send = command => window.chrome.webview.postMessage(`window:${command}`);
    let fallback;
    let activeHeader;
    const positionFilterHelp = () => {
        const popup = activeHeader?.querySelector('.filter-hint-popup');
        if (!popup) return;
        const field = popup.closest('.filter-input-wrapper').getBoundingClientRect();
        popup.style.left = `${Math.max(16, Math.min(field.left, document.documentElement.clientWidth - popup.offsetWidth - 16))}px`;
    };
    const updateScroll = () => {
        if (!activeHeader?.isConnected) return;
        const header = activeHeader;
        header.querySelector('.desktop-window-scroll--back').disabled = header.scrollLeft <= 1;
        header.querySelector('.desktop-window-scroll--next').disabled =
            header.scrollLeft + header.clientWidth >= header.scrollWidth - 1;
        positionFilterHelp();
    };
    const measureScroll = () => {
        if (!activeHeader?.isConnected) return;
        const header = activeHeader;
        const position = header.scrollLeft;
        header.classList.remove('desktop-window-header--overflow');
        const overflow = header.scrollWidth > header.clientWidth + 1;
        header.classList.toggle('desktop-window-header--overflow', overflow);
        header.scrollLeft = position;
        updateScroll();
    };
    const resize = new ResizeObserver(measureScroll);
    const decorate = header => {
        header.classList.add('desktop-window-header');
        if (!header.querySelector('.desktop-window-brand')) {
            const brand = document.createElement('a');
            brand.className = 'desktop-window-brand';
            brand.href = '/';
            brand.setAttribute('aria-label', 'KittyClaw');
            const logo = document.createElement('img');
            logo.src = '/KittyClaw-Logo-Horizontal.webp?v=posture07-rounded';
            logo.alt = 'KittyClaw';
            logo.draggable = false;
            brand.append(logo);
            header.prepend(brand);
        }
        if (header.querySelector('.desktop-window-controls')) return;
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
        header.append(controls);
        for (const [direction, label] of [[-1, 'Previous actions'], [1, 'More actions']]) {
            const button = document.createElement('button');
            button.type = 'button';
            button.className = `desktop-window-scroll desktop-window-scroll--${direction < 0 ? 'back' : 'next'}`;
            button.title = label;
            button.setAttribute('aria-label', label);
            button.innerHTML = `<svg viewBox="0 0 16 16" aria-hidden="true"><path d="${direction < 0 ? 'm10 4-4 4 4 4' : 'm6 4 4 4-4 4'}"/></svg>`;
            button.addEventListener('click', () => header.scrollBy({ left: direction * header.clientWidth / 2 }));
            header.append(button);
        }
        header.addEventListener('scroll', updateScroll, { passive: true });
        header.addEventListener('wheel', event => {
            if (header.scrollWidth <= header.clientWidth + 1) return;
            event.preventDefault();
            header.scrollLeft += event.deltaX || event.deltaY;
        }, { passive: false });
    };

    const install = () => {
        document.documentElement.classList.add('windows-desktop');
        const header = document.querySelector('.page-header, .costs-header');
        if (header) {
            fallback?.remove();
            decorate(header);
        } else if (!fallback?.isConnected) {
            // Keep window controls available while routes load and on pages without a header.
            fallback = document.createElement('header');
            fallback.className = 'desktop-window-fallback';
            decorate(fallback);
            document.body.prepend(fallback);
        }
        const current = header || fallback;
        if (activeHeader !== current) {
            resize.disconnect();
            activeHeader = current;
            resize.observe(current);
        }
        measureScroll();
    };
    document.addEventListener('DOMContentLoaded', () => {
        install();
        // Enhanced navigation can reset attributes without changing any body children.
        // Reapply the shell even when the home link points to the current page.
        window.Blazor?.addEventListener('enhancedload', install);
        new MutationObserver(install).observe(document.body, { childList: true, subtree: true });
    });
})();
