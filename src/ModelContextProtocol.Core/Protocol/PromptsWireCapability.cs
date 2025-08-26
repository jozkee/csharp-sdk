using System.Text.Json.Serialization;

namespace ModelContextProtocol.Protocol;

/// <summary>
/// Represents the prompts capability as advertised over the wire protocol.
/// </summary>
/// <remarks>
/// <para>
/// This class contains only the properties that are relevant to clients and are serialized
/// in the Model Context Protocol. It excludes server-specific implementation details
/// such as handlers and collections.
/// </para>
/// <para>
/// See the <see href="https://github.com/modelcontextprotocol/specification/blob/main/schema/">schema</see> for details.
/// </para>
/// </remarks>
public sealed class PromptsWireCapability
{
    /// <summary>
    /// Gets or sets whether this server supports notifications for changes to the prompt list.
    /// </summary>
    /// <remarks>
    /// When set to <see langword="true"/>, the server will send notifications using 
    /// <see cref="NotificationMethods.PromptListChangedNotification"/> when prompts are added, 
    /// removed, or modified. Clients can register handlers for these notifications to
    /// refresh their prompt cache. This capability enables clients to stay synchronized with server-side changes 
    /// to available prompts.
    /// </remarks>
    [JsonPropertyName("listChanged")]
    public bool? ListChanged { get; set; }
}