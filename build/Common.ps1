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

# =============================================================================
# PINNED RELEASE LAYOUT
#
# The extracted package shows exactly two entries:
#
#   <release>/
#     launcher.bat      <- the only file allowed at the root
#     app/              <- the entire application payload
#
# The payload MUST live in one subdirectory, not be scattered across sibling
# folders and not sit at the root. That is not a cosmetic choice; it is the
# only arrangement measured to produce a visible window:
#
#   - The CLR probes the app host directory for dependencies, so a dependency
#     placed next to the exe but outside its own directory is never found.
#   - Win32 side-by-side resolves the embedded application manifest against the
#     exe directory, so the WinAppSDK bootstrap fails to initialise if its
#     payload is not there.
#   - WinRT activation resolves against the process directory.
#
# With the payload spread out, CreateProcess still succeeds: the process runs,
# reports Responding = True, owns a window handle, and never draws anything
# (a ghost process). See build/smoke-release.ps1 for the regression net.
# =============================================================================

# Single subdirectory that holds the whole payload.
$script:PayloadDir = 'app'

# The one file allowed at the release root. Ships as build/launcher.bat.
$script:LauncherName = 'launcher.bat'

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
# 'Checked but not checkable' -- a precondition was absent, so no verdict was
# reached. Distinct from both pass and fail on purpose: a skipped check must
# never be mistaken for a green one.
$script:ExitSkipped = 3

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

# =============================================================================
# RELEASE LAYOUT HELPERS
# Every path assertion in the pipeline is written against the payload
# directory, so the 'app\' prefix is expressed in exactly one place.
# =============================================================================

function Get-PayloadDirPath {
    # <release>\app
    param([string]$ReleaseDir)
    return (Join-Path $ReleaseDir $script:PayloadDir)
}

function Get-LauncherPath {
    # <release>\launcher.bat
    param([string]$ReleaseDir)
    return (Join-Path $ReleaseDir $script:LauncherName)
}

function Get-MainExePath {
    # <release>\app\<Main>.exe -- where the executable actually lives.
    param([string]$ReleaseDir)
    return (Join-Path (Get-PayloadDirPath -ReleaseDir $ReleaseDir) (Get-MainExeName))
}

function Get-PayloadRelativePath {
    # Turns a payload-relative posix path into a release-root-relative one.
    param([string]$PayloadRelativePath)
    return "$($script:PayloadDir)/$PayloadRelativePath"
}

function Get-ReleaseRootEntries {
    # Direct children of the release root, split into files and directories.
    param([string]$ReleaseDir)
    $items = @(Get-ChildItem -LiteralPath $ReleaseDir -Force)
    return [pscustomobject]@{
        Files = @($items | Where-Object { -not $_.PSIsContainer } | ForEach-Object { $_.Name })
        Dirs  = @($items | Where-Object { $_.PSIsContainer } | ForEach-Object { $_.Name })
    }
}

