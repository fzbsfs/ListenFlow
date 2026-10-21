const test = require("node:test");
const assert = require("node:assert/strict");
const core = require("../snapshot-core.js");

test("中文句子范围包含结束标点", () => {
  const text = "第一句。第二句包含 English 123！第三句。";
  const offset = text.indexOf("English");
  assert.deepEqual(core.findSentenceRange(text, offset), {
    start: text.indexOf("第二"),
    length: "第二句包含 English 123！".length
  });
});

test("UTF-16 边界不允许切断表情符号代理项", () => {
  const text = "甲🙂乙";
  assert.equal(core.isUtf16Boundary(text, 1), true);
  assert.equal(core.isUtf16Boundary(text, 2), false);
  assert.equal(core.isUtf16Boundary(text, 3), true);
});

test("全局范围可切分回多个原始文字节点", () => {
  const first = {};
  const second = {};
  const runs = [
    { node: first, nodeStart: 2, nodeEnd: 6, globalStart: 0, globalEnd: 4 },
    { node: second, nodeStart: 0, nodeEnd: 5, globalStart: 5, globalEnd: 10 }
  ];
  const slices = core.slicesForRange(runs, 3, 5);
  assert.equal(slices.length, 2);
  assert.deepEqual(
    slices.map(slice => [slice.startOffset, slice.endOffset]),
    [[5, 6], [0, 3]]
  );
});

test("文字节点位置可恢复为快照 UTF-16 偏移", () => {
  const node = {};
  const runs = [{ node, nodeStart: 4, nodeEnd: 9, globalStart: 12, globalEnd: 17 }];
  assert.equal(core.globalOffsetForNode(runs, node, 7), 15);
  assert.equal(core.globalOffsetForNode(runs, {}, 7), null);
});

test("正文前方插入新内容后可用文字锚点恢复原朗读位置", () => {
  const previous = "第一段。第二段正在朗读这句较长的中文内容。第三段。";
  const next = "动态加载的新开头。" + previous + "新增结尾。";
  const previousOffset = previous.indexOf("正在朗读");
  const mapped = core.remapOffset(previous, next, previousOffset);
  assert.equal(next.slice(mapped, mapped + 4), "正在朗读");
});

test("重复文字锚点优先选择接近原位置的匹配且不切断代理项", () => {
  const paragraph = "重复段落包含足够长的定位文字🙂结束。";
  const previous = paragraph.repeat(4);
  const next = "新增。" + paragraph.repeat(4);
  const previousOffset = previous.lastIndexOf("🙂");
  const mapped = core.remapOffset(previous, next, previousOffset);
  assert.equal(core.isUtf16Boundary(next, mapped), true);
  assert.ok(mapped > next.length / 2);
});
