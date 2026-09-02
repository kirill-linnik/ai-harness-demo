const app = document.querySelector("#app");
const toastRegion = document.querySelector("#toast-region");

const state = {
  bootstrap: null,
  currentFlow: null,
  intakeFlow: null,
  selectedStepId: null,
  pollTimer: null,
  recognition: null,
  activeVoiceButton: null,
  directoryPath: "",
  feedbackSentForFlow: new Set()
};

const icons = {
  factory: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 20V9l5 3V8l5 3V4h3v7l3 2v7H4Z"/><path d="M8 20v-3h3v3m3 0v-3h3v3"/></svg>`,
  settings: `<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1-2.8 2.8-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.6v.2h-4V21a1.7 1.7 0 0 0-1-1.6 1.7 1.7 0 0 0-1.9.3l-.1.1L4.2 17l.1-.1a1.7 1.7 0 0 0 .3-1.9A1.7 1.7 0 0 0 3 14H2.8v-4H3a1.7 1.7 0 0 0 1.6-1 1.7 1.7 0 0 0-.3-1.9L4.2 7 7 4.2l.1.1A1.7 1.7 0 0 0 9 4.6a1.7 1.7 0 0 0 1-1.6v-.2h4V3a1.7 1.7 0 0 0 1 1.6 1.7 1.7 0 0 0 1.9-.3l.1-.1L19.8 7l-.1.1a1.7 1.7 0 0 0-.3 1.9 1.7 1.7 0 0 0 1.6 1h.2v4H21a1.7 1.7 0 0 0-1.6 1Z"/></svg>`,
  history: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 3v5h5m4-2v6l4 2"/></svg>`,
  memory: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M9 4.5A3 3 0 0 0 4.7 8a3.5 3.5 0 0 0 .6 6.6A3.3 3.3 0 0 0 9 19.5M15 4.5A3 3 0 0 1 19.3 8a3.5 3.5 0 0 1-.6 6.6 3.3 3.3 0 0 1-3.7 4.9M9 3v18m6-18v18M9 8H7m8 4h3M9 16H6m9 2h2"/></svg>`,
  mic: `<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="9" y="3" width="6" height="12" rx="3"/><path d="M5 11a7 7 0 0 0 14 0m-7 7v3m-4 0h8"/></svg>`,
  send: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m21 3-8 18-2-8-8-2 18-8Z"/><path d="m11 13 4-4"/></svg>`,
  plus: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 5v14M5 12h14"/></svg>`,
  copy: `<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="8" y="8" width="11" height="11" rx="2"/><path d="M16 8V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h3"/></svg>`,
  folder: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 6a2 2 0 0 1 2-2h5l2 2h7a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6Z"/></svg>`,
  close: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m6 6 12 12M18 6 6 18"/></svg>`,
  arrow: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 12h14m-5-5 5 5-5 5"/></svg>`,
  check: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m5 12 4 4L19 6"/></svg>`,
  back: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M19 12H5m5 5-5-5 5-5"/></svg>`,
  refresh: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M20 11a8 8 0 1 0-2.3 5.7L20 14"/><path d="M20 6v5h-5"/></svg>`,
  external: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M14 5h5v5M19 5l-9 9"/><path d="M19 13v5a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1h5"/></svg>`,
  more: `<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/></svg>`
};

const accentColors = {
  amber: "#f7b955",
  violet: "#9b87f5",
  indigo: "#7c8cff",
  pink: "#ef88d0",
  cyan: "#41d9e8",
  blue: "#51a8ff",
  orange: "#ff9f68",
  emerald: "#42d6a4",
  teal: "#4bd3c4",
  lime: "#a7dd67",
  rose: "#ff8197"
};

document.addEventListener("DOMContentLoaded", initialize);
window.addEventListener("hashchange", renderRoute);

async function initialize() {
  try {
    state.bootstrap = await request("/api/bootstrap");
    if (!location.hash) {
      location.hash = "#/factory";
      return;
    }
    await renderRoute();
  } catch (error) {
    renderFatal(error);
  }
}

async function renderRoute() {
  clearTimeout(state.pollTimer);
  state.pollTimer = null;
  const route = getRoute();

  try {
    if (route.page === "preview" && route.id) {
      await renderPreview(route.id);
      return;
    }

    if (!state.bootstrap) {
      state.bootstrap = await request("/api/bootstrap");
    }
    if (route.page === "intake" && !state.bootstrap.factoryEnabled) {
      renderFactoryLocked();
      return;
    }

    if (route.page === "settings") {
      renderSettings();
      return;
    }
    if (route.page === "history") {
      await renderHistory();
      return;
    }
    if (route.page === "memory") {
      await renderMemory();
      return;
    }
    if (route.page === "intake") {
      await renderIntake(route.id);
      return;
    }
    if (route.page === "factory" && route.id) {
      const flow = await request(`/api/flows/${route.id}`);
      renderFlow(flow);
      return;
    }

    renderFactoryOverview();
  } catch (error) {
    toast(error.message, "error");
    renderFactoryOverview();
  }
}

