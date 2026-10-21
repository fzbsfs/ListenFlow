(function (global) {
  "use strict";

  const sentenceBreaks = new Set(["。", "！", "？", "!", "?", "；", ";", "\n", "\r"]);
  const closingPunctuation = new Set(["”", "’", "」", "』", ")", "）", "]", "】"]);

  function isUtf16Boundary(text, offset) {
    if (typeof text !== "string" || !Number.isInteger(offset) || offset < 0 || offset > text.length)
      return false;
    if (offset === 0 || offset === text.length) return true;
    return !(isHighSurrogate(text.charCodeAt(offset - 1)) && isLowSurrogate(text.charCodeAt(offset)));
  }

  function findSentenceRange(text, offset) {
    if (!text) return { start: 0, length: 0 };
    let cursor = Math.min(Math.max(Number.isInteger(offset) ? offset : 0, 0), text.length - 1);
    if (!isUtf16Boundary(text, cursor)) cursor--;

    let start = cursor;
    while (start > 0 && !sentenceBreaks.has(text[start - 1])) start--;
    while (start < text.length && /\s/u.test(text[start])) start++;

    let end = Math.max(cursor + 1, start + 1);
    while (end < text.length && !sentenceBreaks.has(text[end - 1])) end++;
    while (end < text.length && closingPunctuation.has(text[end])) end++;
    if (!isUtf16Boundary(text, end)) end++;
    return { start, length: Math.max(0, end - start) };
  }

  function slicesForRange(runs, start, length) {
    if (!Array.isArray(runs) || !Number.isInteger(start) || !Number.isInteger(length) || length < 0)
      return [];
    const end = start + Math.max(length, 1);
    const result = [];
    for (const run of runs) {
      if (!run?.node || run.globalEnd <= start || run.globalStart >= end) continue;
      const overlapStart = Math.max(start, run.globalStart);
      const overlapEnd = Math.min(end, run.globalEnd);
      if (overlapEnd <= overlapStart) continue;
      result.push({
        run,
        startOffset: run.nodeStart + overlapStart - run.globalStart,
        endOffset: run.nodeStart + overlapEnd - run.globalStart
      });
    }
    return result;
  }

  function globalOffsetForNode(runs, node, nodeOffset) {
    if (!Array.isArray(runs) || !node || !Number.isInteger(nodeOffset)) return null;
    for (const run of runs) {
      if (run.node !== node || nodeOffset < run.nodeStart || nodeOffset > run.nodeEnd) continue;
      return run.globalStart + Math.min(nodeOffset, run.nodeEnd) - run.nodeStart;
    }
    return null;
  }

  function remapOffset(previousText, nextText, previousOffset) {
    if (typeof previousText !== "string" || typeof nextText !== "string" || nextText.length === 0)
      return 0;
    const safeOffset = Math.min(Math.max(Number.isInteger(previousOffset) ? previousOffset : 0, 0),
      Math.max(0, previousText.length - 1));
    if (previousText === nextText)
      return Math.min(safeOffset, nextText.length - 1);

    for (const radius of [96, 64, 40, 24, 12]) {
      const start = Math.max(0, safeOffset - Math.floor(radius / 3));
      const end = Math.min(previousText.length, start + radius);
      const anchor = previousText.slice(start, end);
      if (anchor.trim().length < 8) continue;
      const matches = [];
      let index = nextText.indexOf(anchor);
      while (index >= 0 && matches.length < 128) {
        matches.push(index);
        index = nextText.indexOf(anchor, index + 1);
      }
      if (matches.length === 0) continue;
      const expected = Math.min(start, nextText.length - 1);
      const match = matches.sort((left, right) => Math.abs(left - expected) - Math.abs(right - expected))[0];
      const mapped = match + safeOffset - start;
      return normalizeUtf16Offset(nextText, Math.min(Math.max(mapped, 0), nextText.length - 1));
    }

    return normalizeUtf16Offset(nextText, Math.min(safeOffset, nextText.length - 1));
  }

  function normalizeUtf16Offset(text, offset) {
    return isUtf16Boundary(text, offset) ? offset : Math.max(0, offset - 1);
  }

  function isHighSurrogate(value) {
    return value >= 0xd800 && value <= 0xdbff;
  }

  function isLowSurrogate(value) {
    return value >= 0xdc00 && value <= 0xdfff;
  }

  const api = Object.freeze({
    isUtf16Boundary,
    findSentenceRange,
    slicesForRange,
    globalOffsetForNode,
    remapOffset
  });
  global.ListenFlowSnapshotCore = api;
  if (typeof module === "object" && module.exports)
    module.exports = api;
})(globalThis);
