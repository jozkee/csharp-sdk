using ModelContextProtocol.Client;
using ModelContextProtocol.Tests.Utils;
using System.Reflection;

namespace ModelContextProtocol.Tests.Transport;

/// <summary>
/// Tests for the Windows-only PATHEXT command resolution that <see cref="StdioClientTransport"/> performs
/// before launching a server process. Win32 CreateProcess does not consult PATHEXT, so a bare command such
/// as "npx" (really "npx.cmd") would otherwise fail to launch. The resolver is only invoked on Windows, so
/// these tests only run there.
/// </summary>
/// <remarks>
/// The resolver reads <c>PATH</c>, <c>PATHEXT</c>, and the process' current directory from the environment,
/// so the tests mutate that process-global state and therefore run in the non-parallel collection.
/// </remarks>
[Collection(nameof(DisableParallelization))]
public class WindowsCommandResolverTests(ITestOutputHelper testOutputHelper) : LoggedTest(testOutputHelper)
{
    public static bool IsWindows => PlatformDetection.IsWindows;

    /// <summary>A typical Windows PATHEXT value, using lowercase entries so probing also matches on case-sensitive file systems.</summary>
    private static readonly string WindowsPathExt = string.Join(Path.PathSeparator.ToString(), [".com", ".exe", ".bat", ".cmd"]);

