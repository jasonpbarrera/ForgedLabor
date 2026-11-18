export function registerKeyHandler(dotNetHelper) {
    const handler = (e) => {
        if (e.key === 'Escape' || e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
            dotNetHelper.invokeMethodAsync('OnWindowKeyDown', e.key);
        }
    };
    window.addEventListener('keydown', handler);
    return {
        dispose: () => {
            window.removeEventListener('keydown', handler);
        }
    };
}
