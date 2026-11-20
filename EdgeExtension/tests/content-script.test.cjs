const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "../content-script.js"), "utf8");

function createFixture() {
  const messages = [];
  const highlighters = [];
  const observers = [];
  const timers = [];
  let listener;
  let toast = null;
  let uuid = 0;
  let articleBuilds = 0;
  let articleText = "第一句。第二句。";
  let aiText = null;
  let aiOptions;
  const aiStates = [];
  class Element {
    closest() { return null; }
  }
  class Highlighter {
    constructor() { this.ranges = []; this.cleared = false; highlighters.push(this); }
    set(start, length) { this.ranges.push({ start, length }); }
    clear() { this.cleared = true; }
  }
  class MutationObserver {
    constructor(callback) { this.callback = callback; observers.push(this); }
    observe() {}
    disconnect() {}
  }
  const snapshot = {
    text: articleText,
    runs: [{ node: {}, nodeStart: 0, nodeEnd: articleText.length, globalStart: 0, globalEnd: articleText.length }],
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
    setTimeout(callback, delay) { const timer = { callback, delay, cancelled: false }; timers.push(timer); return timer; },
    clearTimeout(timer) { if (timer) timer.cancelled = true; },
    getSelection: () => null,
    TextEncoder,
    Element,
    Node: { ELEMENT_NODE: 1 },
    MutationObserver,
    ListenFlowSnapshotCore: {
      findSentenceRange: (_text, offset) => offset < 4
        ? { start: 0, length: 4 }
        : { start: 4, length: 4 },
      remapOffset: (_previous, next, offset) => Math.min(offset, Math.max(0, next.length - 1))
    },
    ListenFlowDomSnapshot: {
      buildArticleSnapshot: () => {
        articleBuilds++;
        return {
          ...snapshot,
          text: articleText,
          runs: [{ ...snapshot.runs[0], nodeEnd: articleText.length, globalEnd: articleText.length }]
        };
      },
      buildPageSnapshot: () => ({ ...snapshot, source: "page", runs: [...snapshot.runs] }),
      buildSelectionSnapshot: () => snapshot,
      buildElementSnapshot: (_element, includeCode) => ({ ...snapshot, text: aiText ?? snapshot.text, source: includeCode ? "ai-code" : "ai" }),
      locatePoint: () => null,
      mutationAffectsSnapshot: (_snapshot, mutation) => mutation.relevant !== false
    },
    ListenFlowHighlighter: Highlighter,
    ListenFlowAIReader: { install(options) { aiOptions = options; return { onState: state => aiStates.push(state) }; } },
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
    dispatch, messages, snapshot, highlighters, aiOptions, aiStates,
    getToast: () => toast,
    getArticleBuilds: () => articleBuilds,
    setArticleText(value) { articleText = value; },
    setAIText(value) { aiText = value; },
    async triggerRelevantMutation() {
      observers.at(-1).callback([{
        type: "childList", relevant: true, target: {}, addedNodes: [{}], removedNodes: []
      }]);
      for (const timer of timers.filter(item => item.delay === 700 && !item.cancelled)) {
        timer.cancelled = true;
        timer.callback();
      }
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    }
  };
}

test("人工智能回复复用网页播放会话及完成事件，并保留桌面语速选择", async () => {
  const fixture = createFixture();
  await fixture.aiOptions.start({ isConnected: true }, false);
  const start = fixture.messages.find(item => item.kind === "bridge-command").command;
  assert.equal(start.type, "start");
  assert.equal(start.rate, null);
  assert.equal(start.text, fixture.snapshot.text);
  await fixture.dispatch({ kind: "bridge-event", message: {
    type: "state", state: "completed", sessionId: start.sessionId,
    snapshotId: start.snapshotId, snapshotVersion: start.snapshotVersion, playbackGeneration: 1
  }});
  assert.deepEqual(fixture.aiStates, ["completed"]);
});

test("超长人工智能回复在发送前报错且保留已有网页播放会话", async () => {
  const fixture = createFixture();
  await fixture.dispatch({ kind: "context-action", action: "start-article" });
  fixture.setAIText("中".repeat(5_000_001));
  await assert.rejects(fixture.aiOptions.start({ isConnected: true }, false), /通信上限/);
  assert.equal(fixture.messages.filter(item => item.kind === "bridge-command").length, 1);
  assert.equal(fixture.highlighters[0].cleared, false);
});

test("字符数未超限但编码后超过消息大小时拒绝发送", async () => {
  const fixture = createFixture();
  fixture.setAIText("中".repeat(2_800_000));
  await assert.rejects(fixture.aiOptions.start({ isConnected: true }, false), /通信上限/);
  assert.equal(fixture.messages.filter(item => item.kind === "bridge-command").length, 0);
});

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
  assert.equal(Object.hasOwn(start.command, "voiceName"), false, "网页启动应沿用桌面端选择的语音");
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

test("桌面切换语音产生新播放代次后_网页高亮接受新进度并忽略旧进度", async () => {
  const fixture = createFixture();
  await fixture.dispatch({ kind: "context-action", action: "start-article" });
  const start = fixture.messages.find(item => item.kind === "bridge-command").command;
  const identity = {
    sessionId: start.sessionId,
    snapshotId: start.snapshotId,
    snapshotVersion: start.snapshotVersion
  };
  await fixture.dispatch({ kind: "bridge-event", message: {
    ...identity, type: "progress", state: "speaking", playbackGeneration: 1, offset: 0, length: 1
  }});
  await fixture.dispatch({ kind: "bridge-event", message: {
    ...identity, type: "state", state: "speaking", playbackGeneration: 2, offset: 4
  }});
  await fixture.dispatch({ kind: "bridge-event", message: {
    ...identity, type: "progress", state: "speaking", playbackGeneration: 2, offset: 4, length: 1
  }});
  const rangesBeforeOldEvent = fixture.highlighters[0].ranges.length;
  assert.deepEqual(fixture.highlighters[0].ranges.at(-1), { start: 4, length: 4 });
  await fixture.dispatch({ kind: "bridge-event", message: {
    ...identity, type: "progress", state: "speaking", playbackGeneration: 1, offset: 0, length: 1
  }});
  assert.equal(fixture.highlighters[0].ranges.length, rangesBeforeOldEvent);
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

test("朗读中的网页追加正文后自动重建快照而不是继续依赖旧扫描", async () => {
  const fixture = createFixture();
  await fixture.dispatch({ kind: "context-action", action: "start-article", rate: 2 });
  const firstStart = fixture.messages.find(item => item.kind === "bridge-command").command;
  await fixture.dispatch({
    kind: "bridge-event",
    message: {
      type: "state", state: "speaking", sessionId: firstStart.sessionId,
      snapshotId: firstStart.snapshotId, snapshotVersion: firstStart.snapshotVersion,
      playbackGeneration: 1, offset: 4
    }
  });

  fixture.setArticleText("第一句。第二句。滚动后加载的第三句。");
  await fixture.triggerRelevantMutation();

  const starts = fixture.messages.filter(item =>
    item.kind === "bridge-command" && item.command.type === "start");
  assert.equal(starts.length, 2);
  assert.match(starts[1].command.text, /滚动后加载的第三句/);
  assert.equal(starts[1].command.sessionId, firstStart.sessionId);
  assert.ok(starts[1].command.commandSeq > firstStart.commandSeq);
  assert.equal(Object.hasOwn(starts[1].command, "voiceName"), false, "动态重扫不能覆盖桌面端的语音选择");
});
