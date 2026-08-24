using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously

namespace ModelContextProtocol.Client;

/// <summary>
/// Provides a <see cref="IClientTransport"/> implemented via "stdio" (standard input/output).
/// </summary>
/// <remarks>
/// <para>
/// This transport launches an external process and communicates with it through standard input and output streams.
/// It's used to connect to MCP servers launched and hosted in child processes.
/// </para>
/// <para>
/// The transport manages the entire lifecycle of the process: starting it with specified command-line arguments
/// and environment variables, handling output, and properly terminating the process when the transport is closed.
/// </para>
/// </remarks>
public sealed partial class StdioClientTransport : IClientTransport
{
#if !NET
    // On .NET Framework, we need to synchronize access to Console.InputEncoding
    // to prevent race conditions when multiple transports are created concurrently.
    private static readonly object s_consoleEncodingLock = new();
#endif

    private readonly StdioClientTransportOptions _options;
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="StdioClientTransport"/> class.
    /// </summary>
    /// <param name="options">Configuration options for the transport, including the command to execute, arguments, working directory, and environment variables.</param>
    /// <param name="loggerFactory">A logger factory for creating loggers used for diagnostic output during transport operations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public StdioClientTransport(StdioClientTransportOptions options, ILoggerFactory? loggerFactory = null)
    {
        Throw.IfNull(options);

        _options = options;
        _loggerFactory = loggerFactory;
        Name = options.Name ?? $"stdio-{WhitespaceAndPeriods().Replace(Path.GetFileName(options.Command), "-")}";
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        string endpointName = Name;

        Process? process = null;
        bool processStarted = false;
        DataReceivedEventHandler? errorHandler = null;

        string command = _options.Command;
        IList<string>? arguments = _options.Arguments;

        ILogger logger = (ILogger?)_loggerFactory?.CreateLogger<StdioClientTransport>() ?? NullLogger.Instance;
        try
        {
            LogTransportConnecting(logger, endpointName);

            ProcessStartInfo startInfo = new()
            {
                FileName = command,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _options.WorkingDirectory ?? Environment.CurrentDirectory,
                StandardOutputEncoding = StreamClientSessionTransport.NoBomUtf8Encoding,
                StandardErrorEncoding = StreamClientSessionTransport.NoBomUtf8Encoding,
#if NET
                StandardInputEncoding = StreamClientSessionTransport.NoBomUtf8Encoding,
#endif
            };

            if (arguments is not null)
            {
#if NET
                foreach (string arg in arguments)
                {
                    startInfo.ArgumentList.Add(arg);
                }
#else
                StringBuilder argsBuilder = new();
                foreach (string arg in arguments)
                {
                    PasteArguments.AppendArgument(argsBuilder, arg);
                }

                startInfo.Arguments = argsBuilder.ToString();
#endif
            }

            if (!_options.InheritEnvironmentVariables)
            {
                startInfo.Environment.Clear();
            }

            if (_options.EnvironmentVariables != null)
            {
                foreach (var entry in _options.EnvironmentVariables)
                {
                    startInfo.Environment[entry.Key] = entry.Value;
                }
            }

            // Resolve after the environment is finalized so PATH/PATHEXT match what the child sees.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                WindowsCommandResolver.Resolve(
                    startInfo.FileName,
                    WindowsCommandResolver.GetCurrentProcessPath(),
                    startInfo.WorkingDirectory,
                    startInfo.Environment.TryGetValue("PATH", out string? pathValue) ? pathValue : null,
                    startInfo.Environment.TryGetValue("PATHEXT", out string? pathExtValue) ? pathExtValue : null) is { } resolvedCommand)
            {
                if (IsBatchFile(resolvedCommand))
                {
                    // A resolved .cmd/.bat shim (for example npx.cmd) is a script, not an executable, so
                    // Windows CreateProcess cannot launch it directly while UseShellExecute is false. Route it
                    // through cmd.exe, rebuilding the command line with batch-safe quoting so arguments are
                    // preserved and cannot inject additional commands.
#if NET
                    startInfo.ArgumentList.Clear();
#endif
                    startInfo.Arguments = BuildBatchFileCommandLine(resolvedCommand, arguments);
                    startInfo.FileName = GetSystemCommandProcessor();
                }
                else
                {
                    startInfo.FileName = resolvedCommand;
                }
            }

            if (logger.IsEnabled(LogLevel.Trace))
            {
                LogCreateProcessForTransportDetailed(logger, endpointName, _options.Command,
                    startInfo.Arguments,
                    startInfo.WorkingDirectory);
            }
            else
            {
                LogCreateProcessForTransport(logger, endpointName, _options.Command);
            }

            process = new() { StartInfo = startInfo };

