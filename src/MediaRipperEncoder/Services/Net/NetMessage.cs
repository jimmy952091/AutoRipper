using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MediaRipperEncoder.Services.Net
{
    /// <summary>
    /// The message-type vocabulary spoken between a ripper client and an encoder server. Kept as
    /// string constants (not an enum) so an older/newer build receiving an unknown type can log and
    /// skip it instead of failing to deserialize — important for a two-machine setup that might not
    /// always be on the exact same version.
    /// </summary>
    public static class MsgType
    {
        // Handshake / liveness
        public const string Hello = "HELLO";              // client -> server: I am a ripper client (name, version)
        public const string AuthChallenge = "AUTH_CHALLENGE"; // server -> client: prove you hold the shared secret (nonce)
        public const string AuthResponse = "AUTH_RESPONSE";   // client -> server: HMAC(secret, nonce) as hex
        public const string AuthFail = "AUTH_FAIL";        // server -> client: bad/absent proof; connection will close
        public const string HelloAck = "HELLO_ACK";       // server -> client: accepted (server name, version, session id)
        public const string Heartbeat = "HEARTBEAT";      // both ways: keep-alive / detect a dropped peer
        public const string Rejoin = "REJOIN";            // client -> server: reconnecting, here's the session id I had
        public const string SessionState = "SESSION_STATE"; // server -> client: current queue snapshot (for resume)

        // Job + file transfer
        public const string JobSubmit = "JOB_SUBMIT";     // client -> server: metadata package + file size for one encode
        public const string JobAccepted = "JOB_ACCEPTED"; // server -> client: queued, here's its job id
        public const string FileBegin = "FILE_BEGIN";     // client -> server: about to stream a file's bytes
        public const string FileChunk = "FILE_CHUNK";     // client -> server: one chunk (base64 in Data, or raw framing later)
        public const string FileEnd = "FILE_END";         // client -> server: file complete (checksum)

        // Progress + completion
        public const string Progress = "PROGRESS";        // server -> client: encode progress for a job id
        public const string JobDone = "JOB_DONE";         // server -> client: job finished (ok/failed, final path)
    }

    /// <summary>
    /// One framed message: a type plus a free-form JSON payload. Deliberately schema-light so new
    /// fields can be added without breaking older peers.
    /// </summary>
    public class NetMessage
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("data")]
        public JObject Data { get; set; }

        public NetMessage()
        {
            Data = new JObject();
        }

        public NetMessage(string type)
        {
            Type = type;
            Data = new JObject();
        }

        /// <summary>Fluent helper: attach a value and return this for chaining.</summary>
        public NetMessage With(string key, JToken value)
        {
            Data[key] = value;
            return this;
        }

        public string GetString(string key)
        {
            JToken t = Data != null ? Data[key] : null;
            return t == null || t.Type == JTokenType.Null ? "" : (string)t;
        }

        public long GetLong(string key, long fallback = 0)
        {
            JToken t = Data != null ? Data[key] : null;
            return t == null || t.Type == JTokenType.Null ? fallback : (long)t;
        }

        public int GetInt(string key, int fallback = 0)
        {
            return (int)GetLong(key, fallback);
        }
    }
}
