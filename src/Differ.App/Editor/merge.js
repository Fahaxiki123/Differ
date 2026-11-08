'use strict';
require.config({ paths: { vs: 'vs' } });
let editors = {}, models = {}, labels = {}, state = null, applying = false;
let version = 0, sequence = 0, inFlight = false, pendingText = null, queuedCommand = null, locked = false;
let flushRequests = [], activeSource = 'mine', readingScale = 1, decorationIds = {}, sourceZoneIds = {};
let rejectedDraft = false;
let showBase = false, preferenceSequence = 0, preferenceAcknowledgement = 0, preferenceWarning = '';
let sourceLayoutChanging = false, sourceLayoutGeneration = 0;
let zoneSignature = '', scrollSync = false, lastFocused = 'result';
let resultPointerDown = null;
const decisionCommands = new Set(['mine', 'base', 'theirs', 'confirm']);
const post = message => window.chrome.webview.postMessage(message);
const el = id => document.getElementById(id);
const t = key => labels[key] || key;

require(['vs/editor/editor.main'], function () {
    const common = {
        automaticLayout: true, fontFamily: "Consolas, 'Microsoft YaHei', monospace", fontSize: 15,
        lineHeight: 24, minimap: { enabled: false }, scrollBeyondLastLine: false, wordWrap: 'off',
        glyphMargin: false, lineNumbersMinChars: 4, renderLineHighlight: 'none',
        smoothScrolling: false, padding: { top: 8, bottom: 8 }, contextmenu: false,
        fixedOverflowWidgets: true, accessibilitySupport: 'auto', folding: true,
        stickyScroll: { enabled: false }, links: false, mouseWheelZoom: false,
        unicodeHighlight: { ambiguousCharacters: false, invisibleCharacters: false },
        scrollbar: { horizontalScrollbarSize: 10, verticalScrollbarSize: 12 }
    };
    for (const side of ['mine', 'base', 'theirs', 'result']) {
        models[side] = monaco.editor.createModel('', 'plaintext');
        models[side].setEOL(monaco.editor.EndOfLineSequence.LF);
        editors[side] = monaco.editor.create(el(side), { ...common, model: models[side], readOnly: side !== 'result' });
        decorationIds[side] = []; sourceZoneIds[side] = [];
        editors[side].onDidFocusEditorText(() => { lastFocused = side; });
        editors[side].onDidScrollChange(event => {
            if (scrollSync || sourceLayoutChanging || !isEditorVisible(side)) return;
            scrollSync = true;
            try {
                for (const other of Object.keys(editors)) {
                    if (other === side || !isEditorVisible(other)) continue;
                    if (event.scrollLeftChanged) editors[other].setScrollLeft(event.scrollLeft);
                    if (event.scrollTopChanged && side !== 'result' && other !== 'result') editors[other].setScrollTop(event.scrollTop);
                }
            } finally { scrollSync = false; }
        });
        editors[side].onMouseDown(event => {
            if (side === 'result') {
                const pointer = event.event.browserEvent;
                resultPointerDown = { x: pointer.clientX, y: pointer.clientY };
                return;
            }
            if (!state || locked || !event.target.position) return;
            const line = event.target.position.lineNumber;
            const block = state.blocks.find(b => b.conflict && line >= b[side + 'Start'] && line <= Math.max(b[side + 'Start'], b[side + 'End']));
            if (block) requestCommand('select', block.index);
        });
    }
    editors.result.onMouseUp(event => handleResultMouseUp(event));
    models.result.onDidChangeContent(() => {
        if (applying) return;
        pendingText = models.result.getValue();
        el('saveState').textContent = t('Pending');
        pump();
    });
    for (const id of ['previous', 'next', 'undo', 'redo', 'save', 'finish', 'theme', 'confirm'])
        el(id).addEventListener('click', event => {
            if (decisionCommands.has(id) && event.detail > 1) return;
            requestCommand(id);
        });
    for (const side of ['mine', 'base', 'theirs']) el(side + 'Choice').addEventListener('click', event => {
        if (event.detail > 1) return;
        requestCommand(side);
    });
    el('autoAdvance').addEventListener('click', event => { if (event.detail <= 1) requestCommand('toggleAdvance'); });
    el('find').addEventListener('click', () => {
        if (!isEditorVisible(lastFocused)) lastFocused = isEditorVisible(activeSource) ? activeSource : 'result';
        editors[lastFocused].focus(); editors[lastFocused].getAction('actions.find').run();
    });
    el('showBase').addEventListener('click', () => setShowBase(!showBase, true));
    el('zoomOut').addEventListener('click', () => setScale(readingScale - .1));
    el('zoomIn').addEventListener('click', () => setScale(readingScale + .1));
    for (const button of document.querySelectorAll('[data-source]')) button.addEventListener('click', () => showSource(button.dataset.source));
    document.addEventListener('keydown', event => {
        if (event.repeat && (event.target.closest?.('.decision') || event.target.closest?.('#autoAdvance'))) {
            event.preventDefault(); event.stopImmediatePropagation(); return;
        }
        let command = null;
        if (event.key === 'F7') command = 'previous';
        if (event.key === 'F8') command = 'next';
        if (event.key === 'F9') command = 'theme';
        if (event.ctrlKey && event.key.toLowerCase() === 's') command = 'save';
        if (event.ctrlKey && event.key.toLowerCase() === 'z') command = event.shiftKey ? 'redo' : 'undo';
        if (event.ctrlKey && event.key.toLowerCase() === 'y') command = 'redo';
        if (event.ctrlKey && ['+', '=', '-'].includes(event.key)) { event.preventDefault(); event.stopImmediatePropagation(); setScale(readingScale + (event.key === '-' ? -.1 : .1)); return; }
        if (command) {
            event.preventDefault(); event.stopImmediatePropagation();
            if (!event.repeat) requestCommand(command);
        }
    }, true);
    document.addEventListener('wheel', event => {
        if (!event.ctrlKey) return;
        event.preventDefault(); setScale(readingScale + (event.deltaY < 0 ? .1 : -.1));
    }, { passive: false, capture: true });
    el('splitter').addEventListener('pointerdown', event => {
        el('splitter').setPointerCapture(event.pointerId);
        const move = e => { const rect = el('workspace').getBoundingClientRect(); const height = Math.max(110, Math.min(rect.height - 180, e.clientY - rect.top)); el('sources').style.height = height + 'px'; };
        const end = () => { el('splitter').removeEventListener('pointermove', move); el('splitter').removeEventListener('pointerup', end); };
        el('splitter').addEventListener('pointermove', move); el('splitter').addEventListener('pointerup', end);
    });
    el('splitter').addEventListener('keydown', event => {
        if (!['ArrowUp', 'ArrowDown'].includes(event.key)) return;
        event.preventDefault();
        const current = el('sources').getBoundingClientRect().height;
        el('sources').style.height = Math.max(110, Math.min(el('workspace').clientHeight - 180, current + (event.key === 'ArrowDown' ? 20 : -20))) + 'px';
    });
    showSource('mine');
    post({ type: 'ready' });
});

