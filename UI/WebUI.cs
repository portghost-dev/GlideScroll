namespace GlideScroll.UI;

/// <summary>
/// Single-page HTML+CSS+JS that defines the GlideScroll settings UI.
/// All persistent state lives on the C# side; this UI is a thin renderer
/// communicating via window.chrome.webview.postMessage.
/// </summary>
internal static class WebUI
{
    public const string Html = """
<!DOCTYPE html>
<html lang="en" data-system-dark="true">
<head>
<meta charset="UTF-8">
<title>GlideScroll</title>
<style>
:root {
  /* Active palette, overwritten at runtime via JS based on appearance choice.
     Fallbacks match the Dark preset (our own composition). --accent-text is
     recomputed per palette from the accent's luminance. */
  --bg: #0B1626;
  --surface: #142136;
  --text: #DCE8F7;
  --text-muted: #8296B3;
  --accent: #3FA0FF;
  --accent-text: #FFFFFF;
  --border: #1E3049;
  /* Hover tint derives from the theme's TEXT color, not a fixed white
     overlay: text always contrasts with the background, so hover reads
     correctly on light themes (white-on-white was invisible) and dark ones
     alike. */
  --hover: color-mix(in srgb, var(--text) 9%, transparent);
  --danger: #F28974;
}

* { box-sizing: border-box; }
html, body {
  margin: 0; padding: 0;
  height: 100%;
  font-family: 'Segoe UI', -apple-system, sans-serif;
  font-size: 13px;
  background: var(--bg);
  color: var(--text);
  user-select: none;
  -webkit-user-select: none;
  overflow: hidden;
}

/* ----- frameless title bar ----- */
.titlebar {
  display: flex;
  align-items: center;
  height: 32px;
  background: var(--bg);
  flex-shrink: 0;
  position: relative;
  z-index: 10;
}
.titlebar-icon {
  width: 16px; height: 16px;
  margin: 0 8px 0 12px;
  flex-shrink: 0;
  background-image: url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='%23D4D4D4' stroke-width='1.5' stroke-linecap='round' stroke-linejoin='round'><rect x='8' y='6' width='8' height='14' rx='4'/><path d='M12 4v6'/><path d='M12 4l-2 2'/><path d='M12 4l2 2'/><path d='M12 14l-2 2'/><path d='M12 14l2 2'/></svg>");
  background-repeat: no-repeat;
  background-position: center;
  background-size: contain;
}
.titlebar-title {
  font-size: 12px;
  color: var(--text);
  font-weight: 500;
  letter-spacing: 0.2px;
  flex-shrink: 0;
  pointer-events: none;
}
.titlebar-drag {
  flex: 1;
  height: 100%;
  -webkit-app-region: drag;  /* belt-and-braces, primary drag uses mousedown */
}
.titlebar-buttons {
  display: flex;
  height: 100%;
  flex-shrink: 0;
}
.titlebar-btn {
  width: 46px;
  height: 32px;
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  color: var(--text);
  transition: background 0.08s linear;
  -webkit-app-region: no-drag;
}
.titlebar-btn:hover { background: color-mix(in srgb, var(--text) 10%, transparent); }
.titlebar-btn.close:hover { background: #E81123; color: #FFFFFF; }
.titlebar-btn svg { width: 10px; height: 10px; }
.titlebar-btn.close svg { width: 10px; height: 10px; }
body[data-maximized="true"] .titlebar-btn.maximize .glyph-max { display: none; }
body[data-maximized="false"] .titlebar-btn.maximize .glyph-restore { display: none; }

/* ----- resize edges (frameless) -----
   Only sides + bottom + bottom corners. Top-edge resize is dropped so the
   title bar buttons (close/min/max) stay fully clickable; users can still
   resize from corners + Win+arrow snap + drag-to-edge snap. */
.resize-edge {
  position: fixed;
  z-index: 1000;
}
.resize-s  { bottom: 0; left: 6px; right: 6px; height: 4px; cursor: ns-resize; }
.resize-w  { left: 0; top: 36px; bottom: 6px; width: 4px; cursor: ew-resize; }
.resize-e  { right: 0; top: 36px; bottom: 6px; width: 4px; cursor: ew-resize; }
.resize-sw { bottom: 0; left: 0; width: 6px; height: 6px; cursor: nesw-resize; }
.resize-se { bottom: 0; right: 0; width: 6px; height: 6px; cursor: nwse-resize; }
body[data-maximized="true"] .resize-edge { display: none; }

/* When maximized, the OS pads the window past the visible work area.
   Add inner padding to keep content from clipping. */
body[data-maximized="true"] { padding: 8px; }

/* ----- tab strip ----- */
.tabstrip {
  display: flex;
  align-items: stretch;
  background: var(--surface);
  border-bottom: 1px solid var(--border);
  height: 44px;
  flex-shrink: 0;
}
.tab {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 0 12px;
  cursor: pointer;
  color: var(--text-muted);
  border-bottom: 2px solid transparent;
  transition: color 0.1s, background 0.1s, border-color 0.1s;
}
.tab:hover { background: var(--hover); }
.tab.active {
  color: var(--text);
  background: var(--bg);
  border-bottom-color: var(--accent);
  font-weight: 500;
}
.tab .ti { width: 16px; height: 16px; }

/* Inline SVG icon system (replaces the Tabler webfont CDN dependency so the
   app works fully offline). Icons are <svg> elements with class="ti ti-NAME";
   the size flows from font-size via 1em width/height, and stroke color tracks
   currentColor so palette swaps don't need touching. */
.ti {
  display: inline-block;
  width: 1em;
  height: 1em;
  vertical-align: -0.125em;
  fill: none;
  stroke: currentColor;
  stroke-width: 2;
  stroke-linecap: round;
  stroke-linejoin: round;
  flex-shrink: 0;
}
.gear {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 48px;
  cursor: pointer;
  color: var(--text-muted);
  font-size: 18px;
  border-left: 1px solid var(--border);
}
.gear:hover { background: var(--hover); color: var(--text); }

/* ----- pages ----- */
body { display: flex; flex-direction: column; }
.page-host { flex: 1; min-height: 0; position: relative; }
.page {
  display: none;
  position: absolute;
  inset: 0;
  flex-direction: column;
}
.page.active { display: flex; }
.page-content {
  flex: 1;
  overflow-y: auto;
  min-height: 0;
  /* flex-column lets the Scrolling page pin its optimize-row to the bottom
     via margin-top: auto on .optimize-row. Other pages flow normally. */
  display: flex;
  flex-direction: column;
}
/* Root-cause fix for the "control squashed to zero height" bug: children of a
   flex column with overflow (like .segmented's overflow:hidden) get an
   automatic minimum size of 0, so when the page overflows they COMPRESS
   instead of scrolling out of view, and no amount of scrolling brings them
   back. Freeze every direct child at its natural height so overflow always
   goes to the page scrollbar. (margin-top:auto pinning still works.) */
.page-content > * { flex-shrink: 0; }

/* ----- scrollbars, themed to the active palette ----- */
/* WebView2 is Chromium, so every scroll surface (pages, app lists, dropdown
   panels) takes these. Track blends with the page, thumb rides as a rounded
   pill in the border color, accent on hover, and the arrow buttons are drawn
   as CSS border triangles so they can use theme variables too. */
::-webkit-scrollbar { width: 10px; height: 10px; }
::-webkit-scrollbar-track { background: var(--bg); }
::-webkit-scrollbar-corner { background: var(--bg); }
::-webkit-scrollbar-thumb {
  background: var(--border);
  border-radius: 5px;
  /* Inset the pill from the track: reads as a slim 4px floating bar with
     breathing room, not wedged against the window edge. */
  border: 3px solid var(--bg);
  min-height: 40px;
}
::-webkit-scrollbar-thumb:hover,
::-webkit-scrollbar-thumb:active { background: var(--accent); }
/* No arrow buttons. Nobody clicks scrollbar steppers in 2026; the bar alone
   is cleaner. (A themed chevron glyph was impossible in these pseudo-elements
   anyway, so the choice is triangles or nothing, and nothing wins.) */
::-webkit-scrollbar-button { display: none; width: 0; height: 0; }

/* ----- smoothing engine selector (Wheel tab) ----- */
.mode-selector { margin: 14px 20px 0; }
.mode-selector .section-label {
  font-size: 11px; font-weight: 600; color: var(--text-muted);
  letter-spacing: 0.6px; text-transform: uppercase; margin: 0 0 6px;
}
.mode-dropdown {
  position: relative;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 4px;
  cursor: pointer;
  user-select: none;
  transition: border-color 0.1s, background 0.1s;
}
.mode-dropdown:hover { border-color: var(--border); background: color-mix(in srgb, var(--text) 4%, transparent); }
.mode-dropdown[data-open="true"] { border-color: var(--accent); }
.mode-current, .mode-option {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 12px;
}
.mode-icon {
  font-size: 18px;
  color: var(--accent);
  width: 20px;
  text-align: center;
  flex-shrink: 0;
}
.mode-text { flex: 1; min-width: 0; }
.mode-title { font-size: 13px; color: var(--text); font-weight: 500; }
.mode-subtitle { font-size: 11px; color: var(--text-muted); margin-top: 1px; }
.mode-chevron {
  color: var(--text-muted);
  font-size: 14px;
  transition: transform 0.15s;
}
.mode-dropdown[data-open="true"] .mode-chevron { transform: rotate(180deg); }
.mode-options {
  position: absolute;
  top: calc(100% + 4px);
  left: 0; right: 0;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 4px;
  z-index: 100;
  box-shadow: 0 6px 18px rgba(0,0,0,0.5);
  overflow: hidden;
  display: none;
}
.mode-dropdown[data-open="true"] .mode-options { display: block; }
.mode-option { cursor: pointer; transition: background 0.08s; }
/* Slim variant: compact rows without icon/subtitle, used where a plain select
   would sit (Preferences). Options list scrolls (themed) if it outgrows the
   modal. */
.mode-dropdown.slim .mode-current,
.mode-dropdown.slim .mode-option { padding: 7px 10px; gap: 8px; }
.mode-dropdown.slim .mode-title { font-size: 12px; font-weight: 400; }
.mode-dropdown.slim .mode-option.active .mode-title { font-weight: 500; }
.mode-dropdown.slim .mode-options { max-height: 320px; overflow-y: auto; }
/* Appearance row: Reset button directly left of the dropdown. Reset acts on
   ONLY the currently selected theme. */
.appearance-controls { display: flex; gap: 8px; align-items: stretch; }
.appearance-controls .mode-dropdown { flex: 1; min-width: 0; }
#theme-reset {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 0 10px;
  flex-shrink: 0;
}
.mode-option + .mode-option { border-top: 1px solid var(--border); }
.mode-option:hover { background: var(--hover); }
/* Tint derives from the live accent variable, so it follows every theme
   (the old hardcoded rgba ignored the palette). */
.mode-option.active { background: color-mix(in srgb, var(--accent) 15%, transparent); }
.mode-option.active .mode-title { color: var(--accent); }
.mode-check { color: var(--accent); font-size: 14px; margin-left: auto; }

/* Parameter visibility per mode. Hooks let each mode hide irrelevant controls
   without restructuring the layout. Add classes to num-rows like
   .control-smoothness, .control-accel-window, .control-accel-boost to
   participate. Defaults to "all visible". */
/* LowLatency is deliberately a no-physics mode, only Scroll distance and
   Glide duration apply. Hide everything else. */
[data-mode="lowLatency"] .control-smoothness,
[data-mode="lowLatency"] .control-accel-window,
[data-mode="lowLatency"] .control-accel-boost { display: none; }
/* Flywheel is pure momentum physics, Smoothness has no useful interpretation. */
[data-mode="flywheel"] .control-smoothness { display: none; }
/* Silk (settle time is the knob) and Cadence (auto-adaptive) don't use Smoothness. */
[data-mode="silk"] .control-smoothness,
[data-mode="cadence"] .control-smoothness { display: none; }
/* Feather pan drives displacement directly, so the Smoothness low-pass does nothing. */
#pan-params[data-mode="feather"] .control-pan-smoothness { display: none; }

/* ----- master row (top of Wheel/Pan tabs) ----- */
.master-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 18px 20px 14px;
  border-bottom: 1px solid var(--border);
}
.master-title { font-size: 15px; font-weight: 500; color: var(--text); }
.master-desc { font-size: 12px; color: var(--text-muted); margin-top: 2px; }

.toggle-row { display: flex; align-items: center; gap: 12px; }
.toggle-state {
  font-size: 12px;
  font-weight: 600;
  min-width: 28px;
  text-align: right;
  color: var(--text-muted);
  letter-spacing: 0.5px;
}

/* toggle switch */
.toggle {
  width: 38px;
  height: 22px;
  background: var(--border);
  border-radius: 11px;
  position: relative;
  cursor: pointer;
  flex-shrink: 0;
  transition: background 0.15s;
}
.toggle::after {
  content: '';
  width: 18px; height: 18px;
  background: white;
  border-radius: 50%;
  position: absolute;
  top: 2px; left: 2px;
  transition: left 0.15s;
}
.toggle.on { background: var(--accent); }
.toggle.on::after { left: 18px; }

/* ----- form controls ----- */
select, input[type="text"] {
  font-family: inherit;
  font-size: 13px;
  padding: 6px 8px;
  background: var(--surface);
  color: var(--text);
  border: 1px solid var(--border);
  border-radius: 4px;
  outline: none;
}
select:focus, input:focus { border-color: var(--accent); }

/* hide native number spinners (Webkit/Edge + Firefox) */
input[type="number"] {
  font-family: inherit;
  font-size: 13px;
  padding: 4px 6px;
  background: var(--surface);
  color: var(--text);
  border: 1px solid var(--border);
  border-radius: 0;       /* corners square, sits between +/- buttons */
  outline: none;
  text-align: center;     /* requested: centered text */
  width: 60px;
  -moz-appearance: textfield;
  appearance: textfield;
}
input[type="number"]::-webkit-outer-spin-button,
input[type="number"]::-webkit-inner-spin-button {
  -webkit-appearance: none;
  margin: 0;
}
input[type="number"]:focus { border-color: var(--accent); }

/* custom +/- spinner buttons flanking the number input */
.spinner-group {
  display: inline-flex;
  align-items: stretch;
  height: 26px;
}
.spinner-group .spin {
  width: 22px;
  background: var(--surface);
  color: var(--text-muted);
  border: 1px solid var(--border);
  cursor: pointer;
  font-size: 14px;
  line-height: 1;
  padding: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  font-family: inherit;
  user-select: none;
}
.spinner-group .spin:hover {
  background: var(--hover);
  color: var(--text);
}
.spinner-group .spin:active {
  background: var(--accent);
  color: var(--accent-text);
}
.spinner-group .spin.spin-down { border-radius: 4px 0 0 4px; border-right: none; }
.spinner-group .spin.spin-up { border-radius: 0 4px 4px 0; border-left: none; }
.spinner-group input[type="number"] { width: 56px; height: 26px; }

/* ----- tuning grid (Wheel + Pan tabs) ----- */
.grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 18px 28px;
  padding: 14px 20px 18px;
}
.numerics { display: flex; flex-direction: column; gap: 10px; }
.num-row {
  display: grid;
  grid-template-columns: 1fr auto 24px;
  align-items: center;
  gap: 10px;
}
.num-row .label { color: var(--text); font-size: 13px; }
.num-row .unit { color: var(--text-muted); font-size: 11px; }

.toggles { display: flex; flex-direction: column; gap: 10px; }
.checkbox-row { display: flex; align-items: center; gap: 8px; cursor: pointer; }
.checkbox-row input[type="checkbox"] {
  width: 16px; height: 16px;
  accent-color: var(--accent);
  cursor: pointer;
  margin: 0;
}

/* ----- footer (pinned bottom of each page via flexbox) ----- */
.footer {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 10px 20px;
  border-top: 1px solid var(--border);
  background: var(--surface);
  font-size: 12px;
  color: var(--text-muted);
  flex-shrink: 0;
  /* Explicit height (not min-height) so all three tab footers are pixel-identical
     regardless of whether they contain a button or just text. 48px = 10+10
     padding + 28px content area, which fits the .subtle button (~28px tall:
     12px font * 1.15 line + 12 padding + 2 border) exactly without clipping
     and centers plain text spans within the same envelope. */
  height: 48px;
}
button {
  font-family: inherit;
  font-size: 12px;
  padding: 6px 12px;
  background: transparent;
  color: var(--text);
  border: 1px solid var(--border);
  border-radius: 4px;
  cursor: pointer;
  transition: background 0.1s;
}
button:hover { background: var(--hover); }
button.primary {
  background: var(--accent);
  color: var(--accent-text);
  border-color: var(--accent);
}
button.primary:hover { filter: brightness(1.1); }
button.subtle {
  background: var(--bg);
  border: 1px solid var(--border);
  color: var(--text-muted);
}
button.subtle:hover {
  color: var(--text);
  background: var(--surface);
  border-color: var(--border);
}

/* ----- Apps tab ----- */
.section-label {
  font-size: 11px;
  font-weight: 600;
  color: var(--text-muted);
  letter-spacing: 0.6px;
  margin: 0 20px 8px;
  text-transform: uppercase;
}
#page-apps .page-content > .section-label:first-child { margin-top: 18px; }

.segmented {
  display: flex;
  margin: 0 20px 10px;
  border: 1px solid var(--border);
  border-radius: 4px;
  overflow: hidden;
}
.segment {
  flex: 1;
  padding: 9px 12px;
  text-align: center;
  cursor: pointer;
  font-size: 13px;
  color: var(--text-muted);
  background: var(--surface);
  border-right: 1px solid var(--border);
  transition: background 0.1s;
}
.segment:last-child { border-right: none; }
.segment:hover { background: var(--hover); }
.segment.active {
  background: var(--accent);
  color: var(--accent-text);
  font-weight: 500;
}
.mode-desc {
  font-size: 12px;
  color: var(--text-muted);
  margin: 0 20px 14px;
  /* Reserve space for up to 2 lines so the list-header below stays anchored
     when switching between Exclude and Include modes (their text wraps
     differently depending on width). */
  min-height: 34px;
  line-height: 1.5;
}

.list-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 0 20px;
  margin-bottom: 8px;
}
.list-title { font-size: 13px; font-weight: 500; color: var(--text); }
.list-title .count { color: var(--text-muted); font-weight: 400; }
.list-hint { font-size: 11px; color: var(--text-muted); }

.app-list {
  margin: 0 20px;
  border: 1px solid var(--border);
  border-radius: 4px;
  overflow: hidden;
  max-height: 280px;
  overflow-y: auto;
}
.app-item {
  display: flex;
  align-items: center;
  padding: 10px 12px;
  border-bottom: 1px solid var(--border);
  cursor: pointer;
  transition: background 0.1s;
}
.app-item:last-child { border-bottom: none; }
.app-item:hover { background: var(--hover); }
.app-item i { font-size: 16px; color: var(--text-muted); margin-right: 10px; }
.app-item .info { flex: 1; min-width: 0; }
.app-item .name {
  font-size: 13px; color: var(--text);
  white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
}
.app-item .exe { font-size: 11px; color: var(--text-muted); }
.app-item .remove {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  flex-shrink: 0;
  color: var(--text-muted);
  cursor: pointer;
  border-radius: 4px;
  transition: color 0.1s, background 0.1s;
}
.app-item .remove i { font-size: 14px; line-height: 1; margin: 0; color: inherit; }
.app-item .remove:hover { color: var(--danger); background: var(--hover); }

.empty-list {
  padding: 28px 12px;
  text-align: center;
  color: var(--text-muted);
  font-size: 12px;
}

.add-buttons {
  display: flex;
  gap: 8px;
  padding: 12px 20px;
}
.add-buttons button {
  flex: 1;
  padding: 8px;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
}

/* ----- modal ----- */
.modal {
  position: fixed;
  inset: 0;
  background: rgba(0,0,0,0.5);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 100;
}
.modal.hidden { display: none; }
.modal-card {
  background: var(--bg);
  border: 1px solid var(--border);
  border-radius: 6px;
  width: min(460px, calc(100% - 40px));
  max-height: calc(100% - 40px);
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.modal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 10px 16px;
  background: var(--surface);
  border-bottom: 1px solid var(--border);
  font-size: 13px;
  color: var(--text);
}
.modal-header .title { display: flex; align-items: center; gap: 8px; }
.modal-header .close {
  cursor: pointer;
  font-size: 16px;
  width: 24px;
  height: 24px;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: 3px;
  color: var(--text-muted);
}
.modal-header .close:hover { background: var(--hover); color: var(--text); }
.modal-body { padding: 16px; overflow-y: auto; flex: 1; }
.modal-footer {
  padding: 10px 16px;
  border-top: 1px solid var(--border);
  background: var(--surface);
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}

/* ----- preferences (modal) ----- */
.pref-row {
  display: grid;
  grid-template-columns: 1fr 264px;
  align-items: center;
  margin-bottom: 10px;
}
.pref-row .label { font-size: 13px; color: var(--text); }
.pref-row select, .pref-row input { width: 100%; height: 30px; }
.pref-desc {
  font-size: 11px;
  color: var(--text-muted);
  margin: -4px 0 14px 0;
  line-height: 1.5;
}

.custom-panel {
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 4px;
  padding: 12px 14px;
  margin: 4px 0 16px;
}
/* The palette editor is ALWAYS visible now: every theme (preset or Custom
   slot) shows its six colors and accepts edits. Constant modal height comes
   free as a side effect. */
.custom-panel-title {
  font-size: 11px;
  font-weight: 600;
  color: var(--text-muted);
  letter-spacing: 0.6px;
  text-transform: uppercase;
  margin-bottom: 8px;
}

.color-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px 16px;
}
.color-row {
  display: grid;
  grid-template-columns: 1fr 86px 28px;
  align-items: center;
  gap: 6px;
}
.color-row .label { font-size: 12px; color: var(--text-muted); }
.color-row input[type="text"] {
  height: 24px;
  font-family: 'Consolas', 'Courier New', monospace;
  font-size: 11px;
  text-align: center;
  padding: 2px 4px;
}
.swatch {
  width: 26px; height: 26px;
  border: 1px solid var(--border);
  border-radius: 3px;
  cursor: pointer;
  position: relative;
  overflow: hidden;
}
.swatch input[type="color"] {
  position: absolute;
  inset: -8px;
  width: 50px; height: 50px;
  opacity: 0;
  cursor: pointer;
  border: none;
  padding: 0;
}

.general-rows { display: flex; flex-direction: column; gap: 10px; margin-top: 4px; }

/* ----- supporter surface (Preferences SUPPORT section + honor dialog) ----- */
/* GENERAL sized to content, SUPPORT takes the remainder (spec v1.3 2.3).
   The nowrap on checkbox rows is load-bearing: if real Segoe metrics run
   wide, the auto column widens and SUPPORT yields instead of GENERAL
   wrapping. */
.prefs-pair {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 16px;
  align-items: start;
  margin-top: 0;
}
.prefs-pair .checkbox-row { white-space: nowrap; }
/* 1fr's implied min-width:auto lets a long unbreakable supporter name blow
   the SUPPORT cell past its column and crush GENERAL; pin the minimum so
   the ellipsis rules downstream can act. (No effect on the auto column,
   which sizes to its content contribution regardless.) */
.prefs-pair > * { min-width: 0; }
/* The window is user-resizable below the default 634px. Under ~530px the
   1fr track can no longer hold the panel's buttons; stack instead. The C#
   window minimum (580px) keeps this from firing in practice; insurance. */
@media (max-width: 530px) {
  .prefs-pair { grid-template-columns: 1fr; }
}
.support-panel {
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 4px;
  padding: 10px 14px;
  height: 132px;              /* FIXED, not min-height: both states measure this */
  overflow: hidden;           /* fixed height must fail gracefully, never spill */
  display: flex;
  flex-direction: column;
  gap: 6px;
}
/* Size for all panel icons; color only for the head's heart. The buy
   button's icon must inherit the button's own color or it renders
   accent-on-accent and disappears (carried fix from spec v1.0). */
.support-panel .ti { width: 15px; height: 15px; flex-shrink: 0; }
.support-head .ti { color: var(--accent); }
/* .ti sets fill:none + stroke; the filled heart inverts that. */
.ti-heart-filled { fill: currentColor; stroke: none; }
.support-head { display: flex; align-items: center; gap: 8px; }
.support-name { flex: 1; min-width: 0; font-size: 13px; color: var(--text);
                white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.support-sub  { font-size: 11px; color: var(--text-muted); line-height: 1.5;
                white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.support-actions { display: flex; flex-direction: column; gap: 6px; margin-top: auto; }
.support-actions button.primary {
  display: flex; align-items: center; gap: 6px; justify-content: center;
  padding: 6px 10px;
}
.support-actions button.primary .ti { width: 13px; height: 13px; }
.support-foot {
  display: flex; align-items: center; gap: 8px;
  margin-top: auto; padding-top: 8px;
  border-top: 1px solid var(--border);
}
.support-key {
  flex: 1; min-width: 0;
  font-family: 'Consolas', 'Courier New', monospace;
  font-size: 11px; color: var(--text-muted);
  white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
}
.support-edit {
  width: 24px; height: 24px; margin-right: -4px; flex-shrink: 0;
  display: flex; align-items: center; justify-content: center;
  border: none; background: transparent; border-radius: 3px;
  color: var(--text-muted); cursor: pointer;
}
.support-edit:hover { background: var(--hover); color: var(--text); }
.support-edit .ti { width: 14px; height: 14px; color: currentColor; }
/* Stacks above #prefs-modal (z 100), which stays open behind it. */
#supporter-modal { z-index: 110; }
.support-field-label  { font-size: 13px; color: var(--text); margin-bottom: 6px; }
.support-field-labels { display: flex; align-items: baseline; gap: 6px; margin-bottom: 6px; }
.support-field-labels .support-field-label { margin-bottom: 0; }
.support-field-opt    { font-size: 11px; color: var(--text-muted); }
.support-field input[type="text"] { width: 100%; height: 30px; }
.mode-option:not(.active) .mode-check { visibility: hidden; }

/* ----- picker modal ----- */
.picker-search { width: 100%; height: 30px; margin-bottom: 10px; }
.picker-list {
  max-height: 360px;
  overflow-y: auto;
  border: 1px solid var(--border);
  border-radius: 4px;
}
.picker-item {
  padding: 9px 12px;
  cursor: pointer;
  font-size: 13px;
  border-bottom: 1px solid var(--border);
  display: flex;
  align-items: center;
}
.picker-item:last-child { border-bottom: none; }
.picker-item:hover { background: var(--hover); }
.picker-item .name { flex: 1; color: var(--text); }
.picker-item .exe { font-size: 11px; color: var(--text-muted); }

/* ----- optimize-monitor button area ----- */
.optimize-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  /* margin-top: auto pins it to the bottom of .page-content's flex column,
     so showing/hiding parameter rows above doesn't shift it. */
  margin: auto 20px 12px;
  padding: 10px 12px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 4px;
}
.optimize-row .info .title {
  font-size: 12px;
  font-weight: 500;
  color: var(--text);
}
.optimize-row .info .desc {
  font-size: 11px;
  color: var(--text-muted);
  margin-top: 1px;
}
.optimize-row .info .diagonal-row {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 8px;
}
.optimize-row .info .diagonal-row .label { font-size: 12px; color: var(--text); }
.optimize-row .info .diagonal-row .unit { font-size: 11px; color: var(--text-muted); }

/* ----- hover tooltip popup -----
   Shown after 1 sec of hovering any tippable control. Positioned near the
   cursor, flips to fit within the viewport, hidden while user is typing in
   a number/text field (so it doesn't cover what they're editing). */
.tooltip-popup {
  position: fixed;
  z-index: 10000;
  max-width: 280px;
  padding: 8px 10px;
  background: var(--surface);
  color: var(--text);
  border: 1px solid var(--border);
  border-radius: 4px;
  font-size: 12px;
  line-height: 1.45;
  box-shadow: 0 4px 14px rgba(0, 0, 0, 0.5);
  pointer-events: none;
  opacity: 0;
  transition: opacity 0.12s ease-out;
}
.tooltip-popup.visible { opacity: 1; }

/* ----- App rules: advanced button row + class rules ----- */
/* Second button row sits directly under the first with no divider between,
   and uses the same text color as the primary buttons for a uniform look. */
.add-buttons-adv { padding-top: 0; }
/* Third row: Restore defaults alone, centered, half width. */
.add-buttons-center { justify-content: center; }
.add-buttons-center button { flex: 0 1 50%; }

/* ----- Game Mode modal ----- */
.gm-toggle-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 12px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 4px;
}
.gm-toggle-row .gm-toggle-label { font-size: 13px; color: var(--text); font-weight: 500; }
.gm-restore-row { text-align: center; margin-top: 10px; }
.gm-restore-row button {
  padding: 6px 14px;
  display: inline-flex;
  align-items: center;
  gap: 6px;
}
.default-pill {
  display: inline-block;
  margin-left: 8px;
  padding: 1px 7px;
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.3px;
  color: var(--accent-text);
  background: var(--accent);
  border-radius: 3px;
  vertical-align: 1px;
}

/* ----- Add-class modal ----- */
.modal-intro { font-size: 12px; color: var(--text-muted); line-height: 1.5; margin: 0 0 14px; }
.class-finder {
  border: 1.5px dashed var(--border);
  border-radius: 6px;
  padding: 18px 12px;
  text-align: center;
  cursor: grab;
  transition: border-color 0.1s, background 0.1s;
}
.class-finder:hover { border-color: var(--accent); background: var(--hover); }
.class-finder.capturing { border-color: var(--accent); background: var(--hover); cursor: grabbing; }
.class-finder .ti { font-size: 26px; color: var(--accent); }
.class-finder .cf-title { font-size: 13px; color: var(--text); font-weight: 500; margin-top: 6px; }
.class-finder .cf-sub { font-size: 11px; color: var(--text-muted); margin-top: 2px; }
.class-preview { font-size: 12px; color: var(--text-muted); text-align: center; min-height: 17px; margin: 9px 0 2px; }
.class-preview .cls { font-family: 'Consolas','Courier New',monospace; color: var(--text); }
.class-manual-label { font-size: 12px; color: var(--text-muted); margin: 12px 0 6px; }
.class-warn {
  margin-top: 10px;
  padding: 8px 10px;
  border-radius: 4px;
  font-size: 11px;
  line-height: 1.45;
  color: var(--danger);
  background: var(--surface);
  border: 1px solid var(--danger);
  display: flex;
  gap: 6px;
}
.class-warn.hidden { display: none; }
.class-warn .ti { flex-shrink: 0; font-size: 14px; margin-top: 1px; }

/* ----- Add running app: compact "point at a window" finder ----- */
.pick-window-row { margin-bottom: 10px; }
.pick-window {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 6px 10px;
  border: 1px solid var(--border);
  border-radius: 4px;
  cursor: grab;
  font-size: 12px;
  color: var(--text);
  background: var(--surface);
  user-select: none;
  transition: border-color 0.1s, background 0.1s;
}
.pick-window:hover { border-color: var(--accent); background: var(--hover); }
.pick-window.capturing { border-color: var(--accent); background: var(--hover); cursor: grabbing; }
.pick-window .ti { font-size: 15px; color: var(--accent); }

/* ----- Restore-defaults modal ----- */
.restore-opt {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: 6px;
  margin-bottom: 10px;
  cursor: pointer;
  transition: border-color 0.1s, background 0.1s, box-shadow 0.1s;
}
.restore-opt:hover { background: var(--hover); }
.restore-opt.selected { border-color: var(--accent); box-shadow: inset 0 0 0 1px var(--accent); }
.restore-opt.disabled { opacity: 0.4; cursor: default; pointer-events: none; }
.restore-opt .ti { font-size: 18px; color: var(--text-muted); margin-top: 1px; flex-shrink: 0; }
.restore-opt.selected .ti { color: var(--accent); }
.ro-title { font-size: 13px; color: var(--text); font-weight: 500; }
.ro-desc { font-size: 11px; color: var(--text-muted); margin-top: 2px; }

button:disabled { opacity: 0.45; cursor: default; }
button.primary:disabled:hover { filter: none; }
</style>
</head>
<body data-maximized="false">

<div class="titlebar" id="titlebar">
  <div class="titlebar-icon"></div>
  <div class="titlebar-title">GlideScroll</div>
  <div class="titlebar-drag" id="titlebar-drag"></div>
  <div class="titlebar-buttons">
    <div class="titlebar-btn minimize" id="btn-min" title="Minimize">
      <svg viewBox="0 0 10 10" xmlns="http://www.w3.org/2000/svg"><path d="M0 5h10" stroke="currentColor" stroke-width="1"/></svg>
    </div>
    <div class="titlebar-btn maximize" id="btn-max" title="Maximize">
      <svg class="glyph-max" viewBox="0 0 10 10" xmlns="http://www.w3.org/2000/svg"><rect x="0.5" y="0.5" width="9" height="9" fill="none" stroke="currentColor" stroke-width="1"/></svg>
      <svg class="glyph-restore" viewBox="0 0 10 10" xmlns="http://www.w3.org/2000/svg"><rect x="0.5" y="2.5" width="7" height="7" fill="none" stroke="currentColor" stroke-width="1"/><path d="M2.5 2.5V0.5h7v7h-2" fill="none" stroke="currentColor" stroke-width="1"/></svg>
    </div>
    <div class="titlebar-btn close" id="btn-close" title="Close">
      <svg viewBox="0 0 10 10" xmlns="http://www.w3.org/2000/svg"><path d="M0 0L10 10M10 0L0 10" stroke="currentColor" stroke-width="1"/></svg>
    </div>
  </div>
</div>

<div class="resize-edge resize-s"  data-edge="s"></div>
<div class="resize-edge resize-w"  data-edge="w"></div>
<div class="resize-edge resize-e"  data-edge="e"></div>
<div class="resize-edge resize-sw" data-edge="sw"></div>
<div class="resize-edge resize-se" data-edge="se"></div>

<div class="tabstrip">
  <div class="tab active" data-tab="wheel"><i class="ti ti-mouse"></i><span>Scrolling</span></div>
  <div class="tab" data-tab="pan"><i class="ti ti-hand-two-fingers"></i><span>Panning</span></div>
  <div class="tab" data-tab="apps"><i class="ti ti-apps"></i><span>App rules</span></div>
  <div class="gear" id="gear" title="Preferences"><i class="ti ti-settings"></i></div>
</div>

<div class="page-host">

<!-- WHEEL PAGE -->
<div class="page active" id="page-wheel">
  <div class="page-content">
    <div class="master-row">
      <div>
        <div class="master-title">Wheel smoothing</div>
        <div class="master-desc">Eases mouse wheel input into smooth scrolling.</div>
      </div>
      <div class="toggle-row">
        <span class="toggle-state">ON</span>
        <div class="toggle on" data-toggle="wheel.enabled"></div>
      </div>
    </div>

    <div class="mode-selector">
      <div class="section-label">Smoothing engine</div>
      <div class="mode-dropdown" id="mode-dropdown" data-key="wheel.smoothingMode" data-open="false">
        <div class="mode-current" id="mode-current">
          <i class="ti ti-wind mode-icon"></i>
          <div class="mode-text">
            <div class="mode-title">Silk</div>
            <div class="mode-subtitle">Buttery smooth, but settles fast with no lag tail.</div>
          </div>
          <i class="ti ti-chevron-down mode-chevron"></i>
        </div>
        <!-- Ordered most-smoothed to least-smoothed by feel. Silk ships as the
             default engine for fresh installs. -->
        <div class="mode-options" id="mode-options">
          <div class="mode-option" data-value="spring" data-icon="ti-droplet" data-title="Liquid Glide" data-subtitle="Glides smoothly with inertia and configurable bounce.">
            <i class="ti ti-droplet mode-icon"></i>
            <div class="mode-text">
              <div class="mode-title">Liquid Glide</div>
              <div class="mode-subtitle">Glides smoothly with inertia and configurable bounce.</div>
            </div>
            <i class="ti ti-check mode-check"></i>
          </div>
          <div class="mode-option active" data-value="silk" data-icon="ti-wind" data-title="Silk" data-subtitle="Buttery smooth, but settles fast with no lag tail.">
            <i class="ti ti-wind mode-icon"></i>
            <div class="mode-text">
              <div class="mode-title">Silk</div>
              <div class="mode-subtitle">Buttery smooth, but settles fast with no lag tail.</div>
            </div>
            <i class="ti ti-check mode-check"></i>
          </div>
          <div class="mode-option" data-value="cadence" data-icon="ti-gauge" data-title="Cadence" data-subtitle="Adapts as you scroll: crisp for reading, momentum on flicks.">
            <i class="ti ti-gauge mode-icon"></i>
            <div class="mode-text">
              <div class="mode-title">Cadence</div>
              <div class="mode-subtitle">Adapts as you scroll: crisp for reading, momentum on flicks.</div>
            </div>
            <i class="ti ti-check mode-check"></i>
          </div>
          <div class="mode-option" data-value="hybrid" data-icon="ti-wave-sine" data-title="Smoothed Notch" data-subtitle="Stock notched scrolling, but smoothed transition.">
            <i class="ti ti-wave-sine mode-icon"></i>
            <div class="mode-text">
              <div class="mode-title">Smoothed Notch</div>
              <div class="mode-subtitle">Stock notched scrolling, but smoothed transition.</div>
            </div>
            <i class="ti ti-check mode-check"></i>
          </div>
          <div class="mode-option" data-value="flywheel" data-icon="ti-zodiac-aquarius" data-title="Notch with Glide" data-subtitle="Stock notched scrolling, but with a mild glide.">
            <i class="ti ti-zodiac-aquarius mode-icon"></i>
            <div class="mode-text">
              <div class="mode-title">Notch with Glide</div>
              <div class="mode-subtitle">Stock notched scrolling, but with a mild glide.</div>
            </div>
            <i class="ti ti-check mode-check"></i>
          </div>
          <div class="mode-option" data-value="lowLatency" data-icon="ti-bolt" data-title="Low Latency" data-subtitle="Minimal smoothing per notch. Fast, Responsive, and Aggressive.">
            <i class="ti ti-bolt mode-icon"></i>
            <div class="mode-text">
              <div class="mode-title">Low Latency</div>
              <div class="mode-subtitle">Minimal smoothing per notch. Fast, Responsive, and Aggressive.</div>
            </div>
            <i class="ti ti-check mode-check"></i>
          </div>
        </div>
      </div>
    </div>

    <div class="grid" id="wheel-params" data-mode="spring">
      <div class="numerics">
        <div class="num-row control-step-size"><span class="label">Scroll distance</span><div class="spinner-group"><button class="spin spin-down" data-spin="-1" data-target="wheel.stepSize" tabindex="-1">−</button><input type="number" data-key="wheel.stepSize" min="10" max="1000"><button class="spin spin-up" data-spin="1" data-target="wheel.stepSize" tabindex="-1">+</button></div><span class="unit">px</span></div>
        <div class="num-row control-glide-duration"><span class="label">Glide duration</span><div class="spinner-group"><button class="spin spin-down" data-spin="-10" data-target="wheel.animationTimeMs" tabindex="-1">−</button><input type="number" data-key="wheel.animationTimeMs" min="50" max="2000"><button class="spin spin-up" data-spin="10" data-target="wheel.animationTimeMs" tabindex="-1">+</button></div><span class="unit">ms</span></div>
        <div class="num-row control-smoothness"><span class="label" id="feel-bias-label">Smoothness</span><div class="spinner-group"><button class="spin spin-down" data-spin="-1" data-target="wheel.feelBias" tabindex="-1">−</button><input type="number" data-key="wheel.feelBias" min="-10" max="10" step="1"><button class="spin spin-up" data-spin="1" data-target="wheel.feelBias" tabindex="-1">+</button></div><span class="unit"></span></div>
        <div class="num-row control-accel-window"><span class="label">Acceleration window</span><div class="spinner-group"><button class="spin spin-down" data-spin="-5" data-target="wheel.accelWindowMs" tabindex="-1">−</button><input type="number" data-key="wheel.accelWindowMs" min="0" max="1000"><button class="spin spin-up" data-spin="5" data-target="wheel.accelWindowMs" tabindex="-1">+</button></div><span class="unit">ms</span></div>
        <div class="num-row control-accel-boost"><span class="label">Acceleration boost</span><div class="spinner-group"><button class="spin spin-down" data-spin="-1" data-target="wheel.accelBoost" tabindex="-1">−</button><input type="number" data-key="wheel.accelBoost" min="1" max="20" step="0.5"><button class="spin spin-up" data-spin="1" data-target="wheel.accelBoost" tabindex="-1">+</button></div><span class="unit">×</span></div>
      </div>
      <div class="toggles">
        <label class="checkbox-row"><input type="checkbox" data-key="wheel.shiftWheelHorizontal">Shift+wheel = horizontal</label>
        <label class="checkbox-row"><input type="checkbox" data-key="wheel.smoothHorizontal">Smooth horizontal too</label>
        <label class="checkbox-row"><input type="checkbox" data-key="wheel.reverseDirection">Reverse direction</label>
      </div>
    </div>

    <div class="optimize-row" data-tip-id="action:optimize-monitor">
      <div class="info">
        <div class="title">Optimize for monitor</div>
        <div class="desc" id="optimize-desc">Detects refresh rate + resolution and tunes glide duration and step size for your screen.</div>
        <div class="diagonal-row">
          <span class="label">Screen diagonal</span>
          <div class="spinner-group">
            <button class="spin spin-down" data-spin="-0.5" data-target="general.screenDiagonalInches" tabindex="-1">−</button>
            <input type="number" data-key="general.screenDiagonalInches" min="10" max="65" step="0.5">
            <button class="spin spin-up" data-spin="0.5" data-target="general.screenDiagonalInches" tabindex="-1">+</button>
          </div>
          <span class="unit">inches</span>
        </div>
      </div>
      <button class="primary" data-action="optimize-monitor">Optimize</button>
    </div>
  </div>
  <div class="footer">
    <button class="subtle" data-action="reset-wheel">Reset wheel defaults</button>
    <span>GlideScroll v1.3.1 by PortGhost</span>
  </div>
</div>

<!-- PAN PAGE -->
<div class="page" id="page-pan">
  <div class="page-content">
    <div class="master-row">
      <div>
        <div class="master-title">Pan mode</div>
        <div class="master-desc">Hold middle button and drag to scroll like a touchpad.</div>
      </div>
      <div class="toggle-row">
        <span class="toggle-state">ON</span>
        <div class="toggle on" data-toggle="pan.enabled"></div>
      </div>
    </div>

    <div class="mode-selector">
      <div class="section-label">Pan engine</div>
      <div class="mode-dropdown" id="pan-mode-dropdown" data-key="pan.mode" data-open="false">
        <div class="mode-current" id="pan-mode-current">
          <i class="ti ti-feather mode-icon"></i>
          <div class="mode-text">
            <div class="mode-title">Feather</div>
            <div class="mode-subtitle">Paper-light direct drive. Tracks your hand one to one, stops when you stop.</div>
          </div>
          <i class="ti ti-chevron-down mode-chevron"></i>
        </div>
        <div class="mode-options" id="pan-mode-options">
          <div class="mode-option active" data-value="feather" data-icon="ti-feather" data-title="Feather" data-subtitle="Paper-light direct drive. Tracks your hand one to one, stops when you stop.">
            <i class="ti ti-feather mode-icon"></i>
            <div class="mode-text">
              <div class="mode-title">Feather</div>
              <div class="mode-subtitle">Paper-light direct drive. Tracks your hand one to one, stops when you stop.</div>
            </div>
            <i class="ti ti-check mode-check"></i>
          </div>
          <div class="mode-option" data-value="cushion" data-icon="ti-cloud" data-title="Cushion" data-subtitle="Soft and damped, with a smoothing filter that absorbs hand jitter.">
            <i class="ti ti-cloud mode-icon"></i>
            <div class="mode-text">
              <div class="mode-title">Cushion</div>
              <div class="mode-subtitle">Soft and damped, with a smoothing filter that absorbs hand jitter.</div>
            </div>
            <i class="ti ti-check mode-check"></i>
          </div>
        </div>
      </div>
    </div>

    <div class="grid" id="pan-params" data-mode="feather">
      <div class="numerics">
        <div class="num-row"><span class="label">Coast Friction</span><div class="spinner-group"><button class="spin spin-down" data-spin="-1" data-target="pan.friction" tabindex="-1">−</button><input type="number" data-key="pan.friction" min="1" max="200"><button class="spin spin-up" data-spin="1" data-target="pan.friction" tabindex="-1">+</button></div><span class="unit"></span></div>
        <div class="num-row"><span class="label">Sensitivity (horizontal)</span><div class="spinner-group"><button class="spin spin-down" data-spin="-5" data-target="pan.sensitivityX" tabindex="-1">−</button><input type="number" data-key="pan.sensitivityX" min="1" max="500"><button class="spin spin-up" data-spin="5" data-target="pan.sensitivityX" tabindex="-1">+</button></div><span class="unit"></span></div>
        <div class="num-row"><span class="label">Sensitivity (vertical)</span><div class="spinner-group"><button class="spin spin-down" data-spin="-5" data-target="pan.sensitivityY" tabindex="-1">−</button><input type="number" data-key="pan.sensitivityY" min="1" max="500"><button class="spin spin-up" data-spin="5" data-target="pan.sensitivityY" tabindex="-1">+</button></div><span class="unit"></span></div>
        <div class="num-row control-pan-smoothness"><span class="label">Smoothness</span><div class="spinner-group"><button class="spin spin-down" data-spin="-1" data-target="pan.smoothness" tabindex="-1">−</button><input type="number" data-key="pan.smoothness" min="0" max="20" step="1"><button class="spin spin-up" data-spin="1" data-target="pan.smoothness" tabindex="-1">+</button></div><span class="unit"></span></div>
        <div class="num-row"><span class="label">Activation distance</span><div class="spinner-group"><button class="spin spin-down" data-spin="-1" data-target="pan.activationThresholdPixels" tabindex="-1">−</button><input type="number" data-key="pan.activationThresholdPixels" min="0" max="50"><button class="spin spin-up" data-spin="1" data-target="pan.activationThresholdPixels" tabindex="-1">+</button></div><span class="unit">px</span></div>
        <div class="num-row"><span class="label">Emit threshold</span><div class="spinner-group"><button class="spin spin-down" data-spin="-1" data-target="pan.minStepPixels" tabindex="-1">−</button><input type="number" data-key="pan.minStepPixels" min="1" max="50"><button class="spin spin-up" data-spin="1" data-target="pan.minStepPixels" tabindex="-1">+</button></div><span class="unit">px</span></div>
      </div>
      <div class="toggles">
        <label class="checkbox-row"><input type="checkbox" data-key="pan.flickMode">Flick mode (coast on release)</label>
        <label class="checkbox-row"><input type="checkbox" data-key="pan.lockCursorDuringPan">Lock cursor during pan</label>
        <label class="checkbox-row"><input type="checkbox" data-key="pan.snapToAxis">Snap to axis (no diagonal)</label>
        <label class="checkbox-row"><input type="checkbox" data-key="pan.reverseDirection">Reverse pan direction</label>
      </div>
    </div>

    <div class="optimize-row" data-tip-id="action:optimize-pan">
      <div class="info">
        <div class="title">Optimize for monitor</div>
        <div class="desc" id="pan-optimize-desc">Detects resolution and tunes pan sensitivity, activation distance, and emit threshold for your screen density.</div>
        <div class="diagonal-row">
          <span class="label">Screen diagonal</span>
          <div class="spinner-group">
            <button class="spin spin-down" data-spin="-0.5" data-target="general.screenDiagonalInches" tabindex="-1">−</button>
            <input type="number" data-key="general.screenDiagonalInches" min="10" max="65" step="0.5">
            <button class="spin spin-up" data-spin="0.5" data-target="general.screenDiagonalInches" tabindex="-1">+</button>
          </div>
          <span class="unit">inches</span>
        </div>
      </div>
      <button class="primary" data-action="optimize-pan">Optimize</button>
    </div>
  </div>
  <div class="footer">
    <button class="subtle" data-action="reset-pan">Reset pan defaults</button>
    <span>GlideScroll v1.3.1 by PortGhost</span>
  </div>
</div>

<!-- APPS PAGE -->
<div class="page" id="page-apps">
  <div class="page-content">
    <div style="height: 14px"></div>
    <div class="section-label">Mode</div>
    <div class="segmented" id="apps-mode">
      <div class="segment active" data-value="exclusion">Exclude these apps</div>
      <div class="segment" data-value="inclusion">Only run for these apps</div>
    </div>
    <div class="mode-desc" id="apps-mode-desc">
      GlideScroll is active everywhere except apps in the list. Switching modes shows your other list, both are saved separately.
    </div>
    <div class="list-header">
      <span class="list-title"><span id="list-name">Blacklist</span> <span class="count" id="list-count">(0 apps)</span></span>
      <span class="list-hint">× removes an entry</span>
    </div>
    <div class="app-list" id="app-list"><div class="empty-list">No apps in this list yet.</div></div>
    <div class="add-buttons">
      <button data-action="add-running"><i class="ti ti-plus"></i>Add running app</button>
      <button data-action="add-browse"><i class="ti ti-folder"></i>Browse for .exe</button>
    </div>
    <div class="add-buttons add-buttons-adv">
      <button class="btn-adv" data-action="add-class"><i class="ti ti-crosshair"></i>Add class (advanced)</button>
      <button class="btn-adv" id="gamemode-btn" data-action="game-mode"><i class="ti ti-device-gamepad"></i>Game Mode</button>
    </div>
    <div class="add-buttons add-buttons-adv add-buttons-center">
      <button class="btn-adv" data-action="restore-defaults"><i class="ti ti-refresh"></i>Restore defaults</button>
    </div>
  </div>
  <div class="footer">
    <span>Both lists persist independently.</span>
    <span>GlideScroll v1.3.1 by PortGhost</span>
  </div>
</div>

</div>

<!-- PREFERENCES MODAL -->
<div class="modal hidden" id="prefs-modal">
  <div class="modal-card" style="width: min(556px, calc(100% - 40px))">
    <div class="modal-header">
      <span class="title"><i class="ti ti-settings"></i>Preferences</span>
      <span class="close" data-close="prefs-modal"><i class="ti ti-x"></i></span>
    </div>
    <div class="modal-body">
      <div class="section-label" style="margin-left:0; margin-bottom:10px">THEME</div>
      <div class="pref-row">
        <span class="label">Appearance</span>
        <div class="appearance-controls">
          <!-- Resets ONLY the currently selected theme to its factory colors. -->
          <button id="theme-reset" data-action="theme-reset" title="Reset this theme's colors to defaults"><i class="ti ti-refresh"></i>Reset</button>
          <!-- Custom dropdown, not a native select: the OS renders a select's
               popup list, so its option highlight can never follow the theme. -->
          <div class="mode-dropdown slim" id="appearance-dropdown" data-key="theme.appearance" data-open="false">
            <div class="mode-current">
              <div class="mode-text"><div class="mode-title" id="appearance-current">Auto (follow Windows)</div></div>
              <i class="ti ti-chevron-down mode-chevron"></i>
            </div>
            <div class="mode-options">
              <div class="mode-option active" data-value="auto"><div class="mode-text"><div class="mode-title">Auto (follow Windows)</div></div><i class="ti ti-check mode-check"></i></div>
              <div class="mode-option" data-value="light"><div class="mode-text"><div class="mode-title">Light</div></div><i class="ti ti-check mode-check"></i></div>
              <div class="mode-option" data-value="dark"><div class="mode-text"><div class="mode-title">Dark</div></div><i class="ti ti-check mode-check"></i></div>
              <div class="mode-option" data-value="oledElectric"><div class="mode-text"><div class="mode-title">OLED Electric</div></div><i class="ti ti-check mode-check"></i></div>
              <div class="mode-option" data-value="cozyPink"><div class="mode-text"><div class="mode-title">Cozy Pink</div></div><i class="ti ti-check mode-check"></i></div>
              <div class="mode-option" data-value="cozyBlue"><div class="mode-text"><div class="mode-title">Cozy Blue</div></div><i class="ti ti-check mode-check"></i></div>
              <div class="mode-option" data-value="sakuraPop"><div class="mode-text"><div class="mode-title">Sakura Pop</div></div><i class="ti ti-check mode-check"></i></div>
              <div class="mode-option" data-value="lavenderMilk"><div class="mode-text"><div class="mode-title">Lavender Milk</div></div><i class="ti ti-check mode-check"></i></div>
              <div class="mode-option" data-value="gridGraphite"><div class="mode-text"><div class="mode-title">Grid Graphite</div></div><i class="ti ti-check mode-check"></i></div>
              <div class="mode-option" data-value="aizomeIndigo"><div class="mode-text"><div class="mode-title">Aizome Indigo</div></div><i class="ti ti-check mode-check"></i></div>
              <div class="mode-option" data-value="neonDusk"><div class="mode-text"><div class="mode-title">Neon Dusk</div></div><i class="ti ti-check mode-check"></i></div>
              <div class="mode-option" data-value="cyanCircuit"><div class="mode-text"><div class="mode-title">Cyan Circuit</div></div><i class="ti ti-check mode-check"></i></div>
              <div class="mode-option" data-value="greenPhosphor"><div class="mode-text"><div class="mode-title">Green Phosphor</div></div><i class="ti ti-check mode-check"></i></div>
              <div class="mode-option" data-value="amberCrt"><div class="mode-text"><div class="mode-title">Amber CRT</div></div><i class="ti ti-check mode-check"></i></div>
              <div class="mode-option" data-value="abyssGlow"><div class="mode-text"><div class="mode-title">Abyss Glow</div></div><i class="ti ti-check mode-check"></i></div>
              <div class="mode-option" data-value="custom1"><div class="mode-text"><div class="mode-title">Custom 1</div></div><i class="ti ti-check mode-check"></i></div>
              <div class="mode-option" data-value="custom2"><div class="mode-text"><div class="mode-title">Custom 2</div></div><i class="ti ti-check mode-check"></i></div>
              <div class="mode-option" data-value="custom3"><div class="mode-text"><div class="mode-title">Custom 3</div></div><i class="ti ti-check mode-check"></i></div>
            </div>
          </div>
        </div>
      </div>
      <div class="pref-desc" id="theme-desc"></div>

      <div class="custom-panel" id="custom-panel">
        <div class="custom-panel-title">Theme colors</div>
        <div class="color-grid" id="color-grid"></div>
        <div style="font-size: 11px; color: var(--text-muted); margin-top: 10px; line-height: 1.5;">
          Accepts hex (#FFFFFF or FFFFFF), rgb (255,255,255), or click a swatch to pick. Edits save to the theme selected above; Reset restores that theme's defaults.
        </div>
      </div>

      <div class="prefs-pair">
        <div>
          <div class="section-label" style="margin:0 0 10px">GENERAL</div>
          <div class="general-rows">
            <label class="checkbox-row"><input type="checkbox" data-key="general.autostartWithWindows">Auto-start with Windows</label>
            <label class="checkbox-row"><input type="checkbox" data-key="general.minimizeToTrayOnClose">Minimize to tray on close</label>
            <label class="checkbox-row"><input type="checkbox" data-key="general.showNotificationOnModeToggle">Show notification when adding apps</label>
          </div>
        </div>
        <div>
          <div class="section-label" style="margin:0 0 10px">SUPPORT</div>
          <!-- Rendered by renderSupportPanel(): State A (invite) or State B
               (acknowledgment), derived solely from general.supporterName. -->
          <div id="support-root"></div>
        </div>
      </div>
    </div>
    <div class="modal-footer">
      <button data-close="prefs-modal">Close</button>
    </div>
  </div>
</div>

<!-- Honor dialog. Opens OVER Preferences (see #supporter-modal z-index);
     nothing here validates anything, by design. -->
<div class="modal hidden" id="supporter-modal">
  <div class="modal-card" style="width: min(420px, calc(100% - 40px))">
    <div class="modal-header">
      <span class="title"><i class="ti ti-heart"></i>Supporter details</span>
      <span class="close" data-close="supporter-modal"><i class="ti ti-x"></i></span>
    </div>
    <div class="modal-body">
      <p class="modal-intro">GlideScroll takes your word for it. Nothing here is checked and nothing is sent anywhere. What you enter is saved to your config and shown in Preferences.</p>
      <div class="support-field" style="margin-bottom:12px">
        <div class="support-field-label">Name for your license</div>
        <input type="text" id="supporter-name" placeholder="Your name or username">
      </div>
      <div class="support-field">
        <div class="support-field-labels">
          <span class="support-field-label">Order number or license key</span>
          <span class="support-field-opt">optional, nothing reads it</span>
        </div>
        <input type="text" id="supporter-order" placeholder="Paste either one">
      </div>
    </div>
    <div class="modal-footer">
      <button class="subtle" id="supporter-clear" style="margin-right:auto">Clear</button>
      <button data-close="supporter-modal">Cancel</button>
      <button class="primary" id="supporter-save" disabled>Save</button>
    </div>
  </div>
</div>

<!-- APP PICKER MODAL -->
<div class="modal hidden" id="picker-modal">
  <div class="modal-card">
    <div class="modal-header">
      <span class="title"><i class="ti ti-apps"></i>Select an app</span>
      <span class="close" data-close="picker-modal"><i class="ti ti-x"></i></span>
    </div>
    <div class="modal-body">
      <div class="pick-window-row">
        <div class="pick-window" id="pick-window" title="Drag onto any window to add it"><i class="ti ti-crosshair"></i><span>Point at a window</span></div>
      </div>
      <input type="text" class="picker-search" id="picker-search" placeholder="Filter...">
      <div class="picker-list" id="picker-list">
        <div class="empty-list">Loading running apps…</div>
      </div>
    </div>
  </div>
</div>

<!-- ADD CLASS MODAL -->
<div class="modal hidden" id="class-modal">
  <div class="modal-card">
    <div class="modal-header">
      <span class="title"><i class="ti ti-apps"></i>Add window class</span>
      <span class="close" data-close="class-modal"><i class="ti ti-x"></i></span>
    </div>
    <div class="modal-body">
      <p class="modal-intro">This is an advanced feature for surfaces where smoothing actively harms the UI, or that have no EXE of their own to target. It matches by window class. If configured improperly, this can have undesirable effects. Some applications may share classes, such as across Chromium based apps. This is reversible by removing the classes you add or by clicking Restore Defaults.</p>
      <div class="class-finder" id="class-finder">
        <i class="ti ti-crosshair"></i>
        <div class="cf-title">Drag onto any window</div>
        <div class="cf-sub">Grabs the class under your cursor. No typing.</div>
      </div>
      <div class="class-preview" id="class-preview"></div>
      <div class="class-manual-label">Or type it manually</div>
      <input type="text" class="picker-search" id="class-input" placeholder="Window class name" autocomplete="off" spellcheck="false">
      <div class="class-warn hidden" id="class-warn"></div>
    </div>
    <div class="modal-footer">
      <button data-close="class-modal">Cancel</button>
      <button class="primary" id="class-add-btn">Add class</button>
    </div>
  </div>
</div>

<!-- RESTORE DEFAULTS MODAL -->
<div class="modal hidden" id="restore-modal">
  <div class="modal-card" style="width: min(420px, calc(100% - 40px))">
    <div class="modal-header">
      <span class="title"><i class="ti ti-refresh"></i>Restore defaults</span>
      <span class="close" data-close="restore-modal"><i class="ti ti-x"></i></span>
    </div>
    <div class="modal-body">
      <p class="modal-intro" style="margin-bottom:14px">Re-add default blocks, or reset everything?</p>
      <div class="restore-opt" data-restore="readd">
        <i class="ti ti-plus"></i>
        <div><div class="ro-title">Re-add only</div><div class="ro-desc">Restores the default blocks. Keeps your own entries.</div></div>
      </div>
      <div class="restore-opt" data-restore="reset">
        <i class="ti ti-trash"></i>
        <div><div class="ro-title">Reset everything</div><div class="ro-desc">Clears this list back to factory defaults.</div></div>
      </div>
    </div>
    <div class="modal-footer">
      <button data-close="restore-modal">Cancel</button>
      <button class="primary" id="restore-confirm" disabled>Confirm</button>
    </div>
  </div>
</div>

<!-- GAME MODE MODAL -->
<div class="modal hidden" id="gamemode-modal">
  <div class="modal-card">
    <div class="modal-header">
      <span class="title"><i class="ti ti-device-gamepad"></i>Game Mode</span>
      <span class="close" data-close="gamemode-modal"><i class="ti ti-x"></i></span>
    </div>
    <div class="modal-body">
      <p class="modal-intro">When on, GlideScroll pauses itself while you are in a game we detect as having anti-cheat, fullscreen, or a known popular game EXE. This keeps GlideScroll's input smoothing clear of anti-cheat and stops the app from interfering with in-game scrolling.</p>
      <div class="gm-toggle-row" data-tip-id="gameMode.enabled">
        <span class="gm-toggle-label">Enable Game Mode</span>
        <div class="toggle-row">
          <span class="toggle-state">ON</span>
          <div class="toggle on" data-toggle="gameMode.enabled"></div>
        </div>
      </div>
      <div class="list-header" style="padding: 0; margin: 14px 0 8px;">
        <span class="list-title">Known games <span class="count" id="gm-count">(0)</span></span>
        <span class="list-hint">× removes an entry</span>
      </div>
      <div class="app-list" id="gm-list" style="margin: 0; max-height: 190px;">
        <div class="empty-list">No games in this list.</div>
      </div>
      <div class="add-buttons" style="padding: 12px 0 0;">
        <button data-action="gm-browse"><i class="ti ti-folder"></i>Browse for .exe</button>
        <button id="gm-point" title="Drag onto a game window to add it"><i class="ti ti-crosshair"></i>Point at a window</button>
      </div>
      <div class="gm-restore-row">
        <button class="btn-adv" data-action="gm-restore"><i class="ti ti-refresh"></i>Restore default games</button>
      </div>
    </div>
    <div class="modal-footer">
      <button data-close="gamemode-modal">Close</button>
    </div>
  </div>
</div>

<script>
'use strict';

// ----- inline SVG icon system -----
// Replaces the Tabler webfont CDN so the settings window loads fully offline.
// Paths are extracted from the official @tabler/icons outline set, so visual
// fidelity matches the original webfont 1:1. Stroke is rendered via
// currentColor so theme palette swaps work without touching this code.
const ICON_PATHS = {
  'mouse':            '<path d="M6 7a4 4 0 0 1 4 -4h4a4 4 0 0 1 4 4v10a4 4 0 0 1 -4 4h-4a4 4 0 0 1 -4 -4l0 -10"/><path d="M12 7l0 4"/>',
  'hand-two-fingers': '<path d="M8 13v-8.5a1.5 1.5 0 0 1 3 0v7.5"/><path d="M17 11.5a1.5 1.5 0 0 1 3 0v4.5a6 6 0 0 1 -6 6h-2h.208a6 6 0 0 1 -5.012 -2.7a69.74 69.74 0 0 1 -.196 -.3c-.312 -.479 -1.407 -2.388 -3.286 -5.728a1.5 1.5 0 0 1 .536 -2.022a1.867 1.867 0 0 1 2.28 .28l1.47 1.47"/><path d="M14 10.5a1.5 1.5 0 0 1 3 0v1.5"/><path d="M11 5.5v-2a1.5 1.5 0 1 1 3 0v8.5"/>',
  'apps':             '<path d="M4 5a1 1 0 0 1 1 -1h4a1 1 0 0 1 1 1v4a1 1 0 0 1 -1 1h-4a1 1 0 0 1 -1 -1l0 -4"/><path d="M4 15a1 1 0 0 1 1 -1h4a1 1 0 0 1 1 1v4a1 1 0 0 1 -1 1h-4a1 1 0 0 1 -1 -1l0 -4"/><path d="M14 15a1 1 0 0 1 1 -1h4a1 1 0 0 1 1 1v4a1 1 0 0 1 -1 1h-4a1 1 0 0 1 -1 -1l0 -4"/><path d="M14 7l6 0"/><path d="M17 4l0 6"/>',
  'settings':         '<path d="M10.325 4.317c.426 -1.756 2.924 -1.756 3.35 0a1.724 1.724 0 0 0 2.573 1.066c1.543 -.94 3.31 .826 2.37 2.37a1.724 1.724 0 0 0 1.065 2.572c1.756 .426 1.756 2.924 0 3.35a1.724 1.724 0 0 0 -1.066 2.573c.94 1.543 -.826 3.31 -2.37 2.37a1.724 1.724 0 0 0 -2.572 1.065c-.426 1.756 -2.924 1.756 -3.35 0a1.724 1.724 0 0 0 -2.573 -1.066c-1.543 .94 -3.31 -.826 -2.37 -2.37a1.724 1.724 0 0 0 -1.065 -2.572c-1.756 -.426 -1.756 -2.924 0 -3.35a1.724 1.724 0 0 0 1.066 -2.573c-.94 -1.543 .826 -3.31 2.37 -2.37c1 .608 2.296 .07 2.572 -1.065"/><path d="M9 12a3 3 0 1 0 6 0a3 3 0 0 0 -6 0"/>',
  'droplet':          '<path d="M7.502 19.423c2.602 2.105 6.395 2.105 8.996 0c2.602 -2.105 3.262 -5.708 1.566 -8.546l-4.89 -7.26c-.42 -.625 -1.287 -.803 -1.936 -.397a1.376 1.376 0 0 0 -.41 .397l-4.893 7.26c-1.695 2.838 -1.035 6.441 1.567 8.546"/>',
  'chevron-down':     '<path d="M6 9l6 6l6 -6"/>',
  'check':            '<path d="M5 12l5 5l10 -10"/>',
  'wave-sine':        '<path d="M21 12h-2c-.894 0 -1.662 -.857 -1.761 -2c-.296 -3.45 -.749 -6 -2.749 -6s-2.5 3.582 -2.5 8s-.5 8 -2.5 8s-2.452 -2.547 -2.749 -6c-.1 -1.147 -.867 -2 -1.763 -2h-2"/>',
  'zodiac-aquarius':  '<path d="M3 10l3 -3l3 3l3 -3l3 3l3 -3l3 3"/><path d="M3 17l3 -3l3 3l3 -3l3 3l3 -3l3 3"/>',
  'bolt':             '<path d="M13 3l0 7l6 0l-8 11l0 -7l-6 0l8 -11"/>',
  'plus':             '<path d="M12 5l0 14"/><path d="M5 12l14 0"/>',
  'folder':           '<path d="M5 4h4l3 3h7a2 2 0 0 1 2 2v8a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-11a2 2 0 0 1 2 -2"/>',
  'x':                '<path d="M18 6l-12 12"/><path d="M6 6l12 12"/>',
  'app-window':       '<path d="M3 7a2 2 0 0 1 2 -2h14a2 2 0 0 1 2 2v10a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-10"/><path d="M6 8h.01"/><path d="M9 8h.01"/>',
  'crosshair':        '<path d="M12 12m-9 0a9 9 0 1 0 18 0a9 9 0 1 0 -18 0"/><path d="M12 3l0 4"/><path d="M12 17l0 4"/><path d="M3 12l4 0"/><path d="M17 12l4 0"/>',
  'alert-triangle':   '<path d="M12 9v4"/><path d="M10.24 3.957l-8.422 14.06a1.989 1.989 0 0 0 1.7 2.983h16.845a1.989 1.989 0 0 0 1.7 -2.983l-8.423 -14.06a1.989 1.989 0 0 0 -3.4 0z"/><path d="M12 16h.01"/>',
  'refresh':          '<path d="M20 11a8.1 8.1 0 0 0 -15.5 -2m-.5 -4v4h4"/><path d="M4 13a8.1 8.1 0 0 0 15.5 2m.5 4v-4h-4"/>',
  'trash':            '<path d="M4 7l16 0"/><path d="M10 11l0 6"/><path d="M14 11l0 6"/><path d="M5 7l1 12a2 2 0 0 0 2 2h8a2 2 0 0 0 2 -2l1 -12"/><path d="M9 7v-3a1 1 0 0 1 1 -1h4a1 1 0 0 1 1 1v3"/>',
  'wind':             '<path d="M5 8h8.5a2.5 2.5 0 1 0 -2.34 -3.24"/><path d="M3 12h15.5a2.5 2.5 0 1 1 -2.34 3.24"/><path d="M4 16h5.5a2.5 2.5 0 1 1 -2.34 3.24"/>',
  'gauge':            '<path d="M12 12m-9 0a9 9 0 1 0 18 0a9 9 0 1 0 -18 0"/><path d="M13.41 10.59l2.59 -2.59"/><path d="M7 12a5 5 0 0 1 5 -5"/>',
  'feather':          '<path d="M4 20l10 -10m0 -5v5h5m-9 -1v5h5m-9 -1v5h5m-5 -5l4 -4l4 -4"/><path d="M19 10c.638 -.636 1 -1.515 1 -2.486a3.515 3.515 0 0 0 -3.517 -3.514c-.97 0 -1.847 .367 -2.483 1m-3 13l4 -4l4 -4"/>',
  'cloud':            '<path d="M6.657 18c-2.572 0 -4.657 -2.007 -4.657 -4.483c0 -2.475 2.085 -4.482 4.657 -4.482c.393 -1.762 1.794 -3.2 3.675 -3.773c1.88 -.572 3.956 -.193 5.444 1c1.488 1.19 2.162 3.007 1.77 4.769h.99c1.913 0 3.464 1.56 3.464 3.486c0 1.927 -1.551 3.487 -3.465 3.487h-11.878"/>',
  'device-gamepad':   '<path d="M2 8a2 2 0 0 1 2 -2h16a2 2 0 0 1 2 2v8a2 2 0 0 1 -2 2h-16a2 2 0 0 1 -2 -2l0 -8"/><path d="M6 12h4m-2 -2v4"/><path d="M15 11l0 .01"/><path d="M18 13l0 .01"/>',
  'heart':            '<path d="M19.5 12.572l-7.5 7.428l-7.5 -7.428a5 5 0 1 1 7.5 -6.566a5 5 0 1 1 7.5 6.566z"/>',
  'heart-filled':     '<path d="M19.5 12.572l-7.5 7.428l-7.5 -7.428a5 5 0 1 1 7.5 -6.566a5 5 0 1 1 7.5 6.566z"/>',
  'pencil':           '<path d="M4 20h4l10.5 -10.5a2.828 2.828 0 1 0 -4 -4l-10.5 10.5v4"/><path d="M13.5 6.5l4 4"/>',
  'external-link':    '<path d="M12 6h-6a2 2 0 0 0 -2 2v10a2 2 0 0 0 2 2h10a2 2 0 0 0 2 -2v-6"/><path d="M11 13l9 -9"/><path d="M15 4h5v5"/>'
};

/// <summary>Returns an inline SVG string for the named Tabler icon, with
/// the given extra class names appended. Use this in template literals that
/// generate dynamic HTML so new content is iconified at render time.</summary>
function iconHtml(name, extraClasses = '') {
  const body = ICON_PATHS[name] || '';
  const cls = `ti ti-${name}${extraClasses ? ' ' + extraClasses : ''}`;
  return `<svg class="${cls}" viewBox="0 0 24 24" aria-hidden="true">${body}</svg>`;
}

/// <summary>Convert all <i class="ti ti-NAME ..."> elements in the document
/// into inline <svg> elements with the same class list. Runs once on
/// startup, after that, dynamic code should call iconHtml() directly.</summary>
function iconifyAll(root) {
  const nodes = (root || document).querySelectorAll('i.ti');
  nodes.forEach(el => {
    // Extract ti-NAME from the class list to find which icon body to use.
    let name = '';
    for (const c of el.classList) {
      if (c.startsWith('ti-')) { name = c.slice(3); break; }
    }
    if (!name || !ICON_PATHS[name]) return;
    const svgNs = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(svgNs, 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('aria-hidden', 'true');
    // Carry the full class list so .mode-icon, .mode-check, etc. styles
    // still apply unchanged.
    el.classList.forEach(c => svg.classList.add(c));
    svg.innerHTML = ICON_PATHS[name];
    el.replaceWith(svg);
  });
}

// Iconify the initial DOM as soon as it's parsed.
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', () => iconifyAll(document));
} else {
  iconifyAll(document);
}

/// <summary>Replace the icon shown by an already-iconified SVG element,
/// preserving its non-ti-* class list. Used when the smoothing-mode dropdown
/// selection changes and the current-mode row needs a new icon glyph.</summary>
function setIconOn(el, name) {
  if (!el || !ICON_PATHS[name]) return;
  // Strip any existing ti-* class so only the new one remains.
  const toRemove = [];
  el.classList.forEach(c => { if (c.startsWith('ti-')) toRemove.push(c); });
  toRemove.forEach(c => el.classList.remove(c));
  el.classList.add('ti', `ti-${name}`);
  el.innerHTML = ICON_PATHS[name];
}

// ----- state -----
let config = null;
let activePalette = null;
let runningApps = [];
let suppress = false;

const COLOR_KEYS = [
  ['Background', 'background'],
  ['Surface', 'surface'],
  ['Text', 'text'],
  ['Text muted', 'textMuted'],
  ['Accent', 'accent'],
  ['Border', 'border'],
];

const APPEARANCE_DESCRIPTIONS = {
  auto: 'Follows your Windows light/dark setting, switching between the Light and Dark themes.',
  light: 'Clean white-first light theme with grey highlights and a calm blue accent.',
  dark: 'Deep blue-navy dark theme, easy on the eyes with a bright blue accent.',
  oledElectric: 'True #000000 background so OLED pixels switch off, with an electric azure accent.',
  cozyPink: 'Cute white and pink with a hot pink accent.',
  cozyBlue: 'Cozy Pink\'s blue sibling: white and sky with a vivid blue accent.',
  sakuraPop: 'Warm paper tones framed by a dark navy border, with a sakura red-pink accent.',
  lavenderMilk: 'Pale violet light theme, soft and calm.',
  gridGraphite: 'Neutral graphite dark with a crimson accent.',
  aizomeIndigo: 'Japanese indigo dark, muted and sophisticated.',
  neonDusk: 'Purple-noir dark with a neon pink accent.',
  cyanCircuit: 'Near-black dark with a glowing cyan accent.',
  greenPhosphor: 'Green CRT terminal. Night-vision vibes.',
  amberCrt: 'Amber CRT terminal. Retro gamer vibes.',
  abyssGlow: 'Deep ocean dark with a bioluminescent teal accent.',
  custom1: 'Your own palette, slot 1. Edit the colors below.',
  custom2: 'Your own palette, slot 2. Edit the colors below.',
  custom3: 'Your own palette, slot 3. Edit the colors below.',
};

// ----- helpers -----
const $ = (s, root = document) => root.querySelector(s);
const $$ = (s, root = document) => [...root.querySelectorAll(s)];

const esc = (s) => String(s).replace(/[&<>"']/g, c => (
  { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

// Window-class rules (Add class, advanced). Default blocks ship in the
// exclusion list and get an accent "Default" pill. Friendly names map the two
// shipped classes to readable labels; user-added classes show the raw class.
const DEFAULT_CLASS_BLOCKS = ['class:cabinetwclass', 'class:photo_lightweight_viewer'];
const CLASS_FRIENDLY = {
  'cabinetwclass': 'Windows File Explorer',
  'photo_lightweight_viewer': 'Windows Photo Viewer',
};
// Known shared/generic classes: warn (but never block) if the user targets one.
const GENERIC_CLASSES = {
  'chrome_widgetwin_1': 'every Chromium and Electron app (Chrome, Edge, VS Code, Slack, Discord and more)',
  'chrome_widgetwin_0': 'many Chromium and Electron surfaces',
  'mozillawindowclass': 'all Firefox windows',
  '#32770': 'every standard Windows dialog box',
  'applicationframewindow': 'all UWP and Store app windows',
  'mmcmainframe': 'every Microsoft Management Console tool (Device Manager, Event Viewer, and more)',
};

function getNested(obj, path) {
  return path.split('.').reduce((o, k) => o?.[k], obj);
}
function setNested(obj, path, value) {
  const parts = path.split('.');
  let cur = obj;
  for (let i = 0; i < parts.length - 1; i++) {
    cur[parts[i]] = cur[parts[i]] ?? {};
    cur = cur[parts[i]];
  }
  cur[parts[parts.length - 1]] = value;
}

function send(type, payload = {}) {
  if (!window.chrome?.webview) return;
  window.chrome.webview.postMessage(JSON.stringify({ type, ...payload }));
}

function setValue(key, value) {
  if (suppress) return;
  setNested(config, key, value);
  send('set', { key, value });
}

// ----- color parsing -----
function parseColor(s) {
  if (!s) return null;
  s = s.trim();
  const rgb = s.match(/^rgb\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)$/i);
  if (rgb) {
    return '#' + [rgb[1], rgb[2], rgb[3]]
      .map(n => Math.max(0, Math.min(255, +n)).toString(16).padStart(2, '0'))
      .join('').toUpperCase();
  }
  if (s.startsWith('#')) s = s.substring(1);
  if (/^[0-9a-f]{6}$/i.test(s)) return '#' + s.toUpperCase();
  return null;
}

// ----- theme application -----
// Relative luminance (WCAG) for the accent-text auto-pick below.
function relLum(hex) {
  const n = parseInt(String(hex).replace('#', ''), 16);
  if (isNaN(n)) return 0;
  const f = v => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
  return 0.2126 * f((n >> 16) & 255) + 0.7152 * f((n >> 8) & 255) + 0.0722 * f(n & 255);
}

function applyActivePalette() {
  if (!activePalette) return;
  const root = document.documentElement;
  root.style.setProperty('--bg', activePalette.background);
  root.style.setProperty('--surface', activePalette.surface);
  root.style.setProperty('--text', activePalette.text);
  root.style.setProperty('--text-muted', activePalette.textMuted);
  root.style.setProperty('--accent', activePalette.accent);
  root.style.setProperty('--border', activePalette.border);
  // Button/check text on accent fills: auto black-or-white by the accent's
  // luminance, so bright accents (pastels, neons) stay readable without a
  // separate palette slot. Same trick Windows uses for its accent.
  root.style.setProperty('--accent-text', relLum(activePalette.accent) > 0.36 ? '#15181D' : '#FFFFFF');
}

function updateAppearanceUi() {
  if (!config) return;
  const appearance = config.theme.appearance;
  $('#theme-desc').textContent = APPEARANCE_DESCRIPTIONS[appearance] ?? '';
  // The palette editor panel is always visible; it binds to whatever theme
  // is selected (renderColorGrid reads the active palette).
}

// Appearance dropdown (slim custom dropdown replacing the old native select,
// which the OS renders with an unthemeable gray option highlight).
function applyAppearance(value, persist) {
  const dd = $('#appearance-dropdown');
  if (!dd) return;
  const opt = dd.querySelector(`.mode-option[data-value="${value}"]`);
  if (!opt) return;
  $('#appearance-current').textContent = opt.querySelector('.mode-title').textContent;
  $$('.mode-option', dd).forEach(o => o.classList.toggle('active', o === opt));
  if (persist) {
    setValue('theme.appearance', value);
    updateAppearanceUi();
  }
}

// ----- smoothing-mode dropdown wiring -----
// Custom dropdown (not <select>) so we can render icons + descriptions per row.
// Selection persists via 'set' message; param visibility morphs via data-mode.
function syncSmoothingModeFromConfig() {
  if (!config) return;
  const value = config.wheel?.smoothingMode || 'silk';
  applySmoothingMode(value, /*persist*/ false);
}
// Per-mode override for the shared Smoothness control (it backs wheel.feelBias
// internally, but is presented as a different parameter per mode). When the
// mode changes, we swap the visible label and the tooltip text so the field
// genuinely represents what each mode does with that value.
const SMOOTHNESS_OVERRIDES = {
  spring: {
    label: 'Bounce / Glide',
    tip: '0 is neutral, the textbook critical damping with no overshoot. Negative values (toward -10) add bounce, the spring overshoots and oscillates around the target. Positive values (toward 10) add a gradually more sluggish glide, overdamped and slow to reach target.'
  },
  hybrid: {
    label: 'Spring / Coast',
    tip: '0 is balanced, equal time split between spring chase and momentum coast. Negative values (toward -10) bias toward coast for a more kinetic free spin. Positive values (toward 10) bias toward spring for a more controlled chase.'
  }
  // flywheel and lowLatency hide the field entirely (see CSS data-mode rules).
};

function applySmoothnessOverride(mode) {
  const labelEl = document.getElementById('feel-bias-label');
  if (!labelEl) return;
  const o = SMOOTHNESS_OVERRIDES[mode];
  // Default fallback if a mode has no override (Smoothness is hidden anyway).
  labelEl.textContent = o ? o.label : 'Smoothness';
  // Mutate the live tooltip dictionary so hovering the field shows the
  // mode-specific explanation rather than a generic one.
  TIPS['wheel.feelBias'] = o ? o.tip : 'Mode-specific tuning. Hidden when not relevant to the current engine.';
}

function applySmoothingMode(value, persist) {
  const dropdown = $('#mode-dropdown');
  if (!dropdown) return;
  const opt = dropdown.querySelector(`.mode-option[data-value="${value}"]`);
  if (!opt) return;

  // Mirror selected option's icon/title/subtitle into the .mode-current row.
  // opt.dataset.icon is the full Tabler class like "ti-droplet"; strip the
  // "ti-" prefix to get the icon name our inline SVG system expects.
  const current = $('#mode-current');
  const iconName = (opt.dataset.icon || '').replace(/^ti-/, '');
  setIconOn(current.querySelector('.mode-icon'), iconName);
  current.querySelector('.mode-title').textContent = opt.dataset.title;
  current.querySelector('.mode-subtitle').textContent = opt.dataset.subtitle;

  // Mark active option (drives accent tint + check icon visibility).
  $$('.mode-option', dropdown).forEach(o => o.classList.toggle('active', o === opt));

  // Drive parameter visibility via data-mode on the params container.
  const params = $('#wheel-params');
  if (params) params.setAttribute('data-mode', value);

  // Swap the Smoothness label + tooltip for this mode.
  applySmoothnessOverride(value);

  if (persist) {
    if (config?.wheel) config.wheel.smoothingMode = value;
    send('set', { key: 'wheel.smoothingMode', value });
  }
}

// ----- Pan engine (Feather / Cushion) -----
// Same dropdown template as the wheel Smoothing engine: icon + title +
// subtitle rows, selection mirrored into the current row, and per-mode
// parameter visibility via data-mode on the params grid.
function syncPanModeFromConfig() {
  if (!config) return;
  applyPanMode(config.pan?.mode || 'feather', /*persist*/ false);
}

function applyPanMode(value, persist) {
  const dropdown = $('#pan-mode-dropdown');
  if (!dropdown) return;
  const opt = dropdown.querySelector(`.mode-option[data-value="${value}"]`);
  if (!opt) return;

  const current = $('#pan-mode-current');
  const iconName = (opt.dataset.icon || '').replace(/^ti-/, '');
  setIconOn(current.querySelector('.mode-icon'), iconName);
  current.querySelector('.mode-title').textContent = opt.dataset.title;
  current.querySelector('.mode-subtitle').textContent = opt.dataset.subtitle;

  $$('.mode-option', dropdown).forEach(o => o.classList.toggle('active', o === opt));

  // Feather drives displacement directly, so the Smoothness low-pass is
  // hidden (nothing to filter). Cushion shows it.
  const params = $('#pan-params');
  if (params) params.setAttribute('data-mode', value);

  if (persist) {
    if (config?.pan) config.pan.mode = value;
    send('set', { key: 'pan.mode', value });
  }
}

// ----- bind controls to config -----
function bindAllControls() {
  if (!config) return;
  suppress = true;

  renderSupportPanel();

  $$('input[data-key]').forEach(el => {
    const key = el.dataset.key;
    const v = getNested(config, key);
    if (el.type === 'checkbox') el.checked = !!v;
    else el.value = v ?? '';
  });

  $$('select[data-key]').forEach(el => {
    const key = el.dataset.key;
    const v = getNested(config, key);
    if (v !== undefined && v !== null) el.value = String(v);
  });

  $$('.toggle[data-toggle]').forEach(el => {
    const key = el.dataset.toggle;
    const on = !!getNested(config, key);
    el.classList.toggle('on', on);
    const stateEl = el.parentElement.querySelector('.toggle-state');
    if (stateEl) {
      stateEl.textContent = on ? 'ON' : 'OFF';
      stateEl.style.color = on ? 'var(--accent)' : 'var(--text-muted)';
    }
  });

  // segmented (apps mode)
  const mode = config.apps?.mode ?? 'exclusion';
  $$('#apps-mode .segment').forEach(s => {
    s.classList.toggle('active', s.dataset.value === mode);
  });

  // pan engine dropdown
  syncPanModeFromConfig();

  // appearance dropdown (custom, themed)
  applyAppearance(config.theme?.appearance ?? 'auto', /*persist*/ false);

  renderGameModeList();
  refreshAppsList();

  renderColorGrid();
  updateAppearanceUi();
  applyActivePalette();
  syncSmoothingModeFromConfig();
  suppress = false;
}

// The palette editor binds to the ACTIVE palette (whatever theme is selected,
// preset or Custom slot), so every baked-in theme's hexes are visible and
// editable. Edits go to C# as theme-edit, which persists them as an override
// for that theme and pushes the palette back; Reset restores factory values
// for the current theme only.
function renderColorGrid() {
  if (!activePalette) return;
  const grid = $('#color-grid');
  grid.innerHTML = '';
  COLOR_KEYS.forEach(([label, key]) => {
    const value = activePalette[key] || '#000000';
    const row = document.createElement('div');
    row.className = 'color-row';
    row.innerHTML = `
      <span class="label">${label}</span>
      <input type="text" class="hex" value="${value}">
      <span class="swatch" style="background:${value}"><input type="color" value="${value}"></span>
    `;
    grid.appendChild(row);

    const hexInput = row.querySelector('.hex');
    const colorInput = row.querySelector('input[type="color"]');
    const swatch = row.querySelector('.swatch');

    // Two-phase editing so the native picker stays OPEN while dragging:
    // 'input' fires continuously during a drag and only live-previews
    // locally (whole app retints in real time, nothing sent); 'change'
    // fires when the picker commits/closes and persists to C#. Sending on
    // every input event round-tripped a palette push that rebuilt this
    // grid, which destroyed the input and slammed the picker shut.
    const applyLocal = (newValue) => {
      hexInput.value = newValue;
      colorInput.value = newValue;
      swatch.style.background = newValue;
      activePalette[key] = newValue;
      applyActivePalette();
    };
    const commit = (newValue) => {
      applyLocal(newValue);
      send('theme-edit', { slot: key, value: newValue });
    };

    hexInput.addEventListener('change', () => {
      const parsed = parseColor(hexInput.value);
      if (parsed) commit(parsed);
      else hexInput.value = activePalette[key] || '#000000';
    });
    colorInput.addEventListener('input', () => {
      applyLocal(colorInput.value.toUpperCase());
    });
    colorInput.addEventListener('change', () => {
      commit(colorInput.value.toUpperCase());
    });
  });
}

function refreshAppsList() {
  if (!config) return;
  const mode = config.apps.mode;
  const list = mode === 'exclusion' ? config.apps.blacklist : config.apps.whitelist;
  const name = mode === 'exclusion' ? 'Blacklist' : 'Whitelist';
  $('#list-name').textContent = name;
  $('#list-count').textContent = `(${list.length} ${list.length === 1 ? 'rule' : 'rules'})`;
  $('#apps-mode-desc').textContent = mode === 'exclusion'
    ? 'GlideScroll is active everywhere except apps in the list. Switching modes shows your other list, both are saved separately.'
    : 'GlideScroll is only active for apps in the list. Switching modes shows your other list, both are saved separately.';

  const container = $('#app-list');
  container.innerHTML = '';
  if (list.length === 0) {
    container.innerHTML = '<div class="empty-list">No apps in this list yet.</div>';
    return;
  }
  // Build the whole list into a DocumentFragment so layout/paint sees only
  // one mutation instead of N. Each row carries data-exe so the single
  // delegated click handler (added at first render only) can resolve back
  // to the array entry without per-row event listener allocation.
  const frag = document.createDocumentFragment();
  list.forEach(entry => {
    const item = document.createElement('div');
    item.className = 'app-item';
    item.dataset.exe = entry;
    if (/^class:/i.test(entry)) {
      const cls = entry.slice(6);
      const friendly = CLASS_FRIENDLY[cls.toLowerCase()] || cls;
      const isDefault = DEFAULT_CLASS_BLOCKS.includes(entry.toLowerCase());
      item.innerHTML = `
        ${iconHtml('app-window')}
        <div class="info">
          <div class="name">${esc(friendly)}${isDefault ? '<span class="default-pill">Default</span>' : ''}</div>
          <div class="exe">class: ${esc(cls)}</div>
        </div>
        <span class="remove" title="Remove">${iconHtml('x')}</span>
      `;
    } else {
      item.innerHTML = `
        ${iconHtml('app-window')}
        <div class="info">
          <div class="name">${esc(entry.replace(/\.exe$/i, ''))}</div>
          <div class="exe">${esc(entry)}</div>
        </div>
        <span class="remove" title="Remove">${iconHtml('x')}</span>
      `;
    }
    frag.appendChild(item);
  });
  container.appendChild(frag);

  if (!container._delegated) {
    container.addEventListener('click', e => {
      const removeBtn = e.target.closest('.remove');
      if (!removeBtn) return;
      const row = removeBtn.closest('.app-item');
      const exe = row?.dataset?.exe;
      if (!exe) return;
      e.stopPropagation();
      const mode = config.apps.mode;
      const arr = mode === 'exclusion' ? config.apps.blacklist : config.apps.whitelist;
      const idx = arr.indexOf(exe);
      if (idx >= 0) arr.splice(idx, 1);
      send('apps-remove', { exe });
      refreshAppsList();
    });
    container._delegated = true;
  }
}

// ----- event wiring -----
document.addEventListener('change', e => {
  const t = e.target;
  if (suppress) return;
  if (t.matches('input[data-key], select[data-key]')) {
    const key = t.dataset.key;
    let value;
    if (t.type === 'checkbox') value = t.checked;
    else if (t.type === 'number') value = parseFloat(t.value);
    else value = t.value;
    setValue(key, value);
    // Sync any other inputs bound to the same key (e.g. screen diagonal
    // appears on both Scrolling and Pan tabs, typing on one updates both).
    if (t.type === 'number' || t.type === 'text') {
      $$(`input[data-key="${key}"]`).forEach(other => {
        if (other !== t) other.value = t.value;
      });
    }
    if (key === 'theme.appearance') updateAppearanceUi();
  }
});

document.addEventListener('click', e => {
  // Engine dropdowns (wheel Smoothing engine + Pan engine): one delegated
  // handler drives both. The dropdown's data-key picks the apply function.
  {
    const APPLY_BY_KEY = {
      'wheel.smoothingMode': applySmoothingMode,
      'pan.mode': applyPanMode,
      'theme.appearance': applyAppearance,
    };
    const dd = e.target.closest('.mode-dropdown');
    if (dd) {
      const opt = e.target.closest('.mode-option');
      if (opt && dd.contains(opt)) {
        const apply = APPLY_BY_KEY[dd.dataset.key];
        if (apply) apply(opt.dataset.value, /*persist*/ true);
        dd.setAttribute('data-open', 'false');
        return;
      }
      if (e.target.closest('.mode-current')) {
        const wasOpen = dd.getAttribute('data-open') === 'true';
        // Opening one dropdown closes any other.
        $$('.mode-dropdown').forEach(d => d.setAttribute('data-open', 'false'));
        dd.setAttribute('data-open', wasOpen ? 'false' : 'true');
        return;
      }
    } else {
      // Click anywhere outside: close any open dropdown.
      $$('.mode-dropdown[data-open="true"]').forEach(d => d.setAttribute('data-open', 'false'));
    }
  }

  // tabs
  const tab = e.target.closest('.tab');
  if (tab && tab.dataset.tab) {
    const which = tab.dataset.tab;
    $$('.tab').forEach(x => x.classList.toggle('active', x === tab));
    $$('.page').forEach(p => p.classList.toggle('active', p.id === 'page-' + which));
    return;
  }

  // gear
  if (e.target.closest('#gear')) {
    $('#prefs-modal').classList.remove('hidden');
    return;
  }

  // toggle
  const toggle = e.target.closest('.toggle[data-toggle]');
  if (toggle) {
    const key = toggle.dataset.toggle;
    const newVal = !getNested(config, key);
    toggle.classList.toggle('on', newVal);
    const stateEl = toggle.parentElement.querySelector('.toggle-state');
    if (stateEl) {
      stateEl.textContent = newVal ? 'ON' : 'OFF';
      stateEl.style.color = newVal ? 'var(--accent)' : 'var(--text-muted)';
    }
    setValue(key, newVal);
    return;
  }

  // segmented
  const seg = e.target.closest('#apps-mode .segment');
  if (seg) {
    const v = seg.dataset.value;
    $$('#apps-mode .segment').forEach(x => x.classList.toggle('active', x === seg));
    config.apps.mode = v;
    setValue('apps.mode', v);
    refreshAppsList();
    return;
  }


  // closer
  const closer = e.target.closest('[data-close]');
  if (closer) {
    $('#' + closer.dataset.close).classList.add('hidden');
    return;
  }

  // spinner +/-
  const spin = e.target.closest('.spin[data-spin]');
  if (spin) {
    const target = spin.dataset.target;
    const delta = parseFloat(spin.dataset.spin);
    // Some keys (e.g. general.screenDiagonalInches) are bound to inputs on
    // multiple tabs. Update ALL matching inputs visually so they stay in sync,
    // not just the first one in document order.
    const inputs = $$(`input[data-key="${target}"]`);
    if (inputs.length > 0) {
      const ref = inputs[0];
      const cur = parseFloat(ref.value) || 0;
      const min = ref.min !== '' ? parseFloat(ref.min) : -Infinity;
      const max = ref.max !== '' ? parseFloat(ref.max) : Infinity;
      const step = parseFloat(ref.step) || 1;
      // Round to step
      let next = cur + delta;
      next = Math.max(min, Math.min(max, next));
      next = Math.round(next / step) * step;
      // Strip trailing .0 for cleanliness
      const display = (Number.isInteger(next) ? next.toString() : next.toFixed(1));
      inputs.forEach(el => { el.value = display; });
      setValue(target, next);
    }
    return;
  }

  // actions
  const action = e.target.closest('[data-action]');
  if (action) {
    const a = action.dataset.action;
    if (a === 'add-running') {
      $('#picker-modal').classList.remove('hidden');
      send('list-running-apps');
    }
    else if (a === 'add-browse') send('apps-browse');
    else if (a === 'add-class') openClassModal();
    else if (a === 'restore-defaults') openRestoreModal();
    else if (a === 'game-mode') openGameModeModal();
    else if (a === 'gm-browse') send('gamemode-browse');
    else if (a === 'gm-restore') send('gamemode-restore');
    else if (a === 'theme-reset') send('theme-reset-current');
    else if (a === 'reset-wheel') send('reset-wheel');
    else if (a === 'reset-pan') send('reset-pan');
    else if (a === 'optimize-pan') send('optimize-pan');
    else if (a === 'optimize-monitor') send('optimize-monitor');
  }
});

// picker filter, debounced so fast typing doesn't trigger a re-render on
// every keystroke. 60ms is below human-perceptible lag while still
// collapsing typical keypress bursts into a single layout pass.
let _pickerFilterTimer = null;
$('#picker-search').addEventListener('input', e => {
  const v = e.target.value;
  if (_pickerFilterTimer) clearTimeout(_pickerFilterTimer);
  _pickerFilterTimer = setTimeout(() => {
    _pickerFilterTimer = null;
    renderPickerList(runningApps, v);
  }, 60);
});

function renderPickerList(apps, filter = '') {
  const list = $('#picker-list');
  const f = filter.toLowerCase();
  const filtered = apps.filter(a =>
    a.displayName.toLowerCase().includes(f) ||
    a.exeName.toLowerCase().includes(f));
  if (filtered.length === 0) {
    list.innerHTML = '<div class="empty-list">No matching apps.</div>';
    return;
  }
  // DocumentFragment + delegated click. Filter keystrokes used to spawn N
  // listener attachments and rebuild the layout per row; now both are O(1)
  // regardless of how many apps match.
  const frag = document.createDocumentFragment();
  filtered.forEach(a => {
    const it = document.createElement('div');
    it.className = 'picker-item';
    it.dataset.exe = a.exeName;
    it.innerHTML = `<span class="name">${esc(a.displayName)}</span><span class="exe">${esc(a.exeName)}</span>`;
    frag.appendChild(it);
  });
  list.innerHTML = '';
  list.appendChild(frag);

  if (!list._delegated) {
    list.addEventListener('click', e => {
      const row = e.target.closest('.picker-item');
      const exe = row?.dataset?.exe;
      if (!exe) return;
      const mode = config.apps.mode;
      const arr = mode === 'exclusion' ? config.apps.blacklist : config.apps.whitelist;
      if (!arr.some(e => e.toLowerCase() === exe.toLowerCase())) {
        arr.push(exe);
      }
      send('apps-add', { exe });
      refreshAppsList();
      $('#picker-modal').classList.add('hidden');
    });
    list._delegated = true;
  }
}

// ----- Add class (advanced) + Restore defaults modals -----
function classSharedWarning(cls) {
  const key = (cls || '').toLowerCase().trim();
  for (const g in GENERIC_CLASSES) {
    if (key === g || (g.length > 3 && key.startsWith(g))) return GENERIC_CLASSES[g];
  }
  return null;
}
function updateClassWarn() {
  const cls = $('#class-input').value.trim();
  const warn = $('#class-warn');
  const shared = classSharedWarning(cls);
  if (shared) {
    warn.innerHTML = `${iconHtml('alert-triangle')}<span><b>${esc(cls)}</b> is shared by ${esc(shared)}. Excluding it disables smoothing far more broadly than you expect.</span>`;
    warn.classList.remove('hidden');
  } else {
    warn.classList.add('hidden');
  }
}
function openClassModal() {
  $('#class-input').value = '';
  $('#class-preview').textContent = '';
  $('#class-warn').classList.add('hidden');
  $('#class-finder').classList.remove('capturing');
  $('#class-modal').classList.remove('hidden');
}
$('#class-input').addEventListener('input', updateClassWarn);
// Finder tool: pointerdown starts a native capture in C#; drag over any window
// and release. C# streams previews back and delivers the final class.
$('#class-finder').addEventListener('pointerdown', e => {
  if (e.button !== 0) return;
  e.preventDefault();
  $('#class-finder').classList.add('capturing');
  $('#class-preview').textContent = 'Drag over a window and release…';
  send('capture-start', { kind: 'class' });
});
// Compact finder in the Add running app modal: same drag mechanic, but the
// result is added straight to the list (exe, or class for earmarked surfaces).
$('#pick-window').addEventListener('pointerdown', e => {
  if (e.button !== 0) return;
  e.preventDefault();
  $('#pick-window').classList.add('capturing');
  send('capture-start', { kind: 'app' });
});
$('#class-add-btn').addEventListener('click', () => {
  const cls = $('#class-input').value.trim();
  if (!cls) { $('#class-input').focus(); return; }
  const entry = 'class:' + cls;
  const mode = config.apps.mode;
  const arr = mode === 'exclusion' ? config.apps.blacklist : config.apps.whitelist;
  if (!arr.some(x => x.toLowerCase() === entry.toLowerCase())) arr.push(entry);
  send('apps-add', { exe: entry });
  refreshAppsList();
  $('#class-modal').classList.add('hidden');
});

let restoreChoice = null;
function openRestoreModal() {
  restoreChoice = null;
  $$('.restore-opt').forEach(o => o.classList.remove('selected'));
  // The Include list ships no default blocks, so "Re-add only" is a no-op there.
  $('.restore-opt[data-restore="readd"]').classList.toggle('disabled', config.apps.mode === 'inclusion');
  $('#restore-confirm').disabled = true;
  $('#restore-modal').classList.remove('hidden');
}
// Selecting an option lights up Confirm; the actual action fires only on Confirm.
document.addEventListener('click', e => {
  const opt = e.target.closest?.('.restore-opt');
  if (!opt || opt.classList.contains('disabled')) return;
  restoreChoice = opt.dataset.restore;
  $$('.restore-opt').forEach(o => o.classList.toggle('selected', o === opt));
  $('#restore-confirm').disabled = false;
});
$('#restore-confirm').addEventListener('click', () => {
  if (!restoreChoice) return;
  send('restore-defaults', { mode: restoreChoice });
  $('#restore-modal').classList.add('hidden');
});

// ----- Game Mode modal -----
// Shipped seeds get a Default badge, mirrored from the C# seed list.
// KEEP IN SYNC with GameModeConfig.DefaultGames (lowercase here).
const GM_DEFAULTS = [
  'valorant-win64-shipping.exe', 'league of legends.exe', 'cs2.exe',
  'dota2.exe', 'fortniteclient-win64-shipping.exe', 'r5apex.exe',
  'r5apex_dx12.exe', 'rustclient.exe', 'deadbydaylight-win64-shipping.exe',
  'rainbowsix.exe', 'tslgame.exe', 'destiny2.exe', 'escapefromtarkov.exe',
  'cod.exe', 'overwatch.exe'
];

function openGameModeModal() {
  renderGameModeList();
  $('#gamemode-modal').classList.remove('hidden');
}

function renderGameModeList() {
  const listEl = $('#gm-list');
  if (!listEl || !config?.gameMode) return;
  const games = config.gameMode.games || [];
  $('#gm-count').textContent = `(${games.length})`;
  listEl.innerHTML = '';
  if (games.length === 0) {
    listEl.innerHTML = '<div class="empty-list">No games in this list.</div>';
    return;
  }
  const frag = document.createDocumentFragment();
  games.forEach(entry => {
    const item = document.createElement('div');
    item.className = 'app-item';
    item.dataset.exe = entry;
    const isDefault = GM_DEFAULTS.includes(entry.toLowerCase());
    item.innerHTML = `
      ${iconHtml('device-gamepad')}
      <div class="info">
        <div class="name">${esc(entry.replace(/\.exe$/i, ''))}${isDefault ? '<span class="default-pill">Default</span>' : ''}</div>
        <div class="exe">${esc(entry)}</div>
      </div>
      <span class="remove" title="Remove">${iconHtml('x')}</span>
    `;
    frag.appendChild(item);
  });
  listEl.appendChild(frag);

  if (!listEl._delegated) {
    listEl.addEventListener('click', e => {
      const removeBtn = e.target.closest('.remove');
      if (!removeBtn) return;
      const row = removeBtn.closest('.app-item');
      const exe = row?.dataset?.exe;
      if (!exe) return;
      e.stopPropagation();
      const arr = config.gameMode.games;
      const idx = arr.indexOf(exe);
      if (idx >= 0) arr.splice(idx, 1);
      send('gamemode-remove', { exe });
      renderGameModeList();
    });
    listEl._delegated = true;
  }
}

// Point-at-a-window from the Game Mode modal: the same native drag capture as
// the app picker, kind "game" so the result is always the exe and routes to
// the Game Mode list.
$('#gm-point').addEventListener('pointerdown', e => {
  if (e.button !== 0) return;
  e.preventDefault();
  $('#gm-point').classList.add('capturing');
  send('capture-start', { kind: 'game' });
});


// ----- C# message handling -----
// ----- supporter surface (SUPPORT section + honor dialog) -----
// Honor system: State B renders purely because a name is stored. Nothing is
// checked, nothing is sent anywhere; the order field is never parsed.

function supporterState() {
  return (config?.general?.supporterName || '').trim().length > 0 ? 'b' : 'a';
}

function renderSupportPanel() {
  const root = $('#support-root');
  if (!root) return;
  if (supporterState() === 'b') {
    const name = config.general.supporterName.trim();
    const order = (config.general.supporterOrder || '').trim();
    // Information order is deliberate: name, then the thank-you as the
    // actual message, then the key demoted to hairline-footer metadata.
    // The footer renders even with an empty key so the layout never shifts.
    root.innerHTML = `
      <div class="support-panel state-b">
        <div class="support-head">
          ${iconHtml('heart-filled')}
          <span class="support-name">Licensed to ${esc(name)}</span>
        </div>
        <div class="support-sub">Thank you for supporting GlideScroll.</div>
        <div class="support-foot">
          <span class="support-key">${esc(order)}</span>
          <button class="support-edit" id="support-edit"
                  title="Edit or clear" aria-label="Edit supporter details">${iconHtml('pencil')}</button>
        </div>
      </div>`;
  } else {
    root.innerHTML = `
      <div class="support-panel state-a">
        <div class="support-head">
          ${iconHtml('heart')}
          <span class="support-name">Support GlideScroll</span>
        </div>
        <div class="support-sub">Free and unlocked. Pay what you want.</div>
        <div class="support-actions">
          <button class="primary" id="support-buy">${iconHtml('external-link')}Open buy page</button>
          <button class="subtle" id="support-enter">Already supported</button>
        </div>
      </div>`;
  }
}

// The Clear button exists in the DOM only when a name was stored at open
// time (spec 6.3); we detach/reattach the same node rather than hide it.
const supporterClearBtn = $('#supporter-clear');
let supporterReturnFocus = null;

function openSupporterDialog(triggerBtn) {
  supporterReturnFocus = triggerBtn || null;
  const nameEl = $('#supporter-name');
  const orderEl = $('#supporter-order');
  const hasName = supporterState() === 'b';
  nameEl.value = hasName ? config.general.supporterName : '';
  orderEl.value = hasName ? (config.general.supporterOrder || '') : '';
  const footer = $('#supporter-modal .modal-footer');
  if (hasName) {
    if (!supporterClearBtn.parentElement) footer.insertBefore(supporterClearBtn, footer.firstChild);
  } else {
    supporterClearBtn.remove();
  }
  $('#supporter-save').disabled = nameEl.value.trim().length === 0;
  $('#supporter-modal').classList.remove('hidden');
  nameEl.focus();
  if (hasName) nameEl.select();
}

function closeSupporterDialog() {
  $('#supporter-modal').classList.add('hidden');
  if (supporterReturnFocus && document.contains(supporterReturnFocus)) supporterReturnFocus.focus();
  supporterReturnFocus = null;
}

function saveSupporter(name, order) {
  config.general.supporterName = name;
  config.general.supporterOrder = order;
  send('set', { key: 'general.supporterName', value: name });
  send('set', { key: 'general.supporterOrder', value: order });
  closeSupporterDialog();
  renderSupportPanel();
}

// Panel buttons re-render with their panel, so delegate from the stable root.
document.addEventListener('click', e => {
  if (e.target.closest('#support-buy')) {
    send('open-url', { url: 'https://portghost.com/buy' });
  } else if (e.target.closest('#support-enter')) {
    openSupporterDialog(e.target.closest('#support-enter'));
  } else if (e.target.closest('#support-edit')) {
    openSupporterDialog(e.target.closest('#support-edit'));
  } else if (e.target.closest('#supporter-save')) {
    if (!$('#supporter-save').disabled)
      saveSupporter($('#supporter-name').value.trim(), $('#supporter-order').value.trim());
  } else if (e.target.closest('#supporter-clear')) {
    saveSupporter('', '');
  } else if (e.target.closest('[data-close="supporter-modal"]')) {
    // The generic closer already hid the modal; this restores focus.
    closeSupporterDialog();
  }
});

document.addEventListener('input', e => {
  if (e.target.id === 'supporter-name')
    $('#supporter-save').disabled = e.target.value.trim().length === 0;
});

document.addEventListener('keydown', e => {
  if (e.key === 'Enter' && !$('#supporter-modal').classList.contains('hidden')) {
    if (e.target.id === 'supporter-name' || e.target.id === 'supporter-order') {
      if (!$('#supporter-save').disabled)
        saveSupporter($('#supporter-name').value.trim(), $('#supporter-order').value.trim());
    }
  } else if (e.key === 'Escape') {
    // Close the topmost open modal only. The supporter dialog stacks above
    // Preferences, so it always goes first.
    if (!$('#supporter-modal').classList.contains('hidden')) {
      closeSupporterDialog();
    } else {
      const open = $$('.modal:not(.hidden)');
      if (open.length) open[open.length - 1].classList.add('hidden');
    }
  }
});

window.chrome.webview.addEventListener('message', e => {
  let msg;
  try { msg = typeof e.data === 'string' ? JSON.parse(e.data) : e.data; }
  catch { return; }

  if (msg.type === 'init') {
    config = msg.config;
    activePalette = msg.activePalette;
    document.documentElement.setAttribute('data-system-dark', msg.systemDark ? 'true' : 'false');
    bindAllControls();
  } else if (msg.type === 'palette') {
    activePalette = msg.activePalette;
    applyActivePalette();
    // Refresh the editor grid so switching themes (or Reset) shows the new
    // theme's hexes immediately. NEVER rebuild while a native color picker
    // is open (its input would be destroyed and the popup force-closed);
    // during a drag the grid is already live-synced locally.
    const cg = $('#color-grid');
    const ae = document.activeElement;
    const picking = cg && ae && cg.contains(ae) && ae.type === 'color';
    if (!picking) renderColorGrid();
  } else if (msg.type === 'system-theme') {
    document.documentElement.setAttribute('data-system-dark', msg.systemDark ? 'true' : 'false');
  } else if (msg.type === 'running-apps') {
    runningApps = msg.apps || [];
    renderPickerList(runningApps);
  } else if (msg.type === 'config-update') {
    config = msg.config;
    activePalette = msg.activePalette;
    bindAllControls();
  } else if (msg.type === 'apps-list-changed') {
    config.apps = msg.apps;
    refreshAppsList();
  } else if (msg.type === 'optimize-result') {
    const modeLabels = { Flywheel: 'Notch with Glide', Spring: 'Liquid Glide', Hybrid: 'Smoothed Notch', LowLatency: 'Low Latency', Silk: 'Silk', Cadence: 'Cadence' };
    const modeName = modeLabels[msg.mode] || msg.mode;
    $('#optimize-desc').textContent =
      `Tuned ${modeName} for ${msg.refreshRate}Hz @ ${msg.widthPx}×${msg.heightPx} on a ${msg.diagonalInches}" panel (${msg.ppi} PPI). ` +
      `Glide ${msg.animationTimeMs}ms, step ${msg.stepSize}px.`;
  } else if (msg.type === 'pan-optimize-result') {
    $('#pan-optimize-desc').textContent =
      `Tuned pan for ${msg.widthPx}×${msg.heightPx} on a ${msg.diagonalInches}" panel (${msg.ppi} PPI). ` +
      `Sensitivity ${msg.sensitivity}, activation ${msg.activation}px, min step ${msg.minStep}px.`;
  } else if (msg.type === 'capture-result') {
    if (msg.kind === 'class') {
      $('#class-finder').classList.remove('capturing');
      if (msg.ok && msg.cls) {
        $('#class-input').value = msg.cls;
        updateClassWarn();
        $('#class-preview').innerHTML = `Captured: <span class="cls">${esc(msg.cls)}</span>${msg.app ? ' · ' + esc(msg.app) : ''}`;
      } else {
        $('#class-preview').textContent = msg.note || 'No window captured. Try again.';
      }
    } else if (msg.kind === 'app') {
      $('#pick-window').classList.remove('capturing');
      if (msg.ok && msg.entry) {
        const mode = config.apps.mode;
        const arr = mode === 'exclusion' ? config.apps.blacklist : config.apps.whitelist;
        if (!arr.some(x => x.toLowerCase() === msg.entry.toLowerCase())) arr.push(msg.entry);
        send('apps-add', { exe: msg.entry });
        refreshAppsList();
        $('#picker-modal').classList.add('hidden');
      }
    } else if (msg.kind === 'game') {
      $('#gm-point').classList.remove('capturing');
      if (msg.ok && msg.entry) {
        const arr = config.gameMode.games;
        if (!arr.some(x => x.toLowerCase() === msg.entry.toLowerCase())) arr.push(msg.entry);
        send('gamemode-add', { exe: msg.entry });
        renderGameModeList();
        // Modal stays open so several games can be added in a row.
      }
    }
  } else if (msg.type === 'gamemode-changed') {
    config.gameMode = msg.gameMode;
    renderGameModeList();
  } else if (msg.type === 'window-state') {
    document.body.setAttribute('data-maximized', msg.maximized ? 'true' : 'false');
  }
});

// ----- hover tooltip system -----
// Plain-English explanations for every user-facing parameter, surfaced via a
// 1-second hover-delay popup. Dictionary keyed by:
//   data-key       (for inputs and selects)
//   data-toggle    (for the master ON/OFF toggles)
//   action:NAME    (for buttons with data-action="NAME")
//   data-tip-id    (for elements with no other identifier, set explicitly)
// findTipKey() walks the element + its first qualifying inner element to
// pick the right key. Tips don't show while the user has focus on a number
// or text field (so they don't cover what you're editing).
const TIPS = {
  // Wheel / Scrolling tab
  'wheel.enabled': 'Master switch for the wheel smoothing system. When off, your mouse wheel works normally with no smoothing applied.',
  'wheel.smoothingMode': 'Picks which smoothing algorithm runs. Each engine has a different feel, try them to see which you prefer.',
  'wheel.stepSize': 'Pixels of scroll produced per wheel notch. Higher means each notch moves the page further.',
  'wheel.animationTimeMs': 'How long each notch takes to finish playing out, in milliseconds. Higher feels smoother but slower to respond.',
  // wheel.feelBias tip is owned by SMOOTHNESS_OVERRIDES below and rewritten
  // each time the mode changes so the field has a mode-specific explanation.
  'wheel.accelWindowMs': 'When you scroll multiple notches within this many milliseconds of each other, the scroll boosts to feel snappier. Smaller value = boost only triggers on very rapid scrolling.',
  'wheel.accelBoost': 'The maximum multiplier the rapid-scroll boost can reach. 7× means rapid scrolling can move up to 7 times faster than a single notch.',
  'wheel.shiftWheelHorizontal': 'When on, holding Shift while scrolling vertically scrolls the page horizontally instead.',
  'wheel.smoothHorizontal': 'Apply the same smoothing engine to horizontal wheel events. When off, horizontal scrolling passes through unchanged.',
  'wheel.reverseDirection': 'Inverts wheel direction. Scrolling up moves the page down and vice versa.',
  // Pan tab
  'pan.enabled': 'Master switch for middle-button drag panning. When off, middle-click works normally with no pan behavior.',
  'pan.mode': 'Feather drives the scroll straight from your hand motion for an immediate, featherweight feel (paper under your palm). Cushion adds smoothing and damping for a softer, heavier glide. Both flick and coast on release.',
  'pan.friction': 'How quickly the kinetic coast slows down after you release the middle button (only with Flick mode on). Higher = stops faster. Has no effect during active panning.',
  'pan.sensitivityX': 'How much horizontal scroll happens per pixel of cursor motion. Higher = same hand motion produces more scroll. Not a speed cap, your scroll speed always tracks your hand speed times this multiplier.',
  'pan.sensitivityY': 'How much vertical scroll happens per pixel of cursor motion. Higher = same hand motion produces more scroll.',
  'pan.smoothness': 'Filters out small hand-tremor jitter from the scroll motion. 0 = filter off (raw motion). Higher values smooth more aggressively but add slight lag.',
  'pan.activationThresholdPixels': 'How many pixels you have to drag before pan mode engages. Below this, the middle-button press is treated as a click. Lower = pan engages faster but middle-click becomes harder to use as a click.',
  'pan.minStepPixels': 'The smallest scroll movement (in pixels) that gets sent out. Smaller values = smoother visual scroll, slightly more event traffic. Default 1 = per-pixel emit.',
  'pan.flickMode': 'When on, releasing the middle button captures your motion and continues coasting via friction decay. When off, releasing instantly stops all motion.',
  'pan.lockCursorDuringPan': 'When on, the cursor stays visually pinned in place during a pan while your hand moves freely. When off, the cursor moves with your hand so you can see where it is.',
  'pan.snapToAxis': 'When on, only the dominant axis (whichever way you are moving more) emits scroll, suppressing diagonal motion. Useful for content that does not handle diagonal scroll well.',
  'pan.reverseDirection': 'Inverts pan direction. Drag down to scroll up, instead of drag down to scroll down.',
  // General / shared
  'general.screenDiagonalInches': 'Physical size of your monitor measured corner-to-corner in inches. Used by Optimize to scale scroll distance to your screen pixel density. Same setting in both Scrolling and Panning tabs.',
  'general.autostartWithWindows': 'When on, GlideScroll launches automatically every time you sign in to Windows.',
  'general.minimizeToTrayOnClose': 'When on, clicking the X button hides this window to the system tray instead of fully exiting. Right-click the tray icon → Exit to fully quit.',
  'general.showNotificationOnModeToggle': 'When on, shows a brief Windows notification each time you add an app to the active list via the tray menu Disable for app / Enable for app submenu.',
  // Theme
  'theme.appearance': 'Color theme for this settings window. Auto follows your Windows light/dark setting. Custom unlocks the six-color picker below.',
  // Action buttons
  'action:optimize-monitor': 'Auto-tunes glide duration, scroll distance, and smoothness to recommended values for your monitor refresh rate, resolution, and physical size.',
  'action:optimize-pan': 'Auto-tunes pan sensitivity and threshold values for your monitor pixel density. Does not change Coast Friction or Smoothness, those are personal feel preferences.',
  'action:reset-wheel': 'Restores all wheel smoothing settings to factory defaults.',
  'action:reset-pan': 'Restores all pan settings to factory defaults.',
  'action:add-running': 'Pick from a list of currently-running visible apps to add to the active list.',
  'action:add-browse': 'File picker to select any .exe by hand and add to the active list.',
  'action:add-class': 'Advanced. Block or allow a surface by its window class instead of its exe. Drag the finder onto a window to capture its class, or type one. Meant for surfaces smoothing cannot help, like File Explorer.',
  'action:restore-defaults': 'Bring back the default class blocks. Choose Re-add only to keep your own entries, or Reset everything to return this list to its factory state.',
  'action:game-mode': 'Automatically pause GlideScroll in games. Detects anti-cheat, fullscreen games, and a list of known game EXEs you can edit.',
  'action:theme-reset': 'Restore the factory colors of ONLY the theme currently selected in the dropdown. Other themes keep your edits.',
  'gameMode.enabled': 'Master switch for Game Mode. When on, GlideScroll pauses itself while the window under your cursor belongs to a detected game, and resumes the moment you are back on a normal window.',
  'action:gm-browse': 'Pick a game .exe by hand to add it to the Game Mode list.',
  'action:gm-restore': 'Re-add any shipped default games you removed. Games you added yourself are kept.',
};

const TIP_SELECTORS = '.num-row, .checkbox-row, .master-row, .mode-dropdown, .optimize-row, button[data-action], select[data-key], [data-tip-id]';

let tipTimer = null;
let currentTipEl = null;
let typingActive = false;
let lastMouseX = 0, lastMouseY = 0;

function findTipKey(el) {
  // Direct attributes on the hovered element take priority over inner ones.
  if (el.dataset?.tipId) return el.dataset.tipId;
  if (el.dataset?.key) return el.dataset.key;
  if (el.dataset?.toggle) return el.dataset.toggle;
  if (el.dataset?.action) return 'action:' + el.dataset.action;
  // Fall back to the first qualifying inner element.
  const inner = el.querySelector?.('[data-key], [data-toggle], [data-action]');
  if (inner) {
    if (inner.dataset.key) return inner.dataset.key;
    if (inner.dataset.toggle) return inner.dataset.toggle;
    if (inner.dataset.action) return 'action:' + inner.dataset.action;
  }
  return null;
}

function ensureTipEl() {
  let tip = document.getElementById('global-tooltip');
  if (!tip) {
    tip = document.createElement('div');
    tip.id = 'global-tooltip';
    tip.className = 'tooltip-popup';
    document.body.appendChild(tip);
  }
  return tip;
}

function showTipPopup(text) {
  const tip = ensureTipEl();
  tip.textContent = text;
  // Show invisible to measure, then position with viewport flip.
  tip.style.left = '0px';
  tip.style.top = '0px';
  tip.classList.add('visible');
  const rect = tip.getBoundingClientRect();
  let left = lastMouseX + 14;
  let top = lastMouseY + 18;
  if (left + rect.width > window.innerWidth - 8) left = lastMouseX - rect.width - 14;
  if (top + rect.height > window.innerHeight - 8) top = lastMouseY - rect.height - 14;
  if (left < 4) left = 4;
  if (top < 4) top = 4;
  tip.style.left = left + 'px';
  tip.style.top = top + 'px';
}

function hideTipPopup() {
  const tip = document.getElementById('global-tooltip');
  if (tip) tip.classList.remove('visible');
}

function scheduleTip(el) {
  if (typingActive) return;
  cancelTip();
  currentTipEl = el;
  tipTimer = setTimeout(() => {
    if (typingActive) return;
    const key = findTipKey(el);
    if (!key || !TIPS[key]) return;
    showTipPopup(TIPS[key]);
  }, 1000);
}

function cancelTip() {
  if (tipTimer) { clearTimeout(tipTimer); tipTimer = null; }
  hideTipPopup();
  currentTipEl = null;
}

document.addEventListener('mouseover', e => {
  // Capture coords here instead of via a global mousemove listener (which
  // fires at up to 1000 Hz on modern mice and does nothing 99% of the time
  // since lastMouseX/Y is only read when a tooltip actually shows).
  lastMouseX = e.clientX;
  lastMouseY = e.clientY;

  const target = e.target.closest?.(TIP_SELECTORS);
  if (target && target !== currentTipEl) {
    scheduleTip(target);
  }
});

document.addEventListener('mouseout', e => {
  const target = e.target.closest?.(TIP_SELECTORS);
  if (target === currentTipEl) {
    // Only hide if we're truly leaving the current target (not entering a child).
    const newTarget = e.relatedTarget?.closest?.(TIP_SELECTORS);
    if (newTarget !== currentTipEl) cancelTip();
  }
});

// While typing in a number/text field, suppress tooltips so they don't cover
// the field the user is editing.
document.addEventListener('focusin', e => {
  if (e.target.matches?.('input[type="number"], input[type="text"]')) {
    typingActive = true;
    cancelTip();
  }
});
document.addEventListener('focusout', e => {
  if (e.target.matches?.('input[type="number"], input[type="text"]')) {
    typingActive = false;
  }
});

// ----- frameless title bar wiring -----
// Drag: mousedown on the drag region asks C# to start native window-drag
// via ReleaseCapture + SendMessage(WM_NCLBUTTONDOWN, HTCAPTION). The OS
// then handles drag, snap, and double-click-to-maximize natively.
$('#titlebar-drag').addEventListener('mousedown', e => {
  if (e.button !== 0) return;
  send('titlebar-drag');
});
// Double-click on the drag region toggles maximize (matches Windows convention).
$('#titlebar-drag').addEventListener('dblclick', e => {
  if (e.button !== 0) return;
  send('titlebar-max');
});

$('#btn-min').addEventListener('click', () => send('titlebar-min'));
$('#btn-max').addEventListener('click', () => send('titlebar-max'));
$('#btn-close').addEventListener('click', () => send('titlebar-close'));

// Resize edges: each one tells C# which edge so it can pick the right HT*.
document.querySelectorAll('.resize-edge').forEach(el => {
  el.addEventListener('mousedown', e => {
    if (e.button !== 0) return;
    send('titlebar-resize', { edge: el.dataset.edge });
  });
});

// kick off
send('ready');
</script>
</body>
</html>
""";
}
