const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "../content-script.js"), "utf8");

function createFixture() {
  const messages = [];
  const highlighters = [];
  let listener;
  let toast = null;
  let uuid = 0;
  let articleBuilds = 0;
  class Element {
    closest() { return null; }
  }
  class Highlighter {
    constructor() { this.ranges = []; this.cleared = false; highlighters.push(this); }
    set(start, length) { this.ranges.push({ start, length }); }
    clear() { this.cleared = true; }
  }
  class MutationObserver {
    constructor(callback) { this.callback = callback; }
    observe() {}
    disconnect() {}
  }
  const snapshot = {
    text: "第一句。第二句。",
    runs: [{ node: {}, globalStart: 0, globalEnd: 8 }],
    source: "article",
    title: "测试标题"
  };
  const document = {
    body: {},
    documentElement: {
      appendChild(element) { toast = element; }
    },
    addEventListener() {},
    querySelector(selector) { return selector === ".listenflow-status-toast" ? toast : null; },
    createElement() {
      return {
        className: "",
        textContent: "",
        remove() { if (toast === this) toast = null; }
      };
    }
  };
  const context = vm.createContext({
    document,
    location: { href: "https://fixture.invalid/article" },
    performance: { now: (() => { let now = 10; return () => now += 5; })() },
    crypto: { randomUUID: () => `id-${++uuid}` },
    setTimeout: () => 1,
    clearTimeout() {},
    getSelection: () => null,
    Element,
    Node: { ELEMENT_NODE: 1 },
    MutationObserver,
    ListenFlowSnapshotCore: {
      findSentenceRange: (_text, offset) => offset < 4
        ? { start: 0, length: 4 }
        : { start: 4, length: 4 }
    },
    ListenFlowDomSnapshot: {
      buildArticleSnapshot: () => { articleBuilds++; return { ...snapshot, runs: [...snapshot.runs] }; },
      buildPageSnapshot: () => ({ ...snapshot, source: "page", runs: [...snapshot.runs] }),
      buildSelectionSnapshot: () => snapshot,
      locatePoint: () => null
    },
    ListenFlowHighlighter: Highlighter,
    chrome: {
      runtime: {
        onMessage: { addListener(value) { listener = value; } },
        sendMessage(message) { messages.push(message); return Promise.resolve({ ok: true }); }
      }
    }
  });
  vm.runInContext(source, context);
  async function dispatch(message) {
    return new Promise((resolve, reject) => {
      listener(message, {}, response => response?.ok ? resolve(response.result) : reject(new Error(response?.error)));
    });
  }
  return {
    dispatch, messages, snapshot, highlighters,
    getToast: () => toast,
    getArticleBuilds: () => articleBuilds
  };
}

test("正文启动记录本地诊断元数据但不复制正文标题网址", async () => {
  const fixture = createFixture();

  await fixture.dispatch({ kind: "context-action", action: "start-article" });

  const diagnostic = fixture.messages.find(item =>
    item.kind === "browser-diagnostic" && item.diagnostic.diagnosticStage === "edge-snapshot-created");
  assert.ok(diagnostic);
  assert.equal(diagnostic.diagnostic.textLength, fixture.snapshot.text.length);
  assert.equal(diagnostic.diagnostic.nodeCount, 1);
  assert.equal(diagnostic.diagnostic.source, "article");
  assert.equal(Object.hasOwn(diagnostic.diagnostic, "text"), false);
  assert.equal(Object.hasOwn(diagnostic.diagnostic, "title"), false);
  assert.equal(Object.hasOwn(diagnostic.diagnostic, "sourceUri"), false);
  const start = fixture.messages.find(item => item.kind === "bridge-command");
  assert.equal(start.command.type, "start");
  assert.equal(start.command.rate, 2);
});

test("播放状态错误在清理会话前向用户显示真实错误", async () => {
  const fixture = createFixture();
  await fixture.dispatch({ kind: "context-action", action: "start-article" });
  const start = fixture.messages.find(item => item.kind === "bridge-command").command;

  await fixture.dispatch({
    kind: "bridge-event",
    message: {
      type: "state",
      state: "error",
      sessionId: start.sessionId,
      snapshotId: start.snapshotId,
      snapshotVersion: start.snapshotVersion,
      playbackGeneration: 2,
      message: "语音进度超出当前分段边界。"
    }
  });

  assert.match(fixture.getToast().textContent, /语音进度超出当前分段边界/);
});

test("停止后主界面再次播放会保留网页会话并重新扫描快照", async () => {
  const fixture = createFixture();
  await fixture.dispatch({ kind: "context-action", action: "start-article", rate: 2 });
  const firstStart = fixture.messages.find(item => item.kind === "bridge-command").command;

  await fixture.dispatch({
    kind: "bridge-event",
    message: {
      type: "state", state: "stopped", sessionId: firstStart.sessionId,
      snapshotId: firstStart.snapshotId, snapshotVersion: firstStart.snapshotVersion,
      playbackGeneration: 2, offset: 4
    }
  });
  assert.equal(fixture.highlighters[0].cleared, true);

  await fixture.dispatch({
    kind: "bridge-event",
    message: {
      type: "state", state: "speaking", sessionId: firstStart.sessionId,
      snapshotId: firstStart.snapshotId, snapshotVersion: firstStart.snapshotVersion,
      playbackGeneration: 3, offset: 4
    }
  });

  const starts = fixture.messages.filter(item =>
    item.kind === "bridge-command" && item.command.type === "start");
  assert.equal(starts.length, 2);
  assert.equal(starts[1].command.sessionId, firstStart.sessionId);
  assert.notEqual(starts[1].command.snapshotId, firstStart.snapshotId);
  assert.equal(fixture.getArticleBuilds(), 2);
});

test("右键语速命令立即发送到当前网页播放会话", async () => {
  const fixture = createFixture();
  await fixture.dispatch({ kind: "context-action", action: "start-article", rate: 2 });
  await fixture.dispatch({ kind: "context-action", action: "set-rate", rate: 4 });

  const command = fixture.messages.filter(item => item.kind === "bridge-command").at(-1).command;
  assert.equal(command.type, "set-rate");
  assert.equal(command.rate, 4);
  assert.match(fixture.getToast().textContent, /\+4/);
});