            // Set up stderr handling. Log all stderr output, and keep the last
            // few lines in a rolling log for use in exceptions.
            const int MaxStderrLength = 10; // keep the last 10 lines of stderr
            Queue<string> stderrRollingLog = new(MaxStderrLength);
            errorHandler = (sender, args) =>
            {
                string? data = args.Data;
                if (data is not null)
                {
                    lock (stderrRollingLog)
                    {
                        if (stderrRollingLog.Count >= MaxStderrLength)
                        {
                            stderrRollingLog.Dequeue();
                        }

                        stderrRollingLog.Enqueue(data);
                    }

                    try
                    {
                        _options.StandardErrorLines?.Invoke(data);
                    }
                    catch (Exception ex)
                    {
                        // Prevent exceptions in the user callback from propagating
                        // to the background thread that dispatches ErrorDataReceived,
                        // which would crash the process.
                        LogStderrCallbackFailed(logger, endpointName, ex);
                    }

                    LogReadStderr(logger, endpointName, data);
                }
            };
            process.ErrorDataReceived += errorHandler;

            // We need both stdin and stdout to use a no-BOM UTF-8 encoding. On .NET Core,
            // we can use ProcessStartInfo.StandardOutputEncoding/StandardInputEncoding, but
            // StandardInputEncoding doesn't exist on .NET Framework; instead, it always picks
            // up the encoding from Console.InputEncoding. As such, when not targeting .NET Core,
            // we temporarily change Console.InputEncoding to no-BOM UTF-8 around the Process.Start
            // call, to ensure it picks up the correct encoding.
#if NET
            processStarted = process.Start();
#else
            // IMPORTANT: This must be synchronized to prevent race conditions when multiple
            // transports are created concurrently.
            lock (s_consoleEncodingLock)
            {
                Encoding originalInputEncoding = Console.InputEncoding;
                bool encodingChanged = false;
                try
                {
                    try
                    {
                        Console.InputEncoding = StreamClientSessionTransport.NoBomUtf8Encoding;
                        encodingChanged = true;
                    }
                    catch
                    {
                        // Host has no usable console (e.g. WPF/WinForms on .NET Framework with no
                        // AllocConsole). The child inherits the current Console.InputEncoding;
                        // non-ASCII stdin may be misencoded, but the connect itself proceeds.
                    }

                    processStarted = process.Start();
                }
                finally
                {
                    if (encodingChanged)
                    {
                        Console.InputEncoding = originalInputEncoding;
                    }
                }
            }
#endif

            if (!processStarted)
            {
                LogTransportProcessStartFailed(logger, endpointName);
                throw new IOException("Failed to start MCP server process.");
            }

            LogTransportProcessStarted(logger, endpointName, process.Id);

            process.BeginErrorReadLine();

