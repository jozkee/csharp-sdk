using System.Runtime.InteropServices;

namespace ModelContextProtocol.Client;

/// <summary>
/// Resolves a Windows command to a concrete executable path by probing <c>PATHEXT</c> extensions across the
/// current directory and <c>PATH</c>, so a bare command such as <c>npx</c> (really <c>npx.cmd</c>) can be
/// launched directly. The search follows CPython's <c>shutil.which</c>.
/// </summary>
/// <remarks>
/// Win32 <c>CreateProcess</c> does not consult <c>PATHEXT</c> when <c>UseShellExecute</c> is
/// <see langword="false"/>, so a bare command such as <c>npx</c> fails to launch because only
/// <c>npx</c>/<c>npx.exe</c> are tried, never <c>npx.cmd</c>. This resolver fills only that gap. On
/// non-Windows platforms the OS process launcher already resolves commands via <c>PATH</c>, so
/// <see cref="Resolve(string, string)"/> returns <see langword="null"/> there and the caller launches the command unchanged.
/// </remarks>
internal static class WindowsCommandResolver
{
    /// <summary>Resolves <paramref name="command"/> to the full path of the file that would be launched, or <see langword="null"/> if no such file was found (including on non-Windows platforms).</summary>
    /// <param name="command">The command to resolve. It may be rooted, relative, or a bare name.</param>
    /// <param name="currentDirectory">The working directory the child process will use. Relative <c>PATH</c> entries are resolved against it.</param>
    /// <remarks>The search uses the current process' environment, just as <c>CreateProcess</c> does.</remarks>
    public static string? Resolve(string command, string currentDirectory)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        return Resolve(
            command,
            currentDirectory,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT"),
            NeedCurrentDirectoryForExePath(command));
    }

    /// <param name="command">The command to resolve. It may be rooted, relative, or a bare name.</param>
    /// <param name="currentDirectory">The working directory the child process will use. Relative <paramref name="path"/> entries are resolved against it.</param>
    /// <param name="path">The <c>PATH</c> value.</param>
    /// <param name="pathExt">The <c>PATHEXT</c> value.</param>
    /// <param name="searchCurrentDirectory">Whether the current directory should be searched for a bare command name.</param>
    private static string? Resolve(
        string command,
        string currentDirectory,
        string? path,
        string? pathExt,
        bool searchCurrentDirectory)
    {
        // PATHEXT lists the extensions that make a file executable, separated the same way as PATH.
        string[] extensions = (pathExt ?? string.Empty)
            .Split([Path.PathSeparator], StringSplitOptions.RemoveEmptyEntries)
            .Select(static ext => ext.Trim())
            .Where(static ext => ext.Length != 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // PATHEXT expansion is the only thing this resolver adds over CreateProcess, which already searches
        // the current directory and PATH for the exact name. With no extensions to append there is nothing
        // left to do, so defer to CreateProcess by reporting no match.
        if (extensions.Length == 0)
        {
            return null;
        }

        // probeExactName is true when the name already carries an extension, so an explicitly named file is
        // tried verbatim before the PATHEXT candidates:
        //   "npx"     -> false: probes "npx.COM", "npx.EXE", "npx.BAT", "npx.CMD" (the bare name isn't launchable).
        //   "npx.cmd" -> true (dot + ends with a PATHEXT entry): probes "npx.cmd" first, then "npx.cmd.COM", ...
        // Decided from the file name only, so a dot in a directory (e.g. "C:\tools.v1\server") isn't misread.
        string fileName = Path.GetFileName(command);
        bool probeExactName = fileName.IndexOf('.') >= 0 ||
            extensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        if (Path.IsPathRooted(command))
        {
            return FindFirstCandidate(command, probeExactName, extensions);
        }

        ReadOnlySpan<char> separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
        if (command.AsSpan().IndexOfAny(separators) >= 0)
        {
            // A relative path with a directory part is resolved against the working directory only, never
            // against PATH, matching cmd.exe and shutil.which.
            return FindFirstCandidate(Path.Combine(currentDirectory, command), probeExactName, extensions);
        }

        // A bare command name is searched along ".;PATH" when the current directory participates, or "PATH"
        // otherwise, exactly as cmd.exe does per NeedCurrentDirectoryForExePath. Relative PATH entries are
        // resolved against the working directory.
        List<string> searchDirectories = [];
        if (searchCurrentDirectory)
        {
            searchDirectories.Add(currentDirectory);
        }

        if (path is not null)
        {
            foreach (string rawDirectory in path.Split([Path.PathSeparator], StringSplitOptions.RemoveEmptyEntries))
            {
                string directory = rawDirectory.Trim().Trim('"');
                if (directory.Length != 0)
                {
                    searchDirectories.Add(Path.Combine(currentDirectory, directory));
                }
            }
        }

        HashSet<string> seenDirectories = new(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in searchDirectories)
        {
            if (seenDirectories.Add(directory) &&
                FindFirstCandidate(Path.Combine(directory, command), probeExactName, extensions) is { } result)
            {
                return result;
            }
        }

        return null;
    }

    private static string? FindFirstCandidate(string baseName, bool probeExactName, string[] extensions)
    {
        // File.Exists returns false (never throws) for a missing, malformed, or inaccessible path, and for
        // a directory, so any hit is an existing file. On Windows PATHEXT defines the executable extensions.
        if (probeExactName && File.Exists(baseName))
        {
            return baseName;
        }

        foreach (string extension in extensions)
        {
            string candidate = baseName + extension;
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    // Whether the current directory should be searched for a bare command name. The Win32 API is called
    // rather than reading NoDefaultCurrentDirectoryInExePath directly, because its registry location can
    // change. https://learn.microsoft.com/windows/win32/api/processenv/nf-processenv-needcurrentdirectoryforexepatha
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "NeedCurrentDirectoryForExePathW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool NeedCurrentDirectoryForExePath(string exeName);
}
