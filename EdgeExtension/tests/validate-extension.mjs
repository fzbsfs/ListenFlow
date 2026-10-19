import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const manifest = JSON.parse(fs.readFileSync(path.join(root, "manifest.json"), "utf8"));

assert.equal(manifest.manifest_version, 3);
assert.deepEqual(manifest.host_permissions, ["http://*/*", "https://*/*"]);
assert.ok(manifest.permissions.includes("nativeMessaging"));
assert.ok(manifest.permissions.includes("contextMenus"));

const publicKey = Buffer.from(manifest.key, "base64");
const digest = crypto.createHash("sha256").update(publicKey).digest().subarray(0, 16);
const alphabet = "abcdefghijklmnop";
const extensionId = [...digest].map(value => alphabet[value >> 4] + alphabet[value & 15]).join("");
assert.equal(extensionId, "chdkljcgemgeljdnfdnblbelfhmkkfbp");

const content = manifest.content_scripts[0];
for (const relative of [...content.js, ...content.css, manifest.background.service_worker])
  assert.ok(fs.existsSync(path.join(root, relative)), `缺少扩展文件：${relative}`);

const hostTemplate = fs.readFileSync(
  path.join(root, "native-host", "com.listenflow.native_host.json.template"),
  "utf8"
);
assert.ok(hostTemplate.includes(`chrome-extension://${extensionId}/`));
assert.ok(fs.existsSync(path.join(root, "vendor", "READABILITY-LICENSE.md")));
assert.ok(!JSON.stringify(manifest).includes("http://localhost"));
assert.ok(!JSON.stringify(manifest).includes("https://cdn"));

console.log(`扩展结构验证通过，固定扩展标识：${extensionId}`);
