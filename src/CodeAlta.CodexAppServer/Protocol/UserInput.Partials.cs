using System.Text.Json.Serialization;

namespace CodeAlta.CodexAppServer.Protocol;

public partial record UserInput
{
    // Codex flattens an untagged ImageReference ({"url"} or {"fileId"}) into the image input, which its JSON schema
    // loses: the generated type has the detail only.
    public sealed partial record ImageUserInput
    {
        /// <summary>Gets or sets the address of the image: an http(s) or a <c>data:</c> address.</summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        /// <summary>Gets or sets the identifier of an uploaded file, instead of <see cref="Url"/>.</summary>
        [JsonPropertyName("fileId")]
        public string? FileId { get; set; }
    }
}
