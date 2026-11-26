"use strict";

const bridge = window.chrome?.webview;
const byId = id => document.getElementById(id);
const canvas = byId("canvas");
const triggers = [...document.querySelectorAll("[data-editor]")];
const controls = new Map([...document.querySelectorAll("[data-field]")]
  .map(control => [control.dataset.field, control]));
const defaultStyle = Object.freeze({
  font: "",
  color: "",
  style: "",
  voice: "",
  background: null,
  spacing: null,
  radius: null,
  sampleText: null
});
const titleFonts = '"华文宋体", "STSong", "SimSun", serif';
const theme = Object.freeze({
  ...defaultStyle,
  color: "#24766D",
  background: "#FCFAF5",
  spacing: 24,
  radius: 16,
  sampleText: byId("sample").textContent,
  style: byId("work-style").textContent
});
const colorFields = ["color", "background"];
const rangeFields = ["spacing", "radius"];
const textFields = ["style", "voice", "sampleText"];
const colorPattern = /^#[0-9a-f]{6}$/i;
const invalidSingleLine = /[\u0000-\u001f\u007f-\u009f\u2028-\u202e\u2066-\u2069]/u;
const invalidSample = /[\u0000-\u0009\u000b-\u001f\u007f-\u009f\u202a-\u202e\u2066-\u2069]/u;

let draft = { ...defaultStyle };
let globalDefaults = defaultStyle;
let epoch = null;
let activeTrigger = null;
let installedFonts = new Map();

function textField(value, limit, { multiline = false } = {}) {
  if (typeof value !== "string") return "";
  const text = (multiline ? value.replace(/\r\n|[\r\u0085\u2028\u2029]/gu, "\n") : value).trim();
  const invalid = multiline ? invalidSample : invalidSingleLine;
  return text.length <= limit && text.isWellFormed() && !invalid.test(text) ? text : null;
}

const fontKey = font => font.toLocaleLowerCase("en-US");

function readFonts(fonts) {
  const result = new Map();
  for (const value of Array.isArray(fonts) ? fonts : []) {
    const name = textField(value, 100);
    if (name && !result.has(fontKey(name))) result.set(fontKey(name), name);
  }
  return result;
}

function hexColor(value) {
  return typeof value === "string" && value.length === 7 && colorPattern.test(value) ? value.toUpperCase() : null;
}

function boundedInteger(value, maximum) {
  return Number.isInteger(value) && value >= 0 && value <= maximum ? value : null;
}

function readStyle(style) {
  return {
    font: textField(style?.font, 100) ?? "",
    color: hexColor(style?.color) ?? "",
    style: textField(style?.style, 240) ?? "",
    voice: textField(style?.voice, 240) ?? "",
    background: hexColor(style?.background),
    spacing: boundedInteger(style?.spacing, 64),
    radius: boundedInteger(style?.radius, 48),
    sampleText: textField(style?.sampleText, 500, { multiline: true }) || null
  };
}

const hasValue = value => value !== "" && value !== null;
const inheritsGlobal = field => hasValue(globalDefaults[field]);
const fallbackValue = field => inheritsGlobal(field) ? globalDefaults[field] : theme[field];
const effectiveValue = field => hasValue(draft[field]) ? draft[field] : fallbackValue(field);
const fallbackSource = field => inheritsGlobal(field) ? "继承全局" : "主题默认";

function draftSnapshot() {
  return { type: "draft", epoch, style: { ...draft } };
}

window.amifolioDraftSnapshot = draftSnapshot;

const sourceNotes = new Map();
for (const field of [...colorFields, ...textFields]) {
  const control = controls.get(field);
  const hex = byId(field + "-hex");
  const note = document.createElement("p");
  note.id = control.id + "-source";
  note.className = "field-note source-note";
  note.hidden = true;
  const anchor = hex ? byId(field + "-hint") : control;
  anchor.after(note);
  for (const input of hex ? [control, hex] : [control]) {
    const describedBy = input.getAttribute("aria-describedby");
    input.setAttribute("aria-describedby", describedBy ? describedBy + " " + note.id : note.id);
  }
  sourceNotes.set(field, note);
}

function renderSourceNote(field, label) {
  const note = sourceNotes.get(field);
  note.textContent = label;
  note.hidden = hasValue(draft[field]);
}

