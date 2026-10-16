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
#     <product name>.exe  <- the only file allowed at the root
#     app/                <- the entire application payload
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

# The one file allowed at the release root. Built from build/launcher/, never
# copied from the working tree: the bytes are a build product, so nothing
# depends on core.autocrlf the way a checked-in .bat did.
$script:LauncherName = (-join ($script:ProductNameCodePoints | ForEach-Object { [char]$_ })) + '.exe'

# Launcher sources, relative to the repo root.
#   Launcher.cs  the launcher itself; compiled to a .NET Framework 4.x winexe
#   Launcher.rc  icon + VERSIONINFO, compiled by rc.exe and linked in as a
#                .NET resource so the shipped exe carries both.
$script:LauncherSourceRelativePath   = 'build\launcher\Launcher.cs'
$script:LauncherResourceRelativePath = 'build\launcher\Launcher.rc'

# The icon the launcher's .rc embeds. The single copy lives with the app; the
# launcher references it by leaf name and gets the folder from rc.exe /i.
$script:AppIconRelativePath = 'src\StreamingAggregation.App\Assets\AppIcon.ico'

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

# Three-part versions are legal for .NET; Win32 resources (VERSIONINFO, the
# application manifest) only accept four. Pad rather than reject, so bumping
# 0.2.0 -> 0.3.0 does not silently stop matching the resource.
function ConvertTo-FourPartVersion {
    param([string]$Version)
    $parts = @($Version.Split('.'))
    if ($parts.Count -gt 4) {
        throw "Version '$Version' has more than four parts; Win32 version resources accept at most four."
    }
    return ($parts + @('0') * (4 - $parts.Count)) -join '.'
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
    # <release>\<product name>.exe
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

# =============================================================================
# LAUNCHER BUILD
#
# The launcher is COMPILED, not copied. Three consequences the old .bat did
# not have:
#
#   - The source names the payload directory and the app exe as C# string
#     constants, so Assert-LauncherContract reads them back and fails the build
#     when they drift from Common.ps1. A launcher pointing at the wrong file
#     still compiles cleanly and still "passes" every other check in this
#     folder, so this is the only place that can catch it.
#   - The shipped bytes do not depend on the working tree at all, so the
#     core.autocrlf trap that Install-Launcher existed to work around is gone
#     rather than fixed.
#   - Two external tools are needed (csc.exe for the code, rc.exe for the
#     icon and version resource). rc.exe is optional: without it the launcher
#     builds and runs, it just ships with no icon and no version info, which
#     is a cosmetic regression rather than a broken package.
# =============================================================================

function Find-CscPath {
    # Highest-versioned .NET Framework compiler under %WINDIR%. Every Windows
    # since 7 ships one; the v4.0.30319 folder is the in-place upgrade target
    # and is the only one on a modern machine.
    if ([string]::IsNullOrWhiteSpace($env:WINDIR)) {
        throw 'WINDIR is not set; cannot locate csc.exe.'
    }
    $root = Join-Path (Join-Path $env:WINDIR 'Microsoft.NET') 'Framework64'
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "No .NET Framework compiler directory: $root"
    }
    $best = $null
    $bestVersion = $null
    foreach ($dir in (Get-ChildItem -LiteralPath $root -Directory)) {
        $candidate = Join-Path $dir.FullName 'csc.exe'
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        # The folder is named 'v4.0.30319', which [version]::TryParse rejects
        # because of the leading 'v'. Without the trim the loop below would
        # silently find nothing and report the compiler as missing on a machine
        # that has had it since Windows 7.
        $version = $null
        if (-not [version]::TryParse($dir.Name.TrimStart('v', 'V'), [ref]$version)) { continue }
        if ($null -eq $bestVersion -or $version -gt $bestVersion) {
            $best = $candidate
            $bestVersion = $version
        }
    }
    if ($null -eq $best) {
        throw "No csc.exe found under $root. The launcher needs the .NET Framework compiler; install the .NET Framework 4 developer pack."
    }
    return $best
}

