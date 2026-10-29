// Applies the saved theme preference ("system" | "light" | "dark") before first paint and
// follows the OS setting while the preference is "system".
(function () {
    const key = 'piston-theme';
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    const canvas = new URLSearchParams(location.search).get('pistonCanvas') === '1';

    const get = () => {
        try { return localStorage.getItem(key) || 'system'; } catch { return 'system'; }
    };

    const apply = (pref) => {
        const mode = document.documentElement.getAttribute('data-color-mode');
        const dark = canvas && mode ? mode === 'dark' : pref === 'dark' || (pref !== 'light' && media.matches);
        document.documentElement.classList.toggle('dark', dark);
    };

    window.pistonTheme = {
        get,
        isCanvas: () => canvas,
        syncCanvas() { if (canvas) apply(get()); },
        set(pref) {
            if (canvas) return;
            try { localStorage.setItem(key, pref); } catch { /* storage unavailable */ }
            apply(pref);
        },
    };

    media.addEventListener('change', () => apply(get()));
    apply(get());
})();
