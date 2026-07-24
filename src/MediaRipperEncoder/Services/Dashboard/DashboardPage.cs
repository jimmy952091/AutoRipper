// AutoRipper — automated disc ripping/encoding for Plex, Jellyfin, and other media servers.
// Copyright (C) 2026 James Spurgeon (heto.black@gmail.com)
//
// This program is free software: you can redistribute it and/or modify it under the terms of
// the GNU Affero General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License along with this
// program. If not, see <https://www.gnu.org/licenses/>.

namespace MediaRipperEncoder.Services.Dashboard
{
    /// <summary>
    /// The dashboard single-page app, served as one self-contained document (no external scripts,
    /// fonts, or styles — it must work on an isolated LAN and offline, matching the app's ethos).
    /// It contains NO fleet data itself: on load it calls the auth-gated /api/status, showing a
    /// login prompt until the shared secret is entered. The AGPL §13 "appropriate legal notice"
    /// (license + source link) lives in the footer, satisfied here because the dashboard is exactly
    /// the network-interaction case §13 was written for. Phase A is READ-ONLY — no job controls.
    /// </summary>
    public static class DashboardPage
    {
        public const string Html = @"<!doctype html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>AutoRipper Fleet Dashboard</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  body { margin: 0; font-family: Segoe UI, Tahoma, Arial, sans-serif; background: #14171c; color: #e6e9ef; }
  a { color: #6fb3ff; }
  .hidden { display: none !important; }

  .login { max-width: 420px; margin: 12vh auto; padding: 28px; background: #1c2129; border: 1px solid #2b323d; border-radius: 10px; }
  .login h1 { margin: 0 0 6px; font-size: 20px; }
  .login p { margin: 0 0 16px; color: #9aa4b2; font-size: 14px; }
  .login input { width: 100%; padding: 10px; font-size: 15px; background: #0f1216; color: #e6e9ef; border: 1px solid #2b323d; border-radius: 6px; }
  .login button, .pager button { margin-top: 12px; padding: 10px 16px; font-size: 14px; background: #2d6cdf; color: #fff; border: 0; border-radius: 6px; cursor: pointer; }
  .login button:hover, .pager button:hover { background: #3b7bef; }
  .err { color: #ff8a8a; font-size: 13px; margin-top: 10px; min-height: 16px; }

  header { display: flex; justify-content: space-between; align-items: center; padding: 16px 20px; border-bottom: 1px solid #2b323d; flex-wrap: wrap; gap: 8px; }
  header h1 { margin: 0; font-size: 18px; }
  .sub { color: #9aa4b2; font-size: 12px; margin-top: 3px; }
  .headerRight { display: flex; align-items: center; gap: 14px; font-size: 13px; color: #9aa4b2; }

  main { display: grid; grid-template-columns: repeat(2, 1fr); gap: 14px; padding: 18px 20px; }
  @media (max-width: 720px) { main { grid-template-columns: 1fr; } }

  .tile { background: #1c2129; border: 1px solid #2b323d; border-radius: 10px; padding: 16px; }
  .tile.stale { opacity: 0.55; }
  .tileHead { display: flex; justify-content: space-between; align-items: baseline; margin-bottom: 4px; }
  .tileHead .name { font-size: 16px; font-weight: 600; }
  .badge { font-size: 11px; padding: 2px 8px; border-radius: 20px; background: #2b323d; color: #cdd5e0; }
  .badge.server { background: #274b8f; color: #dbe8ff; }
  .badge.ripper { background: #2f6b3d; color: #dbffe2; }
  .meta { color: #9aa4b2; font-size: 12px; margin-bottom: 10px; }
  .dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; margin-right: 5px; vertical-align: middle; }
  .dot.on { background: #56d364; } .dot.off { background: #e0704f; }

  .section { margin-top: 10px; }
  .section .label { font-size: 12px; text-transform: uppercase; letter-spacing: .04em; color: #8b95a4; }
  .section .line { font-size: 13px; margin: 3px 0; }
  .bar { height: 8px; background: #0f1216; border-radius: 5px; overflow: hidden; margin-top: 5px; }
  .bar > span { display: block; height: 100%; background: #2d6cdf; width: 0%; transition: width .4s ease; }
  .bar.enc > span { background: #56a86b; }
  .counts { font-size: 12px; color: #9aa4b2; margin-top: 5px; }
  .errline { color: #ff9a9a; font-size: 12px; margin-top: 8px; word-break: break-word; }
  .idle { color: #7c8798; font-size: 13px; }

  .pager { display: flex; justify-content: center; align-items: center; gap: 16px; padding: 6px 0 18px; }
  .pager button:disabled { background: #333a45; cursor: default; }
  .pager span { color: #9aa4b2; font-size: 13px; }

  /* --- remote disc setup wizard --- */
  .setupBtn { padding: 7px 12px; font-size: 13px; background: #2d6cdf; color: #fff; border: 0; border-radius: 6px; cursor: pointer; }
  .setupBtn:hover { background: #3b7bef; }
  .ctlRow { margin-top: 10px; display: flex; gap: 6px; flex-wrap: wrap; }
  .ctlBtn { padding: 7px 10px; font-size: 12px; background: #2b323d; color: #cdd5e0; border: 0; border-radius: 6px; cursor: pointer; }
  .ctlBtn:hover { background: #37404e; }
  .ctlBtn:disabled { opacity: .5; cursor: default; }
  .modalBack { position: fixed; inset: 0; background: rgba(0,0,0,.62); display: flex; align-items: flex-start; justify-content: center; padding: 5vh 12px; overflow-y: auto; z-index: 20; }
  .wiz { background: #1c2129; border: 1px solid #2b323d; border-radius: 10px; width: 640px; max-width: 100%; padding: 20px; }
  .wiz h2 { margin: 0 0 2px; font-size: 17px; }
  .wiz .sub2 { color: #9aa4b2; font-size: 12px; margin-bottom: 14px; }
  .wiz label { display: block; font-size: 12px; color: #8b95a4; margin: 10px 0 3px; }
  .wiz input[type=text], .wiz input[type=number], .wiz select { width: 100%; padding: 8px; font-size: 14px; background: #0f1216; color: #e6e9ef; border: 1px solid #2b323d; border-radius: 6px; }
  .wiz .row2 { display: flex; gap: 10px; } .wiz .row2 > div { flex: 1; }
  .wiz .btns { display: flex; justify-content: space-between; margin-top: 16px; }
  .wiz button { padding: 9px 16px; font-size: 14px; background: #2d6cdf; color: #fff; border: 0; border-radius: 6px; cursor: pointer; }
  .wiz button:hover { background: #3b7bef; }
  .wiz button.ghost { background: #2b323d; }
  .wiz button:disabled { background: #333a45; cursor: default; }
  .wiz .busy { color: #9aa4b2; font-size: 13px; margin-top: 12px; }
  .wiz .wErr { color: #ff8a8a; font-size: 13px; margin-top: 10px; min-height: 16px; word-break: break-word; }
  .wiz table { width: 100%; border-collapse: collapse; margin-top: 8px; font-size: 13px; }
  .wiz th, .wiz td { text-align: left; padding: 5px 8px; border-bottom: 1px solid #2b323d; }
  .wiz th { color: #8b95a4; font-size: 11px; text-transform: uppercase; letter-spacing: .04em; }
  .cands { max-height: 260px; overflow-y: auto; margin-top: 8px; border: 1px solid #2b323d; border-radius: 6px; }
  .cand { display: block; padding: 9px 10px; border-bottom: 1px solid #2b323d; cursor: pointer; font-size: 13px; }
  .cand:last-child { border-bottom: 0; }
  .cand:hover { background: #232936; }
  .cand input { margin-right: 8px; }
  .cand .cd { color: #9aa4b2; font-size: 12px; margin-top: 2px; }
  .okMsg { color: #56d364; font-size: 14px; margin-top: 12px; }
  .typeBtns { display: flex; gap: 10px; margin-top: 6px; }
  .typeBtns button { flex: 1; padding: 14px; }

  footer { border-top: 1px solid #2b323d; padding: 12px 20px; color: #7c8798; font-size: 11px; line-height: 1.5; }
</style>
</head>
<body>
  <div id='login' class='login hidden'>
    <h1>AutoRipper Dashboard</h1>
    <p>Enter the shared secret to monitor your fleet.</p>
    <input id='secret' type='password' placeholder='Shared secret' autocomplete='off'>
    <button id='loginBtn'>Log in</button>
    <div id='loginErr' class='err'></div>
  </div>

  <div id='dash' class='hidden'>
    <header>
      <div>
        <h1>AutoRipper Fleet</h1>
        <div class='sub'>Host: <span id='serverName'>-</span> &middot; Updated <span id='updated'>-</span></div>
      </div>
      <div class='headerRight'>
        <span id='count'>0 instances</span>
        <a href='#' id='logout'>Log out</a>
      </div>
    </header>
    <main id='tiles'></main>
    <div class='pager'>
      <button id='prev'>&lsaquo; Prev</button>
      <span id='pageInfo'>Page 1 of 1</span>
      <button id='next'>Next &rsaquo;</button>
    </div>
  </div>

  <div id='wizBack' class='modalBack hidden'>
    <div class='wiz'>
      <h2 id='wizTitle'>Set up a disc</h2>
      <div class='sub2' id='wizSub'></div>
      <div id='wizBody'></div>
      <div class='wErr' id='wizErr'></div>
      <div class='btns'>
        <button class='ghost' id='wizCancel'>Cancel</button>
        <span style='display:flex;gap:8px'>
          <button class='ghost hidden' id='wizPrev'>&lsaquo; Back</button>
          <button id='wizNext'>Next</button>
        </span>
      </div>
    </div>
  </div>

  <footer>
    AutoRipper &mdash; licensed under the GNU Affero General Public License, version 3 or later.
    This dashboard is a network service; its complete corresponding source code is available at
    <a href='https://github.com/jimmy952091/AutoRipper' target='_blank' rel='noopener'>github.com/jimmy952091/AutoRipper</a>.
    LAN only &mdash; do not port-forward.
  </footer>

<script>
(function () {
  var PAGE_SIZE = 4;
  var all = [];
  var page = 0;
  var serverNow = 0;
  var timer = null;

  // --- session ---
  // The login cookie can't be used when this page is EMBEDDED in another site's iframe (the
  // Home Assistant panel): browsers treat it as a third-party cookie and drop it, so every call
  // came back 401 and bounced you to the login card. So we also keep the session token here and
  // send it as a header, which no cookie policy can block. A normal tab works either way.
  var TOKEN = '';
  try { TOKEN = localStorage.getItem('ar_dash_token') || ''; } catch (e) { TOKEN = ''; }

  function saveToken(t) {
    TOKEN = t || '';
    try {
      if (TOKEN) { localStorage.setItem('ar_dash_token', TOKEN); }
      else { localStorage.removeItem('ar_dash_token'); }
    } catch (e) { /* private mode / storage disabled — the cookie still covers a normal tab */ }
  }

  // fetch options with the session header attached.
  function auth(opts) {
    opts = opts || {};
    opts.credentials = 'same-origin';
    opts.headers = opts.headers || {};
    if (TOKEN) { opts.headers['X-AR-Session'] = TOKEN; }
    return opts;
  }

  function $(id) { return document.getElementById(id); }
  function esc(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/'/g, '&#39;').replace(/\x22/g, '&quot;');
  }
  function show(view) {
    $('login').classList.toggle('hidden', view !== 'login');
    $('dash').classList.toggle('hidden', view !== 'dash');
  }

  function agoText(sentUnix) {
    var d = Math.max(0, serverNow - (sentUnix || 0));
    if (d < 5) { return 'just now'; }
    if (d < 60) { return d + 's ago'; }
    var m = Math.floor(d / 60);
    return m + 'm ago';
  }

  function pct(n) { n = parseInt(n, 10); if (isNaN(n) || n < 0) { n = 0; } if (n > 100) { n = 100; } return n; }

  function tileHtml(inst) {
    var role = inst.role || '';
    var badgeClass = 'badge';
    if (/server/i.test(role)) { badgeClass += ' server'; }
    else if (/ripper/i.test(role)) { badgeClass += ' ripper'; }

    var ago = agoText(inst.sentAtUnix);
    var stale = (serverNow - (inst.sentAtUnix || 0)) > 8 ? ' stale' : '';

    var conn = '';
    if (inst.remoteConnected === true) { conn = '<span class=\'dot on\'></span>connected'; }
    else if (inst.remoteConnected === false) { conn = '<span class=\'dot off\'></span>reconnecting'; }
    else if (inst.connectionInfo) { conn = esc(inst.connectionInfo); }

    var rip = inst.rip || {};
    var enc = inst.encode || {};

    var ripSection;
    if (rip.active) {
      ripSection = '<div class=\'line\'>' + esc(rip.current || 'Ripping') + '</div>' +
        '<div class=\'bar\'><span style=\'width:' + pct(rip.percent) + '%\'></span></div>' +
        '<div class=\'counts\'>' + pct(rip.percent) + '% &middot; ' + esc(rip.operation || '') +
        (rip.queued ? ' &middot; ' + rip.queued + ' queued' : '') + '</div>';
    } else {
      ripSection = '<div class=\'idle\'>Idle' + (rip.queued ? ' &middot; ' + rip.queued + ' queued' : '') + '</div>';
    }

    var encSection;
    if (enc.active) {
      encSection = '<div class=\'line\'>' + esc(enc.current || 'Encoding') + '</div>' +
        '<div class=\'bar enc\'><span style=\'width:' + pct(enc.percent) + '%\'></span></div>' +
        '<div class=\'counts\'>' + pct(enc.percent) + '% &middot; ' + esc(enc.operation || '') + '</div>';
    } else {
      encSection = '<div class=\'idle\'>Idle</div>';
    }
    var encCounts = '<div class=\'counts\'>' + (enc.queued || 0) + ' queued &middot; ' +
      (enc.done || 0) + ' done &middot; ' + (enc.failed || 0) + ' failed</div>';

    var errLine = inst.lastError ? '<div class=\'errline\'>Last error: ' + esc(inst.lastError) + '</div>' : '';

    // Phase B: instances that allow it get a remote disc-setup button plus quick controls
    // (Stop rip only while one is running; Retry/Eject always — they answer sensibly if idle).
    var setupBtn = '';
    if (inst.allowsRemoteSetup) {
      var iid = esc(inst.instanceId || '');
      setupBtn = '<div class=\'ctlRow\'>' +
        '<button class=\'setupBtn\' data-inst=\'' + iid + '\'>Set up disc&hellip;</button>' +
        (rip.active ? '<button class=\'ctlBtn\' data-act=\'stopRip\' data-inst=\'' + iid + '\'>Stop rip</button>' : '') +
        '<button class=\'ctlBtn\' data-act=\'retryFailed\' data-inst=\'' + iid + '\'>Retry failed</button>' +
        '<button class=\'ctlBtn\' data-act=\'eject\' data-inst=\'' + iid + '\'>Eject</button>' +
        '</div>';
    }

    return '<div class=\'tile' + stale + '\'>' +
      '<div class=\'tileHead\'><span class=\'name\'>' + esc(inst.machineName || '?') + '</span>' +
        '<span class=\'' + badgeClass + '\'>' + esc(role) + '</span></div>' +
      '<div class=\'meta\'>v' + esc(inst.appVersion || '?') + ' &middot; ' + esc(ago) +
        (conn ? ' &middot; ' + conn : '') + '</div>' +
      '<div class=\'section\'><div class=\'label\'>Rip</div>' + ripSection + '</div>' +
      '<div class=\'section\'><div class=\'label\'>Encode</div>' + encSection + encCounts + '</div>' +
      errLine + setupBtn +
      '</div>';
  }

  function render() {
    var pages = Math.max(1, Math.ceil(all.length / PAGE_SIZE));
    if (page >= pages) { page = pages - 1; }
    if (page < 0) { page = 0; }
    var start = page * PAGE_SIZE;
    var slice = all.slice(start, start + PAGE_SIZE);

    $('tiles').innerHTML = slice.map(tileHtml).join('') ||
      '<div class=\'idle\' style=\'padding:20px\'>No instances are reporting yet.</div>';
    $('count').textContent = all.length + (all.length === 1 ? ' instance' : ' instances');
    $('pageInfo').textContent = 'Page ' + (page + 1) + ' of ' + pages;
    $('prev').disabled = (page <= 0);
    $('next').disabled = (page >= pages - 1);
  }

  function load() {
    fetch('/api/status', auth()).then(function (r) {
      if (r.status === 401) { stopPolling(); saveToken(''); show('login'); return null; }
      return r.json();
    }).then(function (data) {
      if (!data || !data.ok) { return; }
      all = data.instances || [];
      serverNow = data.generatedAtUnix || 0;
      $('serverName').textContent = data.server || '-';
      var d = new Date();
      $('updated').textContent = d.toLocaleTimeString();
      show('dash');
      render();
    }).catch(function () { /* transient; next tick retries */ });
  }

  function startPolling() { if (!timer) { timer = setInterval(load, 2000); } }
  function stopPolling() { if (timer) { clearInterval(timer); timer = null; } }

  function doLogin() {
    var secret = $('secret').value || '';
    $('loginErr').textContent = '';
    fetch('/login', {
      method: 'POST', credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ secret: secret })
    }).then(function (r) {
      if (r.status !== 200) { $('loginErr').textContent = 'That secret was not accepted.'; return null; }
      return r.json();
    }).then(function (data) {
      if (!data) { return; }
      saveToken(data.token);   // keeps us logged in even where cookies are blocked
      $('secret').value = '';
      startPolling();
      load();
    }).catch(function () { $('loginErr').textContent = 'Could not reach the dashboard host.'; });
  }

  // ================= remote disc setup wizard (Phase B) =================

  var W = null; // wizard state; null when closed

  // Enqueue a command for an instance, then poll until it finishes. alive() lets the caller
  // abandon polling (e.g. the wizard was closed).
  function cmdApi(instId, action, args, alive, cb) {
    fetch('/api/do', auth({
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ instanceId: instId, action: action, args: args || {} })
    })).then(function (r) { return r.json(); }).then(function (d) {
      if (!d || !d.ok) { cb(false, (d && d.error) || 'Request failed.', null); return; }
      var deadline = Date.now() + 6 * 60 * 1000;
      var t = setInterval(function () {
        if (alive && !alive()) { clearInterval(t); return; }
        fetch('/api/cmd?id=' + encodeURIComponent(d.id), auth())
          .then(function (r) { return r.json(); })
          .then(function (s) {
            if (!s) { return; }
            if (s.done) { clearInterval(t); cb(!!s.ok, s.error || '', s.result || {}); }
            else if (Date.now() > deadline) { clearInterval(t); cb(false, 'Timed out waiting for the machine.', null); }
          }).catch(function () { /* transient; next tick */ });
      }, 1000);
    }).catch(function () { cb(false, 'Could not reach the dashboard host.', null); });
  }

  // The wizard's flavor: bound to the open wizard's instance, stops polling if it's closed.
  function api(action, args, cb) {
    cmdApi(W.instId, action, args, function () { return !!W; }, cb);
  }

  function wizErr(msg) { $('wizErr').textContent = msg || ''; }
  function wizBody(html) { $('wizBody').innerHTML = html; wizErr(''); }
  function nextBtn(label, enabled) {
    var b = $('wizNext');
    b.classList.toggle('hidden', !label);
    b.textContent = label || '';
    b.disabled = !enabled;
  }
  // Each step registers where its Back button goes (null = no Back on this step) — like
  // closing the window at the machine, but without losing the scan.
  function prevBtn(fn) {
    if (W) { W.back = fn || null; }
    $('wizPrev').classList.toggle('hidden', !fn);
  }
  function busy(text) { wizBody('<div class=\'busy\'>' + esc(text) + '</div>'); nextBtn('', false); prevBtn(null); }

  function openWizard(instId, machine) {
    W = { instId: instId, machine: machine, step: 'start' };
    $('wizBack').classList.remove('hidden');
    $('wizSub').textContent = 'On ' + machine;
    $('wizCancel').textContent = 'Cancel';
    wizBody('<div>This scans the disc currently in <b>' + esc(machine) + '</b> with MakeMKV. ' +
      'A DVD takes a moment; a Blu-ray can take a few minutes.</div>');
    nextBtn('Scan disc', true);
  }

  function closeWizard() { $('wizBack').classList.add('hidden'); W = null; }

  // ---- step: scan ----

  function runScan() {
    busy('Scanning the disc on ' + W.machine + '…');
    api('scan', {}, function (ok, err, result) {
      if (!W) { return; }
      if (!ok) { openWizard(W.instId, W.machine); wizErr(err); return; }
      // Keep the disc's label in the header so every later step (search, pick, review) still
      // says which disc is being set up.
      $('wizSub').textContent = 'On ' + W.machine + ' — ' + (result.discName || 'disc');
      if (result.kind === 'audioCd') { W.cd = result; runMusicLookup(null, null); return; }
      W.scan = result;
      stepTitles();
    });
  }

  function stepTitles() {
    W.step = 'titles';
    var s = W.scan;
    var rows = '';
    (s.titles || []).forEach(function (t) {
      rows += '<tr><td>Title ' + t.index + '</td><td>' + esc(t.duration || '?') + '</td><td>' +
        (t.chapters || 0) + ' ch</td><td>' + (t.sizeGb ? t.sizeGb + ' GB' : '') + '</td></tr>';
    });
    var presets = '';
    (s.presets || []).forEach(function (p) {
      presets += '<option value=\'' + esc(p.name) + '\'>' + esc(p.label) + '</option>';
    });
    var mock = s.providerLive ? '' :
      '<div class=\'wErr\'>Note: no API keys are set on that machine — lookups will return TEST data.</div>';

    wizBody(
      '<div><b>' + esc(s.discName || 'Disc') + '</b> &mdash; ' + (s.titles || []).length + ' title(s)</div>' +
      '<table><tr><th>Title</th><th>Length</th><th>Chapters</th><th>Size</th></tr>' + rows + '</table>' +
      '<div class=\'row2\'><div><label>Disc type</label><select id=\'wDiscType\'>' +
        '<option value=\'Dvd\'>DVD</option><option value=\'BluRay\'>Blu-ray</option>' +
        '<option value=\'UhdBluRay\'>4K UHD Blu-ray</option></select></div>' +
      '<div><label>Encode preset</label><select id=\'wPreset\'>' + presets + '</select></div></div>' +
      mock +
      '<label>What is this disc?</label>' +
      '<div class=\'typeBtns\'><button id=\'wMovie\'>One movie</button>' +
      '<button id=\'wMulti\'>Two+ movies</button><button id=\'wTv\'>A TV show</button></div>');
    nextBtn('', false);
    prevBtn(null);

    // Coming BACK to this step restores the disc type / preset the user already chose.
    if (W.discType) { $('wDiscType').value = W.discType; }
    if (W.presetName) { $('wPreset').value = W.presetName; }

    // UHD disc type auto-picks a UHD preset (and back), so 4K never encodes with the SD preset
    // by accident. The user can still override.
    $('wDiscType').addEventListener('change', function () {
      var uhd = this.value === 'UhdBluRay';
      var sel = $('wPreset');
      for (var i = 0; i < sel.options.length; i++) {
        var isUhd = sel.options[i].text.indexOf('UHD') === 0;
        if (uhd === isUhd) { sel.selectedIndex = i; break; }
      }
    });
    $('wMovie').addEventListener('click', function () {
      captureTitleStep(); W.multi = null; W.tv = null; stepMovieSearch();
    });
    $('wMulti').addEventListener('click', function () {
      captureTitleStep(); W.movie = null; W.tv = null;
      if (!W.multi) { W.multi = { assign: {}, exclude: {} }; }
      stepMultiList();
    });
    $('wTv').addEventListener('click', function () {
      captureTitleStep(); W.multi = null; W.movie = null; stepTvSearch();
    });
  }

  function captureTitleStep() {
    W.discType = $('wDiscType').value;
    W.presetName = $('wPreset').value || '';
  }

  // ---- movie flow ----

  function stepMovieSearch(prefTitle, prefYear) {
    W.step = 'movie-search';
    wizBody('<label>Movie title</label><input type=\'text\' id=\'wMTitle\' value=\'' + esc(prefTitle || '') + '\'>' +
      '<div class=\'row2\'><div><label>Year (optional, helps disambiguate)</label>' +
      '<input type=\'text\' id=\'wMYear\' value=\'' + esc(prefYear || '') + '\'></div><div></div></div>');
    nextBtn('Search', true);
    prevBtn(function () { stepTitles(); });
  }

  function runMovieSearch() {
    var title = $('wMTitle').value.trim();
    var year = $('wMYear').value.trim();
    if (!title) { wizErr('Enter the movie title first.'); return; }
    W.movie = { q: title, qy: year };
    busy('Searching on ' + W.machine + '…');
    api('searchMovies', { title: title, year: year }, function (ok, err, result) {
      if (!W) { return; }
      if (!ok) { stepMovieSearch(title, year); wizErr(err); return; }
      var cands = (result && result.candidates) || [];
      if (!cands.length) { stepMovieSearch(title, year); wizErr('No matches — check the spelling or try without the year.'); return; }
      W.movie.cands = cands;
      stepMoviePick();
    });
  }

  function stepMoviePick() {
    W.step = 'movie-pick';
    var html = '<div>Pick the correct movie — nothing is processed until you confirm it. ' +
      'Not it? Go Back and search a different name.</div><div class=\'cands\'>';
    W.movie.cands.forEach(function (c, i) {
      var checked = (W.movie.id && c.id === W.movie.id) ? ' checked' : '';
      html += '<label class=\'cand\'><input type=\'radio\' name=\'wCand\' value=\'' + i + '\'' + checked + '>' +
        '<b>' + esc(c.title) + (c.year ? ' (' + esc(c.year) + ')' : '') + '</b>' +
        (c.detail ? '<div class=\'cd\'>' + esc(c.detail) + '</div>' : '') + '</label>';
    });
    html += '</div>';
    wizBody(html);
    nextBtn('Confirm this match', true);
    prevBtn(function () { stepMovieSearch(W.movie.q, W.movie.qy); });
  }

  function confirmMovie() {
    var sel = document.querySelector('input[name=wCand]:checked');
    if (!sel) { wizErr('Pick the correct movie from the list first.'); return; }
    var c = W.movie.cands[parseInt(sel.value, 10)];
    W.movie.title = c.title; W.movie.year = c.year; W.movie.id = c.id;
    stepReview();
  }

  // ---- TV flow ----

  function stepTvSearch(prefName) {
    W.step = 'tv-search';
    wizBody('<label>Show name</label><input type=\'text\' id=\'wTName\' value=\'' + esc(prefName || '') + '\'>');
    nextBtn('Search', true);
    prevBtn(function () { stepTitles(); });
  }

  function runTvSearch() {
    var name = $('wTName').value.trim();
    if (!name) { wizErr('Enter the show name first.'); return; }
    W.tv = { q: name };
    busy('Searching on ' + W.machine + '…');
    api('searchSeries', { name: name }, function (ok, err, result) {
      if (!W) { return; }
      if (!ok) { stepTvSearch(name); wizErr(err); return; }
      var cands = (result && result.candidates) || [];
      if (!cands.length) { stepTvSearch(name); wizErr('No matches — check the spelling.'); return; }
      W.tv.cands = cands;
      stepTvPick();
    });
  }

  function stepTvPick() {
    W.step = 'tv-pick';
    var html = '<div>Pick the correct show — nothing is processed until you confirm it. ' +
      'Not it? Go Back and search a different name.</div><div class=\'cands\'>';
    W.tv.cands.forEach(function (c, i) {
      var checked = (W.tv.seriesId && c.id === W.tv.seriesId) ? ' checked' : '';
      html += '<label class=\'cand\'><input type=\'radio\' name=\'wCand\' value=\'' + i + '\'' + checked + '>' +
        '<b>' + esc(c.title) + (c.year ? ' (' + esc(c.year) + ')' : '') + '</b>' +
        (c.detail ? '<div class=\'cd\'>' + esc(c.detail) + '</div>' : '') + '</label>';
    });
    html += '</div>' +
      '<div class=\'row2\'><div><label>Season</label><input type=\'number\' id=\'wSeason\' min=\'0\' value=\'' +
      (W.tv.season >= 0 ? W.tv.season : 1) + '\'></div>' +
      '<div><label>Disc # in season</label><input type=\'number\' id=\'wDiscNum\' min=\'1\' value=\'' +
      (W.tv.discNumber || 1) + '\'></div>' +
      '<div><label>Episode order</label><select id=\'wOrder\'><option value=\'Aired\'>Aired</option>' +
      '<option value=\'Dvd\'>DVD</option><option value=\'Absolute\'>Absolute</option></select></div></div>';
    wizBody(html);
    if (W.tv.order) { $('wOrder').value = W.tv.order; }
    nextBtn('Load episodes', true);
    prevBtn(function () { stepTvSearch(W.tv.q); });
  }

  function runEpisodes() {
    var sel = document.querySelector('input[name=wCand]:checked');
    if (!sel) { wizErr('Pick the correct show from the list first.'); return; }
    var c = W.tv.cands[parseInt(sel.value, 10)];
    W.tv.seriesId = c.id; W.tv.showName = c.title;
    W.tv.season = parseInt($('wSeason').value, 10);
    W.tv.discNumber = parseInt($('wDiscNum').value, 10) || 1;
    W.tv.order = $('wOrder').value;
    if (isNaN(W.tv.season) || W.tv.season < 0) { wizErr('Enter the season number.'); return; }
    busy('Loading the season ' + W.tv.season + ' episode list…');
    api('episodes', { seriesId: W.tv.seriesId, season: W.tv.season, order: W.tv.order },
      function (ok, err, result) {
        if (!W) { return; }
        if (!ok) { stepTvPick(); wizErr(err); return; }
        var eps = (result && result.episodes) || [];
        if (!eps.length) { stepTvPick(); wizErr('That season has no episodes listed — check the season number/order.'); return; }
        if (result.seriesName) { W.tv.showName = result.seriesName; }
        W.tv.episodes = eps;
        // Suggest the starting episode from the disc number: discs of a season usually hold
        // equal shares of it — the same suggestion logic as the desktop screen, still editable.
        var perDisc = Math.ceil(eps.length / Math.max(1, W.tv.discNumber));
        var start = (W.tv.discNumber - 1) * Math.min(perDisc, Math.floor(eps.length / Math.max(1, W.tv.discNumber)) || eps.length);
        W.tv.firstIdx = Math.min(Math.max(0, start), eps.length - 1);
        W.tv.segments = 1;
        W.tv.exclude = {};
        stepMap();
      });
  }

  function stepMap() {
    W.step = 'map';
    var eps = W.tv.episodes;
    var opts = '';
    eps.forEach(function (e, i) {
      var code = 'S' + pad2(e.season) + 'E' + pad2(e.episode);
      opts += '<option value=\'' + i + '\'' + (i === W.tv.firstIdx ? ' selected' : '') + '>' +
        code + (e.name ? ' — ' + esc(e.name) : '') + '</option>';
    });
    var html = '<div><b>' + esc(W.tv.showName) + '</b> — season ' + W.tv.season +
      ', disc ' + W.tv.discNumber + ' (' + eps.length + ' episodes in season)</div>' +
      '<div class=\'row2\'><div><label>First episode on this disc</label><select id=\'wFirst\'>' + opts + '</select></div>' +
      '<div><label>Segments per title (2 = two cartoon shorts per file)</label>' +
      '<input type=\'number\' id=\'wSeg\' min=\'1\' max=\'6\' value=\'' + W.tv.segments + '\'></div></div>' +
      '<table id=\'wMapTable\'><tr><th>Include</th><th>Title</th><th>Length</th><th>Becomes</th></tr></table>' +
      '<div class=\'busy\'>Untick special features / play-all titles so they are not mislabeled as episodes.</div>';
    wizBody(html);
    nextBtn('Review', true);
    prevBtn(function () { stepTvPick(); });
    $('wFirst').addEventListener('change', function () { W.tv.firstIdx = parseInt(this.value, 10) || 0; renderMap(); });
    $('wSeg').addEventListener('change', function () {
      W.tv.segments = Math.max(1, Math.min(6, parseInt(this.value, 10) || 1)); renderMap();
    });
    renderMap();
  }

  function pad2(n) { return (n < 10 ? '0' : '') + n; }
  function pad3(n) { return (n < 100 ? (n < 10 ? '00' : '0') : '') + n; }

  // Sequentially assigns episodes to included titles (W.tv.segments per title), computing the
  // preview text and the exact mappings the Process command will send.
  function computeMapping() {
    var eps = W.tv.episodes;
    var cursor = W.tv.firstIdx;
    var out = [];
    (W.scan.titles || []).forEach(function (t) {
      var included = !W.tv.exclude[t.index];
      var m = { titleIndex: t.index, include: included, episodes: [] };
      var text = '— excluded —';
      if (included) {
        var take = [];
        for (var k = 0; k < W.tv.segments && cursor < eps.length; k++) {
          take.push(eps[cursor]); cursor++;
        }
        if (take.length === 0) {
          text = 'PAST END OF SEASON — exclude this title';
          m.include = false; m.shortfall = true;
        } else {
          m.episodes = take.map(function (e) { return { season: e.season, episode: e.episode, name: e.name }; });
          var code = 'S' + pad2(W.tv.season) + 'E' + pad3(take[0].episode);
          if (take.length > 1) { code += '-E' + pad3(take[take.length - 1].episode); }
          text = code + (take[0].name ? ' — ' + take[0].name : '');
          if (m.shortfallPartial) { text += ' (short)'; }
        }
      }
      m.preview = text;
      out.push(m);
    });
    return out;
  }

  function renderMap() {
    var table = $('wMapTable');
    var maps = computeMapping();
    var rows = '<tr><th>Include</th><th>Title</th><th>Length</th><th>Becomes</th></tr>';
    (W.scan.titles || []).forEach(function (t, i) {
      var m = maps[i];
      rows += '<tr><td><input type=\'checkbox\' class=\'wInc\' data-idx=\'' + t.index + '\'' +
        (W.tv.exclude[t.index] ? '' : ' checked') + '></td>' +
        '<td>Title ' + t.index + '</td><td>' + esc(t.duration || '?') + '</td>' +
        '<td>' + esc(m.preview) + '</td></tr>';
    });
    table.innerHTML = rows;
    var incs = table.querySelectorAll('.wInc');
    for (var i = 0; i < incs.length; i++) {
      incs[i].addEventListener('change', function () {
        var idx = parseInt(this.getAttribute('data-idx'), 10);
        if (this.checked) { delete W.tv.exclude[idx]; } else { W.tv.exclude[idx] = true; }
        renderMap();
      });
    }
    // Valid only if at least one title maps to episodes and nothing runs past the season.
    var okCount = 0, bad = false;
    maps.forEach(function (m) { if (m.include && m.episodes.length) { okCount++; } if (m.shortfall && !W.tv.exclude[m.titleIndex]) { bad = true; } });
    W.tv.mappings = maps;
    nextBtn('Review', okCount > 0 && !bad);
  }

  // ---- multi-movie flow (double features: each title is its own confirmed film) ----

  function stepMultiList() {
    W.step = 'multi-list';
    var rows = '<tr><th>Include</th><th>Title</th><th>Length</th><th>Movie</th><th></th></tr>';
    (W.scan.titles || []).forEach(function (t) {
      var a = W.multi.assign[t.index];
      var name = a ? esc(a.title) + (a.year ? ' (' + esc(a.year) + ')' : '') : '<i>none yet</i>';
      rows += '<tr><td><input type=\'checkbox\' class=\'wMInc\' data-idx=\'' + t.index + '\'' +
        (W.multi.exclude[t.index] ? '' : ' checked') + '></td>' +
        '<td>Title ' + t.index + '</td><td>' + esc(t.duration || '?') + '</td>' +
        '<td>' + name + '</td>' +
        '<td><button class=\'ghost wMPick\' data-idx=\'' + t.index + '\'>Choose&hellip;</button></td></tr>';
    });
    wizBody('<div>Assign each included title its movie (a double feature = two titles, two films). ' +
      'Untick trailers/extras.</div><table>' + rows + '</table>');
    prevBtn(function () { stepTitles(); });

    var incs = document.querySelectorAll('.wMInc');
    for (var i = 0; i < incs.length; i++) {
      incs[i].addEventListener('change', function () {
        var idx = parseInt(this.getAttribute('data-idx'), 10);
        if (this.checked) { delete W.multi.exclude[idx]; } else { W.multi.exclude[idx] = true; }
        stepMultiList();
      });
    }
    var picks = document.querySelectorAll('.wMPick');
    for (var j = 0; j < picks.length; j++) {
      picks[j].addEventListener('click', function () {
        W.multi.cur = parseInt(this.getAttribute('data-idx'), 10);
        stepMultiSearch();
      });
    }

    // Ready when every included title has a movie and at least one is included.
    var ready = 0, missing = 0;
    (W.scan.titles || []).forEach(function (t) {
      if (W.multi.exclude[t.index]) { return; }
      if (W.multi.assign[t.index]) { ready++; } else { missing++; }
    });
    nextBtn('Review', ready > 0 && missing === 0);
    if (missing > 0) { wizErr(missing + ' included title(s) still need a movie chosen (or untick them).'); }
  }

  function stepMultiSearch(prefTitle, prefYear) {
    W.step = 'multi-search';
    wizBody('<div>Movie for <b>Title ' + W.multi.cur + '</b>:</div>' +
      '<label>Movie title</label><input type=\'text\' id=\'wMTitle\' value=\'' + esc(prefTitle || '') + '\'>' +
      '<div class=\'row2\'><div><label>Year (optional)</label>' +
      '<input type=\'text\' id=\'wMYear\' value=\'' + esc(prefYear || '') + '\'></div><div></div></div>');
    nextBtn('Search', true);
    prevBtn(function () { stepMultiList(); });
  }

  function runMultiSearch() {
    var title = $('wMTitle').value.trim();
    var year = $('wMYear').value.trim();
    if (!title) { wizErr('Enter the movie title first.'); return; }
    W.multi.q = title; W.multi.qy = year;
    busy('Searching on ' + W.machine + '…');
    api('searchMovies', { title: title, year: year }, function (ok, err, result) {
      if (!W) { return; }
      if (!ok) { stepMultiSearch(title, year); wizErr(err); return; }
      var cands = (result && result.candidates) || [];
      if (!cands.length) { stepMultiSearch(title, year); wizErr('No matches — check the spelling or try without the year.'); return; }
      W.multi.cands = cands;
      stepMultiPick();
    });
  }

  function stepMultiPick() {
    W.step = 'multi-pick';
    var html = '<div>Pick the movie on <b>Title ' + W.multi.cur + '</b>:</div><div class=\'cands\'>';
    W.multi.cands.forEach(function (c, i) {
      html += '<label class=\'cand\'><input type=\'radio\' name=\'wCand\' value=\'' + i + '\'>' +
        '<b>' + esc(c.title) + (c.year ? ' (' + esc(c.year) + ')' : '') + '</b>' +
        (c.detail ? '<div class=\'cd\'>' + esc(c.detail) + '</div>' : '') + '</label>';
    });
    wizBody(html + '</div>');
    nextBtn('Assign to Title ' + W.multi.cur, true);
    prevBtn(function () { stepMultiSearch(W.multi.q, W.multi.qy); });
  }

  function confirmMultiPick() {
    var sel = document.querySelector('input[name=wCand]:checked');
    if (!sel) { wizErr('Pick the correct movie from the list first.'); return; }
    var c = W.multi.cands[parseInt(sel.value, 10)];
    W.multi.assign[W.multi.cur] = { title: c.title, year: c.year, id: c.id };
    stepMultiList();
  }

  // ---- audio CD flow (MusicBrainz + track checkboxes, remotely) ----

  function runMusicLookup(artist, album) {
    W.step = 'music-lookup';
    busy(artist || album ? 'Searching MusicBrainz…' : 'Audio CD — identifying on MusicBrainz…');
    var args = { scanId: W.cd.scanId };
    if (artist) { args.artist = artist; }
    if (album) { args.album = album; }
    api('musicLookup', args, function (ok, err, result) {
      if (!W) { return; }
      if (!ok) { stepMusicSearch(); wizErr(err); return; }
      var rel = (result && result.releases) || [];
      if (!rel.length) {
        stepMusicSearch();
        wizErr('No release matched — try typing the artist and album.');
        return;
      }
      W.cdRel = rel;
      stepMusicPick();
    });
  }

  function stepMusicSearch(prefArtist, prefAlbum) {
    W.step = 'music-search';
    wizBody('<div>Audio CD (' + W.cd.trackCount + ' tracks). Search MusicBrainz by name:</div>' +
      '<div class=\'row2\'><div><label>Artist</label><input type=\'text\' id=\'wArtist\' value=\'' + esc(prefArtist || '') + '\'></div>' +
      '<div><label>Album</label><input type=\'text\' id=\'wAlbum\' value=\'' + esc(prefAlbum || '') + '\'></div></div>');
    nextBtn('Search', true);
    prevBtn(W.cdRel && W.cdRel.length ? function () { stepMusicPick(); } : null);
  }

  function stepMusicPick() {
    W.step = 'music-pick';
    var html = '<div>Pick the correct release — a reissue can have a different track list:</div><div class=\'cands\'>';
    W.cdRel.forEach(function (r, i) {
      var discBit = r.discCount > 1 ? ' &middot; disc ' + r.discNumber + '/' + r.discCount : '';
      html += '<label class=\'cand\'><input type=\'radio\' name=\'wCand\' value=\'' + i + '\'>' +
        '<b>' + esc(r.artist) + ' — ' + esc(r.album) + (r.year ? ' (' + esc(r.year) + ')' : '') + '</b>' +
        '<div class=\'cd\'>' + r.tracks.length + ' tracks' + discBit +
        (r.detail ? ' &middot; ' + esc(r.detail) : '') + '</div></label>';
    });
    wizBody(html + '</div>');
    nextBtn('Choose tracks', true);
    prevBtn(function () { stepMusicSearch(); });
  }

  function stepMusicTracks() {
    var sel = document.querySelector('input[name=wCand]:checked');
    if (W.step === 'music-pick') {
      if (!sel) { wizErr('Pick the correct release from the list first.'); return; }
      W.cdPick = parseInt(sel.value, 10);
    }
    W.step = 'music-tracks';
    var r = W.cdRel[W.cdPick];
    var opts = '';
    (W.cd.formats || []).forEach(function (f) {
      var chosen = f.id === (W.cd.defaultFormatId || 'flac') ? ' selected' : '';
      opts += '<option value=\'' + esc(f.id) + '\'' + chosen + '>' + esc(f.label) + '</option>';
    });
    var rows = '<tr><th>Rip</th><th>#</th><th>Track</th><th>Length</th></tr>';
    r.tracks.forEach(function (t) {
      rows += '<tr><td><input type=\'checkbox\' class=\'wTrk\' data-n=\'' + t.n + '\' checked></td>' +
        '<td>' + t.n + '</td><td>' + esc(t.title) + '</td><td>' + esc(t.len) + '</td></tr>';
    });
    wizBody('<div><b>' + esc(r.artist) + ' — ' + esc(r.album) + '</b></div>' +
      '<label>Output format</label><select id=\'wFmt\'>' + opts + '</select>' +
      '<label>If a track file already exists in the library</label><select id=\'wConf\'>' +
      '<option value=\'keepBoth\'>Keep both — save the new one with a (2) suffix</option>' +
      '<option value=\'overwrite\'>Overwrite the existing file</option>' +
      '<option value=\'skip\'>Skip that track</option></select>' +
      '<div class=\'typeBtns\' style=\'margin-top:8px\'>' +
      '<button class=\'ghost\' id=\'wAllOn\'>Select all</button>' +
      '<button class=\'ghost\' id=\'wAllOff\'>Deselect all</button></div>' +
      '<table>' + rows + '</table>');
    prevBtn(function () { stepMusicPick(); });

    function refresh() {
      var n = document.querySelectorAll('.wTrk:checked').length;
      nextBtn('Rip ' + n + ' track' + (n === 1 ? '' : 's'), n > 0);
    }
    var boxes = document.querySelectorAll('.wTrk');
    for (var i = 0; i < boxes.length; i++) { boxes[i].addEventListener('change', refresh); }
    $('wAllOn').addEventListener('click', function () {
      for (var i = 0; i < boxes.length; i++) { boxes[i].checked = true; } refresh();
    });
    $('wAllOff').addEventListener('click', function () {
      for (var i = 0; i < boxes.length; i++) { boxes[i].checked = false; } refresh();
    });
    refresh();
  }

  function runMusicProcess() {
    var tracks = [];
    var boxes = document.querySelectorAll('.wTrk:checked');
    for (var i = 0; i < boxes.length; i++) { tracks.push(parseInt(boxes[i].getAttribute('data-n'), 10)); }
    var fmt = $('wFmt').value;
    var conf = $('wConf').value;
    var r = W.cdRel[W.cdPick];
    busy('Queueing on ' + W.machine + '…');
    api('process', {
      scanId: W.cd.scanId, confirmed: true, mediaType: 'music',
      releaseIndex: W.cdPick, formatId: fmt, tracks: tracks,
      conflict: conf
    }, function (ok, err, result) {
      if (!W) { return; }
      if (!ok) { W.step = 'music-tracks'; stepMusicTracksRestore(err); return; }
      W.step = 'done';
      wizBody('<div class=\'okMsg\'>Queued! ' + esc((result && result.discLabel) || (r.artist + ' — ' + r.album)) +
        ' is now ripping on ' + esc(W.machine) + '.</div>');
      nextBtn('', false);
      prevBtn(null);
      $('wizCancel').textContent = 'Close';
    });
  }

  function stepMusicTracksRestore(err) {
    // Re-render the tracks step after a failed process, keeping the release choice.
    W.step = 'music-pick-restore';
    stepMusicTracks();
    wizErr(err);
  }

  // ---- review + process ----

  function stepReview() {
    W.step = 'review';
    var html;
    if (W.multi) {
      var mLines = '';
      (W.scan.titles || []).forEach(function (t) {
        var a = W.multi.assign[t.index];
        if (!W.multi.exclude[t.index] && a) {
          mLines += '<div>Title ' + t.index + ' → ' + esc(a.title) + (a.year ? ' (' + esc(a.year) + ')' : '') + '</div>';
        }
      });
      html = '<div><b>Multi-movie disc:</b></div>' + mLines +
        '<div class=\'busy\'>Each film lands in its own Movies folder. Preset “' + esc(W.presetName) + '”.</div>';
    } else if (W.movie) {
      html = '<div><b>Movie:</b> ' + esc(W.movie.title) + (W.movie.year ? ' (' + esc(W.movie.year) + ')' : '') +
        '</div><div class=\'busy\'>Confirmed match: ' + esc(W.movie.id) +
        '. The longest title on the disc will be ripped and encoded with preset “' + esc(W.presetName) +
        '”, then placed in the Movies library on that machine’s configured route.</div>';
    } else {
      var lines = '';
      W.tv.mappings.forEach(function (m) {
        if (m.include && m.episodes.length) { lines += '<div>Title ' + m.titleIndex + ' → ' + esc(m.preview) + '</div>'; }
      });
      html = '<div><b>TV:</b> ' + esc(W.tv.showName) + ' — Season ' + W.tv.season +
        ', disc ' + W.tv.discNumber + '</div>' + lines +
        '<div class=\'busy\'>Preset “' + esc(W.presetName) + '”. Excluded titles are skipped.</div>';
    }
    wizBody(html + '<div class=\'busy\'>Processing queues the rip on ' + esc(W.machine) +
      ' — exactly as if you clicked Process at that machine.</div>');
    nextBtn('Process', true);
    prevBtn(function () {
      if (W.multi) { stepMultiList(); }
      else if (W.movie) { stepMoviePick(); }
      else { stepMap(); }
    });
  }

  function runProcess() {
    var args = {
      scanId: W.scan.scanId, confirmed: true,
      discType: W.discType, presetName: W.presetName
    };
    if (W.multi) {
      args.mediaType = 'multimovie';
      args.mappings = (W.scan.titles || []).map(function (t) {
        var a = W.multi.assign[t.index];
        var inc = !W.multi.exclude[t.index] && !!a;
        return {
          titleIndex: t.index, include: inc,
          movieTitle: a ? a.title : '', movieYear: a ? a.year : '', imdbId: a ? a.id : ''
        };
      });
    } else if (W.movie) {
      args.mediaType = 'movie';
      args.movieTitle = W.movie.title; args.movieYear = W.movie.year; args.imdbId = W.movie.id;
    } else {
      args.mediaType = 'tv';
      args.showName = W.tv.showName; args.seriesId = W.tv.seriesId;
      args.season = W.tv.season; args.discNumber = W.tv.discNumber;
      args.mappings = W.tv.mappings.map(function (m) {
        return { titleIndex: m.titleIndex, include: m.include && m.episodes.length > 0, episodes: m.episodes };
      });
    }
    busy('Queueing on ' + W.machine + '…');
    api('process', args, function (ok, err, result) {
      if (!W) { return; }
      if (!ok) { stepReview(); wizErr(err); return; }
      W.step = 'done';
      wizBody('<div class=\'okMsg\'>Queued! ' + esc((result && result.discLabel) || '') +
        ' is now ripping on ' + esc(W.machine) + ' — watch its tile for progress.</div>');
      nextBtn('', false);
      $('wizCancel').textContent = 'Close';
    });
  }

  // ---- wiring ----

  $('tiles').addEventListener('click', function (e) {
    // Quick controls: fire-and-report, no wizard.
    var c = e.target.closest ? e.target.closest('.ctlBtn') : null;
    if (c) {
      var act = c.getAttribute('data-act');
      var cid = c.getAttribute('data-inst');
      if (act === 'stopRip' &&
          !confirm('Stop the current rip on this machine? The unfinished title is marked failed ' +
                   '(titles already ripped are kept).')) { return; }
      c.disabled = true;
      cmdApi(cid, act, {}, null, function (ok, err, res) {
        alert(ok ? ((res && res.message) || 'Done.') : err);
      });
      return;
    }

    var b = e.target.closest ? e.target.closest('.setupBtn') : null;
    if (!b) { return; }
    var instId = b.getAttribute('data-inst');
    var inst = null;
    all.forEach(function (s) { if (s.instanceId === instId) { inst = s; } });
    openWizard(instId, inst ? inst.machineName : instId);
  });

  $('wizCancel').addEventListener('click', closeWizard);
  $('wizPrev').addEventListener('click', function () { if (W && W.back) { W.back(); } });
  $('wizNext').addEventListener('click', function () {
    if (!W) { return; }
    switch (W.step) {
      case 'start': runScan(); break;
      case 'movie-search': runMovieSearch(); break;
      case 'movie-pick': confirmMovie(); break;
      case 'tv-search': runTvSearch(); break;
      case 'tv-pick': runEpisodes(); break;
      case 'map': stepReview(); break;
      case 'multi-list': stepReview(); break;
      case 'multi-search': runMultiSearch(); break;
      case 'multi-pick': confirmMultiPick(); break;
      case 'music-search': runMusicLookup($('wArtist').value.trim(), $('wAlbum').value.trim()); break;
      case 'music-pick': stepMusicTracks(); break;
      case 'music-tracks': runMusicProcess(); break;
      case 'review': runProcess(); break;
    }
  });

  $('loginBtn').addEventListener('click', doLogin);
  $('secret').addEventListener('keydown', function (e) { if (e.key === 'Enter') { doLogin(); } });
  $('prev').addEventListener('click', function () { page--; render(); });
  $('next').addEventListener('click', function () { page++; render(); });
  $('logout').addEventListener('click', function (e) {
    e.preventDefault();
    fetch('/logout', auth({ method: 'POST' })).then(function () {
      stopPolling(); saveToken(''); all = []; show('login');
    });
  });

  // First load decides which view to show (a live session cookie skips the login prompt).
  show('login');
  startPolling();
  load();
})();
</script>
</body>
</html>";
    }
}
