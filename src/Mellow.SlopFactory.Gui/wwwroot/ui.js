(() => {
    let returnFocus = null;
    document.addEventListener("click", () => {
        if (document.activeElement instanceof HTMLElement) returnFocus = document.activeElement;
    }, true);
    document.addEventListener("keydown", event => {
        if ((event.key === "Enter" || event.key === " ") && document.activeElement instanceof HTMLElement) returnFocus = document.activeElement;
    }, true);
    new MutationObserver(mutations => {
        let dialogAdded = false;
        let dialogRemoved = false;
        for (const mutation of mutations) {
            dialogAdded ||= [...mutation.addedNodes].some(node => node instanceof Element && (node.matches('[role="dialog"]') || node.querySelector('[role="dialog"]')));
            dialogRemoved ||= [...mutation.removedNodes].some(node => node instanceof Element && (node.matches('[role="dialog"]') || node.querySelector('[role="dialog"]')));
        }
        if (dialogAdded) {
            const dialog = document.querySelector('[role="dialog"]');
            const target = dialog?.querySelector('button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), a[href]');
            if (target instanceof HTMLElement) target.focus();
        } else if (dialogRemoved && returnFocus?.isConnected) {
            returnFocus.focus();
        }
    }).observe(document.body, { childList: true, subtree: true });
})();
