# Common.ps1
# Shared helpers for the release pipeline (publish.ps1, make-zip.ps1).
#
# ASCII-only on purpose: PowerShell 5.1 reads a BOM-less .ps1 as ANSI, which
# corrupts non-ASCII characters and raises ParserError. Non-ASCII literals are
# therefore expressed as code points (see $script:ProductNameCodePoints).
#
# Usage: dot-source this file from a sibling script.
#   . (Join-Path $PSScriptRoot 'Common.ps1')

# Captured at load time on purpose: inside a function $PSScriptRoot resolves to
# the CALLING script, not to this file.
$script:CommonScriptRoot = $PSScriptRoot

# =============================================================================
# PINNED RELEASE IDENTITY
# Everything the release is allowed to be called, in one place.
# =============================================================================

# U+6D41 U+5F0F U+805A U+5408 -- the product display name.
$script:ProductNameCodePoints = @(0x6D41, 0x5F0F, 0x805A, 0x5408)

# Fixed main program name. The App csproj uses the same AssemblyName, so the
# host executable is <MainAssemblyName>.exe.
$script:MainAssemblyName = 'StreamingAggregation.App'

# Fixed build shape required by the handoff plan: Release + win-x64.
$script:Configuration      = 'Release'
$script:RuntimeIdentifier = 'win-x64'
$script:BuildPlatform     = 'x64'

# =============================================================================
# PINNED DEPLOYMENT SHAPE
#
# 'FrameworkDependent' -> .NET runtime is NOT bundled; the user installs
#                         .NET 10 Desktop Runtime (x64) once.
# 'SelfContained'       -> the .NET runtime IS bundled.
#
# This is the single source of truth. publish.ps1 passes the derived
# -p:SelfContained / -p:WindowsAppSDKSelfContained to dotnet, and
# Assert-DeploymentShape reads the produced runtimeconfig.json back to prove the
# flag actually took effect. A property that is declared but never verified is
# how a 105 MB framework-dependent build silently turns into a 186 MB
# self-contained one.
# =============================================================================

$script:DeploymentMode = 'FrameworkDependent'

# The Windows App SDK payload stays self-contained in both modes on purpose:
# its bootstrap fails silently with no clear error when the runtime is missing,
# which is far harder to diagnose than a missing .NET runtime (which fails
# before managed code runs and names itself in the Windows error box).
$script:WindowsAppSdkSelfContained = 'true'

$script:SelfContained = $(if ($script:DeploymentMode -eq 'SelfContained') { 'true' } else { 'false' })

# Files that only exist when $script:DeploymentMode is 'SelfContained': they are
# the .NET runtime pack (coreclr + host + the framework's own assemblies).
# In framework-dependent mode their presence means the switch did not take
# effect and the package is carrying a ~80 MB runtime the user does not need.
$script:SelfContainedRuntimeFiles = @(
    'coreclr.dll'
    'hostfxr.dll'
    'hostpolicy.dll'
    'mscorlib.dll'
    'System.Private.CoreLib.dll'
    'System.Data.Common.dll'
)

# Fixed location of the publishable project, relative to the repo root.
$script:AppProjectRelativePath = 'src\StreamingAggregation.App\StreamingAggregation.App.csproj'

# Fixed location of its application manifest. The SDK copies this file verbatim
# into the executable's embedded manifest, so its assemblyIdentity version is a
# release gate, not cosmetics (see Assert-ManifestIdentity).
$script:AppManifestRelativePath = 'src\StreamingAggregation.App\app.manifest'

# Exit codes shared by every script in this folder.
$script:ExitOk      = 0
$script:ExitFailure = 1
$script:ExitUsage   = 2

# =============================================================================
# CONSOLE / LOGGING
# =============================================================================

function Initialize-Console {
    # UTF-8 output so CJK file names and the product name print correctly.
    # Wrapped: a redirected console can refuse the change.
    try {
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $global:OutputEncoding = [System.Text.Encoding]::UTF8
    } catch {
        Write-WarnLine "Could not switch console to UTF-8: $($_.Exception.Message)"
    }
}

function Write-Step { param([string]$Message) Write-Host "==> $Message" }
function Write-Info { param([string]$Message) Write-Host "    $Message" }
function Write-WarnLine { param([string]$Message) Write-Host "WARN: $Message" }

function Fail {
    param([string]$Message, [int]$ExitCode = 1)
    Write-Host "ERROR: $Message"
    exit $ExitCode
}

# =============================================================================
# PATHS AND IDENTITY
# =============================================================================

function Get-RepoRoot {
    if ([string]::IsNullOrEmpty($script:CommonScriptRoot)) {
        throw 'Common.ps1 was not dot-sourced; $PSScriptRoot was empty at load time.'
    }
    return (Resolve-Path -LiteralPath (Join-Path $script:CommonScriptRoot '..')).Path
}

function Get-ProductName {
    return (-join ($script:ProductNameCodePoints | ForEach-Object { [char]$_ }))
}

function Get-MainExeName {
    return "$($script:MainAssemblyName).exe"
}