function getRoute() {
  const value = location.hash.replace(/^#\/?/, "");
  const [page = "factory", id = null] = value.split("/");
  return { page, id };
}

function shell({ active, title, subtitle, content, actions = "" }) {
  const data = state.bootstrap;
  const activeCount = data?.stats?.activeFlows ?? 0;
  const memoryCount = data?.stats?.learnedRefinements ?? 0;
  const repositoryName = data?.settings?.repositoryPath
    ? lastPathPart(data.settings.repositoryPath)
    : "No repository";

  return `
    <div class="app-shell">
      <aside class="sidebar">
        <div class="brand">
          <div class="brand-mark"><span></span><span></span><span></span></div>
          <div class="brand-copy"><strong>AI Harness Studio</strong><span>Symphony .NET core</span></div>
        </div>
        <div class="nav-label">Workspace</div>
        <nav class="nav-list" aria-label="Main navigation">
          ${navItem("factory", "AI Factory", icons.factory, active, activeCount, !data?.factoryEnabled)}
          ${navItem("settings", "Settings", icons.settings, active)}
          ${navItem("history", "Execution history", icons.history, active)}
          ${navItem("memory", "Harness memory", icons.memory, active, memoryCount)}
        </nav>
        <div class="sidebar-spacer"></div>
        <div class="runtime-card">
          <div class="runtime-line">
            <span class="status-light ${data?.copilotCliAvailable ? "" : "offline"}"></span>
            <strong>${data?.copilotCliAvailable ? "Copilot CLI ready" : "Copilot CLI unavailable"}</strong>
          </div>
          <div class="runtime-line">
            <small>Repository</small>
            <strong title="${escapeAttribute(data?.settings?.repositoryPath || "")}">${escapeHtml(repositoryName)}</strong>
          </div>
          <div class="runtime-line">
            <span class="status-light ${data?.workflow?.ready ? "" : "offline"}"></span>
            <strong title="${escapeAttribute(data?.workflow?.sourcePath || "")}">${data?.workflow?.ready ? "WORKFLOW.md live" : "Workflow needs attention"}</strong>
          </div>
        </div>
      </aside>
      <div class="main-column">
        <header class="topbar">
          <div class="topbar-title">
            <h1>${escapeHtml(title)}</h1>
            <p>${escapeHtml(subtitle)}</p>
          </div>
          <div class="topbar-actions">
            ${actions}
            <button class="button primary small js-new-flow" ${data?.factoryEnabled ? "" : "disabled"} title="${data?.factoryEnabled ? "Start a new assignment" : escapeAttribute(data?.factoryDisabledReason || "Configure a project first.")}">${icons.plus} New assignment</button>
          </div>
        </header>
        <main class="content">${content}</main>
      </div>
    </div>`;
}

function navItem(page, label, icon, active, count = null, disabled = false) {
  if (disabled) {
    return `
      <span class="nav-item disabled" aria-disabled="true" title="${escapeAttribute(state.bootstrap?.factoryDisabledReason || "Configure a project first.")}">
        <span class="nav-icon">${icon}</span>
        <span>${escapeHtml(label)}</span>
      </span>`;
  }
  return `
    <a class="nav-item ${active === page ? "active" : ""}" href="#/${page}">
      <span class="nav-icon">${icon}</span>
      <span>${escapeHtml(label)}</span>
      ${count ? `<span class="nav-count">${count}</span>` : ""}
    </a>`;
}

function wireShell() {
  if (!state.bootstrap?.factoryEnabled) return;
  document.querySelectorAll(".js-new-flow").forEach(button => {
    button.addEventListener("click", () => {
      state.intakeFlow = null;
      location.hash = "#/intake";
    });
  });
}

function renderFactoryOverview() {
  const data = state.bootstrap;
  if (!data.factoryEnabled) {
    renderFactoryLocked();
    return;
  }
  const flows = data.flows || [];
  const stats = data.stats;
  const content = `
    <section class="stats-grid">
      ${statCard("Active flows", stats.activeFlows, "Independent tasks in motion", "rgba(81, 168, 255, .14)")}
      ${statCard("Approved", stats.completedFlows, "Customer-accepted outcomes", "rgba(66, 214, 164, .13)")}
      ${statCard("Prompt refinements", stats.learnedRefinements, "Learned across every flow", "rgba(155, 135, 245, .14)")}
      ${statCard("Agent time", stats.totalAgentMinutes, "Minutes in the execution ledger", "rgba(247, 185, 85, .12)")}
    </section>
    <section class="factory-hero">
      <div class="hero-copy">
        <div class="eyebrow">Symphony orchestration · Copilot execution</div>
        <h2>Describe the outcome.<br>The factory assembles the team.</h2>
        <p>Account Manager clarifies the brief. Team Lead selects enabled specialists. The harness observes every handoff, model choice, pushback, and correction.</p>
        <div class="hero-actions">
          <button class="button primary js-start-speaking">${icons.mic} Listen to the next task</button>
          <a class="button" href="#/settings">${icons.settings} Configure the team</a>
        </div>
        <div class="quick-prompts">
          <button class="prompt-chip" data-prompt="Add an accessible onboarding checklist to the selected product and persist completion across sessions.">Onboarding checklist</button>
          <button class="prompt-chip" data-prompt="Design and implement a usage dashboard with clear empty states, filtering, and export.">Usage dashboard</button>
          <button class="prompt-chip" data-prompt="Add secure role-based access for administrative settings and prove unauthorized users are blocked.">Role-based access</button>
        </div>
      </div>
      <div class="voice-orbit" aria-hidden="true">
        <div class="orbit one"></div><div class="orbit two"></div><div class="orbit three"></div>
        <button class="mic-button js-start-speaking" aria-label="Start a new voice assignment">${icons.mic}</button>
        ${waveMarkup()}
      </div>
    </section>
    ${flows.length ? `
      <div class="section-title" style="margin:28px 0 12px">
        <h3>Factory flows</h3>
        <p>Every workflow has a durable, shareable link.</p>
      </div>
      <section class="flow-grid">${flows.map(flowCard).join("")}</section>
    ` : ""}`;

  app.innerHTML = shell({
    active: "factory",
    title: "AI Factory",
    subtitle: `${stats.activeFlows} active · ${stats.completedFlows} approved · ${stats.learnedRefinements} learned refinements`,
    content
  });
  wireShell();

  document.querySelectorAll(".js-start-speaking").forEach(button => {
    button.addEventListener("click", () => {
      state.intakeFlow = null;
      location.hash = "#/intake";
      setTimeout(() => toggleVoice("intake-message", "intake-mic"), 250);
    });
  });
  document.querySelectorAll("[data-prompt]").forEach(button => {
    button.addEventListener("click", () => {
      sessionStorage.setItem("harness-draft-prompt", button.dataset.prompt);
      location.hash = "#/intake";
    });
  });
}

function renderFactoryLocked() {
  const reason = state.bootstrap?.factoryDisabledReason || "Add and study a source repository in Settings.";
  const content = `
    <div class="page-head">
      <div>
        <div class="eyebrow">Project required</div>
        <h2>The factory is waiting for its codebase</h2>
        <p>Agents stay disabled until the harness has a real repository and shared project knowledge.</p>
      </div>
    </div>
    <section class="card empty-state" style="min-height:430px">
      <div>
        <div class="empty-icon">${icons.folder}</div>
        <h3>AI Factory is locked</h3>
        <p>${escapeHtml(reason)}</p>
        <div class="quick-prompts" style="justify-content:center;margin-top:22px">
          <span class="model-chip">1 · Choose a Git repository</span>
          <span class="model-chip">2 · Run Copilot init</span>
          <span class="model-chip">3 · Review project knowledge</span>
        </div>
        <a class="button primary" href="#/settings" style="margin-top:22px">${icons.settings} Add project in Settings</a>
      </div>
    </section>`;

  app.innerHTML = shell({
    active: "factory",
    title: "AI Factory",
    subtitle: "Configure a source project to unlock agent execution",
    content
  });
  wireShell();
}

function statCard(label, value, detail, glow) {
  return `
    <article class="stat-card" style="--stat-glow:${glow}">
      <span>${escapeHtml(label)}</span>
      <strong>${Number(value || 0).toLocaleString()}</strong>
      <small>${escapeHtml(detail)}</small>
    </article>`;
}

function flowCard(flow) {
  const repository = flow.repositoryPath ? lastPathPart(flow.repositoryPath) : "Repository pending";
  return `
    <a class="flow-card" href="#/factory/${flow.id}">
      <div class="flow-card-top">
        ${statusPill(flow.status)}
        <span class="muted">Iteration ${flow.iteration}</span>
      </div>
      <h3>${escapeHtml(flow.title)}</h3>
      <p>${escapeHtml(flow.outcomeLabel || "Open the execution ledger and watch the team work.")}</p>
      <div class="flow-card-foot"><span>${escapeHtml(repository)}</span><span>${timeAgo(flow.updatedAt)}</span></div>
    </a>`;
}

async function renderIntake(flowId) {
  if (flowId && (!state.intakeFlow || state.intakeFlow.id !== flowId)) {
    state.intakeFlow = await request(`/api/flows/${flowId}`);
  }

  const flow = state.intakeFlow;
  const messages = flow?.messages || [];
  const latestAccountManagerMessage = [...messages].reverse().find(message => message.role === "AccountManager");
  const ready = !!latestAccountManagerMessage && !latestAccountManagerMessage.isQuestion;
  const draft = sessionStorage.getItem("harness-draft-prompt") || "";
  sessionStorage.removeItem("harness-draft-prompt");

  const content = `
    <div class="page-head">
      <div>
        <div class="eyebrow">Customer intake</div>
        <h2>Talk to your Account Manager</h2>
        <p>Your voice becomes a durable brief. Clarifications stay attached to the flow and follow every specialist.</p>
      </div>
      ${ready ? `<button class="button primary js-start-flow">${icons.arrow} Send to Team Lead</button>` : ""}
    </div>
    <section class="intake-layout">
      <div class="card conversation">
        <div class="card-header">
          <div>
            <h3>Live brief</h3>
            <p>${flow ? `Flow ${flow.id.slice(0, 8)} · ${messages.length} dialogue turns` : "A new flow starts with your first message."}</p>
          </div>
          ${ready ? `<span class="status-pill approved">Brief ready</span>` : `<span class="status-pill intake">Clarifying</span>`}
        </div>
        <div class="conversation-log" id="conversation-log">
          ${messages.length ? messages.map(messageMarkup).join("") : `
            <div class="message accountmanager">
              <div class="message-avatar">AM</div>
              <div class="message-bubble"><strong>Account Manager</strong>Tell me what you want the team to change. I already have the selected repository context.</div>
            </div>`}
        </div>
        <div class="composer">
          <div class="composer-row">
            <button id="intake-mic" class="icon-button" aria-label="Record task">${icons.mic}</button>
            <textarea id="intake-message" placeholder="Describe the customer outcome..." aria-label="Customer request">${escapeHtml(draft)}</textarea>
            <button class="button primary js-send-intake">${icons.send} Send</button>
          </div>
          <div class="composer-hint">Use voice in Edge or Chrome on localhost, or type when the room is noisy.</div>
        </div>
      </div>
      <div class="intake-signal" id="intake-signal">
        <div class="orbit one"></div><div class="orbit two"></div><div class="orbit three"></div>
        <button id="intake-mic-large" class="mic-button" aria-label="Start voice recording">${icons.mic}</button>
        ${waveMarkup()}
        <div class="intake-signal-copy">
          <strong>${ready ? "The task is ready for orchestration" : "Listening for product intent"}</strong>
          <span>${ready ? "Team Lead will select the smallest capable team." : "Account Manager will ask only for material missing context."}</span>
        </div>
      </div>
    </section>`;

  app.innerHTML = shell({
    active: "factory",
    title: "Customer intake",
    subtitle: "Voice dialogue · project-grounded clarification · durable brief",
    content
  });
  wireShell();
  document.querySelector("#intake-mic")?.addEventListener("click", () => toggleVoice("intake-message", "intake-mic"));
  document.querySelector("#intake-mic-large")?.addEventListener("click", () => toggleVoice("intake-message", "intake-mic-large"));
  document.querySelector(".js-send-intake")?.addEventListener("click", submitIntake);
  document.querySelector("#intake-message")?.addEventListener("keydown", event => {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      submitIntake();
    }
  });
  document.querySelectorAll(".js-start-flow").forEach(button => button.addEventListener("click", startCurrentFlow));

  requestAnimationFrame(() => {
    const log = document.querySelector("#conversation-log");
    if (log) log.scrollTop = log.scrollHeight;
  });
}