function handleResultMouseUp(event) {
    const down = resultPointerDown;
    resultPointerDown = null;
    const pointer = event.event.browserEvent;
    const selection = editors.result.getSelection();
    if (!down || !state || locked || !event.target.position || !selection?.isEmpty()
        || pointer.button !== 0 || pointer.detail > 1 || Math.abs(pointer.clientX - down.x) > 3 || Math.abs(pointer.clientY - down.y) > 3) return;
    requestCommand('selectResult', -1, event.target.position);
}

function setScale(value) {
    readingScale = Math.max(.7, Math.min(2, Math.round(value * 10) / 10));
    for (const editor of Object.values(editors)) editor.updateOptions({ fontSize: 15 * readingScale, lineHeight: 24 * readingScale });
    el('zoom').textContent = Math.round(readingScale * 100) + '%';
}

function showSource(side) {
    if (side === 'base' && !showBase) side = 'mine';
    const previous = activeSource;
    const anchor = editors[previous] && isEditorVisible(previous)
        ? { top: editors[previous].getScrollTop(), left: editors[previous].getScrollLeft() } : null;
    activeSource = side;
    for (const pane of document.querySelectorAll('.source')) pane.classList.toggle('active', pane.dataset.pane === side);
    for (const button of document.querySelectorAll('[data-source]')) {
        button.classList.toggle('active', button.dataset.source === side);
        button.setAttribute('aria-selected', button.dataset.source === side ? 'true' : 'false');
    }
    if (editors[side]) {
        scrollSync = true;
        try {
            editors[side].layout();
            if (anchor && side !== previous) editors[side].setScrollPosition({ scrollTop: anchor.top, scrollLeft: anchor.left });
        } finally { scrollSync = false; }
    }
    if (lastFocused !== 'result' && !isEditorVisible(lastFocused)) lastFocused = side;
}

function isEditorVisible(side) {
    return side === 'result' || !!el(side).offsetParent && el(side).getBoundingClientRect().width > 0;
}

