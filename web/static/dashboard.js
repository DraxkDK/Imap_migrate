// Live dashboard updates + confirm guards. Loaded as an external file so the
// strict Content-Security-Policy (script-src 'self') stays intact — no inline JS.
(function () {
  "use strict";

  // Confirmation guard for destructive / important buttons.
  document.querySelectorAll("button[data-confirm]").forEach(function (btn) {
    btn.addEventListener("click", function (e) {
      if (!window.confirm(btn.getAttribute("data-confirm"))) {
        e.preventDefault();
      }
    });
  });

  var script = document.currentScript;
  var statusUrl = script && script.getAttribute("data-status-url");
  if (!statusUrl) { return; }

  function setText(id, value) {
    var el = document.getElementById(id);
    if (el) { el.textContent = value; }
  }

  function setBadge(id, status) {
    var el = document.getElementById(id);
    if (!el) { return; }
    el.textContent = status;
    el.className = "badge badge-" + status;
  }

  function refresh() {
    fetch(statusUrl, { headers: { "Accept": "application/json" }, credentials: "same-origin" })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (data) {
        if (!data) { return; }
        var j = data.job || {};
        setBadge("job-status", j.status || "idle");
        setText("job-message", j.message || "");
        setText("j-total", j.total || 0);
        setText("j-running", j.running || 0);
        setText("j-completed", j.completed || 0);
        setText("j-failed", j.failed || 0);
        setText("j-pending", j.pending || 0);
        setText("j-migrated", j.total_migrated || 0);
      })
      .catch(function () { /* transient network error — try again next tick */ });
  }

  // Poll every 4s while the page is open.
  refresh();
  setInterval(refresh, 4000);
})();
