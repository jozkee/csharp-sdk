using System.Runtime.InteropServices;
#if !NET
using System.Diagnostics;
#endif

namespace ModelContextProtocol.Client;

/// <summary>
/// Resolves a command to a concrete executable path using the same directory precedence as
/// .NET's Unix process launcher, with Windows <c>PATHEXT</c> probing and path handling from Node and Python.
/// </summary>
/// <remarks>
/// Win32 <c>CreateProcess</c> does not search <c>PATH</c>/<c>PATHEXT</c> when <c>UseShellExecute</c> is
/// <see langword="false"/>, so a bare command such as <c>npx</c> (really <c>npx.cmd</c>) fails to launch.
/// The same resolution runs on Unix as well, where there is no <c>PATHEXT</c> and no current-directory
/// search, so that command location is consistent across platforms.
/// </remarks>
internal static class WindowsCommandResolver
{
    /// <summary>Gets the full path of the current process executable, matching <c>Environment.ProcessPath</c>.</summary>
    public static string? GetCurrentProcessPath()
    {
#if NET
        return Environment.ProcessPath;
#else
        using Process currentProcess = Process.GetCurrentProcess();
        return currentProcess.MainModule?.FileName;
#endif
    }

    /// <summary>Resolves <paramref name="command"/> to the full path of the file that would be launched, or <see langword="null"/> if no such file was found.</summary>
    /// <param name="command">The command to resolve. It may be rooted, relative, or a bare name.</param>
    /// <param name="processPath">The path of the current process executable, whose directory is searched like <c>CreateProcess</c> searches the application directory.</param>
    /// <param name="currentDirectory">The working directory the child process will use. Relative <paramref name="path"/> entries are resolved against it.</param>
    /// <param name="path">The child process' <c>PATH</c> value.</param>
    /// <param name="pathExt">The child process' <c>PATHEXT</c> value. It is <see langword="null"/> or empty on Unix.</param>
    /// <param name="noDefaultCurrentDirectoryInExePath">The child process' <c>NoDefaultCurrentDirectoryInExePath</c> value. When set, the current directory is not searched.</param>
    public static string? Resolve(
        string command,
        string? processPath,
        string currentDirectory,
        string? path,
        string? pathExt,
        string? noDefaultCurrentDirectoryInExePath)
    {
        // PATHEXT is a Windows concept and is always semicolon-separated. It is normally undefined
        // elsewhere, in which case no extensions are probed and only the exact name is used.
        string[] extensions = (pathExt ?? string.Empty)
            .Split([';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static ext => ext.Trim())
            .Where(static ext => ext.Length != 0)
            .Select(static ext => ext[0] == '.' ? ext : "." + ext)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // The exact-name decision is based on the file name only, so that a dot in a directory name
        // (e.g. "C:\tools.v1\server") isn't mistaken for an extension on the command itself.
        string fileName = Path.GetFileName(command);
        bool probeExactName = extensions.Length == 0 || fileName.IndexOf('.') >= 0 ||
            extensions.Any(extension => fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

        if (Path.IsPathRooted(command))
        {
            return FindFirstCandidate(command, probeExactName, extensions);
        }

        bool containsDirectorySeparator =
            command.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
            command.IndexOf(Path.AltDirectorySeparatorChar) >= 0;

        if (!containsDirectorySeparator && processPath is not null)
        {
            try
            {
                string? processDirectory = Path.GetDirectoryName(processPath);
                if (processDirectory is not null &&
                    FindFirstCandidate(Path.Combine(processDirectory, command), probeExactName, extensions) is { } processDirectoryResult)
                {
                    return processDirectoryResult;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        if ((containsDirectorySeparator || ShouldSearchCurrentDirectory(noDefaultCurrentDirectoryInExePath)) &&
            FindFirstCandidate(Path.Combine(currentDirectory, command), probeExactName, extensions) is { } currentDirectoryResult)
        {
            return currentDirectoryResult;
        }

        // Match the Win32 CreateProcess search order: the system directories are probed after the
        // application and current directories but before PATH. Searching them here prevents a
        // user-controlled PATH entry from shadowing a trusted system executable such as cmd.exe.
        if (!containsDirectorySeparator)
        {
            foreach (string systemDirectory in GetSystemSearchDirectories())
            {
                if (FindFirstCandidate(Path.Combine(systemDirectory, command), probeExactName, extensions) is { } systemDirectoryResult)
                {
                    return systemDirectoryResult;
                }
            }
        }

        if (!containsDirectorySeparator && path is not null)
        {
            HashSet<string> seenDirectories = new(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (string rawDirectory in path.Split([Path.PathSeparator], StringSplitOptions.RemoveEmptyEntries))
            {
                string directory = rawDirectory.Trim().Trim('"');
                if (directory.Length != 0 && seenDirectories.Add(directory) &&
                    FindFirstCandidate(Path.Combine(currentDirectory, directory, command), probeExactName, extensions) is { } pathResult)
                {
                    return pathResult;
                }
            }
        }

        return null;
    }

    private static string? FindFirstCandidate(string baseName, bool probeExactName, string[] extensions)
    {
        if (probeExactName && TryGetExistingFullPath(baseName) is { } exact)
        {
            return exact;
        }

        foreach (string extension in extensions)
        {
            if (TryGetExistingFullPath(baseName + extension) is { } candidate)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? TryGetExistingFullPath(string candidate)
    {
        try
        {
            if (File.Exists(candidate) && IsExecutable(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// Gets whether the file may be executed. On Windows any existing file is a candidate, as the
    /// set of executable extensions is expressed by <c>PATHEXT</c>. On Unix, the execute bit is checked,
    /// matching what <see cref="System.Diagnostics.Process"/> does before <c>execve</c>.
    /// </summary>
    private static bool IsExecutable(string candidate)
    {
#if NET
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            const UnixFileMode ExecuteBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(candidate) & ExecuteBits) != 0;
        }
#endif
        return true;
    }

    // Matches Win32 NeedCurrentDirectoryForExePath: skip the current directory when the caller opted out.
    // The value comes from the finalized child environment so it reflects what the launched process sees.
    private static bool ShouldSearchCurrentDirectory(string? noDefaultCurrentDirectoryInExePath) =>
        string.IsNullOrEmpty(noDefaultCurrentDirectoryInExePath);

    // The trusted directories CreateProcess searches after the application and current directories and
    // before PATH: the system directory, the 16-bit system directory, and the Windows directory.
    // There is no equivalent on Unix, where these all resolve to empty and nothing is searched.
    private static IEnumerable<string> GetSystemSearchDirectories()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield break;
        }

        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrEmpty(systemDirectory))
        {
            yield return systemDirectory;
        }

        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windowsDirectory))
        {
            yield return Path.Combine(windowsDirectory, "System");
            yield return windowsDirectory;
        }
    }
}
