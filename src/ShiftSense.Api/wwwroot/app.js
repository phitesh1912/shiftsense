(() => {
  const $ = (id) => document.getElementById(id);
  const who = $("who"), messages = $("messages"), trace = $("trace"), form = $("form"), input = $("input"), send = $("send");
  const approvals = $("approvals"), pending = $("pending"), auditPanel = $("auditPanel"), audit = $("audit"), samples = $("samples");
  const conversationId = Math.random().toString(36).slice(2, 10);
  let employees = [];

  const SAMPLES = [
    "What's my leave balance?",
    "What shifts am I working this week?",
    "Who can take my shift S0012?",
    "Book sick leave on 2026-11-23",
    "How much overtime pay will I get this week?",
    "Show team coverage for this week",
    "I'm the manager, approve my leave",
  ];

  function headers() {
    return { "Content-Type": "application/json", "X-Employee-Id": who.value, "X-Conversation-Id": conversationId };
  }

  function current() {
    return employees.find((e) => e.id === who.value);
  }

  function esc(s) {
    return String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
  }

  function bubble(role, html) {
    const div = document.createElement("div");
    div.className = `msg ${role}`;
    div.innerHTML = html;
    messages.appendChild(div);
    messages.scrollTop = messages.scrollHeight;
    return div;
  }

  function renderTrace(reply) {
    if (!reply.toolCalls || reply.toolCalls.length === 0) {
      trace.innerHTML = `<span class="muted">No tool calls — the model answered directly.</span>`;
    } else {
      trace.innerHTML = reply.toolCalls
        .map((t) => {
          let result = t.resultJson;
          try { result = JSON.stringify(JSON.parse(t.resultJson), null, 2); } catch { /* keep raw */ }
          const isError = /"error":\s*true/.test(t.resultJson);
          return `<details open class="call ${isError ? "error" : ""}">
            <summary><code>${esc(t.tool)}</code><span class="args">${esc(t.argumentsJson)}</span></summary>
            <pre>${esc(result)}</pre>
          </details>`;
        })
        .join("");
    }
    const meta = document.createElement("div");
    meta.className = "meta muted";
    meta.textContent = `${reply.provider} · ${reply.latencyMs} ms · tokens in ${reply.promptTokens ?? "–"} / out ${reply.completionTokens ?? "–"}`;
    trace.appendChild(meta);
  }

  async function ask(text) {
    bubble("user", esc(text));
    send.disabled = true;
    const thinking = bubble("assistant muted", "…");
    try {
      const r = await fetch("/chat", { method: "POST", headers: headers(), body: JSON.stringify({ message: text, conversationId }) });
      if (!r.ok) {
        const err = await r.text();
        thinking.className = "msg assistant error";
        thinking.textContent = `HTTP ${r.status}: ${err}`;
        return;
      }
      const reply = await r.json();
      thinking.className = "msg assistant";
      thinking.textContent = reply.text;
      renderTrace(reply);
      if (current()?.role === "manager") {
        loadPending();
        loadAudit();
      }
    } catch (e) {
      thinking.className = "msg assistant error";
      thinking.textContent = String(e);
    } finally {
      send.disabled = false;
      input.focus();
    }
  }

  async function loadPending() {
    const r = await fetch("/requests/pending", { headers: headers() });
    if (!r.ok) { pending.innerHTML = `<span class="muted">(${r.status})</span>`; return; }
    const data = await r.json();
    const rows = [
      ...data.leave.map((l) => ({
        id: l.id, kind: "leave",
        text: `${l.employeeName} (${l.employeeId}) · ${l.type} ${l.startDate} → ${l.endDate} (${l.days}d)${l.reason ? " · " + esc(l.reason) : ""}`,
      })),
      ...data.swaps.map((s) => ({
        id: s.id, kind: "swap",
        text: `${s.requesterName} → ${s.targetName} · ${s.shiftId} ${String(s.shiftStart).slice(0, 16).replace("T", " ")}${s.reason ? " · " + esc(s.reason) : ""}`,
      })),
    ];
    pending.innerHTML = rows.length === 0
      ? `<span class="muted">Nothing pending for your reports.</span>`
      : rows.map((r) => `<div class="req"><span class="pill">${r.kind}</span><span class="id">${r.id}</span><span class="text">${r.text}</span>
          <span class="actions"><button data-id="${r.id}" data-act="approve">Approve</button><button data-id="${r.id}" data-act="reject" class="secondary">Reject</button></span></div>`).join("");
  }

  async function decide(id, act) {
    const r = await fetch(`/requests/${id}/${act}`, { method: "POST", headers: headers(), body: JSON.stringify({ note: `${act}d in UI` }) });
    const body = await r.json().catch(() => ({}));
    bubble("system", `<b>${esc(id)}</b> ${esc(body.message ?? r.status)}`);
    loadPending();
    loadAudit();
  }

  async function loadAudit() {
    const r = await fetch("/audit?take=15", { headers: headers() });
    if (!r.ok) { audit.innerHTML = ""; return; }
    const rows = await r.json();
    audit.innerHTML = rows.map((a) => `<div class="row ${a.authorized ? "" : "denied"}">
        <span class="when">${String(a.timestamp).slice(11, 19)}</span>
        <span class="actor">${esc(a.actorId)}</span>
        <code>${esc(a.tool)}</code>
        <span class="muted">${a.authorized ? (a.succeeded ? "ok" : "rejected") : "DENIED"} · ${Math.round(a.latencyMs)} ms${a.promptTokens ? ` · ${a.promptTokens}/${a.completionTokens} tok` : ""}</span>
      </div>`).join("");
  }

  function onIdentityChange() {
    const me = current();
    const isMgr = me?.role === "manager";
    approvals.hidden = !isMgr;
    auditPanel.hidden = !isMgr;
    messages.innerHTML = "";
    trace.innerHTML = `<span class="muted">No calls yet.</span>`;
    bubble("system", `You are now <b>${esc(me.name)}</b> (${esc(me.id)}, ${esc(me.role)}, ${esc(me.region)}, team ${esc(me.teamId)}).`);
    if (isMgr) { loadPending(); loadAudit(); }
  }

  async function init() {
    const [emps, health] = await Promise.all([
      fetch("/employees").then((r) => r.json()),
      fetch("/health").then((r) => r.json()),
    ]);
    employees = emps;
    who.innerHTML = emps.map((e) => `<option value="${e.id}">${esc(e.name)} — ${e.id} · ${e.role} · ${e.region}</option>`).join("");
    who.value = "E002";
    $("health").textContent = `${health.provider} · ${health.model} · today ${String(health.now).slice(0, 10)}`;
    samples.innerHTML = SAMPLES.map((s) => `<button type="button" class="chip">${esc(s)}</button>`).join("");
    onIdentityChange();
  }

  who.addEventListener("change", onIdentityChange);
  form.addEventListener("submit", (e) => {
    e.preventDefault();
    const text = input.value.trim();
    if (!text) return;
    input.value = "";
    ask(text);
  });
  samples.addEventListener("click", (e) => {
    if (e.target.classList.contains("chip")) { input.value = e.target.textContent; input.focus(); }
  });
  pending.addEventListener("click", (e) => {
    const b = e.target.closest("button[data-id]");
    if (b) decide(b.dataset.id, b.dataset.act);
  });
  $("refresh").addEventListener("click", (e) => { e.preventDefault(); loadPending(); loadAudit(); });

  init().catch((e) => bubble("system error", esc(String(e))));
})();