            return new StdioClientSessionTransport(_options, process, endpointName, stderrRollingLog, errorHandler, _loggerFactory);
        }
        catch (Exception ex)
        {
            LogTransportConnectFailed(logger, endpointName, ex);

            try
            {
                if (process is not null && errorHandler is not null)
                {
                    process.ErrorDataReceived -= errorHandler;
                }

                DisposeProcess(process, processStarted, _options.ShutdownTimeout);
            }
            catch (Exception ex2)
            {
                LogTransportShutdownFailed(logger, endpointName, ex2);
            }

            throw new IOException("Failed to connect transport.", ex);
        }
    }

    internal static void DisposeProcess(
        Process? process, bool processRunning, TimeSpan shutdownTimeout, Action? beforeDispose = null)
    {
        if (process is not null)
        {
            try
            {
                processRunning = processRunning && !HasExited(process);
                if (processRunning)
                {
                    // Wait for the process to exit.
                    // Kill the while process tree because the process may spawn child processes
                    // and Node.js does not kill its children when it exits properly.
                    process.KillTree(shutdownTimeout);
                }

                // Invoke the callback while the process handle is still valid,
                // e.g. to read ExitCode before Dispose() invalidates it.
                beforeDispose?.Invoke();
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>Gets a value that indicates whether <paramref name="process"/> has exited.</summary>
    internal static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Gets whether the resolved command is a batch script that cmd.exe must interpret.</summary>
    private static bool IsBatchFile(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets the path to the system copy of cmd.exe used to launch batch files.</summary>
    private static string GetSystemCommandProcessor()
    {
        // Use the system copy of cmd.exe rather than the overridable ComSpec environment variable: this
        // launch path requires cmd.exe's /d /c syntax, and an attacker-controlled ComSpec could otherwise
        // redirect batch-file execution to a different interpreter, bypassing the resolver's hardening.
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return string.IsNullOrEmpty(systemDirectory) ? "cmd.exe" : Path.Combine(systemDirectory, "cmd.exe");
    }

    /// <summary>
    /// Builds a verbatim cmd.exe command line that launches a <c>.cmd</c>/<c>.bat</c> script with the
    /// supplied arguments. The script name and every argument are wrapped in an outer quote pair and
    /// escaped so that the batch file receives the arguments intact and cannot inject further commands.
    /// The construction mirrors the batch-file mitigation shipped in the .NET/Rust standard libraries
    /// for CVE-2024-24576.
    /// </summary>
    private static string BuildBatchFileCommandLine(string batchFilePath, IList<string>? arguments)
    {
        StringBuilder builder = new();

        // /e:ON enables command extensions (required by the '%' escaping below), /v:OFF keeps '!' literal,
        // and /d skips AutoRun commands. The command is wrapped in an extra pair of quotes that cmd.exe
        // strips before executing the enclosed script.
        builder.Append("/e:ON /v:OFF /d /c \"");

        builder.Append('"').Append(batchFilePath).Append('"');

        if (arguments is not null)
        {
            foreach (string argument in arguments)
            {
                if (argument.IndexOf('\r') >= 0 || argument.IndexOf('\n') >= 0)
                {
                    throw new ArgumentException(
                        "Arguments passed to a batch file (.cmd/.bat) may not contain carriage return or line feed characters.",
                        nameof(arguments));
                }

                builder.Append(' ');
                AppendBatchArgument(builder, argument);
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static void AppendBatchArgument(StringBuilder builder, string argument)
    {
        // Quote empty arguments (otherwise they are dropped) and arguments ending in '\' (a trailing
        // backslash would otherwise escape the closing quote).
        bool quote = argument.Length == 0 || argument[argument.Length - 1] == '\\';

        if (!quote)
        {
            // Assume every ASCII symbol must be quoted unless it is known to be safe, and quote control
            // characters for good measure. An unquoted '\' is fine so long as the argument is not quoted.
            const string Unquoted = "#$*+-./:?@\\_";
            foreach (char c in argument)
            {
                bool asciiNeedsQuotes = c < 128 && !(char.IsLetterOrDigit(c) || Unquoted.IndexOf(c) >= 0);
                if (asciiNeedsQuotes || char.IsControl(c))
                {
                    quote = true;
                    break;
                }
            }
        }

        if (quote)
        {
            builder.Append('"');
        }

        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else
            {
                if (c == '"')
                {
                    // Emit enough backslashes to total 2n before the internal quote, then double the quote.
                    builder.Append('\\', backslashes);
                    builder.Append('"');
                }
                else if (c == '%' || c == '\r')
                {
                    // Prevent %VAR% expansion by inserting a zero-length substring of the built-in cd variable.
                    builder.Append("%%cd:~,");
                }

                backslashes = 0;
            }

            builder.Append(c);
        }

        if (quote)
        {
            builder.Append('\\', backslashes);
            builder.Append('"');
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{EndpointName} connecting.")]
    private static partial void LogTransportConnecting(ILogger logger, string endpointName);

    [LoggerMessage(Level = LogLevel.Information, Message = "{EndpointName} starting server process. Command: '{Command}'.")]
    private static partial void LogCreateProcessForTransport(ILogger logger, string endpointName, string command);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{EndpointName} starting server process. Command: '{Command}', Arguments: {Arguments}, Working directory: {WorkingDirectory}.")]
    private static partial void LogCreateProcessForTransportDetailed(ILogger logger, string endpointName, string command, string? arguments, string workingDirectory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EndpointName} failed to start server process.")]
    private static partial void LogTransportProcessStartFailed(ILogger logger, string endpointName);

    [LoggerMessage(Level = LogLevel.Information, Message = "{EndpointName} received stderr log: '{Data}'.")]
    private static partial void LogReadStderr(ILogger logger, string endpointName, string data);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EndpointName} StandardErrorLines callback failed.")]
    private static partial void LogStderrCallbackFailed(ILogger logger, string endpointName, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "{EndpointName} started server process with PID {ProcessId}.")]
    private static partial void LogTransportProcessStarted(ILogger logger, string endpointName, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EndpointName} connect failed.")]
    private static partial void LogTransportConnectFailed(ILogger logger, string endpointName, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EndpointName} shutdown failed.")]
    private static partial void LogTransportShutdownFailed(ILogger logger, string endpointName, Exception exception);

#if NET
    [GeneratedRegex(@"[\s\.]+")]
    private static partial Regex WhitespaceAndPeriods();
#else
    private static Regex WhitespaceAndPeriods() => s_whitespaceAndPeriods;
    private static readonly Regex s_whitespaceAndPeriods = new(@"[\s\.]+", RegexOptions.Compiled);
#endif
}
