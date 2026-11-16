'use strict';

const mergeThemeRoot = document.documentElement;
const mergeMistCanvas = document.getElementById('theme-mist');
const mergeReduceMotion = matchMedia('(prefers-reduced-motion: reduce)');
const mergeForcedColors = matchMedia('(forced-colors: active)');
const mergeThemeDuration = 680;
const mergeThemePalettes = new Map();
let mergeTokenScopes;
let mergeThemeMotion = null;
let mergeThemeTarget = null;
let mergeSyntaxBridge = null;

function commitMergePalette(palette) {
    for (const [name, value] of Object.entries(palette)) {
        mergeThemeRoot.style.setProperty('--' + name, value);
    }
}

function applyMergeMonacoTheme(dark, palette) {
    const name = dark ? 'differ-merge-dark' : 'differ-merge-light';
    const builtin = require('vs/editor/standalone/common/themes');
    const base = dark ? builtin.vs_dark : builtin.vs;
    const colors = {
        ...base.colors,
        'editor.background': palette.background,
        'editor.foreground': palette.ink,
        'editorLineNumber.foreground': palette.muted,
        'editorLineNumber.activeForeground': palette.accent,
        'editorGutter.background': palette.background,
        'editor.selectionBackground': palette.selection,
        'editor.inactiveSelectionBackground': palette.selection,
        'editorCursor.foreground': palette.accent,
        'editorWidget.background': palette.header,
        'editorWidget.border': palette.border
    };
    const signature = JSON.stringify(colors);
    if (mergeThemePalettes.get(name) !== signature) {
        monaco.editor.defineTheme(name, {
            base: dark ? 'vs-dark' : 'vs',
            inherit: false,
            rules: mergeTokenRules(builtin, dark, palette),
            colors
        });
        mergeThemePalettes.set(name, signature);
    }
    monaco.editor.setTheme(name);
    mergeThemeRoot.style.colorScheme = dark ? 'dark' : 'light';
}

// A shared rule order and a unique colour per (light, dark) pair keep Monaco's
// .mtkN classes stable when a theme change causes visible spans to be repainted.
function mergeTokenRules(builtin, dark, palette) {
    mergeTokenScopes ??= buildMergeTokenScopes(builtin);
    const foreground = palette.ink.slice(1, 7).toUpperCase();
    const used = new Set([foreground, palette.background.slice(1, 7).toUpperCase()]);
    const pairColors = new Map();
    return mergeTokenScopes.map(({ scope, light, night, style }) => {
        const rule = { token: scope };
        if (light || night) {
            const key = light + '|' + night;
            if (!pairColors.has(key)) {
                const color = (dark ? night : light) || foreground;
                pairColors.set(key, uniqueMergeTokenColor(color.toUpperCase(), used));
            }
            rule.foreground = pairColors.get(key);
        }
        if (style !== null) rule.fontStyle = style;
        return rule;
    });
}

function buildMergeTokenScopes(builtin) {
    const light = new Map(builtin.vs.rules.map(rule => [rule.token, rule]));
    const dark = new Map(builtin.vs_dark.rules.map(rule => [rule.token, rule]));
    const scopes = [...new Set([...light.keys(), ...dark.keys()])].filter(Boolean).sort();
    return scopes.map(scope => ({
        scope,
        light: mergeBuiltinRule(light, scope, 'foreground'),
        night: mergeBuiltinRule(dark, scope, 'foreground'),
        style: mergeBuiltinRule(light, scope, 'fontStyle')
    }));
}

function mergeBuiltinRule(rules, scope, property) {
    for (let name = scope; name; name = name.slice(0, Math.max(0, name.lastIndexOf('.')))) {
        const value = rules.get(name)?.[property];
        if (value !== undefined) return value;
    }
    return null;
}

function uniqueMergeTokenColor(hex, used) {
    const value = parseInt(hex, 16);
    for (let step = 0; ; step++) {
        const delta = step === 0 ? 0 : (step % 2 ? 1 : -1) * Math.ceil(step / 2);
        const blue = (value & 255) + delta;
        if (blue < 0 || blue > 255) continue;
        const candidate = ((value & 0xFFFF00) | blue).toString(16).padStart(6, '0').toUpperCase();
        if (!used.has(candidate)) {
            used.add(candidate);
            return candidate;
        }
    }
}

function roundMergeThemeByte(value) {
    const bounded = Math.max(0, Math.min(255, value));
    const lower = Math.floor(bounded);
    return bounded - lower === .5 ? lower + lower % 2 : Math.round(bounded);
}

