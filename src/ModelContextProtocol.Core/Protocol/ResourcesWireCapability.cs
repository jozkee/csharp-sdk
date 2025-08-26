using System.Text.Json.Serialization;

namespace ModelContextProtocol.Protocol;

/// <summary>
/// Represents the resources capability as advertised over the wire protocol.
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
public sealed class ResourcesWireCapability
{
    /// <summary>
    /// Gets or sets whether this server supports subscribing to resource updates.
    /// </summary>
    [JsonPropertyName("subscribe")]
    public bool? Subscribe { get; set; }

    /// <summary>
    /// Gets or sets whether this server supports notifications for changes to the resource list.
    /// </summary>
    /// <remarks>
    /// When set to <see langword="true"/>, the server will send notifications using 
    /// <see cref="NotificationMethods.ResourceListChangedNotification"/> when resources are added, 
    /// removed, or modified. Clients can register handlers for these notifications to
    /// refresh their resource cache.
    /// </remarks>
    [JsonPropertyName("listChanged")]
    public bool? ListChanged { get; set; }
}