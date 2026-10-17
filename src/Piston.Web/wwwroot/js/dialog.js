// Opens a <dialog> as a browser-native modal: the rest of the page becomes inert, so focus,
// clicks and assistive tech stay inside it. Blazor owns closing (it removes the element), so
// the native Esc cancel is suppressed and the component's own Esc handler runs instead.
window.pistonDialog = {
    showModal(dialog) {
        if (!dialog || dialog.open) return;
        dialog.addEventListener("cancel", e => e.preventDefault());
        dialog.showModal();
    }
};
