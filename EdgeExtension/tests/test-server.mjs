import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const directory = path.dirname(fileURLToPath(import.meta.url));
const page = fs.readFileSync(path.join(directory, "manual-page.html"));
const server = http.createServer((request, response) => {
  if (request.url === "/" || request.url === "/manual-page.html") {
    response.writeHead(200, {
      "content-type": "text/html; charset=utf-8",
      "cache-control": "no-store"
    });
    response.end(page);
    return;
  }
  response.writeHead(404, { "content-type": "text/plain; charset=utf-8" });
  response.end("未找到测试页面");
});

server.listen(8765, "127.0.0.1", () => {
  console.log("ListenFlow Edge 测试页：http://127.0.0.1:8765/");
});