    /// <summary>Sentinel meaning "use the default <see cref="WindowsPathExt"/>", so tests can still pass an explicit <see langword="null"/>.</summary>
    private const string DefaultPathExt = "\u0000default";

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void Resolve_BareName_FoundInPathDirectory()
    {
        using TempDirectory root = new();
        string pathDirectory = root.CreateSubdirectory("path");
        string command = $"probe-{Guid.NewGuid():N}";
        string expected = CreateExecutableFile(Path.Combine(pathDirectory, command + ".exe"));

        AssertSamePath(expected, Resolve(command, path: pathDirectory));
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void Resolve_CommandNotFound_ReturnsNull()
    {
        using TempDirectory root = new();
        Assert.Null(Resolve($"probe-{Guid.NewGuid():N}", path: root.Path, currentDirectory: root.Path));
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void Resolve_RootedCommand_ResolvedWithoutSearching()
    {
        using TempDirectory root = new();
        string command = Path.Combine(root.Path, $"probe-{Guid.NewGuid():N}");
        string expected = CreateExecutableFile(command + ".exe");

        AssertSamePath(expected, Resolve(command));
        Assert.Null(Resolve(Path.Combine(root.Path, $"missing-{Guid.NewGuid():N}")));
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void Resolve_RootedCommandWithSpaces_Resolved()
    {
        // Regression test for https://github.com/modelcontextprotocol/csharp-sdk/issues/1601: a command whose
        // absolute path contains spaces (e.g. under "C:\Program Files\...") must resolve correctly.
        using TempDirectory root = new();
        string directoryWithSpaces = root.CreateSubdirectory("Program Files");
        string command = Path.Combine(directoryWithSpaces, $"probe {Guid.NewGuid():N}");
        string expected = CreateExecutableFile(command + ".exe");

        AssertSamePath(expected, Resolve(command));
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void Resolve_SearchesCurrentDirectoryThenPath()
    {
        using TempDirectory root = new();
        string currentDirectory = root.CreateSubdirectory("current");
        string pathDirectory = root.CreateSubdirectory("path");

        string command = $"probe-{Guid.NewGuid():N}";
        string currentCandidate = CreateExecutableFile(Path.Combine(currentDirectory, command + ".exe"));
        string pathCandidate = CreateExecutableFile(Path.Combine(pathDirectory, command + ".exe"));

        AssertSamePath(currentCandidate, Resolve(command, currentDirectory: currentDirectory, path: pathDirectory));
        File.Delete(currentCandidate);
        AssertSamePath(pathCandidate, Resolve(command, currentDirectory: currentDirectory, path: pathDirectory));
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void Resolve_CurrentDirectoryNotSearched_SkipsCurrentDirectory()
    {
        using TempDirectory root = new();
        string currentDirectory = root.CreateSubdirectory("current");
        string pathDirectory = root.CreateSubdirectory("path");

        string command = $"probe-{Guid.NewGuid():N}";
        CreateExecutableFile(Path.Combine(currentDirectory, command + ".exe"));
        string pathCandidate = CreateExecutableFile(Path.Combine(pathDirectory, command + ".exe"));

        AssertSamePath(pathCandidate, Resolve(command, currentDirectory: currentDirectory, path: pathDirectory, searchCurrentDirectory: false));
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void Resolve_AppendsPathExtExtensions()
    {
        // A bare command such as "npx" must resolve to the "npx.cmd" shim via PATHEXT.
        using TempDirectory root = new();
        string pathDirectory = root.CreateSubdirectory("path");
        string command = $"probe-{Guid.NewGuid():N}";
        string expected = CreateExecutableFile(Path.Combine(pathDirectory, command + ".cmd"));

        AssertSamePath(expected, Resolve(command, path: pathDirectory, pathExt: WindowsPathExt));

        // Without PATHEXT there is nothing to expand, so the resolver defers to CreateProcess and returns null.
        Assert.Null(Resolve(command, path: pathDirectory, pathExt: null));
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void Resolve_PathExtEntries_AreUsedVerbatim()
    {
        using TempDirectory root = new();
        string pathDirectory = root.CreateSubdirectory("path");
        string command = $"probe-{Guid.NewGuid():N}";
        string expected = CreateExecutableFile(Path.Combine(pathDirectory, command + ".cmd"));

        AssertSamePath(expected, Resolve(command, path: pathDirectory, pathExt: ".cmd"));

        // Entries are used verbatim, matching node-which and Python's shutil.which: a dotless entry is
        // concatenated as-is (probing "<command>cmd"), so it does not resolve the ".cmd" shim.
        Assert.Null(Resolve(command, path: pathDirectory, pathExt: "cmd"));
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void Resolve_CommandWithExtension_PrefersExactNameOverPathExtCandidate()
    {
        using TempDirectory root = new();
        string pathDirectory = root.CreateSubdirectory("path");
        string command = $"probe-{Guid.NewGuid():N}.exe";
        string shim = CreateExecutableFile(Path.Combine(pathDirectory, command + ".cmd"));

        // With only the shim present, the PATHEXT candidate wins.
        AssertSamePath(shim, Resolve(command, path: pathDirectory, pathExt: WindowsPathExt));

        // Once the exact name exists, it takes precedence.
        string exact = CreateExecutableFile(Path.Combine(pathDirectory, command));
        AssertSamePath(exact, Resolve(command, path: pathDirectory, pathExt: WindowsPathExt));
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void Resolve_DottedDirectory_DoesNotTreatDirectoryDotAsExtension()
    {
        // A dot in a *directory* name must not make an extensionless command probe its exact
        // (extensionless) name; the decision is based on the file name only.
        using TempDirectory root = new();
        string dottedDirectory = root.CreateSubdirectory("tools.v1");
        string command = Path.Combine(dottedDirectory, "server");
        CreateExecutableFile(command); // Extensionless decoy that must not win.
        string expected = CreateExecutableFile(command + ".exe");

        AssertSamePath(expected, Resolve(command, pathExt: WindowsPathExt));
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void Resolve_WorkingDirectoryDoesNotAffectResolution()
    {
        // The resolver looks up commands using the process' current directory and PATH only. A bare command
        // whose executable lives in some unrelated directory must not resolve just because that directory is
        // the child's working directory. This matches Linux, where the requested working directory does not
        // participate in executable lookup.
        using TempDirectory root = new();
        string workingDirectory = root.CreateSubdirectory("working");
        string command = $"probe-{Guid.NewGuid():N}";
        CreateExecutableFile(Path.Combine(workingDirectory, command + ".exe"));

        // currentDirectory is left as an unrelated empty directory; PATH is empty. The executable exists only
        // under "workingDirectory", which is never consulted, so nothing is found.
        string emptyCurrentDirectory = root.CreateSubdirectory("current");
        Assert.Null(Resolve(command, currentDirectory: emptyCurrentDirectory, path: null));
    }

    private static Type ResolverType { get; } = typeof(StdioClientTransport).Assembly
        .GetType("ModelContextProtocol.Client.WindowsCommandResolver", throwOnError: true)!;

    /// <summary>
    /// Invokes the internal single-argument <c>WindowsCommandResolver.Resolve</c> after applying the requested
    /// <c>PATH</c>, <c>PATHEXT</c>, current-directory, and current-directory-participation environment, then
    /// restores the previous environment. The result is normalized to a full path (resolved against the scoped
    /// current directory) so relative candidates compare cleanly.
    /// </summary>
    private static string? Resolve(
        string command,
        string? currentDirectory = null,
        string? path = null,
        string? pathExt = DefaultPathExt,
        bool searchCurrentDirectory = true)
    {
        if (pathExt == DefaultPathExt)
        {
            pathExt = WindowsPathExt;
        }

        using EnvironmentScope scope = new(path, pathExt, currentDirectory, searchCurrentDirectory);
        string? result = InvokeResolve(command);
        return result is null ? null : Path.GetFullPath(result);
    }

    private static string? InvokeResolve(string command)
    {
        MethodInfo method = ResolverType.GetMethod(
            "Resolve",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static,
            binder: null,
            [typeof(string)],
            modifiers: null)
            ?? throw new InvalidOperationException("Could not find WindowsCommandResolver.Resolve via reflection.");

        try
        {
            return (string?)method.Invoke(null, [command]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    /// <summary>Creates the file if it doesn't exist.</summary>
    private static string CreateExecutableFile(string path)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "@echo off\r\n");
        }

        return path;
    }

    private static void AssertSamePath(string expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(Path.GetFullPath(expected), actual, ignoreCase: true);
    }

    /// <summary>
    /// Applies a temporary <c>PATH</c>, <c>PATHEXT</c>, current directory, and current-directory-participation
    /// (via <c>NoDefaultCurrentDirectoryInExePath</c>) environment, restoring the previous values on dispose.
    /// </summary>
    private sealed class EnvironmentScope : IDisposable
    {
        private const string NoDefaultCurrentDirectory = "NoDefaultCurrentDirectoryInExePath";

        private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
        private readonly string? _pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        private readonly string? _noDefaultCurrentDirectory = Environment.GetEnvironmentVariable(NoDefaultCurrentDirectory);
        private readonly string _currentDirectory = Environment.CurrentDirectory;

        public EnvironmentScope(string? path, string? pathExt, string? currentDirectory, bool searchCurrentDirectory)
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Environment.SetEnvironmentVariable("PATHEXT", pathExt);
            Environment.SetEnvironmentVariable(NoDefaultCurrentDirectory, searchCurrentDirectory ? null : "1");
            if (currentDirectory is not null)
            {
                Environment.CurrentDirectory = currentDirectory;
            }
        }

        public void Dispose()
        {
            Environment.CurrentDirectory = _currentDirectory;
            Environment.SetEnvironmentVariable("PATH", _path);
            Environment.SetEnvironmentVariable("PATHEXT", _pathExt);
            Environment.SetEnvironmentVariable(NoDefaultCurrentDirectory, _noDefaultCurrentDirectory);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mcp-resolve-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateSubdirectory(string name)
        {
            string directory = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(directory);
            return directory;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
