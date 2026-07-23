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
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using MediaRipperEncoder.Models;
using MediaRipperEncoder.Services.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MediaRipperEncoder.Services.Dashboard
{
    /// <summary>
    /// Periodically publishes THIS instance's status to the dashboard so it appears as a tile,
    /// and (Phase B) carries remote-setup commands back from the host on the same cycle.
    ///
    /// Delivery paths, either or both active depending on settings:
    ///  * Self-hosting: the snapshot goes straight into the local registry, and commands are
    ///    pulled straight from the local hub — no network hop for a machine driving itself.
    ///  * Remote host: the snapshot is POSTed (HMAC-signed); the RESPONSE may carry commands and
    ///    is itself HMAC-signed by the host — the reporter verifies that proof before acting, so
    ///    a spoofed response can't make this machine scan or rip anything.
    ///
    /// Reporting stays strictly best-effort and isolated: failures are logged quietly and never
    /// touch the rip/encode work. Plain HTTP on the LAN (no TLS), fine on Windows 7.
    /// </summary>
    public class DashboardReporter : IDisposable
    {
        private const int ReportIntervalMs = 2000;
        private const int HttpTimeoutMs = 5000;

        private readonly AppSettings _settings;
        private readonly DashboardState _state;
        private readonly DashboardRegistry _localRegistry;  // non-null only when we host the dashboard
        private readonly DashboardCommandHub _localHub;     // non-null only when we host the dashboard
        private readonly RemoteSetupAgent _agent;           // non-null when this machine allows remote setup

        private Thread _thread;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);

        public DashboardReporter(AppSettings settings, DashboardState state,
            DashboardRegistry localRegistry, DashboardCommandHub localHub, RemoteSetupAgent agent)
        {
            _settings = settings;
            _state = state;
            _localRegistry = localRegistry;
            _localHub = localHub;
            _agent = agent;

            if (_agent != null)
            {
                _agent.ResultReady += OnCommandResult;
            }
        }

        /// <summary>Phase A compatibility: a pure monitor with no remote setup.</summary>
        public DashboardReporter(AppSettings settings, DashboardState state, DashboardRegistry localRegistry)
            : this(settings, state, localRegistry, null, null)
        {
        }

        public void Start()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "DashboardReporter" };
            _thread.Start();
        }

        private void Loop()
        {
            // Report immediately on start, then every interval until stopped.
            do
            {
                try { ReportOnce(); }
                catch (Exception ex) { Logger.Info("Dashboard report skipped: " + ex.Message); }
            }
            while (!_stop.WaitOne(ReportIntervalMs));
        }

        private void ReportOnce()
        {
            DashboardSnapshot snap = _state.Snapshot();

            // Local self-registration + local command pickup (we are the host).
            if (_localRegistry != null) { _localRegistry.Ingest(snap); }
            if (_localHub != null && _agent != null)
            {
                foreach (DashCommand cmd in _localHub.TakeCommandsFor(snap.InstanceId))
                {
                    _agent.HandleCommand(cmd);
                }
            }

            // Remote report (we point at someone else's host).
            string host = _settings.DashboardReportTo;
            if (string.IsNullOrWhiteSpace(host)) { return; }

            string body = JsonConvert.SerializeObject(snap);
            string proof = NodeAuth.ComputeProof(_settings.NodeSharedSecret, body);

            string url = "http://" + host.Trim() + ":" + _settings.DashboardPort + "/report";
            string responseBody;
            string responseProof;
            PostSigned(url, body, proof, out responseBody, out responseProof);

            // Commands ride the response — but only a response proven to come from a holder of
            // the shared secret is allowed to make this machine do anything.
            if (_agent == null || string.IsNullOrEmpty(responseBody)) { return; }
            if (!NodeAuth.VerifyProof(_settings.NodeSharedSecret, responseBody, responseProof))
            {
                Logger.Info("Dashboard: response signature invalid — ignoring any commands in it.");
                return;
            }

            try
            {
                JObject parsed = JObject.Parse(responseBody);
                JArray cmds = parsed["commands"] as JArray;
                if (cmds == null) { return; }
                foreach (JToken tok in cmds)
                {
                    DashCommand cmd = tok.ToObject<DashCommand>();
                    if (cmd != null) { _agent.HandleCommand(cmd); }
                }
            }
            catch (Exception ex)
            {
                Logger.Info("Dashboard: couldn't parse the host's response commands: " + ex.Message);
            }
        }

        /// <summary>A finished command's outcome goes back to whichever host issued it.</summary>
        private void OnCommandResult(DashCommandResult result)
        {
            // Self-hosting: complete directly. Unknown ids are ignored by the hub, so delivering
            // to both paths when both are configured is harmless.
            if (_localHub != null) { _localHub.Complete(result); }

            string host = _settings.DashboardReportTo;
            if (string.IsNullOrWhiteSpace(host)) { return; }

            try
            {
                string body = JsonConvert.SerializeObject(result);
                string proof = NodeAuth.ComputeProof(_settings.NodeSharedSecret, body);
                string url = "http://" + host.Trim() + ":" + _settings.DashboardPort + "/cmdresult";
                string ignoredBody, ignoredProof;
                PostSigned(url, body, proof, out ignoredBody, out ignoredProof);
            }
            catch (Exception ex)
            {
                Logger.Info("Dashboard: couldn't deliver a command result: " + ex.Message);
            }
        }

        /// <summary>POSTs a signed JSON body; returns the response body and its proof header.</summary>
        private static void PostSigned(string url, string body, string proof,
            out string responseBody, out string responseProof)
        {
            responseBody = "";
            responseProof = "";

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json; charset=utf-8";
            request.Headers["X-AR-Proof"] = proof;
            request.Timeout = HttpTimeoutMs;
            request.ReadWriteTimeout = HttpTimeoutMs;
            // Don't inherit a system/IE proxy for a LAN address — it only adds latency and failure modes.
            request.Proxy = null;

            byte[] bytes = Encoding.UTF8.GetBytes(body);
            request.ContentLength = bytes.Length;
            using (Stream rs = request.GetRequestStream()) { rs.Write(bytes, 0, bytes.Length); }

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                responseProof = response.Headers["X-AR-Proof"] ?? "";
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    responseBody = reader.ReadToEnd();
                }
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    Logger.Info("Dashboard host returned " + (int)response.StatusCode + " for a status report.");
                }
            }
        }

        public void Dispose()
        {
            if (_agent != null) { _agent.ResultReady -= OnCommandResult; }
            _stop.Set();
            try { if (_thread != null) { _thread.Join(1000); } } catch { /* ignore */ }
            _stop.Dispose();
        }
    }
}
