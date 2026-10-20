const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "../highlighter.js"), "utf8");

function createEnvironment({ customHighlight = true, visibleStyle = true } = {}) {
  const listeners = new Map();
  const elements = [];
  const requests = [];
  const highlights = new Map();
  let rects = [{ left: 20, top: 2400, width: 180, height: 24, bottom: 2424 }];
  const forbidScroll = () => assert.fail("高亮不得主动改变用户的滚动位置");
  const parent = { scrollIntoView: forbidScroll, scrollTo: forbidScroll, scrollBy: forbidScroll };
  for (const property of ["scrollTop", "scrollLeft"])
    Object.defineProperty(parent, property, { get: () => 0, set: forbidScroll });
  const range = {
    startContainer: { parentElement: parent },
    getBoundingClientRect: () => rects[0],
    getClientRects: () => rects
  };
  const snapshot = { text: "第一句。第二句。" };
  const context = vm.createContext({
    CSS: customHighlight ? { highlights } : {},
    Highlight: customHighlight ? class {
      constructor(...ranges) { this.ranges = ranges; }
    } : undefined,
    ListenFlowDomSnapshot: {
      createRanges(actualSnapshot, start, length) {
        assert.equal(actualSnapshot, snapshot);
        requests.push({ start, length });
        return [range];
      }
    },
    innerHeight: 800,
    scrollTo: forbidScroll,
    scrollBy: forbidScroll,
    getComputedStyle: () => ({ backgroundColor: visibleStyle ? "rgb(255, 235, 59)" : "transparent" }),
    addEventListener(type, listener, capture) {
      assert.equal(capture, true, "捕获阶段应能接收嵌套滚动容器的事件");
      if (!listeners.has(type)) listeners.set(type, new Set());
      listeners.get(type).add(listener);
    },
    removeEventListener(type, listener, capture) {
      assert.equal(capture, true);
      listeners.get(type)?.delete(listener);
    },
    document: {
      documentElement: Object.assign(Object.create(parent), {
        appendChild(element) { elements.push(element); }
      }),
      body: parent,
      createElement(tag) {
        assert.equal(tag, "div");
        return {
          style: {},
          remove() {
            const index = elements.indexOf(this);
            if (index >= 0) elements.splice(index, 1);
          }
        };
      }
    }
  });
  vm.runInContext(source, context);
  return {
    highlighter: new context.ListenFlowHighlighter(snapshot),
    range, elements, highlights, requests, listeners,
    setRects(value) { rects = value; },
    emit(type) { for (const listener of listeners.get(type) ?? []) listener(); }
  };
}

test("视口外的原生高亮与连续语音进度都不抢夺滚动位置", () => {
  const environment = createEnvironment();
  environment.highlighter.set(0, 4);
  environment.highlighter.set(4, 4);
  environment.emit("scroll");
  environment.emit("resize");
  assert.deepEqual(environment.requests, [{ start: 0, length: 4 }, { start: 4, length: 4 }]);
  const highlight = environment.highlights.get("listenflow-current-sentence");
  assert.equal(highlight.ranges[0], environment.range);
  assert.equal(environment.elements.length, 0);
});

for (const customHighlight of [false, true]) {
  test(`${customHighlight ? "原生样式不可见时" : "没有原生高亮接口时"}覆盖层随手动滚动和窗口变化更新且不自动回滚`, () => {
    const environment = createEnvironment({ customHighlight, visibleStyle: false });
    environment.highlighter.set(0, 4);
    assert.equal(environment.elements[0].style.top, "2400px");
    environment.setRects([{ left: 20, top: -1100, width: 180, height: 24, bottom: -1076 }]);
    environment.emit("scroll");
    assert.equal(environment.elements.length, 1);
    assert.equal(environment.elements[0].style.top, "-1100px");
    environment.setRects([
      { left: 20, top: 110, width: 100, height: 24, bottom: 134 },
      { left: 20, top: 134, width: 80, height: 24, bottom: 158 }
    ]);
    environment.emit("resize");
    assert.deepEqual(environment.elements.map(element => element.style.top), ["110px", "134px"]);
    assert.deepEqual(environment.elements.map(element => element.style.width), ["100px", "80px"]);
    assert.equal(environment.requests.length, 1, "手动浏览只重算当前原文范围的矩形，不改变朗读范围");
  });
}

test("用户滚回原句后覆盖层仍按同一原文范围显示", () => {
  const environment = createEnvironment({ customHighlight: false });
  environment.highlighter.set(4, 4);
  environment.setRects([{ left: 20, top: -800, width: 180, height: 24, bottom: -776 }]);
  environment.emit("scroll");
  environment.setRects([{ left: 20, top: 200, width: 180, height: 24, bottom: 224 }]);
  environment.emit("scroll");
  assert.equal(environment.elements[0].style.top, "200px");
  assert.deepEqual(environment.requests, [{ start: 4, length: 4 }]);
  assert.equal(environment.highlighter.ranges[0], environment.range);
});

test("清理朗读会话同时移除两种高亮及滚动缩放监听", () => {
  const environment = createEnvironment({ visibleStyle: false });
  environment.highlighter.set(0, 4);
  assert.equal(environment.elements.length, 1);
  assert.equal(environment.highlights.size, 1);
  environment.highlighter.clear();
  environment.highlighter.clear();
  environment.emit("scroll");
  environment.emit("resize");
  assert.equal(environment.elements.length, 0);
  assert.equal(environment.highlights.size, 0);
  assert.equal(environment.highlighter.ranges.length, 0);
  assert.equal(environment.listeners.get("scroll").size, 0);
  assert.equal(environment.listeners.get("resize").size, 0);
});
