// Delegation supports static SSR pages added by Blazor enhanced navigation.
// Native showModal places the editor in the top layer, outside table clipping,
// and provides focus containment, an inert background and Escape dismissal.
const openers = new WeakMap();

document.addEventListener("click", event => {
    if (!(event.target instanceof Element)) return;
    const opener = event.target.closest("[data-dialog-open]");
    if (opener instanceof HTMLButtonElement && !opener.disabled) {
        const dialog = document.getElementById(opener.dataset.dialogOpen);
        if (!(dialog instanceof HTMLDialogElement) || dialog.open) return;
        openers.set(dialog, opener);
        dialog.showModal();
        dialog.querySelector("input:not([type=hidden]):not([disabled]), textarea, select")?.focus();
        return;
    }
    const closer = event.target.closest("[data-dialog-close]");
    const dialog = closer?.closest("dialog");
    if (dialog instanceof HTMLDialogElement) dialog.close();
});

// close does not bubble. Capture works for both Close and native Escape.
document.addEventListener("close", event => {
    const dialog = event.target;
    if (!(dialog instanceof HTMLDialogElement)) return;
    for (const password of dialog.querySelectorAll("input[type=password]")) password.value = "";
    const opener = openers.get(dialog);
    openers.delete(dialog);
    if (opener?.isConnected) opener.focus();
}, true);
