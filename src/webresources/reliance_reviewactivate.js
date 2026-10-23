/* Review and activate dialog for health score profiles.
   Loaded by the landing page:  <script src="reliance_ReviewActivate"></script>
   Opened with:                 openReviewDialog(profileId, onChanged)

   - Checks come from the CheckProfileReadiness function (read-only).
   - Activating / deactivating is done here, in the browser, as the signed-in user,
     so Dataverse security and auditing apply.
*/
(function () {
    "use strict";

    // ===== Settings =====
    var FUNCTION_BASE = "https://<your-function-app>.azurewebsites.net/api";
    var FUNCTION_KEY = "<function-key>"; // set when deploying; never commit a real key

    var PROFILE_TABLE = "reliance_healthscoreprofile";
    var STATUS = { Draft: 1, Active: 2, Inactive: 3 };

    var root, dialog, state;

    injectStyles();

    // ===== Public entry point =====
    window.openReviewDialog = function (profileId, onChanged) {
        state = {
            profileId: cleanId(profileId),
            onChanged: onChanged,
            report: null,
            loadError: null,
            sample: null,
            preview: null,          // result of the scoring engine for the chosen record
            previewLoading: false,
            confirming: null,   // "replace" | "deactivate"
            busy: false,
            message: null,      // { kind: "error" | "success", text }
            done: false,
            changed: false
        };
        buildShell();
        loadReport();
    };
 
    // ===== Data =====
    function xrm() { return window.parent && window.parent.Xrm; }
 
    function cleanId(id) { return String(id || "").replace(/[{}]/g, "").toLowerCase(); }
 
    async function callReadiness() {
        var url = FUNCTION_BASE + "/CheckProfileReadiness?code=" + encodeURIComponent(FUNCTION_KEY) +
            "&profileId=" + encodeURIComponent(state.profileId);
        var res = await fetch(url);
        var body = await res.json().catch(function () { return {}; });
        if (!res.ok) throw new Error(body.error || ("The check service returned " + res.status + "."));
        return body;
    }
 
    // Scores one record with the real engine. preview=true means nothing is saved.
    async function callPreview(recordId) {
        var url = FUNCTION_BASE + "/EvaluateHealthScoreProfile?code=" + encodeURIComponent(FUNCTION_KEY) +
            "&profileId=" + encodeURIComponent(state.profileId) +
            "&recordId=" + encodeURIComponent(recordId) +
            "&preview=true";
        var res = await fetch(url);
        var body = await res.json().catch(function () { return {}; });
        if (!res.ok) throw new Error(body.error || body.message || ("The scoring service returned " + res.status + "."));
        return body;
    }
 
    async function loadReport() {
        state.loadError = null;
        state.report = null;
        render();
        try {
            state.report = await callReadiness();
        } catch (e) {
            state.loadError = "Couldn't check this profile. " + e.message;
        }
        render();
    }
 
    async function chooseSample() {
        var x = xrm();
        if (!x) { showMessage("error", "Dynamics isn't available, so a record can't be chosen."); return; }
        var table = state.report.targetTable;
        try {
            var picked = await x.Utility.lookupObjects({ entityTypes: [table], defaultEntityType: table, allowMultiSelect: false });
            if (!picked || !picked.length) return;
            state.sample = { id: cleanId(picked[0].id), name: picked[0].name };
        } catch (e) {
            showMessage("error", "Couldn't open the record picker. " + (e.message || ""));
            return;
        }
        state.message = null;
        state.preview = null;
        state.previewLoading = true;
        render();
        try {
            state.preview = await callPreview(state.sample.id);
        } catch (e) {
            showMessage("error", "Couldn't run the preview. " + e.message);
        }
        state.previewLoading = false;
        render();
    }
 
    // ===== Actions =====
    async function onActivate() {
        state.message = null;
        state.busy = true;
        render();
        try {
            // Re-check right before activating, so the decision uses current data.
            state.report = await callReadiness();
        } catch (e) {
            state.busy = false;
            showMessage("error", "Couldn't re-check the profile before activating. " + e.message);
            return;
        }
        state.busy = false;
        if (!state.report.ready) {
            showMessage("error", "Fix the failed checks before activating.");
            return;
        }
        if (state.report.activeProfile) {
            state.confirming = "replace";
            render();
            return;
        }
        await activate(null);
    }
 
    async function activate(other) {
        var api = xrm() && xrm().WebApi;
        if (!api) { showMessage("error", "Dynamics isn't available, so the profile can't be activated."); return; }
        state.busy = true;
        state.confirming = null;
        render();
        try {
            if (other) await api.updateRecord(PROFILE_TABLE, cleanId(other.id), { reliance_status: STATUS.Inactive });
            try {
                await api.updateRecord(PROFILE_TABLE, state.profileId, { reliance_status: STATUS.Active });
            } catch (e) {
                // Put the previous profile back so the table isn't left without an active profile.
                if (other) {
                    try { await api.updateRecord(PROFILE_TABLE, cleanId(other.id), { reliance_status: STATUS.Active }); } catch (ignore) { }
                }
                throw e;
            }
            state.changed = true;
            state.done = true;
            state.report.status = "Active";
            showMessage("success",
                "Profile activated. It now scores " + state.report.targetTable + " records and is locked for editing." +
                (other ? " " + other.name + " was set to Inactive." : ""));
        } catch (e) {
            state.busy = false;
            showMessage("error", "The profile wasn't activated. " + (e.message || ""));
        }
    }
 
    async function deactivate() {
        var api = xrm() && xrm().WebApi;
        if (!api) { showMessage("error", "Dynamics isn't available, so the profile can't be deactivated."); return; }
        state.busy = true;
        state.confirming = null;
        render();
        try {
            await api.updateRecord(PROFILE_TABLE, state.profileId, { reliance_status: STATUS.Inactive });
            state.changed = true;
            state.done = true;
            state.report.status = "Inactive";
            showMessage("success", "Profile deactivated. You can edit it now, then review and activate it again.");
        } catch (e) {
            state.busy = false;
            showMessage("error", "The profile wasn't deactivated. " + (e.message || ""));
        }
    }
 
    function showMessage(kind, text) {
        state.busy = false;
        state.message = { kind: kind, text: text };
        render();
    }
 
    function close() {
        if (state && state.busy) return;
        if (root) root.remove();
        root = null;
        document.removeEventListener("keydown", onKey);
        if (state && state.changed && typeof state.onChanged === "function") state.onChanged();
    }
 
    function onKey(e) { if (e.key === "Escape") close(); }
 
    // ===== Rendering =====
    function buildShell() {
        if (root) root.remove();
        root = document.createElement("div");
        root.className = "rad-overlay";
        root.addEventListener("click", function (e) { if (e.target === root) close(); });
        dialog = document.createElement("div");
        dialog.className = "rad-dialog";
        dialog.setAttribute("role", "dialog");
        dialog.setAttribute("aria-modal", "true");
        dialog.setAttribute("aria-labelledby", "rad-title");
        root.appendChild(dialog);
        document.body.appendChild(root);
        document.addEventListener("keydown", onKey);
    }
 
    function render() {
        if (!dialog) return;
        var r = state.report;
        var isActive = r && r.status === "Active";
        var h = "";
 
        // Header
        h += '<div class="rad-head"><div class="rad-head-text">';
        h += '<h2 class="rad-title" id="rad-title">' + (isActive ? "Review profile" : "Review and activate") + "</h2>";
        if (r) h += '<p class="rad-sub">' + esc(r.profileName) + " — " + esc(r.targetTable) + "</p>";
        h += "</div>";
        if (r) h += '<span class="rad-badge rad-badge-' + r.status.toLowerCase() + '">' + esc(r.status) + "</span>";
        h += '<button type="button" class="rad-x" id="rad-close" aria-label="Close">&times;</button></div>';
 
        h += '<div class="rad-body">';
        if (state.loadError) {
            h += '<div class="rad-box rad-box-error">' + esc(state.loadError) + "</div>";
            h += '<button type="button" class="rad-btn" id="rad-retry">Try again</button>';
        } else if (!r) {
            h += '<p class="rad-muted">Checking the profile…</p>';
        } else {
            // Checks
            h += '<h3 class="rad-h">Readiness checks</h3><ul class="rad-checks">';
            r.checks.forEach(function (c) {
                var icon = c.level === "pass" ? "✓" : c.level === "warn" ? "!" : "✕";
                h += '<li class="rad-check rad-' + c.level + '"><span class="rad-icon" aria-hidden="true">' + icon + "</span><span>" +
                    (c.area && c.area !== "Profile" && c.area !== "Rules" ? '<span class="rad-area">' + esc(c.area) + "</span> " : "") +
                    esc(c.message) + "</span></li>";
            });
            h += "</ul>";
 
            // Preview
            h += '<h3 class="rad-h">Try it on a record</h3>';
            h += '<div class="rad-row"><span class="' + (state.sample ? "" : "rad-muted") + '">' +
                (state.sample ? esc(state.sample.name) : "No record chosen") + "</span>" +
                '<button type="button" class="rad-btn" id="rad-pick">' + (state.sample ? "Choose another" : "Choose a record") + "</button></div>";
 
            if (state.previewLoading) {
                h += '<p class="rad-muted">Scoring this record, including the AI review. This can take a few seconds…</p>';
            } else if (state.preview) {
                var p = state.preview;
                var a = p.audit || { aiReviewed: false, steps: [] };
 
                // Headline
                h += '<div class="rad-preview"><div class="rad-score"><span class="rad-score-label">Preview score</span>' +
                    '<span class="rad-score-value">' + fmt(p.finalScore, false) + "</span>" +
                    '<span class="rad-score-label">' + esc(p.riskLevel) + " risk</span></div></div>";
 
                // Step 1: the rule score. One table per data source: matches x points each = result
                h += '<h4 class="rad-h">1. Rule score</h4>';
                (p.dataSources || []).forEach(function (ds) {
                    h += '<p class="rad-ds">' + esc(ds.dataSourceName) +
                        ' <span class="rad-muted">(up to ' + fmt(ds.weight, false) + " points)</span></p>";
                    h += '<table class="rad-table"><thead><tr><th>Rule</th><th>Matches</th><th>Points each</th><th>Result</th></tr></thead><tbody>';
                    (ds.rules || []).forEach(function (rule) {
                        var m = rule.matchCount;
                        h += "<tr" + (m === 0 ? ' class="rad-dim"' : "") + "><td>" + esc(rule.ruleName) +
                            '<span class="rad-tag rad-tag-' + esc(String(rule.impact).toLowerCase()) + '">' + esc(rule.impact) + "</span></td>" +
                            "<td>" + m + "</td><td>× " + fmt(rule.pointsPerMatch, false) + "</td>" +
                            "<td><b>= " + fmt(rule.signedPoints, true) + "</b></td></tr>";
                    });
                    h += '<tr class="rad-tr-total"><td>Total for this source</td><td></td><td></td><td>' + fmt(ds.net, true) + "</td></tr></tbody></table>";
                    if (ds.limited) {
                        h += '<p class="rad-limit">Limited to ' + fmt(ds.contribution, true) +
                            ", because this source can never add or remove more than " + fmt(ds.weight, false) + " points.</p>";
                    }
                });
                h += '<p class="rad-total">All data sources: ' + fmt(p.ruleScoreBeforeLimit, true) +
                    " → rule score " + fmt(p.ruleScore, false) +
                    ' <span class="rad-muted">(kept between 0 and 100; the most these rules can give is ' + fmt(p.maxRuleScore, false) + ")</span></p>";
 
                // Step 2: how the AI moved the score, point by point, with the evidence it relied on
                h += '<h4 class="rad-h">2. AI review</h4>';
                if (!a.aiReviewed) {
                    h += '<p class="rad-note rad-tight">The AI review was unavailable, so the final score is the rule score.</p>';
                } else if (!a.steps.length) {
                    h += '<p class="rad-text">The AI kept the rule score unchanged.</p>';
                } else {
                    a.steps.forEach(function (st) {
                        h += '<div class="rad-step"><b>' + fmt(st.points, true) + '</b> <span class="rad-kind">' + esc(st.kind) + "</span> " + esc(st.rationale);
                        if (st.kind === "AI") {
                            if (!st.evidence) {
                                h += '<div class="rad-evidence rad-bad">⚠ No evidence was quoted for this adjustment.</div>';
                            } else if (st.evidenceFound) {
                                h += '<div class="rad-evidence rad-ok">✓ Found in the data: “' + esc(st.evidence) + "”</div>";
                            } else {
                                h += '<div class="rad-evidence rad-bad">⚠ Not found word-for-word in the data: “' + esc(st.evidence) + "”</div>";
                            }
                        }
                        h += "</div>";
                    });
                }
                h += '<p class="rad-total">Rule score ' + fmt(p.ruleScore, false) + " " + (p.aiAdjustment < 0 ? "−" : "+") + " " +
                    fmt(Math.abs(p.aiAdjustment), false) + " = final score " + fmt(p.finalScore, false) + "</p>";
                if (a.addsUp === false) {
                    h += '<p class="rad-evidence rad-bad">⚠ The listed steps do not add up to the final score.</p>';
                }
 
                // What the salesperson would read
                if (p.reason) h += '<h4 class="rad-h">What the salesperson sees</h4><p class="rad-text">' + esc(p.reason) + "</p>";
                if (p.nextAction) h += '<p class="rad-text"><b>Next step:</b> ' + esc(p.nextAction) + "</p>";
                if (p.warnings && p.warnings.length) {
                    h += '<p class="rad-note">Worth checking:</p>';
                    p.warnings.forEach(function (w) { h += '<p class="rad-note rad-tight">• ' + esc(w) + "</p>"; });
                }
                h += '<p class="rad-note rad-tight">Previews aren\'t saved.</p>';
            }
 
            // Heads-up about the currently active profile
            if (!isActive && r.activeProfile && !state.confirming && !state.done) {
                h += '<p class="rad-note">' + esc(r.activeProfile.name) + " is currently active for " + esc(r.targetTable) +
                    ". You'll be asked before it's replaced.</p>";
            }
 
            // Confirmations
            if (state.confirming === "replace") {
                h += '<div class="rad-box rad-box-warn"><p>' + esc(r.activeProfile.name) + " is currently active for " + esc(r.targetTable) +
                    ". Only one profile can be active per table.</p><div class=\"rad-actions\">" +
                    '<button type="button" class="rad-btn" id="rad-confirm-cancel">Cancel</button>' +
                    '<button type="button" class="rad-btn rad-primary" id="rad-confirm-replace">Deactivate it and activate this one</button></div></div>';
            }
            if (state.confirming === "deactivate") {
                h += '<div class="rad-box rad-box-warn"><p>' + esc(r.targetTable) +
                    " records will have no active health score until another profile is activated.</p><div class=\"rad-actions\">" +
                    '<button type="button" class="rad-btn" id="rad-confirm-cancel">Cancel</button>' +
                    '<button type="button" class="rad-btn rad-primary" id="rad-confirm-deactivate">Deactivate</button></div></div>';
            }
 
            if (state.message) {
                h += '<div class="rad-box rad-box-' + state.message.kind + '">' + esc(state.message.text) + "</div>";
            }
        }
        h += "</div>";
 
        // Footer
        h += '<div class="rad-foot">';
        if (r && !state.busy) {
            h += '<button type="button" class="rad-btn rad-download" id="rad-download" title="Download these findings as a report">Download report</button>';
        }
        if (state.busy) {
            h += '<span class="rad-muted">Working…</span>';
        } else if (state.done || state.loadError || !r) {
            h += '<button type="button" class="rad-btn" id="rad-cancel">Close</button>';
        } else if (!state.confirming) {
            if (isActive) {
                h += '<button type="button" class="rad-btn" id="rad-cancel">Close</button>';
                h += '<button type="button" class="rad-btn" id="rad-deactivate">Deactivate</button>';
            } else {
                h += '<button type="button" class="rad-btn" id="rad-cancel">Cancel</button>';
                h += '<button type="button" class="rad-btn rad-primary" id="rad-activate">Activate profile</button>';
            }
        }
        h += "</div>";
 
        dialog.innerHTML = h;
        wire();
    }
 
    function wire() {
        on("rad-close", close);
        on("rad-cancel", close);
        on("rad-retry", loadReport);
        on("rad-pick", chooseSample);
        on("rad-activate", onActivate);
        on("rad-deactivate", function () { state.message = null; state.confirming = "deactivate"; render(); });
        on("rad-confirm-cancel", function () { state.confirming = null; render(); });
        on("rad-confirm-replace", function () { activate(state.report.activeProfile); });
        on("rad-confirm-deactivate", deactivate);
        on("rad-download", downloadReport);
        var first = dialog.querySelector(".rad-box-warn .rad-primary") || dialog.querySelector("#rad-close");
        if (first) first.focus();
    }
 
    // ===== Download report =====
    // Builds a standalone, printable page from what the dialog already shows
    // (readiness checks + the sample-record preview, if one was run) and downloads it.
    // Nothing is sent anywhere and nothing in Dataverse changes.
    function downloadReport() {
        var html = buildReportHtml();
        var stamp = new Date();
        var day = stamp.getFullYear() + "-" + pad(stamp.getMonth() + 1) + "-" + pad(stamp.getDate());
        var name = ("Health score review - " + (state.report.profileName || "profile") + " - " + day)
            .replace(/[\\/:*?"<>|]+/g, "-") + ".html";

        var blob = new Blob([html], { type: "text/html;charset=utf-8" });
        var url = URL.createObjectURL(blob);
        var a = document.createElement("a");
        a.href = url;
        a.download = name;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(function () { URL.revokeObjectURL(url); }, 2000);
    }

    function pad(n) { return (n < 10 ? "0" : "") + n; }

    function currentUserName() {
        try {
            var x = xrm();
            var ctx = x && x.Utility && x.Utility.getGlobalContext && x.Utility.getGlobalContext();
            return (ctx && ctx.userSettings && ctx.userSettings.userName) || "";
        } catch (e) {
            return "";
        }
    }

    function buildReportHtml() {
        var r = state.report;
        var p = state.preview;
        var now = new Date();
        var fails = r.checks.filter(function (c) { return c.level === "fail"; }).length;
        var warns = r.checks.filter(function (c) { return c.level === "warn"; }).length;
        var user = currentUserName();

        var h = "<!DOCTYPE html><html><head><meta charset=\"UTF-8\">" +
            "<title>Health score review - " + esc(r.profileName) + "</title><style>" +
            "body{font-family:'Segoe UI',Arial,sans-serif;color:#242424;max-width:820px;margin:32px auto;padding:0 24px;font-size:14px;line-height:1.5}" +
            "h1{font-size:24px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 8px;padding-bottom:4px;border-bottom:1px solid #e1dfdd}" +
            "h3{font-size:14px;margin:16px 0 4px}.sub{color:#616161;margin:0}" +
            ".meta{display:grid;grid-template-columns:max-content 1fr;gap:4px 16px;margin:16px 0;font-size:13px}.meta b{color:#616161;font-weight:600}" +
            ".verdict{padding:12px 16px;border-radius:8px;font-weight:600;margin:16px 0}.ok{background:#dcfce7;color:#14532d}.bad{background:#fde2e2;color:#7f1d1d}" +
            "ul.checks{list-style:none;padding:0;margin:0}ul.checks li{padding:4px 0 4px 26px;position:relative}" +
            "ul.checks li:before{position:absolute;left:0;top:5px;width:18px;height:18px;border-radius:50%;font-size:11px;font-weight:700;text-align:center;line-height:18px}" +
            "li.pass:before{content:'\\2713';background:#dcfce7;color:#15803d}li.warn:before{content:'!';background:#fef3c7;color:#a16207}li.fail:before{content:'\\2715';background:#fde2e2;color:#b91c1c}" +
            "table{width:100%;border-collapse:collapse;font-size:13px;margin:4px 0 8px}th{text-align:left;color:#616161;font-weight:600;border-bottom:1px solid #e1dfdd;padding:5px 8px}" +
            "td{padding:5px 8px;border-bottom:1px solid #f3f2f1}td.n,th.n{text-align:right;white-space:nowrap}tr.total td{font-weight:600;border-top:1px solid #e1dfdd}" +
            ".score{display:inline-block;background:#f5f7fa;border-radius:8px;padding:10px 18px;margin:8px 0}.score b{font-size:28px;display:block}" +
            ".muted{color:#616161}.small{font-size:12px}.good{color:#15803d}.badtxt{color:#b91c1c}" +
            ".toolbar{background:#f5f7fa;border-radius:8px;padding:10px 14px;margin-bottom:24px;font-size:13px;display:flex;gap:12px;align-items:center}" +
            ".toolbar button{font:inherit;font-weight:600;padding:6px 14px;border-radius:6px;border:0;background:#0f6cbd;color:#fff;cursor:pointer}" +
            "@media print{.toolbar{display:none}body{margin:0 auto}h2{break-after:avoid}table,li{break-inside:avoid}}" +
            "</style></head><body>";

        h += "<div class=\"toolbar\"><button onclick=\"window.print()\">Print or save as PDF</button>" +
            "<span class=\"muted\">This toolbar isn't printed.</span></div>";

        // Header
        h += "<h1>Health score review</h1><p class=\"sub\">" + esc(r.profileName) + " — " + esc(r.targetTable) + "</p>";
        h += "<div class=\"meta\">" +
            "<b>Status</b><span>" + esc(r.status) + "</span>" +
            "<b>Generated</b><span>" + esc(now.toLocaleString()) + "</span>" +
            (user ? "<b>Generated by</b><span>" + esc(user) + "</span>" : "") +
            "<b>Profile id</b><span class=\"small\">" + esc(r.profileId) + "</span>" +
            "</div>";

        h += "<div class=\"verdict " + (r.ready ? "ok" : "bad") + "\">" +
            (r.ready ? "Ready to activate" : "Not ready to activate") +
            " — " + fails + " failed check" + (fails === 1 ? "" : "s") + ", " + warns + " warning" + (warns === 1 ? "" : "s") + "</div>";

        if (r.activeProfile && r.status !== "Active") {
            h += "<p class=\"muted\">" + esc(r.activeProfile.name) + " is currently active for " + esc(r.targetTable) +
                " and would be replaced on activation.</p>";
        }

        // Readiness checks
        h += "<h2>Readiness checks</h2><ul class=\"checks\">";
        r.checks.forEach(function (c) {
            h += "<li class=\"" + esc(c.level) + "\">" +
                (c.area && c.area !== "Profile" && c.area !== "Rules" ? "<b>" + esc(c.area) + "</b> " : "") +
                esc(c.message) + "</li>";
        });
        h += "</ul>";

        // Configuration
        if (r.dataSources && r.dataSources.length) {
            h += "<h2>Configuration</h2>";
            r.dataSources.forEach(function (ds) {
                h += "<h3>" + esc(ds.name) + " <span class=\"muted\">(" + esc(ds.table) + ", up to " + fmt(ds.weight, false) + " points)</span></h3>";
                if (!ds.rules || !ds.rules.length) {
                    h += "<p class=\"muted\">No enabled rules.</p>";
                    return;
                }
                h += "<table><thead><tr><th>Rule</th><th class=\"n\">Points each</th><th class=\"n\">Query runs</th></tr></thead><tbody>";
                ds.rules.forEach(function (rule) {
                    h += "<tr><td>" + esc(rule.ruleName) + "</td><td class=\"n\">" + fmt(rule.scoreValue, false) + "</td>" +
                        "<td class=\"n\">" + (rule.runs ? "<span class=\"good\">Yes</span>" : "<span class=\"badtxt\">No</span>") + "</td></tr>";
                });
                h += "</tbody></table>";
            });
        }

        // Sample-record test
        h += "<h2>Test on a record</h2>";
        if (!p || !state.sample) {
            h += "<p class=\"muted\">No record was tested in this review.</p>";
        } else {
            var a = p.audit || { aiReviewed: false, steps: [] };

            h += "<p>Record: <b>" + esc(state.sample.name) + "</b></p>";
            h += "<div class=\"score\"><span class=\"muted small\">Preview score</span><b>" + fmt(p.finalScore, false) + "</b>" +
                "<span class=\"muted small\">" + esc(p.riskLevel) + " risk</span></div>";

            h += "<h3>1. Rule score</h3>";
            (p.dataSources || []).forEach(function (ds) {
                h += "<p style=\"margin:10px 0 2px\"><b>" + esc(ds.dataSourceName) + "</b> <span class=\"muted\">(up to " + fmt(ds.weight, false) + " points)</span></p>";
                h += "<table><thead><tr><th>Rule</th><th>Impact</th><th class=\"n\">Matches</th><th class=\"n\">Points each</th><th class=\"n\">Result</th></tr></thead><tbody>";
                (ds.rules || []).forEach(function (rule) {
                    h += "<tr><td>" + esc(rule.ruleName) + "</td><td>" + esc(rule.impact) + "</td>" +
                        "<td class=\"n\">" + rule.matchCount + "</td><td class=\"n\">× " + fmt(rule.pointsPerMatch, false) + "</td>" +
                        "<td class=\"n\">" + fmt(rule.signedPoints, true) + "</td></tr>";
                });
                h += "<tr class=\"total\"><td colspan=\"4\">Total for this source" +
                    (ds.limited ? " (limited to " + fmt(ds.contribution, true) + ")" : "") + "</td><td class=\"n\">" +
                    fmt(ds.limited ? ds.contribution : ds.net, true) + "</td></tr></tbody></table>";
            });
            h += "<p><b>All data sources: " + fmt(p.ruleScoreBeforeLimit, true) + " → rule score " + fmt(p.ruleScore, false) + "</b>" +
                " <span class=\"muted\">(kept between 0 and 100; the most these rules can give is " + fmt(p.maxRuleScore, false) + ")</span></p>";

            h += "<h3>2. AI review</h3>";
            if (!a.aiReviewed) {
                h += "<p class=\"muted\">The AI review was unavailable, so the final score is the rule score.</p>";
            } else if (!a.steps || !a.steps.length) {
                h += "<p>The AI kept the rule score unchanged.</p>";
            } else {
                h += "<table><thead><tr><th class=\"n\">Points</th><th>Type</th><th>Reason and evidence</th></tr></thead><tbody>";
                a.steps.forEach(function (st) {
                    var ev = "";
                    if (st.kind === "AI") {
                        ev = !st.evidence
                            ? "<br><span class=\"badtxt small\">No evidence was quoted.</span>"
                            : st.evidenceFound
                                ? "<br><span class=\"good small\">Found in the data: “" + esc(st.evidence) + "”</span>"
                                : "<br><span class=\"badtxt small\">Not found word-for-word in the data: “" + esc(st.evidence) + "”</span>";
                    }
                    h += "<tr><td class=\"n\">" + fmt(st.points, true) + "</td><td>" + esc(st.kind) + "</td><td>" + esc(st.rationale) + ev + "</td></tr>";
                });
                h += "</tbody></table>";
            }
            h += "<p><b>Rule score " + fmt(p.ruleScore, false) + " " + (p.aiAdjustment < 0 ? "−" : "+") + " " +
                fmt(Math.abs(p.aiAdjustment || 0), false) + " = final score " + fmt(p.finalScore, false) + "</b></p>";
            if (a.addsUp === false) h += "<p class=\"badtxt\">The listed steps do not add up to the final score.</p>";

            if (p.reason || p.nextAction) {
                h += "<h3>What the salesperson sees</h3>";
                if (p.reason) h += "<p>" + esc(p.reason) + "</p>";
                if (p.nextAction) h += "<p><b>Next step:</b> " + esc(p.nextAction) + "</p>";
            }

            if (p.warnings && p.warnings.length) {
                h += "<h3>Worth checking</h3><ul>";
                p.warnings.forEach(function (w) { h += "<li>" + esc(w) + "</li>"; });
                h += "</ul>";
            }
            h += "<p class=\"muted small\">This was a preview. The score was not saved.</p>";
        }

        h += "</body></html>";
        return h;
    }

    function on(id, fn) {
        var el = dialog.querySelector("#" + id);
        if (el) el.addEventListener("click", fn);
    }
 
    function esc(s) {
        return String(s === null || s === undefined ? "" : s).replace(/[&<>"']/g, function (c) {
            return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
        });
    }
 
    function fmt(n, signed) {
        var v = Math.round(Number(n || 0) * 100) / 100;
        return (signed && v > 0 ? "+" : "") + v;
    }
 
    function injectStyles() {
        var css =
            ".rad-overlay{position:fixed;inset:0;background:rgba(32,31,30,.4);display:flex;align-items:center;justify-content:center;padding:24px;z-index:10000;text-align:left}" +
            ".rad-dialog{background:#fff;border-radius:10px;width:100%;max-width:620px;max-height:100%;display:flex;flex-direction:column;box-shadow:0 8px 32px rgba(0,0,0,.2);font-family:'Segoe UI',Arial,sans-serif;color:#242424}" +
            ".rad-head{display:flex;align-items:flex-start;gap:12px;padding:20px 20px 14px 24px;border-bottom:1px solid #edebe9}" +
            ".rad-head-text{flex:1}.rad-title{margin:0;font-size:20px;font-weight:600}.rad-sub{margin:4px 0 0;font-size:14px;color:#616161}" +
            ".rad-body{padding:8px 24px 16px;overflow-y:auto}" +
            ".rad-h{font-size:14px;font-weight:600;margin:16px 0 8px}" +
            ".rad-checks{list-style:none;margin:0;padding:0}" +
            ".rad-check{display:flex;gap:10px;font-size:14px;line-height:1.5;padding:4px 0}" +
            ".rad-icon{flex:none;width:18px;height:18px;border-radius:50%;display:inline-flex;align-items:center;justify-content:center;font-size:11px;font-weight:700;margin-top:2px}" +
            ".rad-pass .rad-icon{background:#dcfce7;color:#15803d}.rad-warn .rad-icon{background:#fef3c7;color:#a16207}.rad-fail .rad-icon{background:#fde2e2;color:#b91c1c}" +
            ".rad-area{font-weight:600}" +
            ".rad-row{display:flex;align-items:center;justify-content:space-between;gap:12px;font-size:14px}" +
            ".rad-preview{display:flex;gap:16px;margin-top:12px;flex-wrap:wrap}" +
            ".rad-score{background:#f5f7fa;border-radius:8px;padding:12px 16px;min-width:130px;display:flex;flex-direction:column}" +
            ".rad-score-label{font-size:13px;color:#616161}.rad-score-value{font-size:28px;font-weight:600}" +
            ".rad-rules{flex:1;min-width:220px;font-size:13px}.rad-ds{margin:4px 0 2px;font-weight:600}.rad-rule{margin:2px 0 2px 10px}" +
            ".rad-text{margin:12px 0 0;font-size:13px;line-height:1.5}.rad-text b{font-weight:600}" +
            ".rad-note{font-size:13px;color:#616161;margin:16px 0 0}.rad-tight{margin-top:4px}" +
            ".rad-box{border-radius:8px;padding:12px 14px;font-size:14px;line-height:1.5;margin-top:16px}.rad-box p{margin:0}" +
            ".rad-box-warn{background:#fef3c7;color:#713f12}.rad-box-error{background:#fde2e2;color:#7f1d1d}.rad-box-success{background:#dcfce7;color:#14532d}" +
            ".rad-actions{display:flex;justify-content:flex-end;gap:8px;margin-top:12px;flex-wrap:wrap}" +
            ".rad-foot{display:flex;justify-content:flex-end;align-items:center;gap:8px;padding:14px 24px;border-top:1px solid #edebe9}" +
            ".rad-dialog button.rad-btn{min-width:0;padding:7px 14px;font-size:14px;font-weight:600;border-radius:6px;border:1px solid #d1d1d1;background:#fff;color:#242424;cursor:pointer}" +
            ".rad-dialog button.rad-btn:hover{background:#f5f5f5}" +
            ".rad-dialog button.rad-primary{background:#0f6cbd;border-color:#0f6cbd;color:#fff}.rad-dialog button.rad-primary:hover{background:#115ea3}" +
            ".rad-dialog button.rad-x{min-width:0;padding:2px 8px;border:none;background:transparent;font-size:22px;line-height:1;color:#616161;cursor:pointer;border-radius:4px}" +
            ".rad-dialog button.rad-x:hover{background:#f5f5f5}" +
            ".rad-dialog button:focus-visible{outline:2px solid #0f6cbd;outline-offset:2px}" +
            ".rad-badge{font-size:11px;font-weight:700;padding:3px 10px;border-radius:999px;margin-top:4px}" +
            ".rad-badge-active{background:#dcfce7;color:#15803d}.rad-badge-draft{background:#fef3c7;color:#a16207}.rad-badge-inactive{background:#f3f4f6;color:#6b7280}" +
            ".rad-dim{color:#8a8886}.rad-subtotal{margin:2px 0 10px 10px;font-size:13px;font-weight:600}" +
            ".rad-total{margin:10px 0 0;font-size:13px;font-weight:600}.rad-total .rad-muted{font-weight:400}" +
            ".rad-step{margin:8px 0;font-size:13px;line-height:1.5}.rad-kind{font-size:11px;color:#616161;border:1px solid #d1d1d1;border-radius:999px;padding:0 6px;margin:0 2px}" +
            ".rad-evidence{margin:2px 0 0;font-size:12px}.rad-ok{color:#15803d}.rad-bad{color:#b91c1c}" +
            ".rad-table{width:100%;border-collapse:collapse;font-size:13px;margin:4px 0 2px}" +
            ".rad-table th{text-align:right;font-weight:600;color:#616161;padding:4px 8px;border-bottom:1px solid #edebe9;white-space:nowrap}" +
            ".rad-table td{padding:5px 8px;text-align:right;white-space:nowrap}" +
            ".rad-table th:first-child,.rad-table td:first-child{text-align:left;white-space:normal}" +
            ".rad-table tr.rad-tr-total td{font-weight:600;border-top:1px solid #edebe9}" +
            ".rad-tag{font-size:11px;font-weight:600;border-radius:999px;padding:1px 8px;margin-left:8px}" +
            ".rad-tag-positive{background:#dcfce7;color:#15803d}.rad-tag-negative{background:#fde2e2;color:#b91c1c}.rad-tag-neutral,.rad-tag-unset{background:#f3f4f6;color:#6b7280}" +
            ".rad-limit{margin:2px 0 12px;font-size:12px;color:#616161}" +
            ".rad-dialog button.rad-download{margin-right:auto}" +
            ".rad-muted{color:#616161}";
        var style = document.createElement("style");
        style.textContent = css;
        document.head.appendChild(style);
    }
})();
 