function messageMarkup(message) {
  const customer = message.role === "Customer";
  const initials = customer ? "YOU" : message.role === "ProductManager" ? "PM" : message.role === "Harness" ? "AI" : "AM";
  const label = splitWords(message.role).join(" ");
  return `
    <div class="message ${customer ? "customer" : message.role.toLowerCase()}">
      ${customer ? `
        <div class="message-bubble"><strong>${escapeHtml(label)}</strong>${escapeHtml(message.content)}</div>
        <div class="message-avatar">${initials}</div>
      ` : `
        <div class="message-avatar">${initials}</div>
        <div class="message-bubble"><strong>${escapeHtml(label)}</strong>${escapeHtml(message.content)}</div>
      `}
    </div>`;
}

async function submitIntake() {
  const textarea = document.querySelector("#intake-message");
  const message = textarea?.value.trim();
  if (!message) {
    toast("Describe the change before sending.", "error");
    return;
  }

  const button = document.querySelector(".js-send-intake");
  setBusy(button, true, "Thinking...");
  try {
    const response = await request("/api/intake", {
      method: "POST",
      body: JSON.stringify({ flowId: state.intakeFlow?.id || null, message })
    });
    state.intakeFlow = response.flow;
    history.replaceState(null, "", `#/intake/${response.flow.id}`);
    if (response.shouldSpeak) speak(response.reply);
    await refreshBootstrap();
    await renderIntake(response.flow.id);
  } catch (error) {
    toast(error.message, "error");
    setBusy(button, false);
  }
}

async function startCurrentFlow() {
  if (!state.intakeFlow) return;
  const buttons = document.querySelectorAll(".js-start-flow");
  buttons.forEach(button => setBusy(button, true, "Queuing..."));
  try {
    await request(`/api/flows/${state.intakeFlow.id}/start`, { method: "POST", body: "{}" });
    await refreshBootstrap();
    location.hash = `#/factory/${state.intakeFlow.id}`;
  } catch (error) {
    toast(error.message, "error");
    buttons.forEach(button => setBusy(button, false));
  }
}

