using System;
using System.Net;
using System.Net.Http;

namespace MediaRipperEncoder.Services.Metadata
{
    /// <summary>
    /// One shared HttpClient for the whole app. Creating a new HttpClient per request is a
    /// well-known .NET pitfall (socket exhaustion), so metadata clients reuse this instance.
    ///
    /// TLS note: on Windows 7 / .NET Framework 4.8, the default negotiated protocol can be too
    /// old for modern HTTPS APIs, so TLS 1.2 is enabled explicitly here. Without this, OMDb and
    /// TheTVDB requests can fail with an opaque "connection closed" on older systems.
    /// </summary>
    public static class SharedHttp
    {
        public static readonly HttpClient Client;

        static SharedHttp()
        {
            try
            {
                // Enable TLS 1.2 (and 1.1) alongside whatever the OS default is.
                ServicePointManager.SecurityProtocol |=
                    SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
            }
            catch
            {
                // Some very old frameworks may not know Tls12; the app still runs, and modern
                // Windows will already negotiate a good protocol.
            }

            Client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            // A User-Agent is required/appreciated by several metadata APIs.
            Client.DefaultRequestHeaders.Add("User-Agent", "MediaRipperEncoder/0.1");
        }
    }
}