function Get-AppProjectPath {
    param([string]$RepoRootPath = (Get-RepoRoot))
    $path = Join-Path $RepoRootPath $script:AppProjectRelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Publishable project not found: $path"
    }
    return (Resolve-Path -LiteralPath $path).Path
}

# Reads build/Version.props -> <AssemblyVersion> (pure numeric, no suffix).
function Get-DeclaredAssemblyVersion {
    param([string]$RepoRootPath = (Get-RepoRoot))
    $propsPath = Join-Path (Join-Path $RepoRootPath 'build') 'Version.props'
    if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) {
        throw "Version file not found: $propsPath"
    }
    $text = [System.IO.File]::ReadAllText($propsPath)
    $m = [regex]::Match($text, '<AssemblyVersion>\s*([^<]+?)\s*</AssemblyVersion>')
    if (-not $m.Success) {
        throw "No <AssemblyVersion> element found in $propsPath"
    }
    return $m.Groups[1].Value
}

# Fail-closed check that the release will actually start.
#
# The application manifest is embedded in the executable verbatim. Win32
# side-by-side (SxS) only accepts a four-part numeric assemblyIdentity
# version; a three-part value such as '0.2.0' makes CreateProcess fail with
# "assemblyIdentity version is invalid" and the app never starts. That failure
# only shows up in the SideBySide event log at launch time, long after the
# package is built, so it is checked here instead.
function Assert-ManifestIdentity {
    param([string]$RepoRootPath = (Get-RepoRoot))

    $manifestPath = Join-Path $RepoRootPath $script:AppManifestRelativePath
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Application manifest not found: $manifestPath"
    }
    $text = [System.IO.File]::ReadAllText($manifestPath)
    $m = [regex]::Match($text, '<assemblyIdentity\b[^>]*\bversion\s*=\s*"([^"]+)"')
    if (-not $m.Success) {
        throw "No assemblyIdentity version attribute found in $manifestPath"
    }
    $manifestVersion = $m.Groups[1].Value
    if ($manifestVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw ("Application manifest assemblyIdentity version '$manifestVersion' is not a four-part numeric version. Windows SxS rejects anything else and the executable will not start.")
    }

    $declared = Get-DeclaredAssemblyVersion -RepoRootPath $RepoRootPath
    $expected = $declared
    if ($expected -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        # Three-part assembly versions are legal for .NET; pad them for SxS.
        $parts = $expected.Split('.')
        $expected = ($parts + @('0') * (4 - $parts.Count)) -join '.'
    }
    if ($manifestVersion -ne $expected) {
        throw ("Application manifest assemblyIdentity '$manifestVersion' does not match build/Version.props AssemblyVersion '$declared' (expected '$expected').")
    }
    return $manifestVersion
}

# Fail-closed check that the release ships in the deployment shape the release
# plan pins.
#
# <Main>.runtimeconfig.json is the only artifact that records what was actually
# built, and the two shapes are mutually exclusive in it:
#
#   self-contained      -> runtimeOptions.includedFrameworks = [ { name, version } ]
#   framework-dependent -> runtimeOptions.framework         = { name, version }
#
# Reading it back is deterministic: it needs no installed runtime, no launch,
# no file-size heuristic. It closes the gap that lets a declared-but-ignored
# property ship the wrong package -- if -p:SelfContained=false is dropped, or
# the csproj hardcodes the property so the command line is overridden, the JSON
# still says includedFrameworks and this throws.
function Get-DeploymentShape {
    # Returns the shape actually recorded in the release directory, plus the
    # .NET framework version it was resolved against.
    param([string]$ReleaseDir)

    $rcName = "$($script:MainAssemblyName).runtimeconfig.json"
    $rcPath = Join-Path $ReleaseDir $rcName
    if (-not (Test-Path -LiteralPath $rcPath -PathType Leaf)) {
        throw "Runtime metadata not found: $rcPath"
    }

    try {
        $json = [System.IO.File]::ReadAllText($rcPath) | ConvertFrom-Json
    } catch {
        throw ("Could not parse $rcName as JSON: {0}" -f $_.Exception.Message)
    }

    $opts = $json.runtimeOptions
    if ($null -eq $opts) {
        throw "$rcName has no 'runtimeOptions' object; the deployment shape cannot be determined."
    }

    # PSObject.Properties is the reliable existence test here: on Windows
    # PowerShell 5.1 a missing property silently yields $null, which would make
    # an absent key indistinguishable from an explicit null.
    $names = @($opts.PSObject.Properties | ForEach-Object { $_.Name })
    $hasIncluded = ($names -contains 'includedFrameworks')
    $hasFramework = ($names -contains 'framework')

    if ($hasIncluded -and $hasFramework) {
        throw ("$rcName declares BOTH 'includedFrameworks' and 'framework'. That is not a shape the .NET SDK produces; the file is corrupt or was hand-edited.")
    }

    $shape = 'Unknown'
    $frameworkVersion = ''
    if ($hasIncluded) {
        $shape = 'SelfContained'
        $first = @($opts.includedFrameworks)[0]
        if ($null -ne $first) { $frameworkVersion = $first.version }
    } elseif ($hasFramework) {
        $shape = 'FrameworkDependent'
        if ($null -ne $opts.framework) { $frameworkVersion = $opts.framework.version }
    }

    return [pscustomobject]@{
        Shape            = $shape
        FrameworkVersion = $frameworkVersion
        RuntimeConfigPath = $rcPath
    }
}

