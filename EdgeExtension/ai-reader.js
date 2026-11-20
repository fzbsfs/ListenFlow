(function (global) {
  "use strict";
  if (global.ListenFlowAIReader) return;

  function createQueue(start, onError) {
    const pending = [];
    let known = new Set();
    let running = false;
    let stopped = false;
    let epoch = 0;
    let currentElement = null;
    let currentKey = null;
    const keyOf = element => element.getAttribute?.("data-message-id") || element;
    async function advance() {
      if (running || stopped) return;
      while (pending.length) {
        const element = pending.shift();
        known.delete(keyOf(element));
        if (!element.isConnected) continue;
        running = true;
        currentElement = element;
        currentKey = keyOf(element);
        const current = epoch;
        try { await start(element); }
        catch (error) { if (current === epoch) { running = false; stopped = true; pending.length = 0; known = new Set(); currentElement = null; currentKey = null; onError(error); } }
        return;
      }
    }
    return {
      enqueue(element) {
        if (!element || keyOf(element) === currentKey || known.has(keyOf(element))) return;
        stopped = false;
        known.add(keyOf(element));
        pending.push(element);
        void advance();
      },
      replace(element) { epoch++; pending.length = 0; known = new Set(); running = false; currentElement = null; currentKey = null; stopped = false; this.enqueue(element); },
      complete() { running = false; currentElement = null; currentKey = null; void advance(); },
      stop() { epoch++; stopped = true; running = false; currentElement = null; currentKey = null; pending.length = 0; known = new Set(); },
      get current() { return currentElement; },
      get length() { return pending.length; }
    };
  }

  function install(options) {
    if (!/^(chatgpt\.com|chat\.openai\.com)$/i.test(location.hostname) || global.top !== global) return null;
    const tracked = new WeakMap();
    const queue = createQueue(element => options.start(element, includeCode.checked), options.error);
    let pathname = location.pathname;
    let wasGenerating = false;
    let initialized = false;
    let allowNewDuringGeneration = false;
    let generationReplies = new Set();
    let timer = null;
    const panel = document.createElement("div");
    panel.className = "listenflow-ai-panel";
    panel.dataset.listenflowOwned = "";
    function button(label, handler) {
      const item = document.createElement("button"); item.type = "button"; item.textContent = label;
      item.addEventListener("click", handler); panel.appendChild(item); return item;
    }
    function checkbox(label) {
      const holder = document.createElement("label");
      const input = document.createElement("input"); input.type = "checkbox";
      holder.append(input, document.createTextNode(label)); panel.appendChild(holder); return input;
    }
    const auto = checkbox("自动朗读新回复");
    const includeCode = checkbox("朗读代码块");
    button("连续朗读", () => {
      const replies = [...document.querySelectorAll("[data-message-author-role='assistant']")];
      if (isGenerating()) { options.error(new Error("请等待回复生成完成。")); return; }
      replies.forEach(reply => queue.enqueue(reply));
    });
    button("暂停", () => void options.control("pause").catch(options.error));
    button("继续", () => void options.control("resume").catch(options.error));
    button("停止", () => { queue.stop(); void options.control("stop").catch(options.error); });
    for (const rate of [0, 2, 4, 6])
      button(`语速 ${rate === 0 ? "0" : "+" + rate}`, () => void options.control("set-rate", rate).catch(options.error));
    document.body.appendChild(panel);

    function isGenerating() {
      return !!document.querySelector("button[data-testid='stop-button'], button[aria-label='Stop generating'], button[aria-label='停止生成']");
    }
    function scan() {
      if (pathname !== location.pathname) {
        pathname = location.pathname;
        if (queue.current) options.reset();
        queue.stop(); generationReplies = new Set();
        initialized = false;
        wasGenerating = false;
        allowNewDuringGeneration = false;
      }
      if (queue.current && !queue.current.isConnected) { options.reset(); queue.stop(); }
      const generating = isGenerating();
      if (initialized && generating && !wasGenerating) allowNewDuringGeneration = true;
      for (const reply of document.querySelectorAll("[data-message-author-role='assistant']")) {
        let state = tracked.get(reply);
        if (!state) {
          state = { text: reply.textContent ?? "", button: null };
          tracked.set(reply, state);
          const read = document.createElement("button"); read.type = "button";
          read.textContent = "朗读此回复"; read.className = "listenflow-ai-read";
          read.dataset.listenflowOwned = "";
          read.addEventListener("click", event => {
            event.preventDefault(); event.stopPropagation();
            if (isGenerating()) { options.error(new Error("请等待回复生成完成。")); return; }
            queue.replace(reply);
          });
          reply.insertAdjacentElement("afterend", read); state.button = read;
          if (initialized && generating && allowNewDuringGeneration) generationReplies.add(reply);
        } else {
          const text = reply.textContent ?? "";
          if (generating && text !== state.text) generationReplies.add(reply);
          state.text = text;
          if (!state.button.isConnected) reply.insertAdjacentElement("afterend", state.button);
        }
      }
      if (wasGenerating && !generating) {
        if (auto.checked) for (const reply of generationReplies) if (reply.isConnected) queue.enqueue(reply);
        generationReplies = new Set();
        allowNewDuringGeneration = false;
      }
      wasGenerating = generating;
      initialized = true;
    }
    const observer = new MutationObserver(() => {
      if (timer !== null) return;
      timer = setTimeout(() => { timer = null; scan(); }, 100);
    });
    observer.observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ["data-testid", "aria-label"] });
    scan();
    return {
      onState(state) { if (state === "completed") queue.complete(); else if (["stopped", "error"].includes(state)) queue.stop(); },
      dispose() { queue.stop(); observer.disconnect(); clearTimeout(timer); panel.remove(); }
    };
  }

  global.ListenFlowAIReader = Object.freeze({ createQueue, install });
})(globalThis);