function parseMergeThemeColor(value) {
    const text = value.trim().toLowerCase();
    if (text === 'transparent') return [0, 0, 0, 0];
    const hex = /^#([\da-f]{3,4}|[\da-f]{6}|[\da-f]{8})$/.exec(text);
    if (hex) {
        const full = hex[1].length <= 4 ? [...hex[1]].map(character => character + character).join('') : hex[1];
        return [0, 2, 4].map(index => parseInt(full.slice(index, index + 2), 16))
            .concat(full.length === 8 ? parseInt(full.slice(6, 8), 16) : 255);
    }
    const rgb = /^rgba?\((.+)\)$/.exec(text);
    const srgb = /^color\(srgb\s+(.+)\)$/.exec(text);
    if (!rgb && !srgb) return null;
    const components = (rgb || srgb)[1].trim().split(/[\s,/]+/);
    if (components.length < 3 || components.length > 4 || components.some(component =>
        !/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?%?$/.test(component) || !Number.isFinite(parseFloat(component)))) return null;
    const channel = (component, scale) => roundMergeThemeByte(component.endsWith('%')
        ? parseFloat(component) / 100 * 255 : parseFloat(component) * scale);
    return components.slice(0, 3).map(component => channel(component, srgb ? 255 : 1))
        .concat(components.length === 4 ? channel(components[3], 255) : 255);
}

function interpolateMergeThemeColor(from, to, amount) {
    const alpha = from[3] + (to[3] - from[3]) * amount;
    return [0, 1, 2].map(channel => alpha <= 0 ? to[channel] : roundMergeThemeByte(
        (from[channel] * from[3] * (1 - amount) + to[channel] * to[3] * amount) / alpha))
        .concat(roundMergeThemeByte(alpha));
}

function mergeThemeCss([r, g, b, a]) {
    return `rgba(${r}, ${g}, ${b}, ${a / 255})`;
}

function captureMergePalette(target) {
    const computed = getComputedStyle(mergeThemeRoot);
    const transitions = [];
    for (const [name, value] of Object.entries(target)) {
        const from = parseMergeThemeColor(computed.getPropertyValue('--' + name));
        const to = parseMergeThemeColor(value);
        if (!from || !to) return null;
        transitions.push({ name, from, to });
    }
    return transitions;
}

function readMergeSyntaxColors() {
    const colors = new Map();
    for (const style of document.querySelectorAll('style.monaco-colors')) {
        for (const rule of style.sheet?.cssRules || []) {
            const token = /^\.(mtk\d+)$/.exec(rule.selectorText || '');
            if (!token) continue;
            const color = parseMergeThemeColor(rule.style.color);
            if (color) colors.set(token[1], color);
        }
    }
    return colors;
}

function captureMergeSyntaxColors() {
    const source = readMergeSyntaxColors();
    for (const bridge of mergeSyntaxBridge?.transitions || []) source.set(bridge.token, bridge.current);
    const foreground = parseMergeThemeColor(mergeThemeRoot.style.getPropertyValue('--ink'));
    if (foreground) source.set('mtk1', foreground);
    return source;
}

function prepareMergeSyntaxBridge(source) {
    mergeSyntaxBridge?.style.remove();
    const style = document.createElement('style');
    style.className = 'differ-merge-token-colors';
    document.head.append(style);
    const sheet = style.sheet;
    const transitions = [];
    for (const [token, to] of readMergeSyntaxColors()) {
        const from = source.get(token) || to;
        const index = sheet.insertRule(`.${token} { color: ${mergeThemeCss(from)}; }`, sheet.cssRules.length);
        transitions.push({ token, from, to, current: from, style: sheet.cssRules[index].style });
    }
    mergeSyntaxBridge = { style, transitions };
}

function applyMergeTheme({ dark, palette, mist }) {
    stopMergeThemeMotion(!mist);
    if (!mist) {
        applyMergeMonacoTheme(dark, palette);
        commitMergePalette(palette);
        return;
    }

    const { x, y, width, height, transitionId: id } = mist;
    const transitions = captureMergePalette(palette);
    const context = mergeMistCanvas.getContext('2d');
    if (!transitions || !context || document.hidden || mergeReduceMotion.matches || mergeForcedColors.matches ||
        ![x, y, width, height, id].every(Number.isFinite) || width <= 0 || height <= 0 ||
        !window.innerWidth || !window.innerHeight) {
        stopMergeThemeMotion(true);
        applyMergeMonacoTheme(dark, palette);
        commitMergePalette(palette);
        post({ type: 'themeTransitionUnavailable', id });
        return;
    }

    const source = captureMergeSyntaxColors();
    applyMergeMonacoTheme(dark, palette);
    prepareMergeSyntaxBridge(source);
    document.body.classList.add('theme-color-transition');
    mergeThemeTarget = palette;
    mergeThemeMotion = { id, dark, x, y, width, height, context, transitions, frame: null, timeout: null, startedAt: null };
    post({ type: 'themeTransitionReady', id });
}