function renderSettings() {
  const { settings, agents } = state.bootstrap;
  const enabled = agents.filter(agent => agent.enabled).length;
  const content = `
    <div class="page-head">
      <div>
        <div class="eyebrow">Harness configuration</div>
        <h2>Shape the team and its context</h2>
        <p>Agent definitions come from <span class="mono">.github\\agents</span>; orchestration policy comes from hot-reloadable <span class="mono">WORKFLOW.md</span>. Settings are persisted to SQLite.</p>
      </div>
      <button class="button js-save-settings">${icons.check} Save settings</button>
    </div>
    <section class="settings-grid">
      <div class="settings-stack">
        <div class="card">
          <div class="card-header">
            <div><h3>Symphony workflow contract</h3><p>Version-controlled policy, prompt template, hooks, and runtime limits.</p></div>
            <span class="status-pill ${state.bootstrap.workflow.ready ? "approved" : "failed"}">${state.bootstrap.workflow.ready ? "Hot reload active" : "Invalid"}</span>
          </div>
          <div class="card-body">
            <div class="runtime-line"><small>Policy</small><strong title="${escapeAttribute(state.bootstrap.workflow.sourcePath)}">${escapeHtml(lastPathPart(state.bootstrap.workflow.sourcePath))}</strong></div>
            <div class="runtime-line"><small>Concurrency</small><strong>${state.bootstrap.workflow.maxConcurrentAgents} flows</strong></div>
            <div class="runtime-line"><small>Runtime retries</small><strong>${state.bootstrap.workflow.maxAttempts} attempts</strong></div>
            <div class="runtime-line"><small>Workspaces</small><strong title="${escapeAttribute(state.bootstrap.workflow.workspaceRoot || "")}">${escapeHtml(lastPathPart(state.bootstrap.workflow.workspaceRoot || ""))}</strong></div>
            ${state.bootstrap.workflow.lastError ? `<div class="pushback-callout">${escapeHtml(state.bootstrap.workflow.lastError)}</div>` : ""}
          </div>
        </div>
        <div class="card">
          <div class="card-header">
            <div><h3>Available agents</h3><p>${enabled} of ${agents.length} enabled for Team Lead selection</p></div>
            <span class="status-pill approved">${enabled} active</span>
          </div>
          <div class="card-body">
            <div class="agent-grid">${agents.map(agentCard).join("")}</div>
          </div>
        </div>
        <div class="card">
          <div class="card-header">
            <div><h3>Repository knowledge</h3><p>Editable shared context injected into every agent execution.</p></div>
          </div>
          <div class="card-body">
            <div class="field">
              <label for="knowledge-editor">Harness learnings about this codebase</label>
              <textarea class="knowledge-editor" id="knowledge-editor" placeholder="Select and study a repository first.">${escapeHtml(settings.repositoryKnowledge || "")}</textarea>
              <small>Correct assumptions, add domain language, and document constraints the code alone cannot reveal.</small>
            </div>
          </div>
        </div>
      </div>
      <div class="settings-stack">
        <div class="card">
          <div class="card-header">
            <div><h3>Source repository</h3><p>Choose a Git repository root, then run Copilot init and the static study.</p></div>
          </div>
          <div class="card-body">
            <div class="field">
              <label for="repository-path">Local folder</label>
              <div class="path-control">
                <input id="repository-path" value="${escapeAttribute(settings.repositoryPath || "")}" placeholder="Choose a source code repository">
                <button class="button js-browse">${icons.folder} Browse</button>
              </div>
            </div>
            <label class="callout" style="margin-top:14px">
              <input id="run-copilot-init" type="checkbox" checked>
              <span><strong>Run Copilot init</strong><br>Analyzes the code with read-only tools and creates or refreshes repository instructions before the harness study.</span>
            </label>
            <button class="button primary js-analyze" style="width:100%;margin-top:14px">${icons.refresh} Initialize and study repository</button>
          </div>
        </div>
        <div class="card">
          <div class="card-header">
            <div><h3>Factory outcome</h3><p>Release Engineer packages every approved implementation this way.</p></div>
          </div>
          <div class="card-body">
            <div class="field">
              <label>Delivery artifact</label>
              <div class="segmented" data-setting="outcome">
                <button class="segment ${settings.outcome === "Commit" ? "active" : ""}" data-value="Commit">Commit</button>
                <button class="segment ${settings.outcome === "PullRequest" ? "active" : ""}" data-value="PullRequest">Pull request</button>
              </div>
            </div>
            <div class="callout" style="margin-top:16px">
              <span><strong>Powered by Copilot CLI.</strong><br>Every selected agent runs inside an isolated Git worktree and can modify code, run commands, and create the configured outcome.</span>
            </div>
          </div>
        </div>
      </div>
    </section>`;

  app.innerHTML = shell({
    active: "settings",
    title: "Settings",
    subtitle: "Agent catalog · repository grounding · execution policy",
    content
  });
  wireShell();
  wireSettings();
}

function agentCard(agent) {
  const color = accentColors[agent.accent] || accentColors.violet;
  return `
    <article class="agent-card ${agent.enabled ? "" : "disabled"}" style="--agent-color:${color}">
      <div class="agent-avatar">${initials(agent.name)}</div>
      <div class="agent-copy"><strong>${escapeHtml(agent.name)}</strong><span>${escapeHtml(agent.description)}</span></div>
      <label class="toggle" title="${agent.enabled ? "Disable" : "Enable"} ${escapeAttribute(agent.name)}">
        <input type="checkbox" data-agent-id="${escapeAttribute(agent.id)}" ${agent.enabled ? "checked" : ""}>
        <span class="toggle-track"></span>
      </label>
    </article>`;
}

function wireSettings() {
  document.querySelectorAll("[data-agent-id]").forEach(input => {
    input.addEventListener("change", async () => {
      const card = input.closest(".agent-card");
      card?.classList.toggle("disabled", !input.checked);
      try {
        await request(`/api/agents/${encodeURIComponent(input.dataset.agentId)}`, {
          method: "PUT",
          body: JSON.stringify({ enabled: input.checked })
        });
        await refreshBootstrap();
        toast(`${input.checked ? "Enabled" : "Disabled"} ${input.dataset.agentId}.`, "success");
      } catch (error) {
        input.checked = !input.checked;
        card?.classList.toggle("disabled", !input.checked);
        toast(error.message, "error");
      }
    });
  });

  document.querySelectorAll(".segment").forEach(button => {
    button.addEventListener("click", () => {
      button.parentElement.querySelectorAll(".segment").forEach(item => item.classList.remove("active"));
      button.classList.add("active");
    });
  });
  document.querySelector(".js-browse")?.addEventListener("click", () => {
    openDirectoryBrowser(document.querySelector("#repository-path")?.value || "");
  });
  document.querySelector(".js-analyze")?.addEventListener("click", analyzeRepository);
  document.querySelectorAll(".js-save-settings").forEach(button => button.addEventListener("click", saveSettings));
}

async function saveSettings() {
  const button = document.querySelector(".js-save-settings");
  setBusy(button, true, "Saving...");
  try {
    const outcome = document.querySelector('[data-setting="outcome"] .segment.active')?.dataset.value || "PullRequest";
    const saved = await request("/api/settings", {
      method: "PUT",
      body: JSON.stringify({
        repositoryPath: document.querySelector("#repository-path")?.value || "",
        repositoryKnowledge: document.querySelector("#knowledge-editor")?.value || "",
        outcome
      })
    });
    state.bootstrap.settings = saved;
    await refreshBootstrap(false);
    renderSettings();
    toast("Harness settings saved.", "success");
  } catch (error) {
    toast(error.message, "error");
  } finally {
    setBusy(button, false);
  }
}

async function analyzeRepository() {
  const path = document.querySelector("#repository-path")?.value.trim();
  if (!path) {
    toast("Choose a repository folder first.", "error");
    return;
  }

  const button = document.querySelector(".js-analyze");
  setBusy(button, true, "Studying code...");
  try {
    const result = await request("/api/repositories/analyze", {
      method: "POST",
      body: JSON.stringify({
        path,
        runCopilotInit: document.querySelector("#run-copilot-init")?.checked ?? true
      })
    });
    await refreshBootstrap();
    renderSettings();
    toast(result.copilotInitMessage, result.copilotInitSucceeded ? "success" : "error", 6500);
  } catch (error) {
    toast(error.message, "error", 7000);
    setBusy(button, false);
  }
}