function renderFontOptions() {
  const picker = byId("font");
  const inherited = globalDefaults.font;
  const defaultLabel = inherited
    ? "继承全局 · " + inherited + (installedFonts.has(fontKey(inherited)) ? "" : "（未安装）")
    : "主题默认（华文宋体）";
  const options = [new Option(defaultLabel, "")];
  const installed = installedFonts.get(fontKey(draft.font));
  if (draft.font && !installed) {
    const unavailable = new Option(draft.font + "（未安装）", draft.font);
    unavailable.disabled = true;
    options.push(unavailable);
  }
  const sorted = [...installedFonts.values()].sort(new Intl.Collator("zh-CN").compare);
  options.push(...sorted.map(name => new Option(name, name)));
  picker.replaceChildren(...options);
  picker.value = installed ?? draft.font;
  renderFontNote();
}

function renderFontNote() {
  const note = byId("font-note");
  const requested = effectiveValue("font");
  const unavailable = requested && !installedFonts.has(fontKey(requested));
  const source = draft.font ? "所选字体" : "继承全局字体";
  note.textContent = unavailable ? source + "「" + requested + "」未安装，预览使用主题字体。" : "";
  note.hidden = !unavailable;
}

function useDarkInk(hex) {
  const [red, green, blue] = hex.slice(1).match(/../g).map(pair => {
    const channel = Number.parseInt(pair, 16) / 255;
    return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
  });
  return red * 0.2126 + green * 0.7152 + blue * 0.0722 > 0.179;
}