// These colour stops and the time/radius functions match the WPF overlay.
const mergeMorningMist = [[0, '255 255 255', 80 / 255], [.40, '251 191 36', 57 / 255], [1, '0 0 0', 0]];
const mergeMoonMist = [[0, '186 230 253', 57 / 255], [.35, '56 189 248', 36 / 255],
    [.70, '30 58 138', 21 / 255], [1, '0 0 0', 0]];

function startMergeThemeMotion({ id, startedAt }) {
    const motion = mergeThemeMotion;
    if (!motion || motion.id !== id || motion.startedAt !== null || !Number.isFinite(startedAt)) return;
    if (document.hidden || mergeReduceMotion.matches || mergeForcedColors.matches) {
        invalidateMergeThemeMotion();
        return;
    }

    const width = window.innerWidth;
    const height = window.innerHeight;
    if (!width || !height) {
        invalidateMergeThemeMotion();
        return;
    }
    const pixelWidth = Math.ceil(width / 2);
    const pixelHeight = Math.ceil(height / 2);
    mergeMistCanvas.width = pixelWidth;
    mergeMistCanvas.height = pixelHeight;
    motion.context.setTransform(pixelWidth / width, 0, 0, pixelHeight / height, 0, 0);
    motion.startedAt = startedAt - performance.timeOrigin;
    motion.timeout = setTimeout(() => finishMergeThemeMotion(motion),
        Math.max(0, mergeThemeDuration - (performance.now() - motion.startedAt)));

    const originX = motion.x * width;
    const originY = motion.y * height;
    const distance = Math.hypot(motion.width * width, motion.height * height) * 1.35;
    const stops = motion.dark ? mergeMoonMist : mergeMorningMist;
    const render = () => {
        if (mergeThemeMotion !== motion) return;
        const elapsed = Math.max(0, performance.now() - motion.startedAt);
        if (elapsed >= mergeThemeDuration) {
            finishMergeThemeMotion(motion);
            return;
        }
        const progress = elapsed / mergeThemeDuration;
        const amount = progress < .5 ? 4 * progress ** 3 : 1 - (2 - 2 * progress) ** 3 / 2;
        const radius = Math.max(20, distance * (motion.dark ? 1 - amount : amount));
        const envelope = Math.sin(progress ** .88 * Math.PI);
        const gradient = motion.context.createRadialGradient(originX, originY, 0, originX, originY, radius);
        for (const [offset, rgb, alpha] of stops) gradient.addColorStop(offset, `rgb(${rgb} / ${alpha * envelope})`);
        motion.context.clearRect(0, 0, width, height);
        motion.context.fillStyle = gradient;
        motion.context.fillRect(0, 0, width, height);
        mergeMistCanvas.hidden = false;
        for (const { name, from, to } of motion.transitions) {
            mergeThemeRoot.style.setProperty('--' + name, mergeThemeCss(interpolateMergeThemeColor(from, to, amount)));
        }
        for (const syntax of mergeSyntaxBridge.transitions) {
            syntax.current = interpolateMergeThemeColor(syntax.from, syntax.to, amount);
            syntax.style.color = mergeThemeCss(syntax.current);
        }
        post({ type: 'themeTransitionFrame', id, elapsed });
        motion.frame = requestAnimationFrame(render);
    };
    render();
}

function finishMergeThemeMotion(motion) {
    if (mergeThemeMotion !== motion) return;
    stopMergeThemeMotion(true);
    post({ type: 'themeTransitionFrame', id: motion.id, elapsed: mergeThemeDuration });
}

function stopMergeThemeMotion(complete = true) {
    if (mergeThemeMotion) {
        cancelAnimationFrame(mergeThemeMotion.frame);
        clearTimeout(mergeThemeMotion.timeout);
        mergeThemeMotion = null;
    }
    mergeMistCanvas.hidden = true;
    if (!complete) return;
    if (mergeThemeTarget) commitMergePalette(mergeThemeTarget);
    mergeThemeTarget = null;
    mergeSyntaxBridge?.style.remove();
    mergeSyntaxBridge = null;
    document.body.classList.remove('theme-color-transition');
}

function invalidateMergeThemeMotion() {
    if (!mergeThemeMotion) return;
    const id = mergeThemeMotion.id;
    stopMergeThemeMotion(true);
    post({ type: 'themeTransitionInvalidated', id });
}

window.addEventListener('resize', invalidateMergeThemeMotion);
document.addEventListener('visibilitychange', () => {
    if (document.hidden) invalidateMergeThemeMotion();
});
mergeReduceMotion.addEventListener('change', invalidateMergeThemeMotion);
mergeForcedColors.addEventListener('change', invalidateMergeThemeMotion);
