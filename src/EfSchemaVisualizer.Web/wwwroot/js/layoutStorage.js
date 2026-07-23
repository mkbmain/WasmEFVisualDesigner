const LAYOUT_KEY_PREFIX = 'efSchemaVisualizer.layout.';
const LAYOUT_INDEX_KEY = 'efSchemaVisualizer.layoutIndex';
const MAX_STORED_LAYOUTS = 25;

function saveDiagramLayout(key, json) {
    try {
        localStorage.setItem(LAYOUT_KEY_PREFIX + key, json);
        touchLayoutIndex(key);
    } catch (e) {
        console.warn('Failed to save diagram layout to localStorage', e);
    }
}

function loadDiagramLayout(key) {
    try {
        return localStorage.getItem(LAYOUT_KEY_PREFIX + key);
    } catch (e) {
        console.warn('Failed to load diagram layout from localStorage', e);
        return null;
    }
}

// Bounds how many distinct source snapshots' layouts accumulate in localStorage over a
// long editing session (every diagram gesture changes the source text, and therefore the
// hash key it's saved under) by evicting the least-recently-saved entry past the cap.
function touchLayoutIndex(key) {
    let index;
    try {
        index = JSON.parse(localStorage.getItem(LAYOUT_INDEX_KEY) ?? '[]');
    } catch {
        index = [];
    }

    index = index.filter(k => k !== key);
    index.push(key);

    while (index.length > MAX_STORED_LAYOUTS) {
        const evicted = index.shift();
        localStorage.removeItem(LAYOUT_KEY_PREFIX + evicted);
    }

    localStorage.setItem(LAYOUT_INDEX_KEY, JSON.stringify(index));
}