# Fail-closed check of the user-visible package layout: the release root holds
# the launcher and nothing else, and the payload lives in one subdirectory.
#
# This is the check that encodes the point of the layout: a user who unzips
# the package should see two entries, not a hundred and forty-three. It also
# catches the failure the payload-in-one-subdirectory rule exists to prevent,
# namely a payload that leaked back to the root, which starts but never draws
# a window.
function Assert-ReleaseRootLayout {
    param([string]$ReleaseDir)

    $entries = Get-ReleaseRootEntries -ReleaseDir $ReleaseDir
    $problems = New-Object System.Collections.Generic.List[string]

    $expectedDirs = @($script:PayloadDir)
    foreach ($dir in $entries.Dirs) {
        if ($expectedDirs -notcontains $dir) {
            $problems.Add("unexpected directory at the release root: $dir/")
        }
    }
    foreach ($want in $expectedDirs) {
        if ($entries.Dirs -notcontains $want) {
            $problems.Add("payload directory is missing from the release root: $want/")
        }
    }

    foreach ($file in $entries.Files) {
        if ($file -cne $script:LauncherName) {
            $problems.Add("unexpected file at the release root: $file (only $($script:LauncherName) is allowed there)")
        }
    }
    if ($entries.Files -notcontains $script:LauncherName) {
        $problems.Add("launcher is missing from the release root: $($script:LauncherName)")
    }

    if ($problems.Count -gt 0) {
        # Cap the listing. A flat payload produces one problem per file, and a
        # hundred identical-looking lines tell the reader nothing that the
        # count and the first few names do not already say.
        $shown = 12
        $head = @($problems | Select-Object -First $shown)
        $tail = ''
        if ($problems.Count -gt $shown) {
            $tail = "`n    ... and $($problems.Count - $shown) more"
        }
        throw ("Release root layout is wrong, {0} problem(s):`n    {1}{2}" -f `
            $problems.Count, ($head -join "`n    "), $tail)
    }
    return $entries
}

function Install-Launcher {
    # Copies build/launcher.bat into the release root with its line endings
    # pinned to CRLF.
    #
    # Deliberately not a plain file copy. git rewrites line endings on checkout
    # according to core.autocrlf, which is a per-machine setting; the launcher
    # ships inside the archive, so whatever the working tree happens to hold
    # leaks straight into the ZIP hash and two builds of one commit stop being
    # byte-identical. Normalising here makes the BUILD own those bytes rather
    # than git. CRLF is also what cmd.exe expects from a .bat.
    #
    # ASCIIEncoding throws on any non-ASCII character. Assert-LauncherContract
    # has already rejected those, so this is a second, independent gate rather
    # than a lossy conversion.
    param([string]$SourcePath, [string]$DestinationPath)

    $text = [System.IO.File]::ReadAllText($SourcePath)
    # Normalise to bare LF first: a CRLF source must not become CR CR LF.
    $text = $text -replace "`r`n", "`n"
    $text = $text -replace "`r", "`n"

    # Drop the trailing blank element. Splitting "a\nb\n" yields a final empty
    # string, and appending CRLF to that writes a blank line every run -- which
    # would make each build's launcher two bytes larger than the last and break
    # byte reproducibility in a way that is very hard to see.
    $lineList = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($text -split "`n")) { $lineList.Add($line) }
    while ($lineList.Count -gt 0 -and $lineList[$lineList.Count - 1] -eq '') {
        $lineList.RemoveAt($lineList.Count - 1)
    }

    $builder = New-Object System.Text.StringBuilder
    foreach ($line in $lineList) {
        [void]$builder.Append($line)
        [void]$builder.Append("`r`n")
    }
    [System.IO.File]::WriteAllText($DestinationPath, $builder.ToString(),
                                   (New-Object System.Text.ASCIIEncoding))

    # Carry the source timestamp across, for the same reason Copy-ReleaseWhitelist
    # does: make-zip stores each entry's mtime, so a launcher stamped with the
    # build clock makes every archive unique.
    [System.IO.File]::SetLastWriteTimeUtc($DestinationPath,
                                          [System.IO.File]::GetLastWriteTimeUtc($SourcePath))
}

# Fail-closed check that build/launcher.bat still launches the pinned exe out
# of the pinned subdirectory.
#
# launcher.bat is a static file while $script:MainAssemblyName and
# $script:PayloadDir are variables, so renaming the assembly or the payload
# directory would silently leave a launcher that starts nothing. Nothing else
# in the build would notice: the payload is present, the allow-list is
# satisfied, the archive is clean. This is the only place that knows the
# launcher is a link and not just another file.
function Assert-LauncherContract {
    param([string]$RepoRootPath = (Get-RepoRoot))

    $launcherSource = Join-Path (Join-Path $RepoRootPath 'build') $script:LauncherName
    if (-not (Test-Path -LiteralPath $launcherSource -PathType Leaf)) {
        throw "Launcher source not found: $launcherSource"
    }

    # Read as bytes and fail on a BOM or any non-ASCII byte: cmd.exe decodes a
    # .bat in the console code page, so either would corrupt it on a machine
    # whose code page differs from the author's.
    $bytes = [System.IO.File]::ReadAllBytes($launcherSource)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw "$($script:LauncherName) starts with a UTF-8 BOM; cmd.exe will try to execute it."
    }
    foreach ($b in $bytes) {
        if ($b -gt 127) {
            throw ("{0} contains a non-ASCII byte (0x{1:X2}); cmd.exe reads .bat files in the console code page. Keep it ASCII." -f $script:LauncherName, $b)
        }
    }

    $text = [System.IO.File]::ReadAllText($launcherSource)
    $expectedDirRef = "$($script:PayloadDir)\"
    if ($text -notmatch [regex]::Escape($expectedDirRef)) {
        throw ("{0} does not reference the payload directory '{1}'. The release layout is pinned to <root>/{1}/<payload>." -f $script:LauncherName, $expectedDirRef)
    }
    $exeName = Get-MainExeName
    if ($text -notmatch [regex]::Escape($exeName)) {
        throw ("{0} does not reference '$exeName'. Regenerate it if $script:MainAssemblyName changed." -f $script:LauncherName)
    }
    return $launcherSource
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
    $rcPath = Join-Path (Get-PayloadDirPath -ReleaseDir $ReleaseDir) $rcName
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