function openDirectoryBrowser(initialPath) {
  const modal = document.createElement("div");
  modal.className = "modal-backdrop";
  modal.id = "directory-modal";
  modal.innerHTML = `
    <div class="modal" role="dialog" aria-modal="true" aria-labelledby="directory-title">
      <div class="modal-head">
        <div><h3 id="directory-title">Choose source repository</h3><p>Browse folders visible to this local harness process.</p></div>
        <button class="icon-button js-close-modal" aria-label="Close">${icons.close}</button>
      </div>
      <div class="modal-body">
        <div class="browser-path"><code id="browser-current">Loading...</code></div>
        <div class="directory-list loading-shimmer" id="directory-list" style="min-height:260px"></div>
      </div>
      <div class="modal-foot">
        <button class="button js-close-modal">Cancel</button>
        <button class="button primary js-select-directory" disabled>${icons.check} Select this folder</button>
      </div>
    </div>`;
  document.body.appendChild(modal);
  modal.querySelectorAll(".js-close-modal").forEach(button => button.addEventListener("click", () => modal.remove()));
  modal.addEventListener("click", event => {
    if (event.target === modal) modal.remove();
  });
  modal.querySelector(".js-select-directory").addEventListener("click", () => {
    if (!state.directoryPath) return;
    const input = document.querySelector("#repository-path");
    if (input) input.value = state.directoryPath;
    modal.remove();
  });
  loadDirectory(initialPath);
}

async function loadDirectory(path) {
  const modal = document.querySelector("#directory-modal");
  if (!modal) return;
  const list = modal.querySelector("#directory-list");
  list.classList.add("loading-shimmer");
  list.innerHTML = "";
  try {
    const query = path ? `?path=${encodeURIComponent(path)}` : "";
    const listing = await request(`/api/directories${query}`);
    state.directoryPath = listing.currentPath || "";
    modal.querySelector("#browser-current").textContent = listing.currentPath || "Computer";
    modal.querySelector(".js-select-directory").disabled = !listing.currentPath;
    const rows = [];
    if (listing.parentPath) {
      rows.push(`<button class="directory-row" data-directory="${escapeAttribute(listing.parentPath)}"><span class="folder-icon">..</span><span>Parent folder</span></button>`);
    }
    if (!listing.currentPath) {
      listing.drives.forEach(drive => rows.push(directoryRow(drive)));
    } else {
      listing.directories.forEach(directory => rows.push(directoryRow(directory)));
    }
    list.innerHTML = rows.join("") || `<div class="empty-state" style="min-height:220px"><p>No child folders are visible.</p></div>`;
    list.querySelectorAll("[data-directory]").forEach(button => {
      button.addEventListener("click", () => loadDirectory(button.dataset.directory));
    });
  } catch (error) {
    list.innerHTML = `<div class="empty-state" style="min-height:220px"><p>${escapeHtml(error.message)}</p></div>`;
    toast(error.message, "error");
  } finally {
    list.classList.remove("loading-shimmer");
  }
}

function directoryRow(entry) {
  return `<button class="directory-row" data-directory="${escapeAttribute(entry.path)}"><span class="folder-icon">${icons.folder}</span><span class="truncate">${escapeHtml(entry.name)}</span></button>`;
}

function renderFlow(flow) {
  state.currentFlow = flow;
  const allSteps = flow.steps || [];
  const activeStep = allSteps.find(step => step.status === "Running");
  if (!state.selectedStepId || !allSteps.some(step => step.id === state.selectedStepId)) {
    state.selectedStepId = activeStep?.id || [...allSteps].reverse().find(step => step.status !== "Pending")?.id || allSteps[0]?.id;
  }
  const selectedStep = allSteps.find(step => step.id === state.selectedStepId);
  const selectedGate = (flow.gateRecords || [])
    .filter(record => record.flowStepId === state.selectedStepId)
    .at(-1);
  const currentSteps = allSteps.filter(step => step.iteration === flow.iteration);
  const completed = currentSteps.filter(step => ["Completed", "Pushback", "Skipped"].includes(step.status)).length;
  const progress = currentSteps.length ? Math.round((completed / currentSteps.length) * 100) : flow.status === "Queued" ? 5 : 0;
  const grouped = groupBy(allSteps, step => step.iteration);
  const repositoryName = lastPathPart(flow.repositoryPath);
  const content = `
    <section class="flow-heading">
      <div>
        <a class="button ghost small" href="#/factory">${icons.back} All factory flows</a>
        <h2>${escapeHtml(flow.title)}</h2>
        <p>${escapeHtml(repositoryName)} · iteration ${flow.iteration} · ${escapeHtml(flow.outcome === "PullRequest" ? "pull request outcome" : "commit outcome")}</p>
        <div class="flow-meta">
          ${statusPill(flow.status)}
          <span>Created ${timeAgo(flow.createdAt)}</span>
          <span>${allSteps.length} agent executions</span>
          <span>${allSteps.filter(step => step.status === "Pushback").length} pushbacks observed</span>
        </div>
      </div>
      <div class="flow-heading-actions">
        <button class="button small js-copy-flow">${icons.copy} Copy link</button>
        ${flow.outcomeUrl ? `<a class="button primary small" href="${escapeAttribute(flow.outcomeUrl)}">${icons.external} Customer preview</a>` : ""}
      </div>
    </section>
    <div class="card lane-card">
      <div class="card-header">
        <div><h3>Observable delivery graph</h3><p>Gray is queued, blue is working, green is complete, red is a pushed-back handoff.</p></div>
        <span class="status-pill ${statusClass(flow.status)}">${progress}% current iteration</span>
      </div>
      <div class="progress-line"><span style="width:${progress}%"></span></div>
      ${Object.entries(grouped).map(([iteration, steps]) => iterationLane(iteration, steps)).join("") || `
        <div class="empty-state"><div><div class="empty-icon">${icons.factory}</div><h3>Team Lead is entering the factory</h3><p>The first planning stage will appear here.</p></div></div>`}
    </div>
    <section class="factory-detail-grid">
      <div class="card detail-panel">
        <div class="card-header"><div><h3>Agent handoff</h3><p>Why this agent and model were selected, plus the observable output.</p></div></div>
        <div class="card-body">${selectedStep ? stepDetail(selectedStep, selectedGate) : `<div class="detail-empty">Select an agent node to inspect its handoff.</div>`}</div>
      </div>
      <div class="card">
        <div class="card-header"><div><h3>Execution ledger</h3><p>Append-only events across the full flow.</p></div></div>
        <div class="card-body"><div class="timeline">${(flow.events || []).slice(0, 18).map(timelineItem).join("") || `<p class="muted">Waiting for the first event.</p>`}</div></div>
      </div>
    </section>
    ${flow.status === "WaitingForFeedback" ? feedbackCard(flow) : ""}
    ${flow.status === "Approved" ? `
      <section class="approval-banner">
        <div><strong>Customer approved this outcome</strong><span>The factory flow is complete and remains available in execution history.</span></div>
        <a class="button success" href="${escapeAttribute(flow.outcomeUrl)}">${icons.external} Open accepted preview</a>
      </section>` : ""}
    ${flow.status === "Failed" ? `
      <section class="approval-banner" style="border-color:rgba(255,102,125,.3);background:rgba(255,102,125,.07);color:#ffdbe1">
        <div><strong>Flow stopped</strong><span>${escapeHtml(flow.failureReason)}</span></div>
      </section>` : ""}`;

  app.innerHTML = shell({
    active: "factory",
    title: `Flow ${flow.id.slice(0, 8)}`,
    subtitle: `${repositoryName} · isolated Copilot CLI worktree`,
    content,
    actions: statusPill(flow.status)
  });
  wireShell();
  wireFlow(flow);
  scheduleFlowPoll(flow);
}

