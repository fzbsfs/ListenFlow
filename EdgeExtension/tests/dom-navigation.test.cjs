const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const core = require("../snapshot-core.js");

const source = fs.readFileSync(path.join(__dirname, "../dom-snapshot.js"), "utf8");

// 夹具验证原节点身份、选区边界和范围策略，不模拟真实浏览器的命中测试布局。
function fixture() {
  let html;
  let caret = null;
  class Element {
    constructor(tag, attributes = {}) {
      this.nodeType = 1;
      this.tagName = tag.toUpperCase();
      this.attributes = attributes;
      this.childNodes = [];
      this.parentElement = null;
    }
    append(...children) {
      for (const child of children) {
        child.parentElement = this;
        this.childNodes.push(child);
      }
      return this;
    }
    contains(node) {
      for (let current = node; current; current = current.parentElement)
        if (current === this) return true;
      return false;
    }
    get isConnected() { return html === this || Boolean(this.parentElement?.isConnected); }
    get isContentEditable() {
      const value = this.attributes.contenteditable;
      if (value === "false") return false;
      if (value === "true" || value === "" || value === "plaintext-only") return true;
      return this.parentElement?.isContentEditable ?? false;
    }
    matches(selector) {
      return selector.split(",").some(value => {
        const part = value.trim();
        const attribute = /^\[([^=]+)='([^']*)'\]$/.exec(part);
        return attribute ? this.attributes[attribute[1]] === attribute[2]
          : this.tagName.toLowerCase() === part;
      });
    }
    closest(selector) {
      for (let current = this; current; current = current.parentElement)
        if (current.matches(selector)) return current;
      return null;
    }
  }
  class Text {
    constructor(data) { this.nodeType = 3; this.data = data; this.parentElement = null; }
    get isConnected() { return this.parentElement?.isConnected ?? false; }
  }
  function textNodes(root) {
    if (root.nodeType === 3) return [root];
    return root.childNodes.flatMap(textNodes);
  }
  html = new Element("html");
  const body = new Element("body");
  html.append(body);
  const document = {
    body, documentElement: html, title: "固定文档夹具",
    createTreeWalker(root, kind, filter) {
      const nodes = textNodes(root).filter(node => filter.acceptNode(node) === 1);
      let index = -1;
      return {
        nextNode() { index++; this.currentNode = nodes[index]; return Boolean(this.currentNode); }
      };
    },
    caretPositionFromPoint() { return caret; },
    createRange() {
      return {
        setStart(node, offset) { this.startContainer = node; this.startOffset = offset; },
        setEnd(node, offset) { this.endContainer = node; this.endOffset = offset; },
        get collapsed() { return this.startContainer === this.endContainer && this.startOffset === this.endOffset; },
        getClientRects() { return [{ left: 0, right: 100, top: 0, bottom: 30 }]; },
        toString() { return this.startContainer.data.slice(this.startOffset, this.endOffset); }
      };
    }
  };
  const context = vm.createContext({
    ListenFlowSnapshotCore: core, document,
    Node: { ELEMENT_NODE: 1, TEXT_NODE: 3 },
    NodeFilter: { SHOW_TEXT: 4, FILTER_ACCEPT: 1, FILTER_REJECT: 2 },
    getComputedStyle: () => ({ display: "block", visibility: "visible" })
  });
  vm.runInContext(source, context);
  return {
    api: context.ListenFlowDomSnapshot, body,
    element: (tag, attributes) => new Element(tag, attributes),
    text: data => new Text(data),
    caret(node, offset) { caret = { offsetNode: node, offset }; },
    range(startNode, startOffset, endNode = startNode, endOffset = startNode.data.length) {
      const nodes = textNodes(body);
      let commonAncestorContainer = startNode;
      while (commonAncestorContainer !== endNode && !commonAncestorContainer.contains?.(endNode))
        commonAncestorContainer = commonAncestorContainer.parentElement;
      return {
        startContainer: startNode, startOffset, endContainer: endNode, endOffset,
        commonAncestorContainer,
        collapsed: startNode === endNode && startOffset === endOffset,
        intersectsNode(node) {
          return nodes.indexOf(node) >= nodes.indexOf(startNode) && nodes.indexOf(node) <= nodes.indexOf(endNode);
        }
      };
    }
  };
}

