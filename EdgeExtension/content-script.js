(function (global) {
  "use strict";
  if (global.__listenFlowContentLoaded) return;
  global.__listenFlowContentLoaded = true;

  const core = global.ListenFlowSnapshotCore;
  const dom = global.ListenFlowDomSnapshot;
  const interactiveSelector = [
    "a", "button", "input", "textarea", "select", "option", "label", "summary",
    "[contenteditable='true']", "[role='button']", "[role='link']", "[role='menuitem']"
  ].join(",");

  let active = null;
  let lastContextSelection = null;
  let toastTimer = null;

  document.addEventListener("contextmenu", rememberContextSelection, true);
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
      lastProgressOffset: 0
    };
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
    showToast(mode === "selection" ? "正在朗读所选文字。" : "正在朗读网页正文。左键点击正文可重新定位。");
  }

  async function sendControl(type) {
    if (!active) throw new Error("当前网页没有活动的 ListenFlow 朗读会话。");
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
      if (["stopped", "completed", "error"].includes(message.state))
        clearActiveSession(message.state === "completed" ? "朗读完成。" : undefined);
      return;
    }

    if (message.type === "progress" && Number.isInteger(message.offset) && message.length > 0) {
      active.state = message.state ?? "speaking";
      active.lastProgressOffset = message.offset;
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
  }

  function handleReadingClick(event) {
    if (!active || event.button !== 0 || event.defaultPrevented) return;
    if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    if (event.target instanceof Element && event.target.closest(interactiveSelector)) return;
    const offset = dom.locatePoint(active.snapshot, event.clientX, event.clientY);
    if (offset == null) return;

    event.preventDefault();
    event.stopImmediatePropagation();
    const command = {
      version: 1,
      type: "seek",
      requestId: crypto.randomUUID(),
      sessionId: active.sessionId,
      snapshotId: active.snapshotId,
      snapshotVersion: active.snapshotVersion,
      commandSeq: ++active.commandSeq,
      offsetEncoding: "utf-16",
      offset
    };
    void forwardCommand(command).catch(error => showToast("定位失败：" + error.message));
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

  function clearActiveSession(message) {
    if (active) {
      active.observer?.disconnect();
      active.highlighter?.clear();
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
