let _undoRedoDotNetRef = null;

function isEditableTarget(target) {
    if (!target) {
        return false;
    }

    return target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable;
}

function handleUndoRedoKeydown(event) {
    if (event.key === 'Escape') {
        _undoRedoDotNetRef?.invokeMethodAsync('OnEscapeShortcut');
        return;
    }

    if (isEditableTarget(event.target) || (!event.ctrlKey && !event.metaKey)) {
        return;
    }

    const key = event.key.toLowerCase();
    if (key === 'z' && !event.shiftKey) {
        event.preventDefault();
        _undoRedoDotNetRef?.invokeMethodAsync('OnUndoShortcut');
    } else if (key === 'y' || (key === 'z' && event.shiftKey)) {
        event.preventDefault();
        _undoRedoDotNetRef?.invokeMethodAsync('OnRedoShortcut');
    }
}

function registerUndoRedoShortcuts(dotNetRef) {
    _undoRedoDotNetRef = dotNetRef;
    document.addEventListener('keydown', handleUndoRedoKeydown);
}

function unregisterUndoRedoShortcuts() {
    document.removeEventListener('keydown', handleUndoRedoKeydown);
    _undoRedoDotNetRef = null;
}
