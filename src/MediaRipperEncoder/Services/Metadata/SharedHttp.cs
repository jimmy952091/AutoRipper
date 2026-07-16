using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace MediaRipperEncoder.Services.Metadata
{
    /// <summary>
    /// One shared HttpClient for the whole app. Creating a new HttpClient per request is a
    /// well-known .NET pitfall (socket exhaustion), so metadata clients reuse this instance.
    ///
    /// TLS note: on Windows 7 / .NET Framework 4.8, the default negotiated protocol can be too
    /// old for modern HTTPS APIs, so TLS 1.2 is enabled explicitly here. Without this, OMDb and
    /// TheTVDB requests can fail with an opaque "connection closed" on older systems.
    ///
    /// Trust note: unpatched Windows 7 machines lack root certificates issued after ~2009 unless
    /// automatic root updates work — so perfectly good HTTPS chains fail with "the remote
    /// certificate is invalid". Verified live: MusicBrainz chains to DigiCert Global Root G2
    /// (issued 2013 — newer than Windows 7 itself), and Cover Art Archive/others use Let's
    /// Encrypt's ISRG Root X1 (2015). Both genuine roots are embedded below as PINNED fallback
    /// trust anchors: a chain rejected ONLY for an unknown root is re-verified and accepted
    /// solely when it terminates in one of these exact roots (thumbprint-compared). Name
    /// mismatches and all other TLS errors still fail — this bundles two missing roots; it does
    /// not weaken validation.
    /// </summary>
    public static class SharedHttp
    {
        public static readonly HttpClient Client;

        // The genuine ISRG Root X1 (Let's Encrypt root CA), from letsencrypt.org/certificates.
        // Self-signed root, valid until 2035-06-04.
        private const string IsrgRootX1Pem =
            "MIIFazCCA1OgAwIBAgIRAIIQz7DSQONZRGPgu2OCiwAwDQYJKoZIhvcNAQELBQAw" +
            "TzELMAkGA1UEBhMCVVMxKTAnBgNVBAoTIEludGVybmV0IFNlY3VyaXR5IFJlc2Vh" +
            "cmNoIEdyb3VwMRUwEwYDVQQDEwxJU1JHIFJvb3QgWDEwHhcNMTUwNjA0MTEwNDM4" +
            "WhcNMzUwNjA0MTEwNDM4WjBPMQswCQYDVQQGEwJVUzEpMCcGA1UEChMgSW50ZXJu" +
            "ZXQgU2VjdXJpdHkgUmVzZWFyY2ggR3JvdXAxFTATBgNVBAMTDElTUkcgUm9vdCBY" +
            "MTCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBAK3oJHP0FDfzm54rVygc" +
            "h77ct984kIxuPOZXoHj3dcKi/vVqbvYATyjb3miGbESTtrFj/RQSa78f0uoxmyF+" +
            "0TM8ukj13Xnfs7j/EvEhmkvBioZxaUpmZmyPfjxwv60pIgbz5MDmgK7iS4+3mX6U" +
            "A5/TR5d8mUgjU+g4rk8Kb4Mu0UlXjIB0ttov0DiNewNwIRt18jA8+o+u3dpjq+sW" +
            "T8KOEUt+zwvo/7V3LvSye0rgTBIlDHCNAymg4VMk7BPZ7hm/ELNKjD+Jo2FR3qyH" +
            "B5T0Y3HsLuJvW5iB4YlcNHlsdu87kGJ55tukmi8mxdAQ4Q7e2RCOFvu396j3x+UC" +
            "B5iPNgiV5+I3lg02dZ77DnKxHZu8A/lJBdiB3QW0KtZB6awBdpUKD9jf1b0SHzUv" +
            "KBds0pjBqAlkd25HN7rOrFleaJ1/ctaJxQZBKT5ZPt0m9STJEadao0xAH0ahmbWn" +
            "OlFuhjuefXKnEgV4We0+UXgVCwOPjdAvBbI+e0ocS3MFEvzG6uBQE3xDk3SzynTn" +
            "jh8BCNAw1FtxNrQHusEwMFxIt4I7mKZ9YIqioymCzLq9gwQbooMDQaHWBfEbwrbw" +
            "qHyGO0aoSCqI3Haadr8faqU9GY/rOPNk3sgrDQoo//fb4hVC1CLQJ13hef4Y53CI" +
            "rU7m2Ys6xt0nUW7/vGT1M0NPAgMBAAGjQjBAMA4GA1UdDwEB/wQEAwIBBjAPBgNV" +
            "HRMBAf8EBTADAQH/MB0GA1UdDgQWBBR5tFnme7bl5AFzgAiIyBpY9umbbjANBgkq" +
            "hkiG9w0BAQsFAAOCAgEAVR9YqbyyqFDQDLHYGmkgJykIrGF1XIpu+ILlaS/V9lZL" +
            "ubhzEFnTIZd+50xx+7LSYK05qAvqFyFWhfFQDlnrzuBZ6brJFe+GnY+EgPbk6ZGQ" +
            "3BebYhtF8GaV0nxvwuo77x/Py9auJ/GpsMiu/X1+mvoiBOv/2X/qkSsisRcOj/KK" +
            "NFtY2PwByVS5uCbMiogziUwthDyC3+6WVwW6LLv3xLfHTjuCvjHIInNzktHCgKQ5" +
            "ORAzI4JMPJ+GslWYHb4phowim57iaztXOoJwTdwJx4nLCgdNbOhdjsnvzqvHu7Ur" +
            "TkXWStAmzOVyyghqpZXjFaH3pO3JLF+l+/+sKAIuvtd7u+Nxe5AW0wdeRlN8NwdC" +
            "jNPElpzVmbUq4JUagEiuTDkHzsxHpFKVK7q4+63SM1N95R1NbdWhscdCb+ZAJzVc" +
            "oyi3B43njTOQ5yOf+1CceWxG1bQVs5ZufpsMljq4Ui0/1lvh+wjChP4kqKOJ2qxq" +
            "4RgqsahDYVvTH9w7jXbyLeiNdd8XM2w9U/t7y0Ff/9yi0GE44Za4rF2LN9d11TPA" +
            "mRGunUHBcnWEvgJBQl9nJEiU0Zsnvgc/ubhPgXRR4Xq37Z0j4r7g1SgEEzwxA57d" +
            "emyPxgcYxn/eR44/KJ4EBs+lVDR3veyJm+kXQ99b21/+jh5Xos1AnX5iItreGCc=";

        // The genuine DigiCert Global Root G2, from cacerts.digicert.com. Self-signed root,
        // valid until 2038-01-15. This is what musicbrainz.org's chain terminates in (verified
        // live 2026-07-10: *.musicbrainz.org -> GandiCert -> DigiCert Global Root G2).
        private const string DigiCertGlobalRootG2Pem =
            "MIIDjjCCAnagAwIBAgIQAzrx5qcRqaC7KGSxHQn65TANBgkqhkiG9w0BAQsFADBh" +
            "MQswCQYDVQQGEwJVUzEVMBMGA1UEChMMRGlnaUNlcnQgSW5jMRkwFwYDVQQLExB3" +
            "d3cuZGlnaWNlcnQuY29tMSAwHgYDVQQDExdEaWdpQ2VydCBHbG9iYWwgUm9vdCBH" +
            "MjAeFw0xMzA4MDExMjAwMDBaFw0zODAxMTUxMjAwMDBaMGExCzAJBgNVBAYTAlVT" +
            "MRUwEwYDVQQKEwxEaWdpQ2VydCBJbmMxGTAXBgNVBAsTEHd3dy5kaWdpY2VydC5j" +
            "b20xIDAeBgNVBAMTF0RpZ2lDZXJ0IEdsb2JhbCBSb290IEcyMIIBIjANBgkqhkiG" +
            "9w0BAQEFAAOCAQ8AMIIBCgKCAQEAuzfNNNx7a8myaJCtSnX/RrohCgiN9RlUyfuI" +
            "2/Ou8jqJkTx65qsGGmvPrC3oXgkkRLpimn7Wo6h+4FR1IAWsULecYxpsMNzaHxmx" +
            "1x7e/dfgy5SDN67sH0NO3Xss0r0upS/kqbitOtSZpLYl6ZtrAGCSYP9PIUkY92eQ" +
            "q2EGnI/yuum06ZIya7XzV+hdG82MHauVBJVJ8zUtluNJbd134/tJS7SsVQepj5Wz" +
            "tCO7TG1F8PapspUwtP1MVYwnSlcUfIKdzXOS0xZKBgyMUNGPHgm+F6HmIcr9g+UQ" +
            "vIOlCsRnKPZzFBQ9RnbDhxSJITRNrw9FDKZJobq7nMWxM4MphQIDAQABo0IwQDAP" +
            "BgNVHRMBAf8EBTADAQH/MA4GA1UdDwEB/wQEAwIBhjAdBgNVHQ4EFgQUTiJUIBiV" +
            "5uNu5g/6+rkS7QYXjzkwDQYJKoZIhvcNAQELBQADggEBAGBnKJRvDkhj6zHd6mcY" +
            "1Yl9PMWLSn/pvtsrF9+wX3N3KjITOYFnQoQj8kVnNeyIv/iPsGEMNKSuIEyExtv4" +
            "NeF22d+mQrvHRAiGfzZ0JFrabA0UWTW98kndth/Jsw1HKj2ZL7tcu7XUIOGZX1NG" +
            "Fdtom/DzMNU+MeKNhJ7jitralj41E6Vf8PlwUHBHQRFXGU7Aj64GxJUTFy8bJZ91" +
            "8rGOmaFvE7FBcf6IKshPECBV1/MUReXgRPTqh5Uykw7+U0b6LJ3/iyK5S9kJRaTe" +
            "pLiaWN0bfVKfjllDiIGknibVb63dDcY3fe0Dkhvld1927jyNxF1WW6LZZm6zNTfl" +
            "MrY=";

        private static readonly X509Certificate2[] PinnedRoots =
        {
            new X509Certificate2(Convert.FromBase64String(IsrgRootX1Pem)),
            new X509Certificate2(Convert.FromBase64String(DigiCertGlobalRootG2Pem))
        };

        /// <summary>The embedded fallback trust anchors, shared with <see cref="LegacyTlsHttp"/>
        /// so the BouncyCastle transport applies the exact same pinned-root rule.</summary>
        public static X509Certificate2[] PinnedTrustAnchors
        {
            get { return (X509Certificate2[])PinnedRoots.Clone(); }
        }

        private static bool _fallbackLogged;

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

            ServicePointManager.ServerCertificateValidationCallback = ValidateWithPinnedRootFallback;

            Client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            // A User-Agent is required/appreciated by several metadata APIs.
            Client.DefaultRequestHeaders.Add("User-Agent", "MediaRipperEncoder/0.1");
        }

        /// <summary>See the class doc's trust note. Public+static so it's unit-testable.</summary>
        public static bool ValidateWithPinnedRootFallback(object sender,
            System.Security.Cryptography.X509Certificates.X509Certificate certificate,
            X509Chain chain, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None) { return true; }

            // ONLY pure chain-trust failures get a second chance. A wrong host name, a missing
            // certificate, or any combination involving them stays rejected.
            if (errors != SslPolicyErrors.RemoteCertificateChainErrors || certificate == null)
            {
                return false;
            }

            try
            {
                using (var rebuilt = new X509Chain())
                {
                    foreach (X509Certificate2 pinned in PinnedRoots)
                    {
                        rebuilt.ChainPolicy.ExtraStore.Add(pinned);
                    }
                    rebuilt.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                    // Old machines that lack the root typically can't fetch revocation lists either.
                    rebuilt.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

                    if (!rebuilt.Build(new X509Certificate2(certificate))) { return false; }

                    // Accept ONLY if the rebuilt chain terminates in one of the exact embedded roots.
                    X509Certificate2 root =
                        rebuilt.ChainElements[rebuilt.ChainElements.Count - 1].Certificate;
                    foreach (X509Certificate2 pinned in PinnedRoots)
                    {
                        if (root.Thumbprint == pinned.Thumbprint &&
                            root.RawData.Length == pinned.RawData.Length)
                        {
                            if (!_fallbackLogged)
                            {
                                _fallbackLogged = true;
                                Logger.Info("HTTPS: this Windows doesn't trust the root '" +
                                    pinned.GetNameInfo(X509NameType.SimpleName, true) + "'; using " +
                                    "AutoRipper's embedded copy as the trust anchor. Installing " +
                                    "current Windows root-certificate updates removes the need.");
                            }
                            return true;
                        }
                    }
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
