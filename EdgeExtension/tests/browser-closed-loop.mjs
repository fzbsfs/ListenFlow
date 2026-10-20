import assert from "node:assert/strict";
import fs from "node:fs/promises";
import path from "node:path";

const endpoint = process.argv[2] ?? "http://127.0.0.1:9223";
const pageUrl = process.argv[3] ?? "http://127.0.0.1:8765/";
const extensionId = "chdkljcgemgeljdnfdnblbelfhmkkfbp";
const screenshotDirectory = process.argv[4];

class CdpConnection {
  constructor(url) {
    this.url = url;
    this.nextId = 1;
    this.pending = new Map();
    this.listeners = new Map();
  }

  async connect() {
    this.socket = new WebSocket(this.url);
    this.socket.addEventListener("message", event => {
      const message = JSON.parse(event.data);
      if (message.id) {
        const pending = this.pending.get(message.id);
        if (!pending) return;
        this.pending.delete(message.id);
        if (message.error) pending.reject(new Error(message.error.message));
        else pending.resolve(message.result);
        return;
      }
      for (const listener of this.listeners.get(message.method) ?? [])
        listener(message.params);
    });
    await new Promise((resolve, reject) => {
      this.socket.addEventListener("open", resolve, { once: true });
      this.socket.addEventListener("error", reject, { once: true });
    });
  }

  on(method, listener) {
    const listeners = this.listeners.get(method) ?? [];
    listeners.push(listener);
    this.listeners.set(method, listeners);
  }

  send(method, params = {}) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.socket.send(JSON.stringify({ id, method, params }));
    });
  }

  close() {
    this.socket.close();
  }
}

async function targets() {
  const response = await fetch(`${endpoint}/json/list`);
  assert.equal(response.ok, true, `无法读取调试目标：${response.status}`);
  return response.json();
}

async function evaluate(connection, expression, contextId) {
  const response = await connection.send("Runtime.evaluate", {
    expression,
    contextId,
    awaitPromise: true,
    returnByValue: true
  });
  if (response.exceptionDetails)
    throw new Error(response.exceptionDetails.exception?.description ?? response.exceptionDetails.text);
  return response.result.value;
}

async function waitUntil(check, description, timeoutMs = 15000) {
  const deadline = Date.now() + timeoutMs;
  let last;
  while (Date.now() < deadline) {
    last = await check();
    if (last) return last;
    await new Promise(resolve => setTimeout(resolve, 120));
  }
  throw new Error(`等待超时：${description}；最后状态=${JSON.stringify(last)}`);
}

async function sendContextAction(worker, action) {
  const expression = `(async () => {
    const tabs = await chrome.tabs.query({ url: ${JSON.stringify(pageUrl)} });
    const tab = tabs.find(item => item.active) ?? tabs[0];
    if (!tab) throw new Error("没有找到测试标签页");
    return chrome.tabs.sendMessage(tab.id, {
      kind: "context-action",
      action: ${JSON.stringify(action)}
    }, { frameId: 0 });
  })()`;
  const response = await evaluate(worker, expression);
  if (!response?.ok) throw new Error(response?.error ?? `${action} 命令失败`);
  return response;
}

async function ensureContentScript(worker) {
  const expression = `(async () => {
    const tabs = await chrome.tabs.query({ url: ${JSON.stringify(pageUrl)} });
    const tab = tabs.find(item => item.active) ?? tabs[0];
    if (!tab) throw new Error("没有找到测试标签页");
    await ensureContentAndSend(tab.id, 0, { kind: "listenflow-test-probe" });
    return true;
  })()`;
  await evaluate(worker, expression);
}