function Assert-DeploymentShape {
    # Throws unless the built release matches $script:DeploymentMode.
    param([string]$ReleaseDir, [string]$ExpectedMode = '')

    if ([string]::IsNullOrEmpty($ExpectedMode)) { $ExpectedMode = $script:DeploymentMode }
    $actual = Get-DeploymentShape -ReleaseDir $ReleaseDir
    # NB: PowerShell interpolates a bare $variable in a double-quoted string but
    # not its members -- "$actual.Shape" would print the literal ".Shape".
    $shapeText = $actual.Shape
    if ($actual.Shape -ne $ExpectedMode) {
        throw ("Deployment shape mismatch: expected '$ExpectedMode' but $ReleaseDir is '$shapeText'." +
               "`n    Read $($actual.RuntimeConfigPath) to see what was actually published." +
               "`n    If the csproj property lost its Condition=""'..' == ''"" guard, -p: on the command line is silently overridden and the build keeps its old shape.")
    }
    return $actual
}

# Reads the single source of truth: build/Version.props -> <Version>.
function Get-ProductVersion {

    param([string]$RepoRootPath = (Get-RepoRoot))
    $propsPath = Join-Path (Join-Path $RepoRootPath 'build') 'Version.props'
    if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) {
        throw "Version file not found: $propsPath"
    }
    $text = [System.IO.File]::ReadAllText($propsPath)
    $m = [regex]::Match($text, '<Version>\s*([^<]+?)\s*</Version>')
    if (-not $m.Success) {
        throw "No <Version> element found in $propsPath"
    }
    return $m.Groups[1].Value
}

# -Version is an assertion, not an override: build/Version.props stays the one
# place a version is written down. A mismatch fails instead of silently
# producing an artifact whose embedded version disagrees with its file name.
function Resolve-ReleaseVersion {
    param([string]$Requested, [string]$Actual)
    if ([string]::IsNullOrWhiteSpace($Requested)) { return $Actual }
    if ($Requested -ne $Actual) {
        throw ("Version mismatch: -Version '$Requested' but build/Version.props declares '$Actual'. Update build/Version.props instead of passing -Version.")
    }
    return $Actual
}

function Get-RelativePath {
    # Returns a posix-style path (forward slashes) of $FullName under $BasePath.
    param([string]$BasePath, [string]$FullName)
    if (-not $FullName.StartsWith($BasePath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is not under the expected root: $FullName"
    }
    $rel = $FullName.Substring($BasePath.Length).TrimStart('\', '/')
    return ($rel -replace '\\', '/')
}

# =============================================================================
# FILESYSTEM / HASHING
# =============================================================================

function Reset-Directory {
    # Deletes and recreates a directory; returns the resolved absolute path.
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    [void](New-Item -ItemType Directory -Path $Path -Force)
    return (Resolve-Path -LiteralPath $Path).Path
}

function Format-Bytes {
    param([long]$Bytes)
    if ($Bytes -lt 1KB) { return "$Bytes B" }
    if ($Bytes -lt 1MB) { return ("{0:N1} KB" -f ($Bytes / 1KB)) }
    if ($Bytes -lt 1GB) { return ("{0:N1} MB" -f ($Bytes / 1MB)) }
    return ("{0:N2} GB" -f ($Bytes / 1GB))
}

function Get-Sha256 {
    param([string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $hash = $sha.ComputeHash($stream)
    } finally {
        $stream.Dispose()
        $sha.Dispose()
    }
    return (($hash | ForEach-Object { $_.ToString('x2') }) -join '')
}

function Get-DirectoryStats {
    param([string]$Path)
    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -File)
    $dirs = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -Directory)
    $bytes = 0L
    foreach ($f in $files) { $bytes += $f.Length }
    return [pscustomobject]@{
        FileCount = $files.Count
        DirCount  = $dirs.Count
        Bytes     = $bytes
    }
}

function Invoke-SensitiveScan {
    # Runs build/scan-release.ps1 in a child PowerShell 5.1 and throws on any
    # hit. Both publish.ps1 and make-zip.ps1 gate on this.
    param([string]$RepoRootPath = (Get-RepoRoot), [string]$TargetDir)

    $scanner = Join-Path (Join-Path $RepoRootPath 'build') 'scan-release.ps1'
    if (-not (Test-Path -LiteralPath $scanner -PathType Leaf)) {
        throw "Scanner not found: $scanner"
    }
    Write-Step "Scanning release directory for sensitive files"
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $scanner -Path $TargetDir 2>&1
    $code = $LASTEXITCODE
    foreach ($line in $output) { Write-Host "    $line" }
    if ($code -ne 0) {
        throw "scan-release.ps1 reported hits (exit code $code)."
    }
    return $output
}