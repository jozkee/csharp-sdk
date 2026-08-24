import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';

const marker = process.argv[2] ?? 'missing-marker';
const server = new McpServer({ name: 'windows-command-resolution-server', version: '1.0.0' });

server.registerTool(
  'launcher-marker',
  { description: 'Returns the launcher marker supplied by the command shim.' },
  async () => ({ content: [{ type: 'text', text: marker }] }),
);

await server.connect(new StdioServerTransport());