function setShowBase(value, persist = false) {
    if (persist && locked) return;
    const reference = isEditorVisible(activeSource) ? activeSource : 'mine';
    const anchor = editors[reference] ? { scrollTop: editors[reference].getScrollTop(), scrollLeft: editors[reference].getScrollLeft() } : null;
    const baseWasActive = activeSource === 'base';
    const sourceViews = Object.fromEntries(['mine', 'base', 'theirs'].map(side => [side, editors[side]?.saveViewState()]));
    const baseHadFocus = editors.base?.hasTextFocus();
    showBase = !!value;
    sourceLayoutChanging = true;
    const generation = ++sourceLayoutGeneration;
    document.body.classList.toggle('hide-base', !showBase);
    el('showBase').setAttribute('aria-pressed', String(showBase));
    if (!showBase && activeSource === 'base') activeSource = 'mine';
    if (!showBase && lastFocused === 'base') lastFocused = 'mine';
    showSource(activeSource);
    for (const side of ['mine', 'base', 'theirs']) {
        if (!editors[side] || !isEditorVisible(side)) continue;
        editors[side].layout();
        if (sourceViews[side]) editors[side].restoreViewState(sourceViews[side]);
        // The source view zones already share one alignment. Showing the base
        // adopts the visible source's reading position without moving that source.
        if (anchor && (side === 'base' || !showBase && baseWasActive && side === 'mine')) editors[side].setScrollPosition(anchor);
    }
    if (baseHadFocus && !showBase) editors.mine.focus();
    requestAnimationFrame(() => requestAnimationFrame(() => {
        if (generation === sourceLayoutGeneration) sourceLayoutChanging = false;
    }));
    if (persist) post({ type: 'showBase', value: showBase, sequence: ++preferenceSequence });
}

function renderWarnings() {
    const warnings = [...(state?.warnings || []), ...(preferenceWarning ? [preferenceWarning] : [])];
    el('warning').textContent = warnings.length ? t('Warnings') + ' ' + warnings.length : '';
    el('warning').title = warnings.join('\n');
    if (preferenceWarning) {
        el('notice').textContent = preferenceWarning;
        el('notice').title = preferenceWarning;
        el('notice').classList.add('error');
    } else if (state && el('notice').textContent === t('PreferencesWarning')) {
        const selected = state.blocks.find(b => b.index === state.selected && b.conflict);
        const hint = selected && !selected.canChoose ? t('ManualHint') : state.unresolved ? t('InitialHint') : t('ReadyHint');
        el('notice').textContent = state.error || hint;
        el('notice').title = state.error || hint;
        el('notice').classList.toggle('error', !!state.error);
    }
}

function requestCommand(command, index = -1, resultPosition = null) {
    if (!state || locked || queuedCommand || rejectedDraft) return;
    if (decisionCommands.has(command)) index = state.selected;
    queuedCommand = { command, index };
    if (command === 'selectResult') queuedCommand.resultPosition = resultPosition || editors.result.getPosition();
    locked = true;
    el('showBase').disabled = true;
    editors.result.updateOptions({ readOnly: true });
    pump();
}

// Exactly one edit message may be in flight. The next document uses the host's accepted version.
function pump() {
    if (inFlight || !state) return;
    if (pendingText !== null) {
        const text = pendingText; pendingText = null; inFlight = true;
        post({ type: 'change', sequence: ++sequence, version, text }); return;
    }
    if (rejectedDraft) {
        while (flushRequests.length) post({ type: 'flushFailed', id: flushRequests.shift() });
        queuedCommand = null; locked = false; el('showBase').disabled = false; editors.result.updateOptions({ readOnly: false });
        return;
    }
    while (flushRequests.length) post({ type: 'flushed', id: flushRequests.shift(), version });
    if (queuedCommand) {
        const command = queuedCommand; queuedCommand = null;
        if (command.command === 'selectResult') {
            command.offset = models.result.getOffsetAt(command.resultPosition);
            delete command.resultPosition;
        }
        post({ type: 'command', ...command, version });
    }
}

