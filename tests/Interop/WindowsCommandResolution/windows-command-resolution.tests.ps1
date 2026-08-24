$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-ResolvedPath {
    param(
        [Parameter(Mandatory)] [string] $Command,
        [Parameter(Mandatory)] [string] $ExpectedPath
    )

    $resolved = Get-Command -CommandType Application -Name $Command -ErrorAction Stop |
        Select-Object -First 1 -ExpandProperty Source

    if (-not [string]::Equals($resolved, $ExpectedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Expected '$Command' to resolve to '$ExpectedPath', but it resolved to '$resolved'."
    }
}

$originalPath = $env:PATH
$originalPathExt = $env:PATHEXT
$root = Join-Path ([IO.Path]::GetTempPath()) "mcp-pwsh-command-resolution-$([Guid]::NewGuid().ToString('N'))"
$shims = Join-Path $root 'command shims'

New-Item -ItemType Directory -Path $shims -Force | Out-Null

try {
    $env:PATH = "$shims;$originalPath"
    $env:PATHEXT = ".COM;.EXE;.BAT;.CMD;$originalPathExt"

    $npxShim = Join-Path $shims 'npx.cmd'
    Set-Content -Path $npxShim -Value '@echo off' -Encoding ascii
    Assert-ResolvedPath -Command 'npx' -ExpectedPath $npxShim
    Write-Host 'PASS: PowerShell resolves bare npx to npx.cmd on PATH.'

    $nodePath = (Get-Command -CommandType Application -Name 'node.exe' -ErrorAction Stop |
        Select-Object -First 1 -ExpandProperty Source)
    $uvicornExecutable = Join-Path $shims 'uvicorn.exe'
    Copy-Item -Path $nodePath -Destination $uvicornExecutable
    Assert-ResolvedPath -Command 'uvicorn' -ExpectedPath $uvicornExecutable
    Write-Host 'PASS: PowerShell resolves bare uvicorn to uvicorn.exe on PATH.'

    Remove-Item -Path $uvicornExecutable
    $doubleExtensionShim = Join-Path $shims 'uvicorn.exe.cmd'
    Set-Content -Path $doubleExtensionShim -Value '@echo off' -Encoding ascii
    Assert-ResolvedPath -Command 'uvicorn.exe' -ExpectedPath $doubleExtensionShim
    Write-Host 'PASS: PowerShell appends PATHEXT after an existing extension.'
}
finally {
    $env:PATH = $originalPath
    $env:PATHEXT = $originalPathExt
    Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue
}

$installedNpx = Get-Command -CommandType Application -Name 'npx' -ErrorAction Stop |
    Select-Object -First 1 -ExpandProperty Source
if (-not $installedNpx.EndsWith('\npx.cmd', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Expected installed npx to resolve to npx.cmd, but it resolved to '$installedNpx'."
}
Write-Host "PASS: Installed npx resolves to '$installedNpx'."

