using ModelContextProtocol.Client;
using ModelContextProtocol.Tests.Utils;
using System.Reflection;

namespace ModelContextProtocol.Tests.Transport;

/// <summary>
/// Tests for the Windows-only PATHEXT command resolution that <see cref="StdioClientTransport"/> performs
/// before launching a server process. Win32 CreateProcess does not consult PATHEXT, so a bare command such
/// as "npx" (really "npx.cmd") would otherwise fail to launch. The resolver is a no-op on non-Windows
/// platforms, so these tests only run on Windows.
/// </summary>
public class WindowsCommandResolverTests(ITestOutputHelper testOutputHelper) : LoggedTest(testOutputHelper)
{
    public static bool IsWindows => PlatformDetection.IsWindows;

    /// <summary>A typical Windows PATHEXT value, using lowercase entries so probing also matches on case-sensitive file systems.</summary>
    private static readonly string WindowsPathExt = string.Join(Path.PathSeparator, [".com", ".exe", ".bat", ".cmd"]);

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
    public void Resolve_RelativePathEntry_ResolvedAgainstCurrentDirectory()
    {
        // A relative PATH entry (for example "tools") must be evaluated against the supplied working
        // directory, not the host's actual current directory.
        using TempDirectory root = new();
        string toolsDirectory = root.CreateSubdirectory("tools");
        string command = $"probe-{Guid.NewGuid():N}";
        string expected = CreateExecutableFile(Path.Combine(toolsDirectory, command + ".exe"));

        AssertSamePath(expected, Resolve(command, currentDirectory: root.Path, path: "tools"));
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
    public void Resolve_PathEntries_HandlesQuotesAndDuplicates()
    {
        using TempDirectory root = new();
        string pathDirectory = root.CreateSubdirectory("path with spaces");
        string command = $"probe-{Guid.NewGuid():N}";
        string expected = CreateExecutableFile(Path.Combine(pathDirectory, command + ".exe"));

        string path = $"{Path.PathSeparator}\"{pathDirectory}\"{Path.PathSeparator}{pathDirectory}{Path.PathSeparator}";

        AssertSamePath(expected, Resolve(command, currentDirectory: root.Path, path: path));
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
    public void Resolve_PathExtEntries_AreTrimmedAndDeduplicated()
    {
        using TempDirectory root = new();
        string pathDirectory = root.CreateSubdirectory("path");
        string command = $"probe-{Guid.NewGuid():N}";
        string expected = CreateExecutableFile(Path.Combine(pathDirectory, command + ".cmd"));

        // Entries may be padded or repeat with different casing; they are trimmed and deduplicated.
        char separator = Path.PathSeparator;
        AssertSamePath(expected, Resolve(command, path: pathDirectory, pathExt: $" .cmd {separator}{separator}.CMD"));

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
    public void Resolve_UsesCurrentProcessEnvironment()
    {
        // The public overload takes only the command and working directory: PATH, PATHEXT, and the
        // process path all come from the current process' environment.
        using TempDirectory root = new();
        string pathDirectory = root.CreateSubdirectory("path");
        string command = $"probe-{Guid.NewGuid():N}";
        string expected = CreateExecutableFile(Path.Combine(pathDirectory, command + ".exe"));

        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Assert.Null(ResolveFromCurrentEnvironment(command, root.Path));

            Environment.SetEnvironmentVariable("PATH", pathDirectory + Path.PathSeparator + originalPath);
            AssertSamePath(expected, ResolveFromCurrentEnvironment(command, root.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    private static string? ResolveFromCurrentEnvironment(string command, string currentDirectory)
    {
        MethodInfo method = ResolverType.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static, binder: null, [typeof(string), typeof(string)], modifiers: null)
            ?? throw new InvalidOperationException("Could not find WindowsCommandResolver.Resolve via reflection.");

        return (string?)method.Invoke(null, [command, currentDirectory]);
    }

    private static Type ResolverType { get; } = typeof(StdioClientTransport).Assembly
        .GetType("ModelContextProtocol.Client.WindowsCommandResolver", throwOnError: true)!;

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

        MethodInfo method = ResolverType.GetMethod(
            "Resolve",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [typeof(string), typeof(string), typeof(string), typeof(string), typeof(bool)],
            modifiers: null)
            ?? throw new InvalidOperationException("Could not find WindowsCommandResolver.Resolve via reflection.");

        return (string?)method.Invoke(null, [command, currentDirectory ?? Environment.CurrentDirectory, path, pathExt, searchCurrentDirectory]);
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
