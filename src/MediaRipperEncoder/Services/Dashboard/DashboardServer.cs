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

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using MediaRipperEncoder.Models;
using MediaRipperEncoder.Services.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MediaRipperEncoder.Services.Dashboard
{
    /// <summary>
    /// The dashboard HOST web server. A plain TcpListener (NOT HttpListener — see MiniHttp for why)
    /// that serves a small single-page dashboard to any browser on the LAN and ingests HMAC-signed
    /// status reports from the fleet.
    ///
    /// Security model (identical spirit to the encode connector — a LAN gate, not internet armor):
    ///   * Refuses to start without a shared secret, so it's never wide open.
    ///   * Browsers log in by entering that shared secret; on success they get a random session
    ///     cookie (HttpOnly, SameSite=Strict). The secret is only ever sent in a POST body, never a
    ///     URL, and is never echoed back.
    ///   * Status reports (machine-to-machine) are gated by an HMAC-SHA256 of the report body keyed
    ///     with the shared secret, so a device that doesn't hold the secret can't inject fake tiles.
    ///   * The page and status JSON never contain the secret or any API keys (asserted by tests).
    /// LAN only — do not port-forward. Uses a high port so it can bind without administrator rights.
    /// </summary>
    public class DashboardServer : IDisposable
    {
        private const string SessionCookie = "ar_dash";
        // Alternative to the cookie, and the ONLY one that works when the dashboard is embedded in
        // another site's iframe (the Home Assistant panel): browsers treat our cookie as
        // third-party there and won't send it, and SameSite=None needs HTTPS, which a LAN HTTP
        // service can't offer. A header is unaffected by cookie policy.
        private const string SessionHeader = "X-AR-Session";
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
        // An instance is shown if it reported within this window (a few missed 2s beats forgiven).
        private static readonly TimeSpan LivenessWindow = TimeSpan.FromSeconds(15);
        private const int SocketTimeoutMs = 10000;

        private readonly AppSettings _settings;
        private readonly DashboardRegistry _registry;
        private readonly DashboardCommandHub _hub;
        private readonly object _sessionLock = new object();
        private readonly Dictionary<string, DateTime> _sessions = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        public DashboardServer(AppSettings settings, DashboardRegistry registry)
        {
            _settings = settings;
            _registry = registry;
            _hub = new DashboardCommandHub();
        }

        /// <summary>
        /// The remote-setup command exchange (Phase B). Exposed so a self-hosting instance's
        /// reporter can pull its own commands directly instead of over the loopback network.
        /// </summary>
        public DashboardCommandHub CommandHub { get { return _hub; } }

        public int Port { get { return _settings.DashboardPort; } }
        public bool IsRunning { get { return _running; } }

        /// <summary>
        /// Binds the port and starts accepting. Throws if there's no shared secret (fail safe) or the
        /// port can't be bound (already in use) — the caller surfaces that to the user.
        /// </summary>
        public void Start()
        {
            if (string.IsNullOrWhiteSpace(_settings.NodeSharedSecret))
            {
                throw new InvalidOperationException(
                    "The dashboard needs a shared secret before it can start. Set one in Settings " +
                    "(the same secret every instance uses to report in).");
            }

            _listener = new TcpListener(IPAddress.Any, _settings.DashboardPort);
            _listener.Start();
            _running = true;

            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "DashboardAccept" };
            _acceptThread.Start();

            Logger.Info("Dashboard server listening on port " + _settings.DashboardPort +
                        " (LAN only — do not port-forward).");
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    // Normal on Stop() (listener disposed). If we're still meant to be running,
                    // a transient accept error shouldn't kill the loop.
                    if (!_running) { break; }
                    continue;
                }

                // Thread-per-connection: responses say Connection: close, so each is short-lived.
                var worker = new Thread(() => HandleClient(client)) { IsBackground = true };
                worker.Start();
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = SocketTimeoutMs;
                client.SendTimeout = SocketTimeoutMs;
                using (NetworkStream stream = client.GetStream())
                {
                    MiniHttp.Request req = MiniHttp.ReadRequest(stream);
                    if (req == null) { return; }
                    Route(req, stream);
                }
            }
            catch (Exception ex)
            {
                // A dropped browser or a malformed request must never take down the server.
                Logger.Info("Dashboard request error (ignored): " + ex.Message);
            }
            finally
            {
                try { client.Close(); } catch { /* already closed */ }
            }
        }

        private void Route(MiniHttp.Request req, System.IO.Stream stream)
        {
            // Machine-to-machine endpoints — HMAC-gated, no browser session.
            if (req.Method == "POST" && req.Path == "/report") { HandleReport(req, stream); return; }
            if (req.Method == "POST" && req.Path == "/cmdresult") { HandleCommandResult(req, stream); return; }

            // Browser session endpoints.
            if (req.Method == "POST" && req.Path == "/login") { HandleLogin(req, stream); return; }
            if (req.Path == "/logout") { HandleLogout(req, stream); return; }
            if (req.Method == "GET" && req.Path == "/api/status") { HandleStatus(req, stream); return; }
            if (req.Method == "POST" && req.Path == "/api/do") { HandleDo(req, stream); return; }
            if (req.Method == "GET" && req.Path == "/api/cmd") { HandleCmdQuery(req, stream); return; }
            if (req.Method == "GET" && req.Path == "/health") { MiniHttp.WriteText(stream, 200, "OK", "ok"); return; }

            // The single-page shell (no data in it; all data comes from the auth-gated /api/status).
            if (req.Method == "GET" && (req.Path == "/" || req.Path == "/index.html"))
            {
                MiniHttp.WriteHtml(stream, 200, "OK", DashboardPage.Html);
                return;
            }

            MiniHttp.WriteText(stream, 404, "Not Found", "Not found.");
        }

        // ---- /report : ingest a fleet member's status ----

        private void HandleReport(MiniHttp.Request req, System.IO.Stream stream)
        {
            string proof = req.Header("X-AR-Proof");
            if (!NodeAuth.VerifyProof(_settings.NodeSharedSecret, req.Body, proof))
            {
                // Wrong/absent signature — a device that doesn't hold the shared secret.
                MiniHttp.WriteText(stream, 401, "Unauthorized", "bad signature");
                return;
            }

            try
            {
                DashboardSnapshot snap = JsonConvert.DeserializeObject<DashboardSnapshot>(req.Body);
                _registry.Ingest(snap);

                // Phase B: the report's response carries any queued remote-setup commands for this
                // instance, and is ITSELF signed (X-AR-Proof over the response body) so the
                // instance can verify the commands really came from the host that holds the secret
                // — a LAN device can't inject work by spoofing a response.
                var response = new JObject();
                response["ok"] = true;
                var cmds = new JArray();
                if (snap != null && !string.IsNullOrEmpty(snap.InstanceId))
                {
                    foreach (DashCommand c in _hub.TakeCommandsFor(snap.InstanceId))
                    {
                        cmds.Add(JObject.FromObject(c));
                    }
                }
                response["commands"] = cmds;

                string body = response.ToString(Newtonsoft.Json.Formatting.None);
                string responseProof = NodeAuth.ComputeProof(_settings.NodeSharedSecret, body);
                MiniHttp.WriteJson(stream, 200, "OK", body, new[] { "X-AR-Proof: " + responseProof });
            }
            catch (Exception ex)
            {
                Logger.Info("Dashboard: rejected a malformed status report: " + ex.Message);
                MiniHttp.WriteText(stream, 400, "Bad Request", "bad report");
            }
        }

        /// <summary>An instance posting a finished command's outcome (HMAC-gated like /report).</summary>
        private void HandleCommandResult(MiniHttp.Request req, System.IO.Stream stream)
        {
            string proof = req.Header("X-AR-Proof");
            if (!NodeAuth.VerifyProof(_settings.NodeSharedSecret, req.Body, proof))
            {
                MiniHttp.WriteText(stream, 401, "Unauthorized", "bad signature");
                return;
            }
            try
            {
                DashCommandResult result = JsonConvert.DeserializeObject<DashCommandResult>(req.Body);
                _hub.Complete(result);
                MiniHttp.WriteText(stream, 200, "OK", "ok");
            }
            catch (Exception ex)
            {
                Logger.Info("Dashboard: rejected a malformed command result: " + ex.Message);
                MiniHttp.WriteText(stream, 400, "Bad Request", "bad result");
            }
        }

        // ---- /api/do , /api/cmd : the browser's remote-setup API (session-gated) ----

        private void HandleDo(MiniHttp.Request req, System.IO.Stream stream)
        {
            if (!IsAuthed(req))
            {
                MiniHttp.WriteJson(stream, 401, "Unauthorized", "{\"ok\":false,\"error\":\"login required\"}");
                return;
            }

            string instanceId, action;
            JObject args;
            try
            {
                JObject body = JObject.Parse(req.Body);
                instanceId = (string)body["instanceId"] ?? "";
                action = (string)body["action"] ?? "";
                args = body["args"] as JObject ?? new JObject();
            }
            catch
            {
                MiniHttp.WriteJson(stream, 400, "Bad Request", "{\"ok\":false,\"error\":\"bad request\"}");
                return;
            }

            // Only instances currently reporting (and advertising remote setup) can be driven.
            DashboardSnapshot target = null;
            foreach (DashboardSnapshot s in _registry.LiveSnapshots(LivenessWindow))
            {
                if (string.Equals(s.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase)) { target = s; break; }
            }
            if (target == null)
            {
                MiniHttp.WriteJson(stream, 200, "OK",
                    "{\"ok\":false,\"error\":\"That machine isn't reporting to this dashboard right now.\"}");
                return;
            }
            if (!target.AllowsRemoteSetup)
            {
                MiniHttp.WriteJson(stream, 200, "OK",
                    "{\"ok\":false,\"error\":\"That machine doesn't allow disc setup from the dashboard.\"}");
                return;
            }

            string id = _hub.Enqueue(target.InstanceId, action, args);
            var o = new JObject();
            o["ok"] = true;
            o["id"] = id;
            MiniHttp.WriteJson(stream, 200, "OK", o.ToString(Newtonsoft.Json.Formatting.None));
        }

        private void HandleCmdQuery(MiniHttp.Request req, System.IO.Stream stream)
        {
            if (!IsAuthed(req))
            {
                MiniHttp.WriteJson(stream, 401, "Unauthorized", "{\"ok\":false,\"error\":\"login required\"}");
                return;
            }
            string id = QueryValue(req.Query, "id");
            // The hub's status already carries the command's own ok/error — do NOT stamp an
            // envelope ok over it. (Doing so was a real bug: every failed command rendered in the
            // browser as an empty success instead of showing its red error message.)
            JObject status = _hub.Query(id);
            MiniHttp.WriteJson(stream, 200, "OK", status.ToString(Newtonsoft.Json.Formatting.None));
        }

        /// <summary>Pulls one value out of a raw query string ("a=1&b=2"); "" if absent.</summary>
        private static string QueryValue(string query, string name)
        {
            if (string.IsNullOrEmpty(query)) { return ""; }
            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) { continue; }
                if (string.Equals(pair.Substring(0, eq), name, StringComparison.Ordinal))
                {
                    return Uri.UnescapeDataString(pair.Substring(eq + 1));
                }
            }
            return "";
        }

        // ---- /login , /logout , session handling ----

        private void HandleLogin(MiniHttp.Request req, System.IO.Stream stream)
        {
            string provided;
            try
            {
                JObject body = JObject.Parse(req.Body);
                provided = (string)body["secret"] ?? "";
            }
            catch
            {
                provided = "";
            }

            if (!ConstantTimeEquals(provided, _settings.NodeSharedSecret ?? ""))
            {
                // Deliberately generic — don't hint whether the secret was close.
                MiniHttp.WriteJson(stream, 401, "Unauthorized", "{\"ok\":false}");
                return;
            }

            string token = NewSessionToken();
            // The session is handed back BOTH ways, because neither works everywhere:
            //  * Cookie — HttpOnly + SameSite=Strict; ideal for a normal top-level browser tab.
            //  * Token in the body — the page stores it and returns it as the X-AR-Session
            //    header. This is what lets the dashboard work inside an iframe (see SessionHeader).
            // Trade-off accepted: a token the page can read is visible to script, unlike an
            // HttpOnly cookie. Fine here — the page is self-contained (no third-party scripts)
            // and escapes everything it renders — and it's the standard pattern for embedded apps.
            string setCookie = "Set-Cookie: " + SessionCookie + "=" + token +
                               "; HttpOnly; SameSite=Strict; Path=/";
            var okBody = new JObject();
            okBody["ok"] = true;
            okBody["token"] = token;
            MiniHttp.WriteJson(stream, 200, "OK",
                okBody.ToString(Newtonsoft.Json.Formatting.None), new[] { setCookie });
        }

        private void HandleLogout(MiniHttp.Request req, System.IO.Stream stream)
        {
            string token = SessionTokenOf(req);
            if (!string.IsNullOrEmpty(token))
            {
                lock (_sessionLock) { _sessions.Remove(token); }
            }
            string clear = "Set-Cookie: " + SessionCookie + "=; HttpOnly; SameSite=Strict; Path=/; Max-Age=0";
            MiniHttp.WriteText(stream, 200, "OK", "logged out", new[] { clear });
        }

        // ---- /api/status : the tile data (auth-gated) ----

        private void HandleStatus(MiniHttp.Request req, System.IO.Stream stream)
        {
            if (!IsAuthed(req))
            {
                MiniHttp.WriteJson(stream, 401, "Unauthorized", "{\"ok\":false,\"error\":\"login required\"}");
                return;
            }

            List<DashboardSnapshot> live = _registry.LiveSnapshots(LivenessWindow);
            var payload = new
            {
                ok = true,
                server = Environment.MachineName,
                generatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                instances = live
            };
            MiniHttp.WriteJson(stream, 200, "OK", JsonConvert.SerializeObject(payload));
        }

        /// <summary>
        /// The caller's session token: the X-AR-Session header if present (iframe-safe), otherwise
        /// the cookie (normal top-level tab). Empty when neither is supplied.
        /// </summary>
        private static string SessionTokenOf(MiniHttp.Request req)
        {
            string token = req.Header(SessionHeader);
            return string.IsNullOrEmpty(token) ? req.Cookie(SessionCookie) : token;
        }

        private bool IsAuthed(MiniHttp.Request req)
        {
            string token = SessionTokenOf(req);
            if (string.IsNullOrEmpty(token)) { return false; }
            lock (_sessionLock)
            {
                DateTime expiry;
                if (!_sessions.TryGetValue(token, out expiry)) { return false; }
                if (DateTime.UtcNow > expiry)
                {
                    _sessions.Remove(token);
                    return false;
                }
                // Sliding expiry: an active viewer stays logged in.
                _sessions[token] = DateTime.UtcNow + SessionLifetime;
                return true;
            }
        }

        private string NewSessionToken()
        {
            byte[] bytes = new byte[32];
            using (var rng = new RNGCryptoServiceProvider()) { rng.GetBytes(bytes); }
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) { sb.Append(b.ToString("x2")); }
            string token = sb.ToString();
            lock (_sessionLock) { _sessions[token] = DateTime.UtcNow + SessionLifetime; }
            return token;
        }

        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null) { return false; }
            if (a.Length != b.Length) { return false; }
            int diff = 0;
            for (int i = 0; i < a.Length; i++) { diff |= a[i] ^ b[i]; }
            return diff == 0;
        }

        public void Dispose()
        {
            _running = false;
            try { if (_listener != null) { _listener.Stop(); } } catch { /* ignore */ }
        }
    }
}
