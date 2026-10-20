const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "../content-script.js"), "utf8");

function fixture() {
  const domListeners = new Map();
  const sent = [];
  const highlighters = [];
  let runtimeListener;
  let selectionCollapsed = false;
  let resolver = () => null;
  let uuid = 0;
  class Element {
    constructor(kind = "plain") { this.kind = kind; }
    closest(selector) {
      if (this.kind === "link" && selector.includes("a")) return this;
      if (this.kind === "editable" && selector.includes("[contenteditable]")) return this;
      return null;
    }
  }
  class Highlighter {
    constructor(snapshot) { this.snapshot = snapshot; this.ranges = []; this.cleared = false; highlighters.push(this); }
    set(start, length) { this.ranges.push({ start, length }); }
    clear() { this.cleared = true; }
  }
  class MutationObserver { observe() {} disconnect() {} }
  const selectionSnapshot = {
    text: "选中文字。",
    title: "选区",
    source: "selection",
    runs: [{ node: { isConnected: true } }]
  };
  const expandedSnapshot = {
    text: "开头正文。中间正文。目标正文。末尾正文。",
    title: "同一文档",
    source: "document",
    runs: [{ node: { isConnected: true } }]
  };
  const selection = {
    get isCollapsed() { return selectionCollapsed; },
    rangeCount: 1,
    getRangeAt() { return { cloneRange: () => ({}) }; }
  };
  const document = {
    body: {},
    documentElement: { appendChild() {} },
    addEventListener(type, handler) { domListeners.set(type, handler); },
    querySelector() { return null; },
    createElement() { return { remove() {}, textContent: "", className: "" }; }
  };
  const context = vm.createContext({
    document,
    location: { href: "https://fixture.invalid/doc" },
    performance: { now: () => 1 },
    crypto: { randomUUID: () => `uuid-${++uuid}` },
    setTimeout: () => 1,
    clearTimeout() {},
    getSelection: () => selection,
    Element,
    Node: { ELEMENT_NODE: 1 },
    MutationObserver,
    ListenFlowSnapshotCore: {
      findSentenceRange: (_text, offset) => ({ start: offset, length: 2 })
    },
    ListenFlowDomSnapshot: {
      buildSelectionSnapshot: () => selectionSnapshot,
      buildArticleSnapshot: () => expandedSnapshot,
      resolveReadingTarget: (snapshot, x, y) => resolver(snapshot, x, y),
      isEditableTargetAllowed: () => true
    },
    ListenFlowHighlighter: Highlighter,
    chrome: {
      runtime: {
        onMessage: { addListener(handler) { runtimeListener = handler; } },
        sendMessage(message) { sent.push(message); return Promise.resolve({ ok: true }); }
      }
    }
  });
  vm.runInContext(source, context);

  function extension(message) {
    return new Promise((resolve, reject) => runtimeListener(message, {}, response =>
      response?.ok ? resolve(response.result) : reject(new Error(response?.error))));
  }
  function dom(type, values = {}) {
    let prevented = 0;
    let stopped = 0;
    domListeners.get(type)?.({
      button: 0, defaultPrevented: false, altKey: false, ctrlKey: false, metaKey: false, shiftKey: false,
      detail: 1, pointerId: 1, clientX: 10, clientY: 10, target: new Element(),
      preventDefault() { prevented++; }, stopImmediatePropagation() { stopped++; }, ...values
    });
    return { prevented, stopped };
  }
  async function startSelection() {
    selectionCollapsed = false;
    dom("contextmenu");
    await extension({ kind: "context-action", action: "start-selection" });
    selectionCollapsed = true;
    return sent.find(item => item.kind === "bridge-command").command;
  }
  return {
    sent, highlighters, selectionSnapshot, expandedSnapshot, Element, extension, dom, startSelection,
    setResolver(value) { resolver = value; },
    setSelectionCollapsed(value) { selectionCollapsed = value; }
  };
}

const settle = () => new Promise(resolve => setImmediate(resolve));

test("选区朗读时点击同一明确文档范围会用同一会话原子替换快照", async () => {
  const env = fixture();
  const first = await env.startSelection();
  env.setResolver(() => ({ snapshot: env.expandedSnapshot, offset: 12, expanded: true }));

  const event = env.dom("click");
  await settle();

  const commands = env.sent.filter(item => item.kind === "bridge-command").map(item => item.command);
  assert.equal(commands.length, 2);
  assert.equal(commands[1].type, "start");
  assert.equal(commands[1].sessionId, first.sessionId);
  assert.notEqual(commands[1].snapshotId, first.snapshotId);
  assert.equal(commands[1].text, env.expandedSnapshot.text);
  assert.equal(commands[1].offset, 12);
  assert.equal(event.prevented, 0);
  assert.equal(event.stopped, 0);
});

test("暂停时连续跨快照定位只保留最后目标并在继续时开始发声", async () => {
  const env = fixture();
  const first = await env.startSelection();
  await env.extension({ kind: "bridge-event", message: {
    type: "state", state: "paused", sessionId: first.sessionId,
    snapshotId: first.snapshotId, snapshotVersion: first.snapshotVersion, playbackGeneration: 2, offset: 0
  }});
  let click = 0;
  env.setResolver(snapshot => ++click === 1
    ? { snapshot: env.expandedSnapshot, offset: 8, expanded: true }
    : { snapshot, offset: 18, expanded: false });

  env.dom("click");
  env.dom("pointerdown");
  env.dom("click");
  await settle();
  assert.equal(env.sent.filter(item => item.kind === "bridge-command").length, 1,
    "暂停点击不得先启动语音");

  await env.extension({ kind: "context-action", action: "resume" });
  const commands = env.sent.filter(item => item.kind === "bridge-command").map(item => item.command);
  assert.equal(commands.length, 2);
  assert.equal(commands[1].type, "start");
  assert.equal(commands[1].offset, 18);
  assert.equal(commands.some(item => item.type === "resume"), false);
});

test("链接拖选和非折叠选区保持原交互，可编辑正文定位不阻止点击", async () => {
  const env = fixture();
  await env.startSelection();
  let resolutions = 0;
  env.setResolver(() => { resolutions++; return { snapshot: env.expandedSnapshot, offset: 6, expanded: true }; });

  env.dom("pointerdown", { target: new env.Element("link") });
  env.dom("click", { target: new env.Element("link") });
  env.dom("pointerdown");
  env.dom("pointermove", { clientX: 30, clientY: 30 });
  env.dom("click");
  env.setSelectionCollapsed(false);
  env.dom("pointerdown");
  env.dom("click");
  env.setSelectionCollapsed(true);
  env.dom("pointerdown", { target: new env.Element("editable") });
  const editableClick = env.dom("click", { target: new env.Element("editable") });
  await settle();

  assert.equal(resolutions, 1);
  assert.equal(editableClick.prevented, 0);
  assert.equal(editableClick.stopped, 0);
});
