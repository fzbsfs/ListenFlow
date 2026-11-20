const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "../ai-reader.js"), "utf8");
function createQueue(start, errors = []) {
  const context = vm.createContext({});
  vm.runInContext(source, context);
  return context.ListenFlowAIReader.createQueue(start, error => errors.push(error));
}
const turn = () => new Promise(resolve => setImmediate(resolve));

test("多条回复按完成事件串行朗读且忽略重复与移除节点", async () => {
  const calls = [];
  const queue = createQueue(async reply => calls.push(reply.id));
  const first = { id: "一", isConnected: true }, second = { id: "二", isConnected: true };
  const removed = { id: "已删除", isConnected: false };
  queue.enqueue(first); queue.enqueue(first); queue.enqueue(removed); queue.enqueue(second); queue.enqueue(second);
  await turn();
  assert.deepEqual(calls, ["一"]);
  queue.complete(); await turn();
  assert.deepEqual(calls, ["一", "二"]);
  queue.complete(); await turn();
  assert.equal(queue.length, 0);
});

test("停止后可重新加入同一回复", async () => {
  const calls = [], queue = createQueue(async reply => calls.push(reply.id));
  const first = { id: "一", isConnected: true }, second = { id: "二", isConnected: true };
  queue.enqueue(first); queue.enqueue(second); queue.stop(); queue.enqueue(second);
  await turn(); assert.deepEqual(calls, ["一", "二"]);
});

test("重绘后使用相同消息标识的回复不会重复加入队列", async () => {
  const calls = [], queue = createQueue(async reply => calls.push(reply.id));
  const reply = id => ({ id, isConnected: true, getAttribute: () => "同一消息" });
  queue.enqueue(reply("第一次")); queue.enqueue(reply("重绘后"));
  await turn(); queue.complete(); await turn();
  assert.deepEqual(calls, ["第一次"]);
});

test("被替换的旧任务失败不会清空新任务队列", async () => {
  let rejectOld;
  const old = { id: "旧", isConnected: true }, fresh = { id: "新", isConnected: true };
  const calls = [], errors = [];
  const queue = createQueue(reply => {
    calls.push(reply.id);
    return reply === old ? new Promise((_, reject) => { rejectOld = reject; }) : Promise.resolve();
  }, errors);
  queue.enqueue(old); queue.replace(fresh); rejectOld(new Error("旧任务失败"));
  await turn(); assert.deepEqual(calls, ["旧", "新"]); assert.equal(errors.length, 0);
});

function createInstallFixture(initialGenerating = false) {
  const replies = [], started = [], observers = [], timers = [];
  let resets = 0;
  let generating = initialGenerating;
  function element(tag) {
    return {
      tag, children: [], dataset: {}, listeners: {}, isConnected: true, textContent: "",
      appendChild(child) { this.children.push(child); return child; },
      append(...children) { this.children.push(...children); },
      addEventListener(name, fn) { this.listeners[name] = fn; },
      insertAdjacentElement(_where, child) { child.isConnected = true; this.button = child; },
      remove() { this.isConnected = false; }
    };
  }
  const body = element("body");
  const document = {
    body, createElement: element, createTextNode: text => ({ textContent: text }),
    querySelector: () => generating ? {} : null,
    querySelectorAll: () => replies
  };
  const context = vm.createContext({
    document, location: { hostname: "chatgpt.com", pathname: "/c/1" },
    MutationObserver: class { constructor(fn) { this.callback = fn; observers.push(this); } observe() {} disconnect() {} },
    setTimeout(fn) { timers.push(fn); return timers.length; }, clearTimeout() {}
  });
  context.top = context;
  vm.runInContext(source, context);
  return { replies, started, observers, timers, body, context, element,
    get resets() { return resets; }, setGenerating(value) { generating = value; },
    install() { return context.ListenFlowAIReader.install({
      start: async reply => started.push(reply.textContent), control: async () => {}, reset() { resets++; }, error: e => { throw e; }
    }); }
  };
}

test("自动朗读只在观察到生成结束后排队，不朗读历史回复", async () => {
  const env = createInstallFixture();
  const { replies, started, observers, timers, body, context, element } = env;
  const historical = element("div"); historical.textContent = "历史回复"; replies.push(historical);
  const installed = env.install();
  assert.ok(installed);
  const panel = body.children[0]; panel.children[0].children[0].checked = true;
  env.setGenerating(true); observers[0].callback(); timers.pop()();
  const fresh = element("div"); fresh.textContent = "生成中的回复"; replies.push(fresh);
  observers[0].callback(); timers.pop()(); await turn();
  assert.deepEqual(started, []);
  fresh.textContent = "已完成的回复"; env.setGenerating(false); observers[0].callback(); timers.pop()(); await turn();
  assert.deepEqual(started, ["已完成的回复"]);
  context.location.pathname = "/c/2"; observers[0].callback(); timers.pop()();
  assert.equal(env.resets, 1);
  installed.dispose();
});

test("首次扫描时已在生成，旧回复不加入自动朗读", async () => {
  const env = createInstallFixture(true);
  const history = env.element("div"), live = env.element("div");
  history.textContent = "历史回复"; live.textContent = "流式内容";
  env.replies.push(history, live);
  const installed = env.install();
  env.body.children[0].children[0].children[0].checked = true;
  live.textContent = "完整的新回复";
  env.observers[0].callback(); env.timers.pop()();
  env.setGenerating(false); env.observers[0].callback(); env.timers.pop()(); await turn();
  assert.deepEqual(env.started, ["完整的新回复"]);
  installed.dispose();
});

test("持续流式更新不会不断推迟生成状态扫描", async () => {
  const env = createInstallFixture();
  const installed = env.install();
  env.body.children[0].children[0].children[0].checked = true;
  env.setGenerating(true);
  const fresh = env.element("div"); fresh.textContent = "开头"; env.replies.push(fresh);
  for (let i = 0; i < 20; i++) env.observers[0].callback();
  assert.equal(env.timers.length, 1);
  env.timers.shift()();
  fresh.textContent = "完整回复";
  for (let i = 0; i < 20; i++) env.observers[0].callback();
  assert.equal(env.timers.length, 1);
  env.setGenerating(false);
  env.timers.shift()(); await turn();
  assert.deepEqual(env.started, ["完整回复"]);
  installed.dispose();
});

test("切换对话后已在生成，建立新基线且不朗读延迟加载的旧回复", async () => {
  const env = createInstallFixture();
  const installed = env.install();
  env.body.children[0].children[0].children[0].checked = true;
  const history = env.element("div"), live = env.element("div");
  history.textContent = "新对话中的旧回复"; live.textContent = "正在生成";
  env.replies.push(history, live);
  env.context.location.pathname = "/c/2"; env.setGenerating(true);
  env.observers[0].callback(); env.timers.shift()();
  const delayed = env.element("div"); delayed.textContent = "延迟加载的旧回复"; env.replies.push(delayed);
  live.textContent = "新对话的完整回复";
  env.observers[0].callback(); env.timers.shift()();
  env.setGenerating(false); env.observers[0].callback(); env.timers.shift()(); await turn();
  assert.deepEqual(env.started, ["新对话的完整回复"]);
  installed.dispose();
});