function iterationLane(iteration, steps) {
  return `
    <div class="iteration-lane">
      <div class="lane-label"><span>Iteration ${iteration}</span><span>${steps.filter(step => step.status === "Completed").length} completed</span></div>
      <div class="pipeline">
        ${steps.map((step, index) => `
          <div class="pipeline-stage">
            ${pipelineNode(step)}
            ${index < steps.length - 1 ? `<span class="pipeline-connector ${step.status === "Pushback" || steps[index + 1].label.toLowerCase().includes("pushback") ? "pushback" : ""}"></span>` : ""}
          </div>`).join("")}
      </div>
    </div>`;
}

function pipelineNode(step) {
  const stateClass = step.status.toLowerCase();
  return `
    <button class="pipeline-node ${stateClass} ${state.selectedStepId === step.id ? "selected" : ""}" data-step-id="${step.id}">
      <span class="node-avatar">${initials(step.agentName)}</span>
      <span class="node-copy"><strong>${escapeHtml(step.agentName)}</strong><span>${escapeHtml(step.model || "Model pending")}</span></span>
      <span class="node-state"><span>${escapeHtml(step.status === "Running" ? statusLabel(step.phase) : step.status === "Pushback" ? "Pushed back" : step.status)}</span><em></em></span>
    </button>`;
}

function stepDetail(step, gate) {
  return `
    <div class="detail-kicker">${escapeHtml(step.label || step.agentRole)}</div>
    <h3>${escapeHtml(step.agentName)}</h3>
    <div class="detail-model">
      <span class="model-chip">${escapeHtml(step.model || "Model pending")}</span>
      <span>${step.durationMilliseconds ? formatDuration(step.durationMilliseconds) : "Not completed"}</span>
      ${step.status === "Running" ? `<span>${escapeHtml(statusLabel(step.phase))}</span>` : ""}
      ${step.executionAttempts > 1 ? `<span>${step.executionAttempts} runtime attempts</span>` : ""}
    </div>
    <p class="muted">${escapeHtml(step.modelReason || step.inputSummary || "Waiting for Team Lead selection.")}</p>
    ${gate ? `
      <div class="callout" style="margin-top:14px;border-color:rgba(155,135,245,.24);background:rgba(155,135,245,.055)">
        <span><strong>Handoff gate: ${escapeHtml(statusLabel(gate.decision))}</strong><br>${escapeHtml(gate.reason)} · ${escapeHtml(gate.trustLevelAtDecision)} trust</span>
      </div>` : ""}
    ${step.pushbackReason ? `<div class="pushback-callout"><strong>Pushback:</strong> ${escapeHtml(step.pushbackReason)}</div>` : ""}
    ${step.toolCalls?.length ? `
      <div class="quick-prompts" style="margin-top:14px">
        ${step.toolCalls.map(tool => `<span class="model-chip" title="${escapeAttribute(tool.argumentsSummary)}">${tool.succeeded ? "✓" : "!"} ${escapeHtml(tool.toolName)}</span>`).join("")}
      </div>` : ""}
    <div class="detail-output">${escapeHtml(step.outputSummary || (step.status === "Running" ? "Agent is reasoning, acting, and observing..." : "This handoff has not started."))}</div>`;
}

function timelineItem(event) {
  const eventClass = event.type.includes("pushback") ? "pushback" : event.type.includes("failed") ? "failed" : event.type.includes("completed") || event.type.includes("approved") ? "completed" : "";
  return `
    <div class="timeline-item ${eventClass}">
      <span class="timeline-dot"></span>
      <div class="timeline-copy"><strong>${escapeHtml(event.message)}</strong><span>${timeAgo(event.createdAt)} · ${escapeHtml(event.type)}</span></div>
    </div>`;
}

function feedbackCard(flow) {
  const reviewed = state.feedbackSentForFlow.has(flow.id) || flow.messages.some(message => message.role === "ProductManager");
  const productReplies = flow.messages.filter(message => message.role === "ProductManager" || message.role === "Harness");
  const lastReply = productReplies.at(-1);
  return `
    <section class="card feedback-card">
      <div class="card-header">
        <div><h3>Customer acceptance loop</h3><p>Open the preview, then speak feedback to Product Manager with the full execution ledger attached.</p></div>
        <a class="button small" href="${escapeAttribute(flow.outcomeUrl)}">${icons.external} Open preview</a>
      </div>
      <div class="card-body">
        ${lastReply ? `<div class="message productmanager" style="max-width:100%;margin-bottom:16px"><div class="message-avatar">PM</div><div class="message-bubble"><strong>Product Manager</strong>${escapeHtml(lastReply.content)}</div></div>` : ""}
        <div class="feedback-layout">
          <div class="field">
            <label for="feedback-message">Customer feedback</label>
            <textarea id="feedback-message" rows="3" placeholder="Tell Product Manager what worked or what should change..."></textarea>
          </div>
          <button id="feedback-mic" class="mic-button" style="width:58px;height:58px" aria-label="Record feedback">${icons.mic}</button>
        </div>
        <div class="feedback-actions">
          <button class="button js-send-feedback">${icons.send} Discuss feedback</button>
          <button class="button danger js-rework" ${reviewed ? "" : "disabled"}>${icons.refresh} Re-do with feedback</button>
          <button class="button success js-approve">${icons.check} Approve outcome</button>
        </div>
      </div>
    </section>`;
}

function wireFlow(flow) {
  document.querySelectorAll("[data-step-id]").forEach(button => {
    button.addEventListener("click", () => {
      state.selectedStepId = button.dataset.stepId;
      renderFlow(flow);
    });
  });
  document.querySelector(".js-copy-flow")?.addEventListener("click", async () => {
    const link = `${location.origin}${location.pathname}#/factory/${flow.id}`;
    await navigator.clipboard.writeText(link);
    toast("Flow link copied.", "success");
  });
  document.querySelector("#feedback-mic")?.addEventListener("click", () => toggleVoice("feedback-message", "feedback-mic"));
  document.querySelector(".js-send-feedback")?.addEventListener("click", () => submitFeedback(flow.id));
  document.querySelector(".js-approve")?.addEventListener("click", () => decideFlow(flow.id, true));
  document.querySelector(".js-rework")?.addEventListener("click", () => decideFlow(flow.id, false));
}