window.chrome.webview.addEventListener('message', event => {
    const message = event.data;
    if (message.type === 'load') {
        labels = message.labels;
        for (const node of document.querySelectorAll('[data-label]')) node.textContent = t(node.dataset.label);
        for (const side of ['mine', 'base', 'theirs']) {
            models[side].setValue(side === 'base' ? message.ancestor : message[side]);
            el(side + 'Name').textContent = message[side + 'Name'];
            el(side + 'Name').parentElement.title = message[side + 'Path'];
        }
        applying = true; models.result.setValue(message.result); applying = false;
        el('confirm').title = t('ConfirmHint'); el('finish').title = t('FinishHint');
        el('find').title = t('SearchHint'); el('splitter').title = t('Resize');
        el('showBase').title = t('ShowBaseHint');
        el('autoAdvance').title = t('AutoAdvanceHint');
        for (const [id, label] of [['previous', 'Previous'], ['next', 'Next']]) {
            el(id).title = t(label) + (id === 'previous' ? ' (F7)' : ' (F8)');
            el(id).setAttribute('aria-label', t(label));
        }
        preferenceWarning = message.preferenceWarning || '';
        setShowBase(message.showBase);
        const extension = message.minePath.split('.').pop().toLowerCase();
        const language = { cs: 'csharp', js: 'javascript', ts: 'typescript', json: 'json', xml: 'xml', lua: 'lua', py: 'python', md: 'markdown', yaml: 'yaml', yml: 'yaml', cpp: 'cpp', h: 'cpp', html: 'html', css: 'css', sql: 'sql', sh: 'shell' }[extension] || 'plaintext';
        for (const model of Object.values(models)) monaco.editor.setModelLanguage(model, language);
    } else if (message.type === 'state') {
        const wasEdit = message.acknowledgement > 0;
        if (wasEdit) { inFlight = false; rejectedDraft = !!message.error; }
        state = message; version = message.version;
        // Never replace more recent typing with a delayed acknowledgement.
        if (!rejectedDraft && pendingText === null && models.result.getValue() !== message.text) {
            const viewState = editors.result.saveViewState();
            applying = true; models.result.setValue(message.text); applying = false;
            editors.result.restoreViewState(viewState);
        }
        renderState(message);
        if (!wasEdit) {
            locked = flushRequests.length > 0;
            el('showBase').disabled = locked;
            editors.result.updateOptions({ readOnly: locked });
        }
        pump();
    } else if (message.type === 'preference') {
        preferenceAcknowledgement = Math.max(preferenceAcknowledgement, message.sequence);
        if (message.sequence === preferenceSequence) {
            preferenceWarning = message.warning || '';
            renderWarnings();
        }
    } else if (message.type === 'theme') {
        for (const [key, value] of Object.entries(message.palette)) document.documentElement.style.setProperty('--' + key, value);
        document.documentElement.style.colorScheme = message.dark ? 'dark' : 'light';
        monaco.editor.defineTheme('differ-merge', { base: message.dark ? 'vs-dark' : 'vs', inherit: true, rules: [], colors: {
            'editor.background': message.palette.background, 'editor.foreground': message.palette.ink,
            'editorLineNumber.foreground': message.palette.muted, 'editorGutter.background': message.palette.background,
            'editor.selectionBackground': message.palette.selection, 'editorCursor.foreground': message.palette.accent,
            'editorWidget.background': message.palette.header, 'editorWidget.border': message.palette.border
        } }); monaco.editor.setTheme('differ-merge');
    } else if (message.type === 'flush') {
        locked = true; el('showBase').disabled = true; editors.result.updateOptions({ readOnly: true }); flushRequests.push(message.id); pump();
    } else if (message.type === 'resume') {
        locked = false; el('showBase').disabled = false; editors.result.updateOptions({ readOnly: false });
    }
});

