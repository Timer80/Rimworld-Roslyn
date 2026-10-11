<#
.SYNOPSIS
    Guard: assert the preload assembly contains NO Microsoft.CodeAnalysis typeref.

.DESCRIPTION
    Why this is a hard constraint:
    This package exists to eliminate "a mod type's field references an unloaded Roslyn
    type -> TypeLoadException during field scan". If the preload package itself referenced
    Roslyn types at compile time, it would become a new victim of that very bug -- moving a
    guaranteed failure from an optional consumer onto a package everyone must install.

    How the check works (and why ASCII-only):
    ECMA-335 keeps type and assembly names in the #Strings heap as plain UTF-8 (one byte per
    char), so an AssemblyRef or TypeRef naming Microsoft.CodeAnalysis shows up as contiguous
    ASCII bytes in the file. String *literals* live in the #US heap as length-prefixed UTF-16,
    so they appear as "M.i.c.r.o..." with interleaved NUL bytes and never match. Scanning only
    the ASCII projection therefore separates real metadata references from the deliberate
    string constants this package uses to drive reflection.

    The authoritative deep audit (real AssemblyRef/TypeRef table enumeration via
    System.Reflection.Metadata) lives in tests/TyperefAudit. This script is the fast,
    dependency-free build-time gate.

.PARAMETER Assembly
    Path to the dll to inspect.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Assembly
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Assembly)) {
    Write-Host "FAIL: assembly not found: $Assembly"
    exit 2
}

$full = (Resolve-Path -LiteralPath $Assembly).Path
$bytes = [System.IO.File]::ReadAllBytes($full)

# Only the ASCII projection matters: metadata names are UTF-8 in #Strings, while string
# literals are UTF-16 in #US and therefore never appear as contiguous ASCII here.
$ascii = [System.Text.Encoding]::ASCII.GetString($bytes)

$hits = New-Object System.Collections.Generic.List[string]
foreach ($needle in @('Microsoft.CodeAnalysis', 'Microsoft.CodeAnalysis.CSharp')) {
    if ($ascii.Contains($needle)) { $hits.Add("ASCII metadata name: $needle") }
}

Write-Host "=== typeref guard ==="
Write-Host "assembly : $full"
Write-Host ("size     : {0} bytes" -f $bytes.Length)

if ($hits.Count -gt 0) {
    Write-Host "FAIL: Roslyn metadata reference present in product:" -ForegroundColor Red
    foreach ($h in $hits) { Write-Host "  - $h" -ForegroundColor Red }
    Write-Host "This package must not reference Roslyn at compile time. Use string + reflection."
    exit 1
}

Write-Host "PASS: no Microsoft.CodeAssembly AssemblyRef/TypeRef metadata in product." -ForegroundColor Green
Write-Host "      (UTF-16 string literals are expected and harmless -- they drive reflection.)"
exit 0