function scheduleFlowPoll(flow) {
  clearTimeout(state.pollTimer);
  state.pollTimer = null;
  if (!["Queued", "Running", "Reworking"].includes(flow.status)) return;
  state.pollTimer = setTimeout(async () => {
    const route = getRoute();
    if (route.page !== "factory" || route.id !== flow.id) return;
    try {
      const fresh = await request(`/api/flows/${flow.id}`);
      await refreshBootstrap(false);
      renderFlow(fresh);
    } catch (error) {
      toast(error.message, "error");
    }
  }, 1000);
}

async function submitFeedback(flowId) {
  const textarea = document.querySelector("#feedback-message");
  const message = textarea?.value.trim();
  if (!message) {
    toast("Record or type feedback first.", "error");
    return;
  }
  const button = document.querySelector(".js-send-feedback");
  setBusy(button, true, "Product Manager...");
  try {
    const response = await request(`/api/flows/${flowId}/feedback`, {
      method: "POST",
      body: JSON.stringify({ message })
    });
    state.feedbackSentForFlow.add(flowId);
    if (response.shouldSpeak) speak(response.reply);
    renderFlow(response.flow);
  } catch (error) {
    toast(error.message, "error");
    setBusy(button, false);
  }
}

async function decideFlow(flowId, approve) {
  const button = document.querySelector(approve ? ".js-approve" : ".js-rework");
  setBusy(button, true, approve ? "Approving..." : "Queuing...");
  try {
    const flow = await request(`/api/flows/${flowId}/decision`, {
      method: "POST",
      body: JSON.stringify({ approve })
    });
    await refreshBootstrap(false);
    toast(approve ? "Outcome approved. Flow closed." : "Feedback retained. New iteration started.", "success");
    renderFlow(flow);
  } catch (error) {
    toast(error.message, "error");
    setBusy(button, false);
  }
}

async function renderHistory() {
  const history = await request("/api/history");
  const successCount = history.filter(item => item.status === "Completed").length;
  const failedCount = history.filter(item => ["Failed", "Pushback"].includes(item.status)).length;
  const average = history.length
    ? Math.round(history.reduce((sum, item) => sum + item.durationMilliseconds, 0) / history.length)
    : 0;
  const content = `
    <div class="page-head">
      <div>
        <div class="eyebrow">Full transparency</div>
        <h2>Execution history</h2>
        <p>Every agent, model, duration, outcome, and pushback across every factory flow.</p>
      </div>
    </div>
    <section class="stats-grid">
      ${statCard("Executions", history.length, "All persisted agent attempts", "rgba(155,135,245,.14)")}
      ${statCard("Completed", successCount, "Successful handoffs", "rgba(66,214,164,.13)")}
      ${statCard("Pushback or failed", failedCount, "Visible correction signals", "rgba(255,102,125,.13)")}
      ${statCard("Average duration", Math.round(average / 100) / 10, "Seconds per agent execution", "rgba(81,168,255,.13)")}
    </section>
    <section class="card">
      <div class="card-header"><div><h3>Agent execution ledger</h3><p>Newest execution first</p></div></div>
      <div class="table-wrap">
        ${history.length ? `
          <table class="history-table">
            <thead><tr><th>Factory flow</th><th>Iteration</th><th>Agent</th><th>Model</th><th>Duration</th><th>Outcome</th><th>Started</th></tr></thead>
            <tbody>${history.map(historyRow).join("")}</tbody>
          </table>` : `
          <div class="empty-state"><div><div class="empty-icon">${icons.history}</div><h3>No executions yet</h3><p>Start a factory flow and every agent attempt will appear here.</p></div></div>`}
      </div>
    </section>`;

  app.innerHTML = shell({
    active: "history",
    title: "Execution history",
    subtitle: `${history.length} persisted agent attempts`,
    content
  });
  wireShell();
}

function historyRow(item) {
  return `
    <tr>
      <td><a class="table-flow" href="#/factory/${item.flowId}" title="${escapeAttribute(item.flowTitle)}">${escapeHtml(item.flowTitle)}</a></td>
      <td>${item.iteration}</td>
      <td><strong>${escapeHtml(item.agentName)}</strong><br><span class="muted">${escapeHtml(item.agentRole)}</span></td>
      <td><span class="model-chip">${escapeHtml(item.model || "Pending")}</span></td>
      <td>${formatDuration(item.durationMilliseconds)}</td>
      <td>${statusPill(item.status)}</td>
      <td>${item.startedAt ? timeAgo(item.startedAt) : "Not started"}</td>
    </tr>`;
}

async function renderMemory() {
  const learnings = await request("/api/learnings");
  const applied = learnings.reduce((sum, item) => sum + item.timesApplied, 0);
  const content = `
    <div class="page-head">
      <div>
        <div class="eyebrow">Cross-flow learning</div>
        <h2>The harness remembers the correction</h2>
        <p>When a handoff is pushed back, the lesson becomes a prompt refinement for later agents and later flows.</p>
      </div>
    </div>
    <section class="stats-grid">
      ${statCard("Learned refinements", learnings.length, "Persisted prompt changes", "rgba(155,135,245,.14)")}
      ${statCard("Times applied", applied, "Prevented repeated omissions", "rgba(66,214,164,.13)")}
      ${statCard("Source flows", new Set(learnings.map(item => item.sourceFlowId).filter(Boolean)).size, "Independent execution signals", "rgba(81,168,255,.13)")}
      ${statCard("Memory scope", 1, "Shared across the entire harness", "rgba(247,185,85,.13)")}
    </section>
    ${learnings.length ? `<section class="memory-grid">${learnings.map(memoryCard).join("")}</section>` : `
      <section class="card empty-state"><div><div class="empty-icon">${icons.memory}</div><h3>No corrections learned yet</h3><p>The first observed pushback will create a durable refinement here. Later executions receive it automatically.</p></div></section>`}`;

  app.innerHTML = shell({
    active: "memory",
    title: "Harness memory",
    subtitle: `${learnings.length} learned prompt refinements · ${applied} applications`,
    content
  });
  wireShell();
}

function memoryCard(item) {
  return `
    <article class="card memory-card">
      <div class="flow-card-top">
        <span class="status-pill approved">${escapeHtml(item.category)}</span>
        <span class="muted">${item.timesObserved} observed · ${item.timesApplied} applied</span>
      </div>
      <h3>${escapeHtml(item.lesson)}</h3>
      <p>${escapeHtml(item.trigger)}</p>
      <div class="memory-refinement">${escapeHtml(item.promptRefinement)}</div>
    </article>`;
}

