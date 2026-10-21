(function (global) {
  "use strict";

  const core = global.ListenFlowSnapshotCore;
  const excludedSelector = [
    "script", "style", "noscript", "template", "svg", "canvas", "video", "audio",
    "input", "textarea", "select", "option", "button", "[aria-hidden='true']",
    ".listenflow-status-toast", ".listenflow-highlight-overlay", "[data-listenflow-owned]"
  ].join(",");
  const blockTags = new Set([
    "ADDRESS", "ARTICLE", "ASIDE", "BLOCKQUOTE", "DIV", "DL", "FIELDSET", "FIGCAPTION",
    "FIGURE", "FOOTER", "FORM", "H1", "H2", "H3", "H4", "H5", "H6", "HEADER",
    "HR", "LI", "MAIN", "NAV", "OL", "P", "PRE", "SECTION", "TABLE", "TD", "TH", "TR", "UL"
  ]);

  function buildSelectionSnapshot(rangeOverride) {
    const range = rangeOverride ?? currentSelectionRange();
    if (!range || range.collapsed)
      throw new Error("请先选择需要朗读的网页文字。");
    const nodes = readableTextNodes(document.body).filter(node => {
      try { return range.intersectsNode(node); }
      catch { return false; }
    });
    const slices = nodes.map(node => ({
      node,
      start: node === range.startContainer ? range.startOffset : 0,
      end: node === range.endContainer ? range.endOffset : node.data.length
    })).filter(item => item.end > item.start);
    const snapshot = buildSnapshotFromSlices(slices, "selection", commonAncestorElement(range), document.title);
    const scope = navigationScopeForSelection(range);
    snapshot.navigationRoot = scope?.root ?? null;
    snapshot.navigationScopeKind = scope?.kind ?? null;
    return snapshot;
  }

  function buildArticleSnapshot() {
    const candidates = [];
    const roots = readabilityRoots();
    if (roots.length > 0) {
      const nodes = readableTextNodes(document.body).filter(node =>
        roots.some(root => root === node.parentElement || root.contains(node)));
      if (nodes.length > 0)
        candidates.push(buildSnapshotFromNodes(nodes, "article", roots[0], "readability"));
    }

    const structuralRoot = findFallbackArticleRoot();
    if (structuralRoot !== document.body || candidates.length === 0) {
      const nodes = readableTextNodes(structuralRoot);
      if (nodes.length > 0)
        candidates.push(buildSnapshotFromNodes(nodes, "article", structuralRoot, "structural"));
    }

    const snapshot = candidates.sort((left, right) => right.text.length - left.text.length)[0];
    if (!snapshot)
      throw new Error("没有在当前网页中识别到可朗读的正文文字。");
    if (snapshot.text.trim().length < 20)
      throw new Error("没有在当前网页中识别到足够的正文文字。");
    return snapshot;
  }

  function buildPageSnapshot() {
    const nodes = readableTextNodes(document.body);
    const snapshot = buildSnapshotFromNodes(nodes, "page", document.body, "full-page");
    if (snapshot.text.trim().length < 20)
      throw new Error("当前页面没有足够的可见文字。");
    return snapshot;
  }

  function buildSnapshotFromNodes(nodes, source, root, extraction) {
    const slices = nodes.map(node => ({ node, start: 0, end: node.data.length }));
    const snapshot = buildSnapshotFromSlices(slices, source, root, document.title);
    snapshot.extraction = extraction;
    return snapshot;
  }

  function buildSnapshotFromSlices(slices, source, root, title) {
    let text = "";
    const runs = [];
    let previousBlock = null;
    for (const slice of slices) {
      const value = slice.node.data.slice(slice.start, slice.end);
      if (!value || !value.trim()) continue;
      const block = nearestBlock(slice.node.parentElement);
      if (text.length > 0 && block && previousBlock && block !== previousBlock && !text.endsWith("\n"))
        text += "\n";
      const globalStart = text.length;
      text += value;
      runs.push({
        node: slice.node,
        nodeStart: slice.start,
        nodeEnd: slice.end,
        globalStart,
        globalEnd: text.length
      });
      previousBlock = block;
    }
    if (!text.trim()) throw new Error("选定范围中没有可朗读文字。");
    return { text, runs, source, root, title: title || "网页文章" };
  }

  function readabilityRoots() {
    if (typeof global.Readability === "function") {
      try {
        const clone = document.cloneNode(true);
        const originalElements = [document.body, ...document.body.querySelectorAll("*")];
        const cloneElements = [clone.body, ...clone.body.querySelectorAll("*")];
        const count = Math.min(originalElements.length, cloneElements.length);
        for (let index = 0; index < count; index++)
          cloneElements[index].setAttribute("data-listenflow-map-id", String(index));
        const article = new global.Readability(clone, { charThreshold: 20, keepClasses: false }).parse();
        if (article?.content) {
          const detached = document.implementation.createHTMLDocument("");
          detached.body.innerHTML = article.content;
          const indexes = new Set(
            [...detached.querySelectorAll("[data-listenflow-map-id]")]
              .map(element => Number.parseInt(element.getAttribute("data-listenflow-map-id"), 10))
              .filter(index => Number.isInteger(index) && index >= 0 && index < originalElements.length)
          );
          const mapped = [...indexes].map(index => originalElements[index]).filter(Boolean);
          const minimal = mapped.filter(element => !mapped.some(other => other !== element && other.contains(element)));
          if (minimal.length > 0) return minimal;
        }
      } catch (error) {
        console.debug("ListenFlow Readability 映射失败，使用本地结构回退。", error);
      }
    }
    return [];
  }

  function findFallbackArticleRoot() {
    const direct = [...document.querySelectorAll("article, main, [role='main']")]
      .filter(element => !element.closest(excludedSelector))
      .map(element => ({ element, score: visibleTextLength(element) }))
      .sort((left, right) => right.score - left.score)[0]?.element;
    if (direct) return direct;
    const candidates = [...document.body.querySelectorAll("section, div")]
      .filter(element => !element.closest(excludedSelector))
      .map(element => ({ element, score: visibleTextLength(element) }))
      .filter(item => item.score >= 100)
      .sort((left, right) => right.score - left.score);
    return candidates[0]?.element ?? document.body;
  }

  function visibleTextLength(element) {
    return readableTextNodes(element).reduce((total, node) => total + node.data.trim().length, 0);
  }

  function readableTextNodes(root) {
    if (!root) return [];
    const result = [];
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
      acceptNode(node) {
        if (!node.data || !node.data.trim()) return NodeFilter.FILTER_REJECT;
        const parent = node.parentElement;
        if (!parent || parent.closest(excludedSelector)) return NodeFilter.FILTER_REJECT;
        const style = getComputedStyle(parent);
        if (style.display === "none" || style.visibility === "hidden" || style.visibility === "collapse")
          return NodeFilter.FILTER_REJECT;
        return NodeFilter.FILTER_ACCEPT;
      }
    });
    while (walker.nextNode()) result.push(walker.currentNode);
    return result;
  }

  function mutationAffectsSnapshot(snapshot, mutation) {
    if (!snapshot?.root || !mutation) return false;
    if (mutation.type === "characterData")
      return snapshot.runs.some(run => run.node === mutation.target) ||
        (containsNode(snapshot.root, mutation.target) && isReadableTextNode(mutation.target));
    if (mutation.type !== "childList" || !containsNode(snapshot.root, mutation.target)) return false;

    for (const node of mutation.removedNodes ?? []) {
      if (snapshot.runs.some(run => run.node === node || containsNode(node, run.node))) return true;
      if (containsReadableText(node)) return true;
    }
    for (const node of mutation.addedNodes ?? []) {
      if (isListenFlowOwned(node)) continue;
      if (containsReadableText(node)) return true;
    }
    return false;
  }

  function containsReadableText(node) {
    if (!node) return false;
    if (node.nodeType === Node.TEXT_NODE) return isReadableTextNode(node);
    if (node.nodeType !== Node.ELEMENT_NODE || isListenFlowOwned(node)) return false;
    return readableTextNodes(node).length > 0;
  }

  function isReadableTextNode(node) {
    if (!node?.data?.trim()) return false;
    const parent = node.parentElement;
    if (!parent || parent.closest(excludedSelector) || isListenFlowOwned(parent)) return false;
    const style = getComputedStyle(parent);
    return style.display !== "none" && style.visibility !== "hidden" && style.visibility !== "collapse";
  }

  function isListenFlowOwned(node) {
    const element = node?.nodeType === Node.ELEMENT_NODE ? node : node?.parentElement;
    return Boolean(element?.matches?.(".listenflow-status-toast, .listenflow-highlight-overlay, [data-listenflow-owned]") ||
      element?.closest?.(".listenflow-status-toast, .listenflow-highlight-overlay, [data-listenflow-owned]"));
  }

  function containsNode(root, node) {
    return Boolean(root && node && (root === node || root.contains?.(node)));
  }

  function locatePoint(snapshot, x, y) {
    const position = textPositionAtPoint(x, y);
    return position ? offsetForPosition(snapshot, position) : null;
  }

  function resolveReadingTarget(snapshot, x, y) {
    const position = textPositionAtPoint(x, y);
    if (!position) return null;
    const offset = offsetForPosition(snapshot, position);
    if (offset != null) return { snapshot, offset, expanded: false };

    // 只有选区会话允许按明确的同一文档范围扩展，不根据相同文字猜测位置。
    const root = snapshot.navigationRoot;
    if (snapshot.source !== "selection" || !root?.isConnected || !root.contains(position.node))
      return null;
    const slices = readableTextNodes(root).map(node => ({ node, start: 0, end: node.data.length }));
    const expandedSnapshot = buildSnapshotFromSlices(slices, "document", root, snapshot.title);
    expandedSnapshot.navigationRoot = root;
    expandedSnapshot.navigationScopeKind = snapshot.navigationScopeKind;
    const expandedOffset = offsetForPosition(expandedSnapshot, position);
    return expandedOffset == null ? null : { snapshot: expandedSnapshot, offset: expandedOffset, expanded: true };
  }

  function isEditableTargetAllowed(snapshot, element) {
    if (!snapshot || !element) return false;
    const root = snapshot.navigationRoot ?? (snapshot.source === "article" ? snapshot.root : null);
    return Boolean(root && root !== document.body && root !== document.documentElement &&
      root.isConnected && root.contains(element));
  }

  function navigationScopeForSelection(range) {
    const ancestor = commonAncestorElement(range);
    for (let element = ancestor; element && element !== document.body && element !== document.documentElement;
      element = element.parentElement) {
      if (element.matches("article, main, [role='document'], [role='main']"))
        return { root: element, kind: "document" };
    }

    const startHost = editingHost(range.startContainer);
    const endHost = editingHost(range.endContainer);
    return startHost && startHost === endHost ? { root: startHost, kind: "editable" } : null;
  }

  function editingHost(node) {
    let element = node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement;
    if (!element?.isContentEditable) return null;
    while (element.parentElement?.isContentEditable)
      element = element.parentElement;
    return element === document.body || element === document.documentElement ? null : element;
  }

  function textPositionAtPoint(x, y) {
    let node = null;
    let offset = 0;
    if (typeof document.caretPositionFromPoint === "function") {
      const position = document.caretPositionFromPoint(x, y);
      node = position?.offsetNode ?? null;
      offset = position?.offset ?? 0;
    } else if (typeof document.caretRangeFromPoint === "function") {
      const range = document.caretRangeFromPoint(x, y);
      node = range?.startContainer ?? null;
      offset = range?.startOffset ?? 0;
    }
    if (!node) return null;
    if (node.nodeType !== Node.TEXT_NODE) {
      const child = node.childNodes[Math.min(offset, Math.max(0, node.childNodes.length - 1))];
      node = child?.nodeType === Node.TEXT_NODE ? child : firstReadableDescendant(child);
      offset = 0;
    }
    if (!node || node.nodeType !== Node.TEXT_NODE) return null;
    if (!pointTouchesText(node, offset, x, y)) return null;
    return { node, offset };
  }

  function offsetForPosition(snapshot, position) {
    const globalOffset = core.globalOffsetForNode(snapshot.runs, position.node, position.offset);
    if (globalOffset == null || globalOffset >= snapshot.text.length) return null;
    return core.isUtf16Boundary(snapshot.text, globalOffset) ? globalOffset : Math.max(0, globalOffset - 1);
  }

  function createRanges(snapshot, start, length) {
    return core.slicesForRange(snapshot.runs, start, length).map(slice => {
      const range = document.createRange();
      range.setStart(slice.run.node, slice.startOffset);
      range.setEnd(slice.run.node, slice.endOffset);
      return range;
    }).filter(range => !range.collapsed);
  }

  function pointTouchesText(node, offset, x, y) {
    if (!node.isConnected || node.data.length === 0) return false;
    const start = Math.min(Math.max(offset, 0), node.data.length - 1);
    const end = Math.min(start + 1, node.data.length);
    const range = document.createRange();
    range.setStart(node, start);
    range.setEnd(node, end);
    const rects = [...range.getClientRects()];
    return rects.some(rect =>
      x >= rect.left - 12 && x <= rect.right + 12 && y >= rect.top - 8 && y <= rect.bottom + 8);
  }

  function currentSelectionRange() {
    const selection = getSelection();
    return selection && selection.rangeCount > 0 ? selection.getRangeAt(0).cloneRange() : null;
  }

  function commonAncestorElement(range) {
    const node = range.commonAncestorContainer;
    return node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement;
  }

  function nearestBlock(element) {
    for (let current = element; current; current = current.parentElement) {
      if (blockTags.has(current.tagName)) return current;
    }
    return element;
  }

  function firstReadableDescendant(root) {
    if (!root) return null;
    return readableTextNodes(root)[0] ?? null;
  }

  global.ListenFlowDomSnapshot = Object.freeze({
    buildSelectionSnapshot,
    buildArticleSnapshot,
    buildPageSnapshot,
    locatePoint,
    resolveReadingTarget,
    isEditableTargetAllowed,
    createRanges,
    mutationAffectsSnapshot
  });
})(globalThis);
