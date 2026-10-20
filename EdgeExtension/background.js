const NATIVE_HOST_NAME = "com.listenflow.native_host";
const MENU = Object.freeze({
  selection: "listenflow-read-selection",
  article: "listenflow-read-article",
  pause: "listenflow-pause",
  resume: "listenflow-resume",
  stop: "listenflow-stop"
});

let nativePort = null;
const sessions = new Map();

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: MENU.selection,
      title: "使用 ListenFlow 朗读所选文字",
      contexts: ["selection"]
    });
    chrome.contextMenus.create({
      id: MENU.article,
      title: "使用 ListenFlow 朗读网页正文",
      contexts: ["page", "selection"]
    });
    chrome.contextMenus.create({
      id: "listenflow-separator",
      type: "separator",
      contexts: ["page", "selection"]
    });
    chrome.contextMenus.create({ id: MENU.pause, title: "暂停 ListenFlow", contexts: ["page", "selection"] });
    chrome.contextMenus.create({ id: MENU.resume, title: "继续 ListenFlow", contexts: ["page", "selection"] });
    chrome.contextMenus.create({ id: MENU.stop, title: "停止 ListenFlow", contexts: ["page", "selection"] });
  });
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (!tab?.id) return;
  const action = info.menuItemId === MENU.selection ? "start-selection"
    : info.menuItemId === MENU.article ? "start-article"
      : info.menuItemId === MENU.pause ? "pause"
        : info.menuItemId === MENU.resume ? "resume"
          : info.menuItemId === MENU.stop ? "stop"
            : null;
  if (!action) return;
  postBrowserDiagnostic("edge-menu-clicked", { state: action, frameId: info.frameId ?? 0 });
  void ensureContentAndSend(tab.id, info.frameId ?? 0, { kind: "context-action", action });
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.kind === "browser-diagnostic" && message.diagnostic) {
    try {
      ensureNativePort().postMessage(message.diagnostic);
      sendResponse({ ok: true });
    } catch (error) {
      sendResponse({ ok: false, error: String(error?.message ?? error) });
    }
    return false;
  }
  if (message?.kind !== "bridge-command" || !message.command) return false;
  const tabId = sender.tab?.id;
  if (tabId == null) {
    sendResponse({ ok: false, error: "缺少网页标签页标识。" });
    return false;
  }

  try {
    registerCommand(message.command, tabId, sender.frameId ?? 0);
    const port = ensureNativePort();
    port.postMessage(message.command);
    sendResponse({ ok: true });
  } catch (error) {
    sendResponse({ ok: false, error: String(error?.message ?? error) });
  }
  return false;
});

chrome.tabs.onRemoved.addListener(tabId => stopSessionsForTab(tabId));
chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo.status === "loading" || changeInfo.url)
    stopSessionsForTab(tabId);
});

function ensureNativePort() {
  if (nativePort) return nativePort;
  nativePort = chrome.runtime.connectNative(NATIVE_HOST_NAME);
  nativePort.onMessage.addListener(routeNativeMessage);
  nativePort.onDisconnect.addListener(() => {
    const reason = chrome.runtime.lastError?.message ?? "本机通信宿主已断开。";
    nativePort = null;
    for (const session of sessions.values()) {
      void sendToFrame(session, {
        kind: "bridge-event",
        message: {
          version: 1,
          type: "error",
          sessionId: session.sessionId,
          code: "native_port_disconnected",
          message: reason,
          recoverable: true
        }
      });
    }
  });
  return nativePort;
}

function registerCommand(command, tabId, frameId) {
  if (command.type === "start") {
    for (const [sessionId, existing] of sessions) {
      if (sessionId === command.sessionId) continue;
      void sendToFrame(existing, { kind: "session-replaced" });
      sessions.delete(sessionId);
    }
    sessions.set(command.sessionId, {
      sessionId: command.sessionId,
      tabId,
      frameId,
      lastCommandSeq: command.commandSeq ?? 0
    });
    return;
  }

  const session = sessions.get(command.sessionId);
  if (session)
    session.lastCommandSeq = Math.max(session.lastCommandSeq, command.commandSeq ?? 0);
}

function routeNativeMessage(message) {
  if (message?.sessionId && sessions.has(message.sessionId)) {
    const session = sessions.get(message.sessionId);
    void sendToFrame(session, { kind: "bridge-event", message });
    if (message.type === "state" && ["stopped", "completed", "error"].includes(message.state))
      sessions.delete(message.sessionId);
    return;
  }

  if (message?.type === "error") {
    for (const session of sessions.values())
      void sendToFrame(session, { kind: "bridge-event", message });
  }
}

function stopSessionsForTab(tabId) {
  for (const [sessionId, session] of sessions) {
    if (session.tabId !== tabId) continue;
    try {
      ensureNativePort().postMessage({
        version: 1,
        type: "stop",
        requestId: crypto.randomUUID(),
        sessionId,
        commandSeq: session.lastCommandSeq + 1
      });
    } catch {
    }
    sessions.delete(sessionId);
  }
}

async function ensureContentAndSend(tabId, frameId, message) {
  const target = { frameId };
  try {
    await chrome.tabs.sendMessage(tabId, message, target);
    return;
  } catch {
  }

  try {
    await chrome.scripting.insertCSS({
      target: { tabId, frameIds: [frameId] },
      files: ["content.css"]
    });
    await chrome.scripting.executeScript({
      target: { tabId, frameIds: [frameId] },
      files: [
        "vendor/Readability.js",
        "snapshot-core.js",
        "dom-snapshot.js",
        "highlighter.js",
        "content-script.js"
      ]
    });
    await chrome.tabs.sendMessage(tabId, message, target);
  } catch (error) {
    console.warn("ListenFlow 无法注入当前页面：", error);
    postBrowserDiagnostic("edge-content-injection-failed", {
      frameId,
      reason: String(error?.message ?? error)
    });
  }
}

function postBrowserDiagnostic(stage, details = {}) {
  try {
    ensureNativePort().postMessage({
      version: 1,
      type: "diagnostic",
      requestId: crypto.randomUUID(),
      diagnosticStage: stage,
      ...details
    });
  } catch (error) {
    console.warn("ListenFlow 无法写入本地诊断：", error);
  }
}

function sendToFrame(session, message) {
  return chrome.tabs.sendMessage(session.tabId, message, { frameId: session.frameId }).catch(() => {
    sessions.delete(session.sessionId);
  });
}
