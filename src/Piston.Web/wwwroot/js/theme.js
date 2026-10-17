// Applies the saved theme preference ("system" | "light" | "dark") before first paint and
// follows the OS setting while the preference is "system".
(function () {
    const key = 'piston-theme';
    const media = window.matchMedia('(prefers-color-scheme: dark)');

    const get = () => {
        try { return localStorage.getItem(key) || 'system'; } catch { return 'system'; }
    };

    const apply = (pref) => {
        const dark = pref === 'dark' || (pref !== 'light' && media.matches);
        document.documentElement.classList.toggle('dark', dark);
    };

    window.pistonTheme = {
        get,
        set(pref) {
            try { localStorage.setItem(key, pref); } catch { /* storage unavailable */ }
            apply(pref);
        },
    };

    media.addEventListener('change', () => apply(get()));
    apply(get());
})();
