const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "../background.js"), "utf8");

function fixture() {
  const menus = [];
  const frameMessages = [];
  const nativeMessages = [];
  let installedListener;
  let clickedListener;
  let runtimeListener;
  let nativeMessageListener;
  const port = {
    postMessage(message) { nativeMessages.push(message); },
    onMessage: { addListener(listener) { nativeMessageListener = listener; } },
    onDisconnect: { addListener() {} }
  };
  const context = vm.createContext({
    console,
    crypto: { randomUUID: () => "uuid" },
    chrome: {
      contextMenus: {
        removeAll(callback) { menus.length = 0; callback(); },
        create(menu) { menus.push(menu); },
        onClicked: { addListener(listener) { clickedListener = listener; } }
      },
      runtime: {
        lastError: null,
        onInstalled: { addListener(listener) { installedListener = listener; } },
        onMessage: { addListener(listener) { runtimeListener = listener; } },
        connectNative() { return port; }
      },
      tabs: {
        sendMessage(tabId, message, target) {
          frameMessages.push({ tabId, message, target });
          return Promise.resolve();
        },
        onRemoved: { addListener() {} },
        onUpdated: { addListener() {} }
      },
      scripting: {
        insertCSS() { return Promise.resolve(); },
        executeScript() { return Promise.resolve(); }
      }
    }
  });
  vm.runInContext(source, context);
  return {
    menus, frameMessages, nativeMessages,
    install() { installedListener(); },
    click(menuItemId) { clickedListener({ menuItemId, frameId: 0 }, { id: 7 }); },
    command(command) {
      let response;
      runtimeListener({ kind: "bridge-command", command }, { tab: { id: 7 }, frameId: 0 }, value => response = value);
      return response;
    },
    native(message) { nativeMessageListener(message); }
  };
}

test("右键菜单提供完整页面重新扫描与四档语速", async () => {
  const env = fixture();
  env.install();

  assert.ok(env.menus.some(menu => menu.id === "listenflow-read-article" && /重新扫描/.test(menu.title)));
  assert.ok(env.menus.some(menu => menu.id === "listenflow-read-page" && /全部可见文字/.test(menu.title)));
  assert.equal(env.menus.filter(menu => String(menu.id).startsWith("listenflow-rate-")).length, 4);

  env.click("listenflow-rate-4");
  await Promise.resolve();
  assert.equal(env.frameMessages.at(-1).message.action, "set-rate");
  assert.equal(env.frameMessages.at(-1).message.rate, 4);

  env.click("listenflow-read-article");
  await Promise.resolve();
  assert.equal(env.frameMessages.at(-1).message.action, "start-article");
  assert.equal(env.frameMessages.at(-1).message.rate, 4);
});

test("停止状态不会删除网页会话以便主界面播放时重新扫描", () => {
  const env = fixture();
  const start = {
    version: 1, type: "start", requestId: "r1", sessionId: "s1",
    snapshotId: "p1", snapshotVersion: 1, commandSeq: 1,
    offsetEncoding: "utf-16", offset: 0, text: "正文内容。"
  };
  assert.equal(env.command(start).ok, true);

  env.native({ type: "state", state: "stopped", sessionId: "s1" });
  env.native({ type: "state", state: "speaking", sessionId: "s1" });

  const states = env.frameMessages
    .filter(item => item.message.kind === "bridge-event")
    .map(item => item.message.message.state);
  assert.deepEqual(states, ["stopped", "speaking"]);
});