async function main() {
  let available = await targets();
  const pageTarget = available.find(target => target.type === "page" && target.url === pageUrl);
  assert.ok(pageTarget, "没有找到 Edge 测试页调试目标。");

  const page = new CdpConnection(pageTarget.webSocketDebuggerUrl);
  const contexts = [];
  page.on("Runtime.executionContextCreated", ({ context }) => contexts.push(context));
  await page.connect();
  await Promise.all([page.send("Runtime.enable"), page.send("Page.enable"), page.send("ServiceWorker.enable")]);

  let workerTarget = available.find(target =>
    target.type === "service_worker" && target.url === `chrome-extension://${extensionId}/background.js`);
  if (!workerTarget) {
    await page.send("ServiceWorker.startWorker", { scopeURL: `chrome-extension://${extensionId}/` });
    workerTarget = await waitUntil(async () => {
      available = await targets();
      return available.find(target =>
        target.type === "service_worker" && target.url === `chrome-extension://${extensionId}/background.js`);
    }, "启动 ListenFlow 扩展后台");
  }
  const worker = new CdpConnection(workerTarget.webSocketDebuggerUrl);
  await worker.connect();
  await worker.send("Runtime.enable");
  await ensureContentScript(worker);

  const extensionContext = await waitUntil(async () => {
    for (const context of contexts) {
      try {
        const loaded = await evaluate(page, "globalThis.__listenFlowContentLoaded === true", context.id);
        if (loaded) return context;
      } catch {
      }
    }
    return null;
  }, "扩展内容脚本执行环境");

  const readHighlight = () => evaluate(page, `(() => {
    const highlight = globalThis.CSS?.highlights?.get("listenflow-current-sentence");
    const firstRange = highlight ? [...highlight][0] : null;
    const element = firstRange?.startContainer?.parentElement;
    const style = element ? getComputedStyle(element, "::highlight(listenflow-current-sentence)") : null;
    return {
      text: highlight ? [...highlight].map(range => range.toString()).join("") : "",
      count: highlight ? [...highlight].length : 0,
      backgroundColor: style?.backgroundColor ?? "",
      textDecorationLine: style?.textDecorationLine ?? "",
      overlayCount: document.querySelectorAll(".listenflow-highlight-overlay").length,
      toast: document.querySelector(".listenflow-status-toast")?.textContent ?? ""
    };
  })()`, extensionContext.id);

  const textPoint = async (selector, text) => evaluate(page, `(() => {
    const element = document.querySelector(${JSON.stringify(selector)});
    const node = [...element.childNodes].find(item => item.nodeType === Node.TEXT_NODE && item.data.includes(${JSON.stringify(text)}));
    if (!node) throw new Error("找不到定位文字");
    const start = node.data.indexOf(${JSON.stringify(text)});
    const range = document.createRange();
    range.setStart(node, start);
    range.setEnd(node, start + ${JSON.stringify(text)}.length);
    const rect = range.getBoundingClientRect();
    return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
  })()`);

  const clickText = async (selector, text) => {
    const point = await textPoint(selector, text);
    await page.send("Input.dispatchMouseEvent", { type: "mousePressed", x: point.x, y: point.y, button: "left", clickCount: 1 });
    await page.send("Input.dispatchMouseEvent", { type: "mouseReleased", x: point.x, y: point.y, button: "left", clickCount: 1 });
  };

  const saveScreenshot = async name => {
    if (!screenshotDirectory) return;
    await fs.mkdir(screenshotDirectory, { recursive: true });
    const result = await page.send("Page.captureScreenshot", {
      format: "png",
      fromSurface: true,
      captureBeyondViewport: false
    });
    await fs.writeFile(path.join(screenshotDirectory, `${name}.png`), Buffer.from(result.data, "base64"));
  };

  await evaluate(page, `(() => {
    const node = document.querySelector("#paragraph-2").firstChild;
    const text = "真实语音进度会从本机程序返回";
    const start = node.data.indexOf(text);
    const range = document.createRange();
    range.setStart(node, start);
    range.setEnd(node, start + text.length);
    const selection = getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
    const rect = range.getBoundingClientRect();
    document.elementFromPoint(rect.left + 4, rect.top + 4).dispatchEvent(new MouseEvent("contextmenu", {
      bubbles: true,
      cancelable: true,
      clientX: rect.left + 4,
      clientY: rect.top + 4,
      button: 2
    }));
  })()`);
  await sendContextAction(worker, "start-selection");
  const selected = await waitUntil(async () => {
    const value = await readHighlight();
    return value.text.includes("真实语音进度会从本机程序返回") ? value : null;
  }, "所选网页文字的进度高亮");
  console.log(`通过：所选网页文字进入本地朗读并高亮：${selected.text}`);
  await clickText("#paragraph-3", "点击定位目标");
  const expandedSelection = await waitUntil(async () => {
    const value = await readHighlight();
    return value.text.includes("点击位置附近重新开始朗读") ? value : null;
  }, "选区朗读扩展到同一文档远处正文", 10000);
  console.log(`通过：选区朗读可点击同文档远处正文并原子替换快照：${expandedSelection.text}`);
  await sendContextAction(worker, "stop");
  await waitUntil(async () => (await readHighlight()).count === 0, "停止所选文字朗读");

  await sendContextAction(worker, "start-article");
  const first = await waitUntil(async () => {
    const value = await readHighlight();
    return value.count > 0 ? value : null;
  }, "实际 SAPI 进度驱动首个网页高亮", 20000);
  assert.match(first.text, /ListenFlow|第一段/);
  const customHighlightVisible = first.backgroundColor !== "rgba(0, 0, 0, 0)"
    && first.backgroundColor !== "transparent" && first.backgroundColor !== "";
  assert.ok(customHighlightVisible || first.overlayCount > 0, "高亮范围存在，但视觉高亮没有生效。");
  await saveScreenshot("01-实际进度高亮");
  console.log(`通过：实际进度已在网页原文高亮：${first.text}`);

  const manualScroll = await evaluate(page, `(() => {
    const maximum = Math.max(0, document.documentElement.scrollHeight - innerHeight);
    window.scrollTo(0, maximum);
    return { maximum, position: scrollY };
  })()`);
  if (manualScroll.maximum > 0) {
    await new Promise(resolve => setTimeout(resolve, 1200));
    const afterProgress = await evaluate(page, "scrollY");
    assert.ok(Math.abs(afterProgress - manualScroll.position) <= 2,
      `语音进度不应强制改变手动滚动位置：${manualScroll.position} -> ${afterProgress}`);
    console.log("通过：语音进度更新没有把手动滚动位置拉回高亮句。");
  }

  await clickText("#paragraph-3", "点击定位目标");
  const located = await waitUntil(async () => {
    const value = await readHighlight();
    return value.text.includes("点击位置附近重新开始朗读") ? value : null;
  }, "左键定位后的高亮跳转", 10000);
  await saveScreenshot("02-左键定位高亮");
  console.log(`通过：左键定位后高亮跳转：${located.text}`);

  await sendContextAction(worker, "pause");
  await clickText("#paragraph-5", "暂停以后点击");
  const pausedSeek = await waitUntil(async () => {
    const value = await readHighlight();
    return value.text.includes("暂停以后点击这一段的文字") ? value : null;
  }, "暂停期间定位后的高亮跳转");
  await new Promise(resolve => setTimeout(resolve, 800));
  const pausedStable = await readHighlight();
  assert.equal(pausedStable.text, pausedSeek.text, "暂停期间高亮不应自行推进。");
  await saveScreenshot("03-暂停定位高亮");
  console.log(`通过：暂停定位不发起进度推进：${pausedSeek.text}`);

  await sendContextAction(worker, "resume");
  await waitUntil(async () => {
    const value = await readHighlight();
    return value.count > 0 ? value : null;
  }, "继续后的高亮");
  console.log("通过：暂停后继续，高亮仍由进度事件驱动。");

  await clickText("#paragraph-4", "先点击这一句");
  await clickText("#paragraph-4", "再快速点击下一句");
  await clickText("#paragraph-6", "停止以后");
  const rapid = await waitUntil(async () => {
    const value = await readHighlight();
    return value.text.includes("停止以后，高亮应被清除") ? value : null;
  }, "连续点击仅保留最后定位");
  await saveScreenshot("04-连续定位最终位置");
  console.log(`通过：连续定位最终落在最后一次点击：${rapid.text}`);

  const buttonPoint = await evaluate(page, `(() => {
    const rect = document.querySelector("#test-button").getBoundingClientRect();
    return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
  })()`);
  await page.send("Input.dispatchMouseEvent", { type: "mousePressed", x: buttonPoint.x, y: buttonPoint.y, button: "left", clickCount: 1 });
  await page.send("Input.dispatchMouseEvent", { type: "mouseReleased", x: buttonPoint.x, y: buttonPoint.y, button: "left", clickCount: 1 });
  const buttonText = await evaluate(page, "document.querySelector('#test-button').textContent");
  assert.equal(buttonText, "按钮点击正常");
  console.log("通过：按钮点击未被指读模式拦截。");

  await sendContextAction(worker, "stop");
  await waitUntil(async () => {
    const value = await readHighlight();
    return value.count === 0 ? value : null;
  }, "停止后清除网页高亮");
  await saveScreenshot("05-停止后清除高亮");
  console.log("通过：停止后网页高亮已清除。");

  page.close();
  worker.close();
}

main().catch(error => {
  console.error(error.stack ?? error);
  process.exit(1);
});