function Find-RcToolchain {
    # Returns the newest Windows SDK's rc.exe plus the two include folders it
    # needs for <windows.h>, or $null when no SDK is installed.
    #
    # The SDK is a separate download from the .NET Framework, so this genuinely
    # may be absent; callers must treat that as a downgrade, not a failure.
    $kitRoot = $null
    # ProgramFiles(x86) is absent on 32-bit Windows and ProgramFiles is absent
    # on a few stripped images, so both are probed and neither may be joined
    # blindly: Join-Path throws on a null path, which would turn "no SDK here"
    # into a hard build failure instead of the documented downgrade.
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if ([string]::IsNullOrWhiteSpace($base)) { continue }
        $candidate = Join-Path $base 'Windows Kits\10'
        if (Test-Path -LiteralPath $candidate -PathType Container) { $kitRoot = $candidate }
    }
    if ([string]::IsNullOrWhiteSpace($kitRoot)) { return $null }

    $best = $null
    $bestVersion = $null
    $binRoot = Join-Path $kitRoot 'bin'
    if (-not (Test-Path -LiteralPath $binRoot -PathType Container)) { return $null }
    foreach ($dir in (Get-ChildItem -LiteralPath $binRoot -Directory)) {
        $candidate = Join-Path (Join-Path $dir.FullName 'x64') 'rc.exe'
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $version = $null
        if (-not [version]::TryParse($dir.Name, [ref]$version)) { continue }
        if ($null -eq $bestVersion -or $version -gt $bestVersion) {
            $best = $candidate
            $bestVersion = $version
        }
    }
    if ($null -eq $best) { return $null }

    $includeRoot = Join-Path (Join-Path $kitRoot 'Include') $bestVersion.ToString()
    $um = Join-Path $includeRoot 'um'
    $shared = Join-Path $includeRoot 'shared'
    if (-not (Test-Path -LiteralPath (Join-Path $um 'windows.h') -PathType Leaf)) { return $null }
    if (-not (Test-Path -LiteralPath (Join-Path $shared 'sdkddkver.h') -PathType Leaf)) { return $null }

    return [pscustomobject]@{
        RcExe    = $best
        IncludeU = $um
        IncludeShared = $shared
        Version  = $bestVersion.ToString()
    }
}

