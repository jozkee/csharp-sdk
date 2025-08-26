using System.Text.Json.Serialization;

namespace ModelContextProtocol.Protocol;

/// <summary>
/// Represents the logging capability as advertised over the wire protocol.
/// </summary>
/// <remarks>
/// <para>
/// This class contains only the properties that are relevant to clients and are serialized
/// in the Model Context Protocol. It excludes server-specific implementation details
/// such as handlers.
/// </para>
/// <para>
/// See the <see href="https://github.com/modelcontextprotocol/specification/blob/main/schema/">schema</see> for details.
/// </para>
/// </remarks>
public sealed class LoggingWireCapability
{
    // Currently empty in the spec, but may be extended in the future
}