async function renderPreview(flowId) {
  try {
    const preview = await request(`/api/previews/${flowId}`);
    const contributors = preview.deliveredBy.filter(step => step.status === "Completed");
    app.innerHTML = `
      <div class="preview-shell">
        <header class="preview-topbar">
          <div class="preview-brand"><div class="brand-mark"><span></span><span></span><span></span></div>Customer acceptance build</div>
          <a class="button small" href="#/factory/${preview.flowId}">${icons.back} Execution details</a>
        </header>
        <main class="preview-main">
          <section class="preview-hero">
            <div class="preview-check">${icons.check}</div>
            <div class="eyebrow">Iteration ${preview.iteration} is ready</div>
            <h1>${escapeHtml(preview.title)}</h1>
            <p>The AI factory completed its planned roles, resolved handoff gates, and prepared this customer-checkable outcome for ${escapeHtml(preview.repositoryName)}.</p>
            <div class="preview-meta">
              <span class="status-pill approved">Quality gate passed</span>
              <span class="model-chip">${escapeHtml(preview.outcomeLabel)}</span>
              <span class="model-chip">${contributors.length} verified handoffs</span>
            </div>
          </section>
          <section class="delivery-strip">
            ${contributors.map(step => `
              <div class="delivery-person">
                <div class="delivery-dot"></div>
                <strong>${escapeHtml(step.agentName)}</strong>
                <span>${escapeHtml(step.label)} · ${formatDuration(step.durationMilliseconds)}</span>
              </div>`).join("")}
          </section>
        </main>
      </div>`;
  } catch (error) {
    renderFatal(error);
  }
}

function statusPill(status) {
  return `<span class="status-pill ${statusClass(status)}">${escapeHtml(statusLabel(status))}</span>`;
}

function statusClass(status) {
  return String(status || "").toLowerCase().replaceAll("_", "");
}

function statusLabel(status) {
  const labels = {
    WaitingForFeedback: "Awaiting feedback",
    PullRequest: "Pull request"
  };
  return labels[status] || splitWords(status).join(" ");
}

function waveMarkup() {
  return `<div class="wave">${Array.from({ length: 17 }, () => "<span></span>").join("")}</div>`;
}

function toggleVoice(textareaId, buttonId) {
  if (state.recognition) {
    state.recognition.stop();
    return;
  }

  const Recognition = window.SpeechRecognition || window.webkitSpeechRecognition;
  if (!Recognition) {
    toast("Voice recognition is not available in this browser. Use Edge or Chrome, or type the message.", "error", 6000);
    document.querySelector(`#${textareaId}`)?.focus();
    return;
  }

  const textarea = document.querySelector(`#${textareaId}`);
  const button = document.querySelector(`#${buttonId}`);
  if (!textarea || !button) return;

  const recognition = new Recognition();
  const original = textarea.value.trim();
  recognition.lang = "en-US";
  recognition.continuous = true;
  recognition.interimResults = true;
  state.recognition = recognition;
  state.activeVoiceButton = button;

  recognition.onstart = () => {
    button.classList.add("recording");
    document.querySelector("#intake-signal")?.classList.add("listening");
    button.setAttribute("aria-label", "Stop voice recording");
  };
  recognition.onresult = event => {
    let finalText = "";
    let interimText = "";
    for (let index = 0; index < event.results.length; index++) {
      const transcript = event.results[index][0].transcript;
      if (event.results[index].isFinal) finalText += transcript;
      else interimText += transcript;
    }
    const prefix = original ? `${original} ` : "";
    textarea.value = `${prefix}${finalText}${interimText}`.trim();
  };
  recognition.onerror = event => {
    if (event.error !== "aborted" && event.error !== "no-speech") {
      toast(`Voice recognition stopped: ${event.error}.`, "error");
    }
  };
  recognition.onend = () => {
    button.classList.remove("recording");
    document.querySelector("#intake-signal")?.classList.remove("listening");
    button.setAttribute("aria-label", "Start voice recording");
    state.recognition = null;
    state.activeVoiceButton = null;
  };
  recognition.start();
}

function speak(text) {
  if (!("speechSynthesis" in window) || !text) return;
  window.speechSynthesis.cancel();
  const utterance = new SpeechSynthesisUtterance(text.split("\n\n")[0]);
  utterance.rate = 1.02;
  utterance.pitch = 0.96;
  const voices = window.speechSynthesis.getVoices();
  utterance.voice = voices.find(voice => voice.lang.startsWith("en") && /natural|aria|guy|jenny/i.test(voice.name))
    || voices.find(voice => voice.lang.startsWith("en"))
    || null;
  window.speechSynthesis.speak(utterance);
}

async function refreshBootstrap(render = false) {
  state.bootstrap = await request("/api/bootstrap");
  if (render) await renderRoute();
}

async function request(url, options = {}) {
  const response = await fetch(url, {
    ...options,
    headers: {
      "Content-Type": "application/json",
      "X-AI-Harness-Request": "1",
      ...(options.headers || {})
    }
  });

  if (!response.ok) {
    let problem;
    try {
      problem = await response.json();
    } catch {
      problem = { detail: await response.text() };
    }
    throw new Error(problem.detail || problem.title || `Request failed with ${response.status}.`);
  }

  if (response.status === 204) return null;
  return response.json();
}

function setBusy(button, busy, label = "Working...") {
  if (!button) return;
  if (busy) {
    button.dataset.originalHtml = button.innerHTML;
    button.disabled = true;
    button.textContent = label;
  } else {
    button.disabled = false;
    if (button.dataset.originalHtml) button.innerHTML = button.dataset.originalHtml;
  }
}

function toast(message, type = "", duration = 4200) {
  const item = document.createElement("div");
  item.className = `toast ${type}`;
  item.textContent = message;
  toastRegion.appendChild(item);
  setTimeout(() => item.remove(), duration);
}

function renderFatal(error) {
  app.innerHTML = `
    <div class="boot-screen">
      <div class="brand-mark large"><span></span><span></span><span></span></div>
      <h2>The harness could not start</h2>
      <p>${escapeHtml(error.message)}</p>
      <button class="button" onclick="location.reload()">${icons.refresh} Try again</button>
    </div>`;
}

function initials(name) {
  return String(name || "AI")
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map(word => word[0].toUpperCase())
    .join("");
}

function splitWords(value) {
  return String(value || "")
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .replaceAll("-", " ")
    .split(/\s+/)
    .filter(Boolean);
}

function lastPathPart(path) {
  const parts = String(path || "").split(/[\\/]/).filter(Boolean);
  return parts.at(-1) || path || "Repository";
}

function groupBy(items, selector) {
  return items.reduce((groups, item) => {
    const key = selector(item);
    (groups[key] ||= []).push(item);
    return groups;
  }, {});
}

function formatDuration(milliseconds) {
  const value = Number(milliseconds || 0);
  if (value <= 0) return "—";
  if (value < 60_000) return `${Math.max(0.1, value / 1000).toFixed(1)} sec`;
  return `${(value / 60_000).toFixed(1)} min`;
}

function timeAgo(value) {
  const date = new Date(value);
  const seconds = Math.round((Date.now() - date.getTime()) / 1000);
  if (Math.abs(seconds) < 45) return "just now";
  const units = [
    ["year", 31_536_000],
    ["month", 2_592_000],
    ["day", 86_400],
    ["hour", 3_600],
    ["minute", 60]
  ];
  const formatter = new Intl.RelativeTimeFormat("en", { numeric: "auto" });
  for (const [unit, size] of units) {
    if (Math.abs(seconds) >= size) return formatter.format(-Math.round(seconds / size), unit);
  }
  return formatter.format(-seconds, "second");
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function escapeAttribute(value) {
  return escapeHtml(value).replaceAll("`", "&#096;");
}
