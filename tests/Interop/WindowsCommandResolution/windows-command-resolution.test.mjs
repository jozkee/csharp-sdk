import assert from 'node:assert/strict';
import { copyFile, link, mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { delimiter, dirname, isAbsolute, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

const testDirectory = dirname(fileURLToPath(import.meta.url));
const serverPath = join(testDirectory, 'mcp-server.mjs');

function stringEnvironment(overrides = {}) {
  return Object.fromEntries(
    Object.entries({ ...process.env, ...overrides }).filter(([, value]) => value !== undefined),
  );
}

async function assertMcpRoundTrip(command, args, cwd, env, expectedMarker) {
  const client = new Client({ name: 'windows-command-resolution-test', version: '1.0.0' });
  const transport = new StdioClientTransport({ command, args, cwd, env, stderr: 'pipe' });
  let stderr = '';
  transport.stderr?.on('data', chunk => { stderr += chunk.toString(); });

  try {
    await client.connect(transport);
    const tools = await client.listTools();
    assert.ok(tools.tools.some(tool => tool.name === 'launcher-marker'));

    const result = await client.callTool({ name: 'launcher-marker', arguments: {} });
    assert.deepEqual(result.content, [{ type: 'text', text: expectedMarker }]);
  } catch (error) {
    throw new Error(`TypeScript MCP SDK failed to launch ${command}. stderr: ${stderr}`, { cause: error });
  } finally {
    await client.close();
  }
}

async function createNodeExecutable(target) {
  try {
    await link(process.execPath, target);
  } catch {
    await copyFile(process.execPath, target);
  }
}

async function withCommandEnvironment(run) {
  const root = await mkdtemp(join(tmpdir(), 'mcp command resolution '));
  const shims = join(root, 'command shims');
  const cwd = join(root, 'working directory');
  await mkdir(shims);
  await mkdir(cwd);

  const path = `${shims}${delimiter}${process.env.PATH ?? ''}`;
  const pathExt = `.COM;.EXE;.BAT;.CMD;${process.env.PATHEXT ?? ''}`;
  const env = stringEnvironment({ PATH: path, Path: path, PATHEXT: pathExt });

  try {
    await run({ shims, cwd, env });
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

test('TypeScript MCP SDK resolves bare npx to npx.cmd on PATH', async () => {
  await withCommandEnvironment(async ({ shims, cwd, env }) => {
    await writeFile(
      join(shims, 'npx.cmd'),
      `@echo off\r\n"${process.execPath}" "${serverPath}" "npx.cmd"\r\n`,
    );

    await assertMcpRoundTrip('npx', [], cwd, env, 'npx.cmd');
  });
});

test('TypeScript MCP SDK resolves bare uvicorn to uvicorn.exe on PATH', async () => {
  await withCommandEnvironment(async ({ shims, cwd, env }) => {
    await createNodeExecutable(join(shims, 'uvicorn.exe'));
    await assertMcpRoundTrip('uvicorn', [serverPath, 'uvicorn.exe'], cwd, env, 'uvicorn.exe');
  });
});

test('TypeScript MCP SDK appends PATHEXT after an existing extension', async () => {
  await withCommandEnvironment(async ({ shims, cwd, env }) => {
    await writeFile(
      join(shims, 'uvicorn.exe.cmd'),
      `@echo off\r\n"${process.execPath}" "${serverPath}" "uvicorn.exe.cmd"\r\n`,
    );

    await assertMcpRoundTrip('uvicorn.exe', [], cwd, env, 'uvicorn.exe.cmd');
  });
});

test('TypeScript MCP SDK appends PATHEXT to a rooted extensionless command', async () => {
  await withCommandEnvironment(async ({ shims, cwd, env }) => {
    const rootedCommand = join(shims, 'rooted-npx');
    assert.ok(isAbsolute(rootedCommand), `expected a rooted command path: ${rootedCommand}`);
    await writeFile(
      `${rootedCommand}.cmd`,
      `@echo off\r\n"${process.execPath}" "${serverPath}" "rooted-npx.cmd"\r\n`,
    );

    await assertMcpRoundTrip(rootedCommand, [], cwd, env, 'rooted-npx.cmd');
  });
});

test('TypeScript MCP SDK appends PATHEXT to a rooted command with an extension', async () => {
  await withCommandEnvironment(async ({ shims, cwd, env }) => {
    const rootedCommand = join(shims, 'rooted-uvicorn.exe');
    assert.ok(isAbsolute(rootedCommand), `expected a rooted command path: ${rootedCommand}`);
    await writeFile(
      `${rootedCommand}.cmd`,
      `@echo off\r\n"${process.execPath}" "${serverPath}" "rooted-uvicorn.exe.cmd"\r\n`,
    );

    await assertMcpRoundTrip(rootedCommand, [], cwd, env, 'rooted-uvicorn.exe.cmd');
  });
});

test('TypeScript MCP SDK resolves a relative directory command from cwd without searching PATH', async () => {
  await withCommandEnvironment(async ({ shims, cwd, env }) => {
    const relativeCommand = join('relative-tools', 'launcher');
    assert.ok(!isAbsolute(relativeCommand), `expected a relative command path: ${relativeCommand}`);
    assert.notEqual(dirname(relativeCommand), '.', 'command should contain a directory component');

    const cwdCommandDirectory = join(cwd, dirname(relativeCommand));
    const pathCommandDirectory = join(shims, dirname(relativeCommand));
    await mkdir(cwdCommandDirectory, { recursive: true });
    await mkdir(pathCommandDirectory, { recursive: true });
    await writeFile(
      `${join(cwd, relativeCommand)}.cmd`,
      `@echo off\r\n"${process.execPath}" "${serverPath}" "relative-cwd.cmd"\r\n`,
    );
    await writeFile(
      `${join(shims, relativeCommand)}.cmd`,
      `@echo off\r\n"${process.execPath}" "${serverPath}" "relative-path.cmd"\r\n`,
    );

    await assertMcpRoundTrip(relativeCommand, [], cwd, env, 'relative-cwd.cmd');
  });
});

// Reference (ground-truth) test: this is the TypeScript MCP SDK equivalent of the C# integration
// call in https://github.com/modelcontextprotocol/csharp-sdk/pull/1703 -
//   new StdioClientTransport(new StdioClientTransportOptions {
//       Command = "npx",
//       Arguments = ["-y", "@modelcontextprotocol/server-memory"],
//       Name = "memory",
//   })
// It uses the real `npx` on PATH (a `npx.cmd` shim, resolved via cross-spawn/PATHEXT), launches the
// real published @modelcontextprotocol/server-memory package, completes the MCP handshake, and calls
// the no-argument `read_graph` tool. This demonstrates how the reference implementation resolves and
// runs the exact command/args the C# transport must match. It is skipped when `npx` is unavailable.
test('TypeScript MCP SDK launches real npx -y @modelcontextprotocol/server-memory', async t => {
  if (process.platform !== 'win32') {
    t.skip('Windows-only reference scenario.');
    return;
  }

  // Isolate the memory server's persisted knowledge graph so the test never touches real state.
  const memoryRoot = await mkdtemp(join(tmpdir(), 'mcp-memory-'));
  const env = stringEnvironment({ MEMORY_FILE_PATH: join(memoryRoot, 'memory.jsonl') });

  const client = new Client({ name: 'windows-command-resolution-test', version: '1.0.0' });
  const transport = new StdioClientTransport({
    command: 'npx',
    args: ['-y', '@modelcontextprotocol/server-memory'],
    env,
    stderr: 'pipe',
  });
  let stderr = '';
  transport.stderr?.on('data', chunk => { stderr += chunk.toString(); });

  try {
    await client.connect(transport);

    const tools = await client.listTools();
    assert.ok(
      tools.tools.some(tool => tool.name === 'read_graph'),
      'memory server should expose the read_graph tool',
    );

    const result = await client.callTool({ name: 'read_graph', arguments: {} });
    assert.deepEqual(result.structuredContent, { entities: [], relations: [] });
  } catch (error) {
    throw new Error(`Real npx -y @modelcontextprotocol/server-memory launch failed. stderr: ${stderr}`, { cause: error });
  } finally {
    await client.close();
    await rm(memoryRoot, { recursive: true, force: true });
  }
});