function addParagraph(environment, root, text, tag = "p") {
  const node = environment.text(text);
  root.append(environment.element(tag).append(node));
  return node;
}

test("选区初次快照只包含所选字符并保留明确的文档定位范围", () => {
  const env = fixture();
  const article = env.element("article");
  env.body.append(article);
  const selected = addParagraph(env, article, "之前。所选内容。之后。");
  addParagraph(env, article, "远处正文不会被初次发送。");
  const snapshot = env.api.buildSelectionSnapshot(env.range(selected, 3, selected, 8));
  assert.equal(snapshot.text, "所选内容。");
  assert.equal(snapshot.source, "selection");
  assert.equal(snapshot.runs.length, 1);
  assert.equal(snapshot.runs[0].node, selected);
  assert.equal(snapshot.runs[0].nodeStart, 3);
  assert.equal(snapshot.navigationRoot, article);
  env.caret(selected, 5);
  const target = env.api.resolveReadingTarget(snapshot, 10, 10);
  assert.equal(target.expanded, false);
  assert.equal(target.snapshot, snapshot);
  assert.equal(target.offset, 2);
});

for (const [tag, attributes] of [["article", {}], ["main", {}], ["div", { role: "document" }], ["div", { role: "main" }]]) {
  test(`${tag} ${attributes.role ?? ""} 范围中选区外相同文字按节点身份定位`, () => {
    const env = fixture();
    const root = env.element(tag, attributes);
    env.body.append(root);
    const selected = addParagraph(env, root, "重复正文。");
    const far = addParagraph(env, root, "重复正文。");
    const snapshot = env.api.buildSelectionSnapshot(env.range(selected, 0));
    env.caret(far, 2);
    assert.equal(env.api.locatePoint(snapshot, 10, 10), null);
    const target = env.api.resolveReadingTarget(snapshot, 10, 10);
    assert.equal(target.expanded, true);
    assert.equal(target.snapshot.source, "document");
    assert.equal(target.snapshot.text, "重复正文。\n重复正文。");
    assert.equal(target.offset, 8);
    const ranges = env.api.createRanges(target.snapshot, target.offset, 2);
    assert.equal(ranges.length, 1);
    assert.equal(ranges[0].startContainer, far);
    assert.equal(ranges[0].toString(), "正文");
    assert.equal(snapshot.text, "重复正文。", "扩展不得修改旧快照");
  });
}

test("同一文字节点中选区外的点击也可以扩展且保持 UTF-16 边界", () => {
  const env = fixture();
  const root = env.element("main");
  env.body.append(root);
  const node = addParagraph(env, root, "初选。🙂目标。");
  const snapshot = env.api.buildSelectionSnapshot(env.range(node, 0, node, 3));
  env.caret(node, 4);
  const target = env.api.resolveReadingTarget(snapshot, 10, 10);
  assert.equal(target.expanded, true);
  assert.equal(target.offset, 3);
  assert.equal(core.isUtf16Boundary(target.snapshot.text, target.offset), true);
});

test("另一个文档内相同文字不会触发范围扩展", () => {
  const env = fixture();
  const first = env.element("article");
  const second = env.element("article");
  env.body.append(first, second);
  const selected = addParagraph(env, first, "重复正文。");
  const outside = addParagraph(env, second, "重复正文。");
  const snapshot = env.api.buildSelectionSnapshot(env.range(selected, 0));
  env.caret(outside, 2);
  assert.equal(env.api.resolveReadingTarget(snapshot, 10, 10), null);
  assert.equal(env.api.isEditableTargetAllowed(snapshot, outside.parentElement), false);
});

test("没有明确文档或编辑宿主时不以整页正文猜测范围", () => {
  const env = fixture();
  const selected = addParagraph(env, env.body, "所选正文。");
  const outside = addParagraph(env, env.body, "其他正文。");
  const snapshot = env.api.buildSelectionSnapshot(env.range(selected, 0));
  assert.equal(snapshot.navigationRoot, null);
  env.caret(outside, 0);
  assert.equal(env.api.resolveReadingTarget(snapshot, 10, 10), null);
});