function renderCanvas() {
  const accent = effectiveValue("color");
  const background = effectiveValue("background");
  const canvasInk = background === theme.background ? "#303A35" : useDarkInk(background) ? "#000" : "#fff";
  const family = installedFonts.get(fontKey(effectiveValue("font")));
  const quotedFamily = family ? '"' + family.replace(/["\\]/g, "\\$&") + '", ' : "";
  canvas.style.setProperty("--accent", accent);
  canvas.style.setProperty("--on-accent", useDarkInk(accent) ? "#000" : "#fff");
  canvas.style.setProperty("--canvas-paper", background);
  canvas.style.setProperty("--canvas-ink", canvasInk);
  canvas.style.setProperty("--spacing", effectiveValue("spacing") + "px");
  canvas.style.setProperty("--radius", effectiveValue("radius") + "px");
  canvas.style.setProperty("--heading-font", quotedFamily + titleFonts);
  document.querySelector(".swatch").style.backgroundColor = background;
  byId("sample").textContent = effectiveValue("sampleText");
  byId("work-style").textContent = effectiveValue("style");
  const voice = byId("voice-note");
  const effectiveVoice = effectiveValue("voice");
  voice.textContent = effectiveVoice ? "语气 · " + effectiveVoice : "";
  voice.hidden = !effectiveVoice;
}

function renderColorControl(field, keepText = false) {
  controls.get(field).value = effectiveValue(field);
  const hex = byId(field + "-hex");
  if (!keepText) hex.value = draft[field] ?? "";
  hex.placeholder = fallbackValue(field);
  hex.removeAttribute("aria-invalid");
  byId(field + "-hint").hidden = true;
  renderSourceNote(field, fallbackSource(field) + " · " + fallbackValue(field));
  renderReset(field);
}

function renderReset(field) {
  const reset = document.querySelector('[data-clear="' + field + '"]');
  reset.textContent = inheritsGlobal(field) ? "恢复继承" : "用主题默认";
  reset.disabled = !hasValue(draft[field]);
}

function renderRange(field) {
  const range = controls.get(field);
  const value = effectiveValue(field).toString();
  range.value = value;
  const description = value + " px" + (hasValue(draft[field]) ? "" : "（" + fallbackSource(field) + "）");
  range.setAttribute("aria-valuetext", description);
  byId(field + "-value").value = description;
  renderReset(field);
}

function renderTextSource(field) {
  controls.get(field).placeholder = fallbackValue(field) || "例如轻松、自然";
  const exampleLabel = field === "voice" ? "未设置语气" : "示例默认";
  renderSourceNote(field, inheritsGlobal(field) ? "继承全局" : exampleLabel);
}

function renderState() {
  renderFontOptions();
  for (const field of colorFields) renderColorControl(field);
  for (const field of rangeFields) renderRange(field);
  for (const field of textFields) {
    controls.get(field).value = draft[field] ?? "";
    controls.get(field).removeAttribute("aria-invalid");
    renderTextSource(field);
  }
  renderCanvas();
}

function updateDraft(field, value) {
  if (draft[field] === value) return;
  draft[field] = value;
  renderCanvas();
  if (bridge && epoch !== null) bridge.postMessage(draftSnapshot());
}

function changeEditor(next, moveFocus = false) {
  if (activeTrigger) {
    byId(activeTrigger.dataset.editor).hidden = true;
    activeTrigger.setAttribute("aria-expanded", "false");
  }
  activeTrigger = next;
  if (next) {
    const panel = byId(next.dataset.editor);
    panel.hidden = false;
    next.setAttribute("aria-expanded", "true");
    if (moveFocus) panel.querySelector("input, select, textarea").focus();
  }
}

for (const trigger of triggers) {
  trigger.addEventListener("click", () => changeEditor(activeTrigger === trigger ? null : trigger, true));
}
canvas.addEventListener("click", event => {
  if (event.target !== canvas) return;
  const trigger = triggers.find(button => button.dataset.editor === "background-editor");
  if (!trigger.disabled) trigger.click();
});
document.addEventListener("keydown", event => {
  if (event.key !== "Escape" || event.defaultPrevented || !activeTrigger) return;
  const previous = activeTrigger;
  changeEditor(null);
  previous.focus();
  event.preventDefault();
});

byId("font").addEventListener("change", event => {
  updateDraft("font", event.currentTarget.value);
  renderFontOptions();
});

for (const field of textFields) {
  const control = controls.get(field);
  const multiline = field === "sampleText";
  const apply = event => {
    if (event.isComposing) return;
    const value = textField(control.value, control.maxLength, { multiline });
    if (value === null) {
      control.setAttribute("aria-invalid", "true");
      return;
    }
    control.removeAttribute("aria-invalid");
    updateDraft(field, multiline ? value || null : value);
    renderTextSource(field);
  };
  control.addEventListener("input", apply);
  control.addEventListener("compositionend", apply);
  control.addEventListener("blur", () => {
    control.value = draft[field] ?? "";
    control.removeAttribute("aria-invalid");
  });
}

for (const field of colorFields) {
  const color = controls.get(field);
  const hex = byId(field + "-hex");
  const fallback = defaultStyle[field];
  color.addEventListener("input", () => {
    updateDraft(field, hexColor(color.value) ?? fallback);
    renderColorControl(field);
  });
  hex.addEventListener("input", () => {
    const value = hexColor(hex.value);
    const invalid = hex.value.length > 0 && value === null;
    byId(field + "-hint").hidden = !invalid;
    if (invalid) {
      hex.setAttribute("aria-invalid", "true");
      return;
    }
    updateDraft(field, value ?? fallback);
    renderColorControl(field, true);
  });
  hex.addEventListener("blur", () => renderColorControl(field));
  document.querySelector('[data-clear="' + field + '"]').addEventListener("click", () => {
    updateDraft(field, fallback);
    renderColorControl(field);
    hex.focus();
  });
}

for (const field of rangeFields) {
  const control = controls.get(field);
  control.addEventListener("input", () => {
    updateDraft(field, boundedInteger(control.valueAsNumber, Number(control.max)));
    renderRange(field);
  });
  document.querySelector('[data-clear="' + field + '"]').addEventListener("click", () => {
    updateDraft(field, null);
    renderRange(field);
    control.focus();
  });
}

function loadState(message) {
  epoch = message.epoch;
  draft = readStyle(message.style);
  globalDefaults = readStyle(message.defaults);
  installedFonts = readFonts(message.fonts);
  changeEditor(null);
  renderState();
  for (const trigger of triggers) trigger.disabled = false;
}

if (bridge) {
  bridge.addEventListener("message", event => {
    const message = event.data;
    if (message?.type === "state" && Number.isSafeInteger(message.epoch)) loadState(message);
  });
  bridge.postMessage({ type: "ready" });
} else {
  loadState({ epoch: 0, style: null, fonts: [] });
}
