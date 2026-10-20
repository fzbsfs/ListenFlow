(function (global) {
  "use strict";
  if (global.__listenFlowContentLoaded) return;
  global.__listenFlowContentLoaded = true;

  const core = global.ListenFlowSnapshotCore;
  const dom = global.ListenFlowDomSnapshot;
  const interactiveSelector = [
    "a", "button", "input", "textarea", "select", "option", "label", "summary",
    "[role='button']", "[role='link']", "[role='menuitem']", "[role='toolbar']"
  ].join(",");
  const editableSelector = "[contenteditable]:not([contenteditable='false'])";

  let active = null;
  let lastContextSelection = null;
  let toastTimer = null;
  let pointerGesture = null;

  document.addEventListener("contextmenu", rememberContextSelection, true);
  document.addEventListener("pointerdown", rememberPointerDown, true);
  document.addEventListener("pointermove", rememberPointerMove, true);
  document.addEventListener("pointerup", rememberPointerUp, true);
  document.addEventListener("click", handleReadingClick, true);
  chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    void handleExtensionMessage(message).then(
      result => sendResponse({ ok: true, result }),
      error => {
        showToast(String(error?.message ?? error));
        sendResponse({ ok: false, error: String(error?.message ?? error) });
      });
    return true;
  });

  async function handleExtensionMessage(message) {
    if (message?.kind === "context-action") {
      if (message.action === "start-selection") return startReading("selection");
      if (message.action === "start-article") return startReading("article");
      if (message.action === "pause") return sendControl("pause");
      if (message.action === "resume") return sendControl("resume");
      if (message.action === "stop") return sendControl("stop");
    }
    if (message?.kind === "bridge-event") return applyBridgeEvent(message.message);
    if (message?.kind === "session-replaced") {
      clearActiveSession("朗读已切换到另一网页。");
      return;
    }
  }

  async function startReading(mode) {
    clearActiveSession();
    const extractionStarted = performance.now();
    const snapshot = mode === "selection"
      ? dom.buildSelectionSnapshot(lastContextSelection)
      : dom.buildArticleSnapshot();
    const sessionId = crypto.randomUUID();
    const snapshotId = crypto.randomUUID();
    const snapshotVersion = Date.now();
    active = {
      snapshot,
      sessionId,
      snapshotId,
      snapshotVersion,
      commandSeq: 1,
      generation: -1,
      state: "starting",
      highlighter: new global.ListenFlowHighlighter(snapshot),
      runNodes: new Set(snapshot.runs.map(run => run.node)),
      observer: null,
      lastProgressOffset: 0,
      hasProgress: false,
      hasHighlighted: false
    };
    emitDiagnostic("edge-snapshot-created", {
      textLength: snapshot.text.length,
      nodeCount: snapshot.runs.length,
      elapsedMilliseconds: Math.round(performance.now() - extractionStarted),
      source: mode
    });
    observeSnapshot(active);
    const command = {
      version: 1,
      type: "start",
      requestId: crypto.randomUUID(),
      sessionId,
      snapshotId,
      snapshotVersion,
      commandSeq: 1,
      offsetEncoding: "utf-16",
      offset: 0,
      text: snapshot.text,
      title: snapshot.title,
      sourceUri: location.href,
      voiceName: "Microsoft Xiaoxiao"
    };
    await forwardCommand(command);
    emitDiagnostic("edge-start-posted", { textLength: snapshot.text.length, source: mode });
    showToast(mode === "selection" ? "正在朗读所选文字。" : "正在朗读网页正文。左键点击正文可重新定位。");
  }

  async function sendControl(type) {
    if (!active) throw new Error("当前网页没有活动的 ListenFlow 朗读会话。");
    if (type === "resume" && active.pending)
      return startPendingSnapshot();
    const command = {
      version: 1,
      type,
      requestId: crypto.randomUUID(),
      sessionId: active.sessionId,
      commandSeq: ++active.commandSeq
    };
    if (type === "stop") active.state = "stopping";
    await forwardCommand(command);
  }

  async function forwardCommand(command) {
    const response = await chrome.runtime.sendMessage({ kind: "bridge-command", command });
    if (!response?.ok) throw new Error(response?.error ?? "无法发送 ListenFlow 命令。");
  }

  function applyBridgeEvent(message) {
    if (!message) return;
    if (message.type === "error" && !message.sessionId) {
      showToast("ListenFlow 通信错误：" + (message.message ?? message.code));
      return;
    }
    if (!active || message.sessionId !== active.sessionId) return;
    if (message.snapshotId && message.snapshotId !== active.snapshotId) return;
    if (message.snapshotVersion != null && message.snapshotVersion !== active.snapshotVersion) return;
    if (message.playbackGeneration != null && message.playbackGeneration < active.generation) return;
    if (message.playbackGeneration != null) active.generation = message.playbackGeneration;

    if (message.type === "error") {
      showToast("ListenFlow 错误：" + (message.message ?? message.code));
      if (message.recoverable === false) clearActiveSession();
      return;
    }

    if (message.type === "state") {
      active.state = message.state;
      if (["speaking", "paused"].includes(message.state) && Number.isInteger(message.offset))
        highlightSentence(message.offset);
      if (message.state === "error")
        showToast("ListenFlow 播放错误：" + (message.message ?? message.code ?? "未知错误"));
      if (["stopped", "completed", "error"].includes(message.state))
        clearActiveSession(message.state === "completed" ? "朗读完成。" : undefined);
      return;
    }

    if (message.type === "progress" && Number.isInteger(message.offset) && message.length > 0) {
      active.state = message.state ?? "speaking";
      active.lastProgressOffset = message.offset;
      if (!active.hasProgress) {
        active.hasProgress = true;
        emitDiagnostic("edge-first-progress", {
          offset: message.offset,
          length: message.length,
          playbackGeneration: message.playbackGeneration
        });
      }
      highlightSentence(message.offset);
      return;
    }

    if (message.type === "ack" && message.state)
      active.state = message.state;
  }

  function highlightSentence(offset) {
    if (!active || offset < 0 || offset > active.snapshot.text.length) return;
    const sentence = core.findSentenceRange(active.snapshot.text, offset);
    active.highlighter.set(sentence.start, sentence.length);
    if (!active.hasHighlighted) {
      active.hasHighlighted = true;
      emitDiagnostic("edge-first-highlight", {
        offset: sentence.start,
        length: sentence.length,
        playbackGeneration: active.generation
      });
    }
  }

  function handleReadingClick(event) {
    if (!active || event.button !== 0 || event.defaultPrevented) return;
    if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    if (event.detail > 1 || pointerGesture?.moved) return;
    if (event.target instanceof Element && event.target.closest(interactiveSelector)) return;
    const selection = getSelection();
    if (selection && !selection.isCollapsed) return;
    const navigationSnapshot = active.pending?.snapshot ?? active.snapshot;
    if (event.target instanceof Element && event.target.closest(editableSelector) &&
        !dom.isEditableTargetAllowed(navigationSnapshot, event.target)) return;
    const target = dom.resolveReadingTarget(navigationSnapshot, event.clientX, event.clientY);
    if (!target) return;

    if (active.state === "paused" && (active.pending || target.expanded)) {
      setPendingTarget(target.snapshot, target.offset);
      return;
    }
    if (target.expanded) {
      void startReplacementSnapshot(target.snapshot, target.offset)
        .catch(error => showToast("定位失败：" + error.message));
      return;
    }

    highlightSnapshotRange(navigationSnapshot, active.pending?.highlighter ?? active.highlighter, target.offset);
    const command = {
      version: 1,
      type: "seek",
      requestId: crypto.randomUUID(),
      sessionId: active.sessionId,
      snapshotId: active.pending?.snapshotId ?? active.snapshotId,
      snapshotVersion: active.pending?.snapshotVersion ?? active.snapshotVersion,
      commandSeq: ++active.commandSeq,
      offsetEncoding: "utf-16",
      offset: target.offset
    };
    void forwardCommand(command).catch(error => showToast("定位失败：" + error.message));
  }

  function setPendingTarget(snapshot, offset) {
    if (!active) return;
    if (active.pending?.snapshot !== snapshot) {
      active.pending?.highlighter?.clear();
      active.highlighter?.clear();
      active.pending = {
        snapshot,
        snapshotId: crypto.randomUUID(),
        snapshotVersion: Date.now(),
        offset,
        highlighter: new global.ListenFlowHighlighter(snapshot)
      };
    } else {
      active.pending.offset = offset;
    }
    highlightSnapshotRange(snapshot, active.pending.highlighter, offset);
    emitDiagnostic("edge-paused-target-pending", {
      offset,
      textLength: snapshot.text.length,
      nodeCount: snapshot.runs.length
    });
  }

  async function startPendingSnapshot() {
    if (!active?.pending) return;
    const pending = active.pending;
    promoteSnapshot(pending);
    await sendStartForActive(pending.offset);
  }

  async function startReplacementSnapshot(snapshot, offset) {
    if (!active) return;
    active.highlighter?.clear();
    const replacement = {
      snapshot,
      snapshotId: crypto.randomUUID(),
      snapshotVersion: Date.now(),
      offset,
      highlighter: new global.ListenFlowHighlighter(snapshot)
    };
    highlightSnapshotRange(snapshot, replacement.highlighter, offset);
    promoteSnapshot(replacement);
    await sendStartForActive(offset);
  }

  function promoteSnapshot(replacement) {
    if (!active) return;
    active.observer?.disconnect();
    if (active.highlighter !== replacement.highlighter)
      active.highlighter?.clear();
    if (active.pending && active.pending !== replacement && active.pending.highlighter !== replacement.highlighter)
      active.pending.highlighter?.clear();
    active.snapshot = replacement.snapshot;
    active.snapshotId = replacement.snapshotId;
    active.snapshotVersion = replacement.snapshotVersion;
    active.highlighter = replacement.highlighter;
    active.runNodes = new Set(replacement.snapshot.runs.map(run => run.node));
    active.pending = null;
    active.state = "starting";
    active.generation = -1;
    active.hasProgress = false;
    active.hasHighlighted = true;
    observeSnapshot(active);
  }

  async function sendStartForActive(offset) {
    if (!active) return;
    const command = {
      version: 1,
      type: "start",
      requestId: crypto.randomUUID(),
      sessionId: active.sessionId,
      snapshotId: active.snapshotId,
      snapshotVersion: active.snapshotVersion,
      commandSeq: ++active.commandSeq,
      offsetEncoding: "utf-16",
      offset,
      text: active.snapshot.text,
      title: active.snapshot.title,
      sourceUri: location.href,
      voiceName: "Microsoft Xiaoxiao"
    };
    emitDiagnostic("edge-snapshot-replaced", {
      offset,
      textLength: active.snapshot.text.length,
      nodeCount: active.snapshot.runs.length,
      source: active.snapshot.source
    });
    await forwardCommand(command);
  }

  function highlightSnapshotRange(snapshot, highlighter, offset) {
    if (!snapshot || !highlighter || offset < 0 || offset >= snapshot.text.length) return;
    const sentence = core.findSentenceRange(snapshot.text, offset);
    highlighter.set(sentence.start, sentence.length);
  }

  function rememberPointerDown(event) {
    if (event.button !== 0) return;
    pointerGesture = { pointerId: event.pointerId, x: event.clientX, y: event.clientY, moved: false };
  }

  function rememberPointerMove(event) {
    if (!pointerGesture || event.pointerId !== pointerGesture.pointerId) return;
    if (Math.hypot(event.clientX - pointerGesture.x, event.clientY - pointerGesture.y) > 5)
      pointerGesture.moved = true;
  }

  function rememberPointerUp(event) {
    if (!pointerGesture || event.pointerId !== pointerGesture.pointerId) return;
    // 保留到紧随 pointerup 的 click；下一次 pointerdown 会覆盖。
  }

  function rememberContextSelection() {
    const selection = getSelection();
    lastContextSelection = selection && selection.rangeCount > 0 && !selection.isCollapsed
      ? selection.getRangeAt(0).cloneRange()
      : null;
  }

  function observeSnapshot(session) {
    const observer = new MutationObserver(mutations => {
      if (!active || active !== session) return;
      if (!mutations.some(mutation => affectsSnapshot(session, mutation))) return;
      const stop = {
        version: 1,
        type: "stop",
        requestId: crypto.randomUUID(),
        sessionId: session.sessionId,
        commandSeq: ++session.commandSeq
      };
      emitDiagnostic("edge-snapshot-invalidated", {
        reason: mutationReason(session, mutations),
        offset: session.lastProgressOffset,
        playbackGeneration: session.generation
      });
      void forwardCommand(stop).catch(() => {});
      clearActiveSession("网页正文已发生变化，请重新启动朗读。");
    });
    observer.observe(document.body, { subtree: true, childList: true, characterData: true });
    session.observer = observer;
  }

  function affectsSnapshot(session, mutation) {
    if (mutation.type === "characterData") return session.runNodes.has(mutation.target);
    for (const node of [...mutation.removedNodes, ...mutation.addedNodes]) {
      if (session.runNodes.has(node)) return true;
      if (node.nodeType === Node.ELEMENT_NODE) {
        for (const runNode of session.runNodes) {
          if (node.contains(runNode)) return true;
        }
      }
    }
    return false;
  }

  function mutationReason(session, mutations) {
    const mutation = mutations.find(item => affectsSnapshot(session, item));
    if (!mutation) return "unknown";
    return mutation.type === "characterData"
      ? "mapped-character-data"
      : `mapped-child-list:${mutation.addedNodes.length}:${mutation.removedNodes.length}`;
  }

  function emitDiagnostic(stage, details = {}) {
    const session = active;
    void chrome.runtime.sendMessage({
      kind: "browser-diagnostic",
      diagnostic: {
        version: 1,
        type: "diagnostic",
        requestId: crypto.randomUUID(),
        diagnosticStage: stage,
        sessionId: session?.sessionId,
        snapshotId: session?.snapshotId,
        snapshotVersion: session?.snapshotVersion,
        commandSeq: session?.commandSeq,
        ...details
      }
    }).catch(() => {});
  }

  function clearActiveSession(message) {
    if (active) {
      active.observer?.disconnect();
      active.highlighter?.clear();
      active.pending?.highlighter?.clear();
    }
    active = null;
    if (message) showToast(message);
  }

  function showToast(message) {
    document.querySelector(".listenflow-status-toast")?.remove();
    const toast = document.createElement("div");
    toast.className = "listenflow-status-toast";
    toast.textContent = message;
    document.documentElement.appendChild(toast);
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => toast.remove(), 4200);
  }
})(globalThis);