for (const mode of ["true", "", "plaintext-only"]) {
  test(`明确编辑宿主 contenteditable=${JSON.stringify(mode)} 中代码块可按原节点定位`, () => {
    const env = fixture();
    const editor = env.element("div", { contenteditable: mode });
    env.body.append(editor);
    const selected = addParagraph(env, editor, "所选正文。");
    const code = addParagraph(env, editor, "const value = 123;", "pre");
    const snapshot = env.api.buildSelectionSnapshot(env.range(selected, 0));
    assert.equal(snapshot.navigationRoot, editor);
    assert.equal(snapshot.navigationScopeKind, "editable");
    assert.equal(env.api.isEditableTargetAllowed(snapshot, code.parentElement), true);
    env.caret(code, 6);
    const target = env.api.resolveReadingTarget(snapshot, 10, 10);
    assert.equal(target.expanded, true);
    assert.equal(target.offset, selected.data.length + 1 + 6);
    assert.equal(env.api.createRanges(target.snapshot, target.offset, 5)[0].startContainer, code);
  });
}

test("两个无共同文档范围的独立编辑器不会互相扩展", () => {
  const env = fixture();
  const first = env.element("div", { contenteditable: "true" });
  const second = env.element("div", { contenteditable: "true" });
  env.body.append(first, second);
  const selected = addParagraph(env, first, "所选正文。");
  const outside = addParagraph(env, second, "其他编辑器正文。");
  const snapshot = env.api.buildSelectionSnapshot(env.range(selected, 0));
  env.caret(outside, 0);
  assert.equal(env.api.resolveReadingTarget(snapshot, 10, 10), null);
  const crossed = env.api.buildSelectionSnapshot(env.range(selected, 0, outside, 3));
  assert.equal(crossed.navigationRoot, null);
});

test("整页可编辑也不把 body 视为可信编辑宿主", () => {
  const env = fixture();
  env.body.attributes.contenteditable = "true";
  const selected = addParagraph(env, env.body, "所选正文。");
  const snapshot = env.api.buildSelectionSnapshot(env.range(selected, 0));
  assert.equal(snapshot.navigationRoot, null);
  assert.equal(env.api.isEditableTargetAllowed(snapshot, selected.parentElement), false);
});

test("明确文档范围内分块编辑正文与代码可以建立统一节点映射", () => {
  const env = fixture();
  const root = env.element("div", { role: "document" });
  const first = env.element("div", { contenteditable: "true" });
  const second = env.element("div", { contenteditable: "true" });
  env.body.append(root.append(first, second));
  const selected = addParagraph(env, first, "开头正文。");
  const code = addParagraph(env, second, "代码🙂结尾。", "pre");
  const snapshot = env.api.buildSelectionSnapshot(env.range(selected, 0));
  assert.equal(snapshot.navigationRoot, root);
  assert.equal(snapshot.navigationScopeKind, "document");
  env.caret(code, 2);
  const target = env.api.resolveReadingTarget(snapshot, 10, 10);
  assert.equal(target.offset, selected.data.length + 1 + 2);
  assert.equal(env.api.isEditableTargetAllowed(target.snapshot, code.parentElement), true);
});

test("被排除的输入控件和隐藏节点不会进入扩展快照", () => {
  const env = fixture();
  const root = env.element("article");
  env.body.append(root);
  const selected = addParagraph(env, root, "可朗读正文。");
  const hidden = env.text("隐藏正文。");
  root.append(env.element("div", { "aria-hidden": "true" }).append(hidden));
  const control = addParagraph(env, root, "按钮文字。", "button");
  const snapshot = env.api.buildSelectionSnapshot(env.range(selected, 0));
  for (const node of [hidden, control]) {
    env.caret(node, 0);
    assert.equal(env.api.resolveReadingTarget(snapshot, 10, 10), null);
  }
});

test("离开实际文字矩形或文档节点已卸载时不扩展", () => {
  const env = fixture();
  const root = env.element("article");
  env.body.append(root);
  const selected = addParagraph(env, root, "初始正文。");
  const far = addParagraph(env, root, "远处正文。");
  const snapshot = env.api.buildSelectionSnapshot(env.range(selected, 0));
  env.caret(far, 0);
  assert.equal(env.api.resolveReadingTarget(snapshot, 500, 500), null);
  root.parentElement = null;
  assert.equal(env.api.resolveReadingTarget(snapshot, 10, 10), null);
});
