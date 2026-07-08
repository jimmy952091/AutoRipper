using System;
using MediaRipperEncoder.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MediaRipperEncoder.Services.Net
{
    /// <summary>
    /// What one distributed encode job carries from the ripper client to the encoder server, and
    /// helpers to pack/unpack it into a JOB_SUBMIT message.
    ///
    /// The whole point is that the CONFIRMED <see cref="MediaMetadata"/> package rides along with the
    /// job — exactly the project rule that metadata must travel with the job and never be re-derived
    /// from a filename. The server uses this package (plus ITS OWN library roots and preset files) to
    /// name and place the output, so the two machines can have different folder layouts and it still
    /// lands correctly.
    ///
    /// The preset is referenced by KIND (general vs. animation), not by file path, because the preset
    /// .json files live independently on each machine — the server resolves the kind to its own path.
    /// </summary>
    public class RemoteEncodeRequest
    {
        /// <summary>The confirmed metadata package for the disc this file came from.</summary>
        public MediaMetadata Metadata { get; set; }

        /// <summary>MakeMKV title index this file is, so the server can find the right TitleMapping.</summary>
        public int TitleIndex { get; set; }

        /// <summary>True to encode with the animation preset; false for the general preset.</summary>
        public bool UseAnimationPreset { get; set; }

        /// <summary>Original ripped file name (informational / logging). Not used for naming.</summary>
        public string SourceFileName { get; set; }

        /// <summary>Size in bytes of the file that will follow via FILE_BEGIN (lets the server pre-check disk space).</summary>
        public long FileSize { get; set; }

        /// <summary>Client-generated id so both sides refer to the same job in PROGRESS/JOB_DONE.</summary>
        public string ClientJobId { get; set; }
    }

    /// <summary>Packs/unpacks <see cref="RemoteEncodeRequest"/> to and from a JOB_SUBMIT NetMessage.</summary>
    public static class RemoteJobProtocol
    {
        public static NetMessage BuildJobSubmit(RemoteEncodeRequest request)
        {
            if (request == null) { throw new ArgumentNullException("request"); }

            // Embed the whole request as a nested JSON object so nested lists (title mappings ->
            // episodes) survive intact. Newtonsoft handles the object graph.
            JObject payload = JObject.FromObject(request);
            return new NetMessage(MsgType.JobSubmit).With("request", payload);
        }

        public static RemoteEncodeRequest ParseJobSubmit(NetMessage message)
        {
            if (message == null || message.Type != MsgType.JobSubmit)
            {
                throw new ArgumentException("Message is not a JOB_SUBMIT.");
            }

            JToken requestToken = message.Data != null ? message.Data["request"] : null;
            if (requestToken == null || requestToken.Type == JTokenType.Null)
            {
                throw new InvalidOperationException("JOB_SUBMIT has no 'request' payload.");
            }

            return requestToken.ToObject<RemoteEncodeRequest>();
        }
    }
}