function renderState(message) {
    el('counter').textContent = message.status;
    el('saveState').textContent = message.dirty ? t('Unsaved') : t('Saved');
    el('outputPath').textContent = t('SavedTo') + message.outputPath;
    el('outputPath').title = message.outputPath;
    el('encoding').textContent = message.encoding;
    const selected = message.blocks.find(b => b.index === message.selected && b.conflict);
    const conflicts = message.blocks.filter(b => b.conflict);
    el('selectionStatus').textContent = selected ? t('CurrentConflictFormat').replace('{0}', conflicts.indexOf(selected) + 1).replace('{1}', conflicts.length).replace('{2}', selected.resolved ? t('Resolved') : t('Conflict')) : t('NoConflicts');
    el('selectionStatus').title = el('selectionStatus').textContent;
    el('autoAdvance').setAttribute('aria-pressed', String(!!message.autoAdvance));
    const hint = selected && !selected.canChoose ? t('ManualHint') : message.unresolved ? t('InitialHint') : t('ReadyHint');
    el('notice').textContent = message.error || hint; el('notice').title = message.error || hint;
    el('notice').classList.toggle('error', !!message.error);
    renderWarnings();
    el('previous').disabled = el('next').disabled = !conflicts.length;
    for (const side of ['mine', 'base', 'theirs']) {
        el(side + 'Choice').disabled = !selected || !selected.canChoose;
        el(side + 'Choice').classList.toggle('selected', !!selected && selected.choice.toLowerCase() === side);
    }
    el('confirm').disabled = !selected || selected.resolved;
    el('undo').disabled = !message.canUndo; el('redo').disabled = !message.canRedo;
    el('save').disabled = el('finish').disabled = message.unresolved !== 0 || rejectedDraft;
    updateZones(message.blocks);
    for (const side of Object.keys(editors)) {
        const decorations = message.blocks.flatMap(block => {
            const range = rangeFor(side, block);
            const current = block.index === message.selected;
            const empty = side === 'result' ? block.length === 0 : block[side + 'End'] < block[side + 'Start'];
            const fill = block.conflict && !block.resolved ? 'merge-conflict' : 'merge-resolved';
            const rail = current ? 'merge-gutter-current' : block.conflict && !block.resolved ? 'merge-gutter-conflict' : 'merge-gutter-resolved';
            const label = block.conflict ? block.resolved ? t('Resolved') : t('Conflict') : t('Automatic');
            // At most three decorations per block. End caps belong to the real
            // block endpoints, never to each row or a viewport clipping edge.
            const part = (start, end, edge) => ({
                range: new monaco.Range(start, 1, end, models[side].getLineMaxColumn(end)),
                options: { isWholeLine: true,
                    className: empty ? '' : fill + (current ? ' merge-current' : '') + ' ' + edge,
                    linesDecorationsClassName: 'merge-gutter ' + rail + ' ' + edge,
                    hoverMessage: { value: label }
                }
            });
            if (range.startLineNumber === range.endLineNumber)
                return [part(range.startLineNumber, range.endLineNumber, 'merge-start merge-end')];
            const parts = [part(range.startLineNumber, range.startLineNumber, 'merge-start')];
            if (range.endLineNumber - range.startLineNumber > 1)
                parts.push(part(range.startLineNumber + 1, range.endLineNumber - 1, 'merge-middle'));
            parts.push(part(range.endLineNumber, range.endLineNumber, 'merge-end'));
            return parts;
        });
        decorationIds[side] = editors[side].deltaDecorations(decorationIds[side], decorations);
    }
    if (message.navigate && selected) {
        scrollSync = true;
        try {
            const sources = ['mine', 'base', 'theirs'].filter(isEditorVisible);
            const anchor = sources.find(side => selected[side + 'End'] >= selected[side + 'Start']) || sources[0];
            if (anchor) {
                editors[anchor].revealRangeInCenter(rangeFor(anchor, selected));
                const position = { scrollTop: editors[anchor].getScrollTop(), scrollLeft: editors[anchor].getScrollLeft() };
                for (const side of sources) if (side !== anchor) editors[side].setScrollPosition(position);
            }
            if (message.revealResult !== false) editors.result.revealRangeInCenter(rangeFor('result', selected));
        } finally { scrollSync = false; }
    }
}

function rangeFor(side, block) {
    const model = models[side];
    if (side === 'result') {
        const start = model.getPositionAt(block.offset), end = model.getPositionAt(block.offset + block.length);
        const endLine = block.length > 0 && end.column === 1 && end.lineNumber > start.lineNumber ? end.lineNumber - 1 : end.lineNumber;
        return new monaco.Range(start.lineNumber, start.column, endLine, model.getLineMaxColumn(endLine));
    }
    const start = Math.max(1, Math.min(model.getLineCount(), block[side + 'Start']));
    const end = Math.max(start, Math.min(model.getLineCount(), block[side + 'End']));
    return new monaco.Range(start, 1, end, model.getLineMaxColumn(end));
}

function updateZones(blocks) {
    const signature = JSON.stringify(blocks.map(b => [b.baseStart, b.baseEnd, b.mineStart, b.mineEnd, b.theirsStart, b.theirsEnd]));
    if (signature === zoneSignature) return;
    zoneSignature = signature;
    for (const side of ['mine', 'base', 'theirs']) editors[side].changeViewZones(accessor => {
        for (const id of sourceZoneIds[side]) accessor.removeZone(id);
        sourceZoneIds[side] = [];
        for (const block of blocks) {
            const counts = ['mine', 'base', 'theirs'].map(s => Math.max(0, block[s + 'End'] - block[s + 'Start'] + 1));
            const count = Math.max(0, block[side + 'End'] - block[side + 'Start'] + 1);
            const missing = Math.max(...counts) - count;
            if (missing > 0) sourceZoneIds[side].push(accessor.addZone({ afterLineNumber: Math.max(0, block[side + 'End']), heightInLines: missing, domNode: document.createElement('div') }));
        }
    });
}