function New-LauncherResourceFile {
    # Renders build/launcher/Launcher.rc with its placeholders substituted and
    # writes it as UTF-16LE with a BOM.
    #
    # The encoding is the whole reason this function exists. rc.exe reads a .rc
    # in the ANSI code page, which is 936 on this machine and something else on
    # the next one, and the product name it has to carry is CJK -- so the
    # bytes written here would come out as mojibake on any machine whose ACP
    # is not a superset of the source encoding. rc.exe does honour a UTF-16
    # BOM, and that path is locale independent, so the generated file is
    # UTF-16LE. The .rc on disk stays plain ASCII with placeholders.
    #
    # A second reason for the same encoding choice: rc.exe truncates its own
    # error messages at the first non-ASCII character, and every path in this
    # repository contains CJK. Build-Launcher runs it with the staging folder
    # as the working directory and ASCII relative names so a real error is
    # actually readable.
    param(
        [string]$TemplatePath,
        [string]$OutputPath,
        [string]$Version
    )

    $template = [System.IO.File]::ReadAllText($TemplatePath)
    $productName = Get-ProductName
    $fourPart = ConvertTo-FourPartVersion -Version $Version
    $commaForm = ($fourPart -split '\.') -join ','

    $substitutions = [ordered]@{
        '@ICON@'          = [System.IO.Path]::GetFileName($script:AppIconRelativePath)
        '@FILEVER@'       = $commaForm
        '@PRODVER@'       = $commaForm
        '@COMPANY@'       = ''
        '@FILEDESC@'      = $productName
        '@PRODUCTNAME@'   = $productName
        '@LAUNCHERNAME@'  = $script:LauncherName
    }
    foreach ($key in $substitutions.Keys) {
        $template = $template.Replace($key, $substitutions[$key])
    }

    # A leftover placeholder would reach rc.exe as a literal string and land in
    # Explorer's property sheet. Cheap to catch, invisible until someone looks.
    $leftover = [regex]::Match($template, '@[A-Z0-9_]+@')
    if ($leftover.Success) {
        throw ("Launcher.rc still contains the placeholder '{0}' after substitution. Known placeholders: {1}." -f `
                $leftover.Value, (($substitutions.Keys) -join ', '))
    }

    [System.IO.File]::WriteAllText($OutputPath, $template, [System.Text.Encoding]::Unicode)
    return $OutputPath
}

function Build-Launcher {
    # Compiles the launcher into $DestinationPath.
    #
    # Returns a result object describing what was produced and, when rc.exe
    # was unavailable, which parts are missing. Never throws for a missing
    # resource toolchain -- that is a downgrade the caller reports, not a
    # broken build.
    param(
        [string]$RepoRootPath,
        [string]$DestinationPath,
        [string]$Version
    )

    $sourcePath = Join-Path $RepoRootPath $script:LauncherSourceRelativePath
    $resourceTemplate = Join-Path $RepoRootPath $script:LauncherResourceRelativePath
    $iconDir = Join-Path $RepoRootPath ([System.IO.Path]::GetDirectoryName($script:AppIconRelativePath))
    $iconPath = Join-Path $RepoRootPath $script:AppIconRelativePath
    if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
        throw "Application icon not found: $iconPath"
    }

    $csc = Find-CscPath
    $toolchain = Find-RcToolchain

    # Scratch directory. Inside the repo, next to the other build logs, so a
    # failed launcher build leaves its inputs and diagnostics where the rest of
    # the pipeline's debugging artefacts already live.
    $workDir = Join-Path (Join-Path $RepoRootPath '.tmp') 'launcher-build'
    if (-not (Test-Path -LiteralPath $workDir -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $workDir -Force)
    }
    $workDir = (Resolve-Path -LiteralPath $workDir).Path

    $resPath = Join-Path $workDir 'Launcher.res'
    $haveResources = $false
    if ($null -eq $toolchain) {
        Write-WarnLine 'rc.exe (Windows SDK) not found; the launcher will ship without an icon or version resource.'
    } else {
        [void](New-LauncherResourceFile -TemplatePath $resourceTemplate `
                                        -OutputPath (Join-Path $workDir 'Launcher.rc') `
                                        -Version $Version)
        $incArgs = ('/i "{0}" /i "{1}" /i "{2}"' -f $toolchain.IncludeU, $toolchain.IncludeShared, $iconDir)
        $rcCommand = '"{0}" /nologo /fo "Launcher.res" {1} "Launcher.rc" 2>&1' -f $toolchain.RcExe, $incArgs
        # Relative names + the staging folder as the working directory: rc.exe
        # truncates its diagnostics at the first non-ASCII character and every
        # path in this repo is CJK.
        Push-Location $workDir
        try {
            $rcOutput = & cmd /c $rcCommand
            $rcExit = $LASTEXITCODE
        } finally {
            Pop-Location
        }
        if ($rcExit -ne 0) {
            $decoded = @($rcOutput) -join ' | '
            throw ("rc.exe failed with exit code {0}: {1}`n    command: {2}" -f $rcExit, $decoded, $rcCommand)
        }
        $haveResources = Test-Path -LiteralPath $resPath -PathType Leaf
        if (-not $haveResources) {
            throw "rc.exe reported success but produced no $resPath"
        }
        Write-Info ("resources: rc.exe {0} -> {1} bytes" -f $toolchain.Version, (Get-Item -LiteralPath $resPath).Length)
    }

    $cscArgs = @(
        '/nologo',
        # The launcher source carries CJK literals for its error dialogs.
        # Without /codepage:65001 csc.exe decodes the file in the machine's
        # ANSI code page and every one of them becomes mojibake in the shipped
        # binary -- on any machine whose ACP is not UTF-8.
        '/codepage:65001',
        '/target:winexe',
        '/platform:x64',
        '/optimize+',
        '/debug-',
        ('/out:' + $DestinationPath)
    )
    if ($haveResources) {
        $cscArgs += ('/resource:' + $resPath)
    }
    $cscArgs += $sourcePath

    $cscOutput = & $csc @cscArgs 2>&1
    $cscExit = $LASTEXITCODE
    if ($cscExit -ne 0) {
        throw ("csc.exe failed with exit code {0}: {1}`n    command: {2} {3}" -f `
            $cscExit, (@($cscOutput) -join ' | '), $csc, ($cscArgs -join ' '))
    }
    if (-not (Test-Path -LiteralPath $DestinationPath -PathType Leaf)) {
        throw "csc.exe reported success but produced no $DestinationPath"
    }

    # Stamp the output with the newest input timestamp instead of the build
    # clock. make-zip stores each entry's mtime, so leaving the clock in place
    # would make every archive of an unchanged tree unique for no reason.
    # (The launcher's bytes still differ between builds -- csc stamps a PE
    # timestamp and emits a fresh assembly MVID -- so the ZIP hash is not
    # byte-reproducible. The mtime at least stops being a moving target.)
    $inputs = @($sourcePath, $resourceTemplate, $iconPath) | Where-Object { Test-Path -LiteralPath $_ }
    $stamp = ($inputs | ForEach-Object { [System.IO.File]::GetLastWriteTimeUtc($_) } |
              Measure-Object -Maximum).Maximum
    if ($stamp) {
        [System.IO.File]::SetLastWriteTimeUtc($DestinationPath, $stamp)
    }

    $info = Get-Item -LiteralPath $DestinationPath
    return [pscustomobject]@{
        Path          = $info.FullName
        Bytes         = $info.Length
        Compiler      = $csc
        ResourceTool  = $(if ($toolchain) { $toolchain.RcExe } else { '' })
        HasIcon       = $haveResources
        HasVersion    = $haveResources
    }
}

# Fail-closed check that build/launcher/Launcher.cs still launches the pinned
# exe out of the pinned subdirectory, and carries the pinned version.
#
# The launcher is a compiled artifact whose inputs are static files while
# $script:MainAssemblyName, $script:PayloadDir and Version.props are variables,
# so renaming any of those three would produce a launcher that starts nothing
# and compiles without a warning. Nothing else in the build would notice: the
# payload is present, the allow-list is satisfied, the archive is clean, and
# the exe at the root is a perfectly valid PE file. This is the only place
# that knows the launcher is a link and not just another file.
function Assert-LauncherContract {
    param([string]$RepoRootPath = (Get-RepoRoot))

    $sourcePath = Join-Path $RepoRootPath $script:LauncherSourceRelativePath
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Launcher source not found: $sourcePath"
    }
    $resourceTemplate = Join-Path $RepoRootPath $script:LauncherResourceRelativePath
    if (-not (Test-Path -LiteralPath $resourceTemplate -PathType Leaf)) {
        throw "Launcher resource script not found: $resourceTemplate"
    }

    # Decoded strictly: throwOnInvalidBytes, so a file that was actually saved
    # in the machine's ANSI code page fails here instead of turning every
    # Chinese string literal into mojibake inside the shipped exe.
    $strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
    try {
        $text = $strictUtf8.GetString([System.IO.File]::ReadAllBytes($sourcePath))
    } catch {
        throw ("{0} is not valid UTF-8. It is compiled with /codepage:65001, so anything else turns its string literals into mojibake. {1}" -f `
                $script:LauncherSourceRelativePath, $_.Exception.Message)
    }

    $expectedExeName = Get-MainExeName
    $fourPart = ConvertTo-FourPartVersion -Version (Get-DeclaredAssemblyVersion -RepoRootPath $RepoRootPath)

    $required = [ordered]@{
        'PayloadDirectoryName' = $script:PayloadDir
        'PayloadExeName'       = $expectedExeName
    }
    foreach ($entry in $required.GetEnumerator()) {
        $pattern = [regex]::Escape($entry.Key) + '\s*=\s*"' + [regex]::Escape($entry.Value) + '"'
        if ($text -notmatch $pattern) {
            throw ("{0} does not pin {1} to '{2}'. The release layout is pinned to <root>/{3}/<payload>; update the launcher's constants when either changes." -f `
                    $script:LauncherSourceRelativePath, $entry.Key, $entry.Value, $script:PayloadDir)
        }
    }

    foreach ($attribute in @('AssemblyVersion', 'AssemblyFileVersion')) {
        $pattern = '\[assembly:\s*' + $attribute + '\(\s*"' + [regex]::Escape($fourPart) + '"\s*\)\]'
        if ($text -notmatch $pattern) {
            throw ("{0} does not pin [assembly: {1}('{2}')] to the version in build/Version.props ({3})." -f `
                    $script:LauncherSourceRelativePath, $attribute, $fourPart, (Get-DeclaredAssemblyVersion -RepoRootPath $RepoRootPath))
        }
    }

    # The version the .rc will be rendered with must come from the same place,
    # and the .rc must not carry a hand-written version that can drift.
    $rcText = [System.IO.File]::ReadAllText($resourceTemplate)
    foreach ($byte in [System.Text.Encoding]::ASCII.GetBytes($rcText)) {
        if ($byte -gt 127) {
            throw ("{0} contains a non-ASCII byte (0x{1:X2}). It is a template read by rc.exe, which decodes .rc files in the ANSI code page; keep it ASCII with placeholders." -f `
                    $script:LauncherResourceRelativePath, $byte)
        }
    }
    foreach ($placeholder in @('@ICON@', '@FILEVER@', '@PRODVER@', '@PRODUCTNAME@', '@FILEDESC@', '@LAUNCHERNAME@')) {
        if ($rcText -notmatch [regex]::Escape($placeholder)) {
            throw ("{0} has no '{1}' placeholder. It is rendered by New-LauncherResourceFile, so every value that must not be hard-coded needs one." -f `
                    $script:LauncherResourceRelativePath, $placeholder)
        }
    }
    # Whitespace-normalised so this does not depend on how the columns line up
    # in the template. Checks the VALUE is a placeholder rather than a literal
    # number, which is what would silently fork the version away from
    # Version.props the next time somebody bumps it.
    $normalized = ($rcText -replace '\s+', ' ')
    foreach ($keyword in @('FILEVERSION', 'PRODUCTVERSION')) {
        if ($normalized -match ('(?i)\b' + $keyword + '\s+\d')) {
            $expected = $(if ($keyword -eq 'FILEVERSION') { '@FILEVER@' } else { '@PRODVER@' })
            throw ("{0} sets {1} to a literal number. It must be {2}, rendered from build/Version.props, so that file stays the only place the version is written." -f `
                    $script:LauncherResourceRelativePath, $keyword, $expected)
        }
    }

    return [pscustomobject]@{
        SourcePath       = $sourcePath
        ResourceTemplate = $resourceTemplate
        Version          = $fourPart
    }
}

# Fail-closed check of the launcher that actually shipped.
#
# Assert-LauncherContract proves the SOURCE is right; this proves the ARTIFACT
# is. The gap between them is not hypothetical: csc can fail, rc.exe can be
# absent, a resource can silently fail to link, and in every one of those cases
# the release directory still ends up holding a well-formed file at the root
# that starts nothing -- or starts something with a generic icon and the wrong
# version in Explorer's property sheet.
#
# Checked here rather than by the smoke test because the smoke test can only
# observe a launch, and a launcher with no icon launches perfectly well.
function Assert-CompiledLauncher {
    param(
        [string]$Path,
        [string]$ExpectedVersion,
        [bool]$ExpectResources = $true
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Launcher executable not found: $Path"
    }

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $length = $bytes.Length

    # A plausible size is a cheap first gate: an empty or truncated file is the
    # most common way a botched resource link shows up, and every later check
    # below reads at a fixed offset.
    if ($length -lt 4KB -or $length -gt 1MB) {
        throw ("{0} is {1} bytes, which is not a launcher. Expected a .NET Framework winexe between 4 KB and 1 MB." -f `
                $script:LauncherName, $length)
    }
    if ($bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
        throw ("{0} does not start with 'MZ'; it is not a PE executable." -f $script:LauncherName)
    }

    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -lt 0 -or $peOffset + 24 -gt $length) {
        throw ("{0} has an out-of-range PE header offset (0x{1:X})." -f $script:LauncherName, $peOffset)
    }
    if ($bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45 -or
        $bytes[$peOffset + 2] -ne 0x00 -or $bytes[$peOffset + 3] -ne 0x00) {
        throw ("{0} has no 'PE\0\0' signature at the PE header offset." -f $script:LauncherName)
    }

    $coff = $peOffset + 4
    $machine = [System.BitConverter]::ToUInt16($bytes, $coff)
    if ($machine -ne 0x014C -and $machine -ne 0x8664) {
        throw ("{0} targets machine 0x{1:X4}; only x86 (0x014C) and x64 (0x8664) are supported." -f `
                $script:LauncherName, $machine)
    }
    $optionalSize = [System.BitConverter]::ToUInt16($bytes, $coff + 16)
    $optional = $coff + 20
    if ($optional + $optionalSize -gt $length) {
        throw ("{0} has a truncated optional header." -f $script:LauncherName)
    }

    $magic = [System.BitConverter]::ToUInt16($bytes, $optional)
    $isPe32Plus = ($magic -eq 0x020B)
    if ($magic -ne 0x010B -and -not $isPe32Plus) {
        throw ("{0} has an unknown optional header magic 0x{1:X4}." -f $script:LauncherName, $magic)
    }

    # Subsystem 2 is IMAGE_SUBSYSTEM_WINDOWS_GUI. This is the one PE field
    # that directly encodes the user-visible requirement: a console subsystem
    # flashes a black window on every double-click, which is exactly what the
    # .bat it replaced was chosen to avoid.
    $subsystem = [System.BitConverter]::ToUInt16($bytes, $optional + 68)
    if ($subsystem -ne 2) {
        $what = $(if ($subsystem -eq 3) { 'IMAGE_SUBSYSTEM_WINDOWS_CUI (a black console window on every double-click)' }
                 else { "subsystem $subsystem" })
        throw ("{0} is built as {1}. It must be /target:winexe (IMAGE_SUBSYSTEM_WINDOWS_GUI)." -f $script:LauncherName, $what)
    }

    # Data directory entry 2 is the resource table. Non-zero means rc.exe's
    # output was linked in rather than silently dropped.
    #
    # Offsets into the optional header differ between PE32 and PE32+ because
    # PE32+ widens ImageBase and the four stack/heap sizes from 4 to 8 bytes:
    #   standard fields  28 bytes (PE32) / 24 bytes (PE32+)
    #   windows fields   68 bytes (PE32) / 84 bytes (PE32+)
    # Subsystem sits at +68 either way -- the extra bytes all sit before it in
    # PE32+ and after it as well, which cancels out.
    $dataDirectory = if ($isPe32Plus) { 112 } else { 96 }
    $resourceEntry = $optional + $dataDirectory + (2 * 8)
    if ($resourceEntry + 8 -gt $length) {
        throw ("{0} has no data directory entry for the resource table." -f $script:LauncherName)
    }
    $resourceSize = [System.BitConverter]::ToUInt32($bytes, $resourceEntry + 4)
    if ($ExpectResources -and $resourceSize -eq 0) {
        throw ("{0} carries no resource directory: the icon and version information were not linked in." -f $script:LauncherName)
    }

    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $productName = Get-ProductName
    $expectedFileVersion = ConvertTo-FourPartVersion -Version $ExpectedVersion

    $problems = New-Object System.Collections.Generic.List[string]
    if ($version.FileVersion -cne $expectedFileVersion) {
        $problems.Add("file version is '$($version.FileVersion)', expected '$expectedFileVersion'")
    }
    if ($version.ProductName -cne $productName) {
        $problems.Add("product name is '$($version.ProductName)', expected the product name")
    }
    if ($version.FileDescription -cne $productName) {
        $problems.Add("file description is '$($version.FileDescription)', expected the product name")
    }
    if ($version.OriginalFilename -cne $script:LauncherName) {
        $problems.Add("original filename is '$($version.OriginalFilename)', expected '$($script:LauncherName)'")
    }
    if ($problems.Count -gt 0) {
        throw ("{0} carries the wrong version information, {1} problem(s):`n    {2}" -f `
                $script:LauncherName, $problems.Count, ($problems -join "`n    "))
    }

    $hasIcon = $false
    try {
        Add-Type -AssemblyName System.Drawing -ErrorAction Stop
        $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($Path)
        if ($null -ne $icon) {
            $hasIcon = ($icon.Width -gt 0)
            $icon.Dispose()
        }
    } catch {
        # System.Drawing is not present everywhere (Server Core, some locked
        # down images). Missing verification is not a broken launcher.
        Write-WarnLine ("Could not verify the launcher icon: {0}" -f $_.Exception.Message)
    }

    if ($ExpectResources -and -not $hasIcon) {
        throw ("{0} has a version resource but no icon. rc.exe reported success, so the .rc's ICON line did not match anything." -f $script:LauncherName)
    }

    return [pscustomobject]@{
        Path        = $Path
        Bytes       = $length
        Machine     = $(if ($machine -eq 0x8664) { 'x64' } else { 'x86' })
        Subsystem   = 'WINDOWS_GUI'
        FileVersion = $version.FileVersion
        ProductName = $version.ProductName
        HasIcon     = $hasIcon
    }
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
    $expected = ConvertTo-FourPartVersion -Version $declared
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