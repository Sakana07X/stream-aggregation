# publish.ps1
# Builds the whitelisted win-x64 release directory.
#
# Release shape is fixed by the handoff plan:
#   Release + win-x64 + full-directory publish
#   Deployment: .NET framework-dependent + self-contained Windows App SDK
#   (no single-file merge: it has never been validated on a real machine)
#
# The .NET runtime is deliberately NOT bundled; the user installs .NET 10
# Desktop Runtime (x64) once. The Windows App SDK payload IS bundled: its
# bootstrap fails silently when the runtime is absent, which is much harder to
# diagnose than a missing .NET runtime.
#
# Layout produced:
#   <OutputRoot>/<version>/win-x64/          <- the release directory
#     launcher.bat                           <- the only entry at the root
#     app/                                   <- the entire application payload
#
# The payload is moved into a single 'app\' subdirectory after the allow-list
# has been applied. That is not cosmetic: an unzipped package then shows two
# entries instead of 143, and the app host, the Win32 side-by-side loader and
# WinRT activation all resolve against the directory the exe actually sits in.
#
# The directory is built by copying an EXPLICIT ALLOW-LIST out of the raw
# `dotnet publish` staging folder. This is deliberately not a deny-list: a new
# or unexpected file type is dropped and reported, never shipped by accident.
#
# ASCII-only on purpose (PowerShell 5.1 reads BOM-less scripts as ANSI).
# CJK literals are expressed as \uXXXX regex escapes.
#
# Usage:
#   powershell -File build/publish.ps1
#   powershell -File build/publish.ps1 -Version 0.2.0
#   powershell -File build/publish.ps1 -OutputRoot D:\somewhere\release
#   powershell -File build/publish.ps1 -SkipPublish      # re-filter only
#
# Exit codes: 0 = ok, 1 = failure, 2 = usage error

param(
    [string]$Version = '',
    [string]$OutputRoot = '',
    [string]$StageRoot = '',
    [switch]$SkipPublish,
    [switch]$SkipScan
)

. (Join-Path $PSScriptRoot 'Common.ps1')

# =============================================================================
# RELEASE WHITELIST
# The only thing that decides whether a file ships. Everything not matched is
# dropped and printed with its reason. Do not convert this into a deny-list.
# =============================================================================

# Directories allowed directly under the release root.
$AllowedRootDirs   = @('Assets', 'Microsoft.UI.Xaml', 'NpuDetect')

# Satellite resource folders, listed explicitly. The MUI path
# '<install>\<culture>\<name>.mui' is a Windows hard convention and cannot
# be redirected, so the only lever is which culture folders we copy. The
# Windows App SDK ships 86 of them; we keep only what the product needs.
# -contains is case-insensitive, so 'en-us' matches the on-disk 'en-us'.
# Windows App SDK 1.7 only ships zh-CN / zh-TW / en-us; zh-HK does not exist,
# so listing it here would fail the fail-closed required-file check below.
$AllowedCultureDirs = @('zh-CN', 'zh-TW', 'en-us')

# Directories allowed below the release root, as posix relative paths.
$AllowedNestedDirs = @('Microsoft.UI.Xaml/Assets')

# A culture folder may only contain localized resource files. Without this, a
# non-allow-listed folder that happens to look culture-shaped (e.g. 'obj-x64')
# could smuggle any allow-listed binary through.
$AllowedCultureExtensions = @('.mui')

# File rules, matched against the relative path. First match wins.
# The .NET metadata rule is derived from $script:MainAssemblyName so the pinned
# main program name stays the single source of truth. A development variant
# such as '<Main>.runtimeconfig.dev.json' therefore does NOT match.
$FileRules = @(
    @{ Id = 'AppBinary';         Match = '\.dll$';                              Note = 'managed assembly or native runtime DLL' }
    @{ Id = 'AppExecutable';     Match = '\.exe$';                              Note = 'application host or runtime helper executable' }
    @{ Id = 'SatelliteResource'; Match = '\.mui$';                              Note = 'localized WinUI satellite resource' }
    @{ Id = 'WinRtMetadata';     Match = '\.winmd$';                            Note = 'WinRT metadata' }
    @{ Id = 'ResourceIndex';     Match = '\.pri$';                              Note = 'compiled resource index' }
    @{ Id = 'RuntimeMetadata';   Match = ('^' + [regex]::Escape($script:MainAssemblyName) + '\.(deps|runtimeconfig)\.json$'); Note = 'generated .NET runtime metadata' }
    @{ Id = 'RuntimeMetadata';   Match = '^workloads[^/]*\.json$';               Note = 'Windows App SDK workload manifest' }
    @{ Id = 'AppAsset';          Match = '\.(png|ico)$';                        Note = 'application icon and logo asset' }
    @{ Id = 'FrameworkAsset';    Match = '^Microsoft\.UI\.Xaml/Assets/[^/]+\.(html|png)$'; Note = 'Windows App SDK bundled asset' }
)

# Display-only hints used to explain a drop in one word. These never decide
# what ships -- the allow-list above does. They exist so the log explains
# itself ("PDB debug symbols") instead of just naming an extension.
$DropHints = @(
    @{ Pattern = '\.pdb$';                                     Label = 'PDB debug symbols' }
    @{ Pattern = '\.(cs|csproj|sln|slnx|vb|fs|resx|xaml|props|targets)$'; Label = 'source or project file' }
    @{ Pattern = '\.(db|db-wal|db-shm|sqlite|sqlite3)$';      Label = 'database file' }
    @{ Pattern = '\.log$';                                     Label = 'log file' }
    @{ Pattern = '\.(pfx|p12|key|pem)$';                       Label = 'key or certificate material' }
    @{ Pattern = '\.dat$';                                     Label = 'local data or credential material' }
    @{ Pattern = '\.(bak|tmp)$';                               Label = 'backup or temp file' }
    @{ Pattern = '\.(user|suo|sln\.dotsettings)$';             Label = 'IDE user settings' }
    @{ Pattern = '^[\u5F00\u59CB\u6D4B\u8BD5].*\.txt$';       Label = 'development / smoke test note' }
    @{ Pattern = '\.txt$';                                     Label = 'text note' }
)

# Files that must be present in the release payload, in BOTH deployment modes.
# Fail-closed check: it catches a runtime dependency silently lost to an
# over-eager filter rule.
#
# Easy to get wrong on purpose: these four look like framework files but are
# NOT provided by the .NET runtime and are not pruned in framework-dependent
# mode. Deleting any of them makes the package throw DllNotFoundException from
# inside managed code on the user's machine, which no build-time check catches:
#
#   e_sqlite3.dll             native asset of the SQLitePCLRaw NuGet package
#   Microsoft.ui.xaml.dll     comes from the self-contained Windows App SDK payload
#   WebView2Loader.dll        native asset of the Windows App SDK
#   NpuDetect/NPUDetect.dll   native asset of the Windows App SDK
#
# These paths are relative to the PAYLOAD directory, not to the release root.
# $RequiredFiles below re-bases them onto the root so that every assertion in
# this script speaks in release-root-relative paths.
$RequiredPayloadFiles = @(
    'StreamingAggregation.App.exe'
    'StreamingAggregation.App.dll'
    'StreamingAggregation.Core.dll'
    'StreamingAggregation.Infrastructure.dll'
    'StreamingAggregation.App.deps.json'
    'StreamingAggregation.App.runtimeconfig.json'
    'Assets/AppIcon.ico'
    'e_sqlite3.dll'
    'Microsoft.Data.Sqlite.dll'
    'SQLitePCLRaw.core.dll'
    'SQLitePCLRaw.provider.e_sqlite3.dll'
    'Microsoft.ui.xaml.dll'
    'Microsoft.UI.Xaml.Controls.dll'
    'Microsoft.UI.Xaml.winmd'
    'Microsoft.UI.Xaml/Assets/map.html'
    'zh-CN/Microsoft.ui.xaml.dll.mui'
    'zh-CN/Microsoft.UI.Xaml.Phone.dll.mui'
    'en-us/Microsoft.ui.xaml.dll.mui'
    'en-us/Microsoft.UI.Xaml.Phone.dll.mui'
    'zh-TW/Microsoft.ui.xaml.dll.mui'
    'zh-TW/Microsoft.UI.Xaml.Phone.dll.mui'
    'Microsoft.WindowsAppRuntime.Bootstrap.dll'
    'Microsoft.WindowsAppRuntime.Bootstrap.Net.dll'
    'WindowsAppRuntime.png'
    'WindowsAppSdk.AppxDeploymentExtensions.Desktop.dll'
    'WebView2Loader.dll'
    'H.NotifyIcon.WinUI.dll'
    'resources.pri'
    'NpuDetect/NPUDetect.dll'
)

# The same list, relative to the release root.
$RequiredFiles = @(
    foreach ($rel in $RequiredPayloadFiles) { Get-PayloadRelativePath -PayloadRelativePath $rel }
)

# The .NET runtime-pack files (coreclr.dll, hostfxr.dll, hostpolicy.dll,
# mscorlib.dll, System.Private.CoreLib.dll, System.Data.Common.dll) are NOT
# listed here because they must be ABSENT in the shipped shape. They live in
# $SelfContainedRuntimeFiles in build/Common.ps1, next to $script:DeploymentMode
# so the list and the flag it belongs to cannot drift apart.

# =============================================================================
# WHITELIST EVALUATION
# =============================================================================

function Test-AllowedDirectory {
    # $RelDir is a posix relative path ('' for the release root itself).
    param([string]$RelDir)
    if ([string]::IsNullOrEmpty($RelDir)) { return $true }

    $parts = $RelDir.Split('/')
    $head = $parts[0]
    $headOk = ($AllowedRootDirs -contains $head) -or ($AllowedCultureDirs -contains $head)
    if (-not $headOk) { return $false }

    for ($i = 1; $i -lt $parts.Count; $i++) {
        $prefix = ($parts[0..$i] -join '/')
        if ($AllowedNestedDirs -notcontains $prefix) { return $false }
    }
    return $true
}

function Get-MatchedFileRule {
    param([string]$RelPath)
    foreach ($rule in $FileRules) {
        if ([regex]::IsMatch($RelPath, $rule.Match, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            return $rule
        }
    }
    return $null
}

function Get-DropReason {
    param([string]$RelPath)
    $name = [System.IO.Path]::GetFileName($RelPath)
    $ext = [System.IO.Path]::GetExtension($RelPath)
    foreach ($hint in $DropHints) {
        if ([regex]::IsMatch($name, $hint.Pattern)) {
            return "$($hint.Label): not on the release allow-list"
        }
    }
    return "no allow-list rule matched (name='$name', ext='$ext')"
}

function Copy-ReleaseWhitelist {
    # Copies only allow-listed files from $StageDir to $ReleaseDir.
    # Returns a result object with the kept / dropped breakdown.
    param([string]$StageDir, [string]$ReleaseDir, [string]$DropLogPath = '')

    $kept = 0
    $copiedBytes = 0L
    $drops = New-Object System.Collections.Generic.List[string]
    $skippedDirs = New-Object System.Collections.Generic.List[string]

    # Directories that are not allow-listed are reported once, not per file.
    foreach ($dir in (Get-ChildItem -LiteralPath $StageDir -Recurse -Force -Directory | Sort-Object FullName)) {
        $relDir = Get-RelativePath -BasePath $StageDir -FullName $dir.FullName
        if (-not (Test-AllowedDirectory -RelDir $relDir)) {
            if (-not $skippedDirs.Contains($relDir)) {
                $skippedDirs.Add($relDir)
                Write-Host "DROP  $relDir/  -> directory is not on the release allow-list"
            }
        }
    }

    $files = @(Get-ChildItem -LiteralPath $StageDir -Recurse -Force -File | Sort-Object FullName)
    foreach ($file in $files) {
        $rel = Get-RelativePath -BasePath $StageDir -FullName $file.FullName
        $segments = $rel.Split('/')
        $relDir = ''
        if ($segments.Length -gt 1) {
            $relDir = ($segments[0..($segments.Length - 2)] -join '/')
        }
        if ($relDir -and -not (Test-AllowedDirectory -RelDir $relDir)) {
            $drops.Add("$rel  -> directory is not on the release allow-list")
            continue
        }

        if ($segments.Length -gt 1) {
            $topDir = $segments[0]
            $isCultureFolder = ($AllowedRootDirs -notcontains $topDir) -and ($AllowedCultureDirs -contains $topDir)
            if ($isCultureFolder -and ($AllowedCultureExtensions -notcontains [System.IO.Path]::GetExtension($rel))) {
                $drops.Add("$rel  -> localized resource folders may only hold $($AllowedCultureExtensions -join ', ')")
                continue
            }
        }

        $rule = Get-MatchedFileRule -RelPath $rel
        if ($null -eq $rule) {
            $drops.Add("$rel  -> $(Get-DropReason -RelPath $rel)")
            continue
        }

        $dest = Join-Path $ReleaseDir ($rel -replace '/', '\')
        $destDir = [System.IO.Path]::GetDirectoryName($dest)
        if (-not (Test-Path -LiteralPath $destDir)) {
            [void](New-Item -ItemType Directory -Path $destDir -Force)
        }
        [System.IO.File]::Copy($file.FullName, $dest, $true)
        # Copy stamps the destination with the CURRENT time, which would make
        # the release tree a record of when the build ran rather than what it
        # built. make-zip stores each entry's mtime, so two runs over the same
        # staging directory would produce two different archives. Carry the
        # source timestamp across instead.
        [System.IO.File]::SetLastWriteTimeUtc($dest, $file.LastWriteTimeUtc)
        $copiedBytes += $file.Length
        $kept++
    }

    foreach ($line in $drops) { Write-Host "DROP  $line" }

    if ($drops.Count -gt 0 -and -not [string]::IsNullOrEmpty($DropLogPath)) {
        $logDir = [System.IO.Path]::GetDirectoryName($DropLogPath)
        if (-not (Test-Path -LiteralPath $logDir)) {
            [void](New-Item -ItemType Directory -Path $logDir -Force)
        }
        [System.IO.File]::WriteAllLines($DropLogPath, $drops)
        Write-Info ("full drop list: {0}" -f $DropLogPath)
    }

    return [pscustomobject]@{
        Kept         = $kept
        Dropped      = $drops.Count
        DroppedDirs  = $skippedDirs.Count
        CopiedBytes  = $copiedBytes
    }
}

function Move-PayloadIntoSubdirectory {
    # Moves everything the allow-list just copied into <ReleaseDir>\app\ and
    # puts the launcher at the root.
    #
    # Runs AFTER Copy-ReleaseWhitelist on purpose: the allow-list keeps
    # matching staging-relative paths, so its rules stay exactly as audited,
    # and the only thing this step decides is WHERE the accepted files land.
    # A move (not a re-copy) means the bytes and timestamps are the ones the
    # allow-list already accepted.
    #
    # Order is deterministic -- sorted by name -- so two runs over an unchanged
    # staging directory produce an identical tree.
    param([string]$ReleaseDir, [string]$LauncherSourcePath)

    $payloadDir = Get-PayloadDirPath -ReleaseDir $ReleaseDir

    # Snapshot the root BEFORE the payload directory exists. Enumerating after
    # creating it would put 'app' into its own move list, and Move-Item refuses
    # to move a directory into itself. Snapshotting first also makes the move
    # order independent of when the payload directory appears.
    $items = @(Get-ChildItem -LiteralPath $ReleaseDir -Force |
               Where-Object { $_.FullName -ne $payloadDir } |
               Sort-Object Name)

    if (-not (Test-Path -LiteralPath $payloadDir -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $payloadDir -Force)
    }

    # Everything in the snapshot belongs to the payload. The launcher is not
    # there yet, so there is nothing to exclude.
    $movedFiles = 0
    $movedDirs = 0
    foreach ($item in $items) {
        $dest = Join-Path $payloadDir $item.Name
        if (Test-Path -LiteralPath $dest) {
            throw ("Refusing to move '{0}': '{1}' already exists." -f $item.Name, $dest)
        }
        # -ErrorAction Stop: a failed move must fail the build. Left as a
        # non-terminating error it would print a red line, carry on, and leave a
        # payload the layout assertion then has to guess about.
        Move-Item -LiteralPath $item.FullName -Destination $dest -ErrorAction Stop
        if ($item.PSIsContainer) { $movedDirs++ } else { $movedFiles++ }
    }

    # Written, not copied: the build pins the line endings so the shipped
    # bytes do not depend on the checkout's core.autocrlf. build/launcher.bat
    # itself stays in the repository so the next run -- including
    # -SkipPublish -- still has its input.
    Install-Launcher -SourcePath $LauncherSourcePath `
        -DestinationPath (Get-LauncherPath -ReleaseDir $ReleaseDir)

    return [pscustomobject]@{
        MovedFiles    = $movedFiles
        MovedDirs     = $movedDirs
        PayloadDir    = $payloadDir
        LauncherPath  = (Get-LauncherPath -ReleaseDir $ReleaseDir)
    }
}

function Assert-RequiredFiles {
    # Two-sided check, both halves expressed in release-root-relative paths.
    #
    #   existence : everything in $RequiredFiles ('app/...') must be there.
    #               Catches a dependency silently lost to an over-eager filter
    #               rule.
    #   absence   : in framework-dependent mode nothing in
    #               $script:SelfContainedRuntimeFiles may be there. Their
    #               presence means the deployment switch did not take effect
    #               and the package is still carrying the .NET runtime pack.
    #
    # The absence half matters because the failure it catches is otherwise
    # silent: a self-contained package built from a framework-dependent command
    # line looks complete, scans clean, and is simply 80 MB too big.
    #
    # Each absence check runs against BOTH the payload directory and the release
    # root. The runtime files have to be absent from the payload to not ship;
    # a stray copy at the root would mean something bypassed the payload move
    # entirely, which is exactly the bug this layout change is meant to make
    # impossible.
    param([string]$ReleaseDir, [string]$ExpectedMode = '')

    if ([string]::IsNullOrEmpty($ExpectedMode)) { $ExpectedMode = $script:DeploymentMode }

    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($rel in $RequiredFiles) {
        $path = Join-Path $ReleaseDir ($rel -replace '/', '\')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            $missing.Add($rel)
        }
    }
    if ($missing.Count -gt 0) {
        throw ("Release payload is incomplete, {0} required file(s) missing:`n    {1}" -f `
            $missing.Count, ($missing -join "`n    "))
    }

    $unexpected = New-Object System.Collections.Generic.List[string]
    if ($ExpectedMode -ne 'SelfContained') {
        foreach ($rel in $script:SelfContainedRuntimeFiles) {
            foreach ($root in @((Get-PayloadDirPath -ReleaseDir $ReleaseDir), $ReleaseDir)) {
                $path = Join-Path $root $rel
                if (Test-Path -LiteralPath $path -PathType Leaf) {
                    $where = Get-RelativePath -BasePath $ReleaseDir -FullName $path
                    $unexpected.Add($where)
                }
            }
        }
        if ($unexpected.Count -gt 0) {
            throw ("Release directory is '$ExpectedMode' but still ships {0} self-contained runtime file(s):`n    {1}`n    The deployment switch did not take effect." -f `
                $unexpected.Count, ($unexpected -join "`n    "))
        }
    }
}

function Invoke-StagePublish {
    # dotnet publish into a scratch staging folder.
    param([string]$RepoRootPath, [string]$ProjectPath, [string]$StageDir, [string]$ReleaseVersion)

    $logDir = Join-Path $RepoRootPath '.tmp\build-logs'
    if (-not (Test-Path -LiteralPath $logDir)) {
        [void](New-Item -ItemType Directory -Path $logDir -Force)
    }
    $logPath = Join-Path $logDir "publish-$ReleaseVersion-win-x64.log"

    # Debug symbols are switched off at the source; the whitelist would drop
    # them anyway. Belt and braces: no PDB is generated in the first place.
    #
    # The deployment shape is passed as -p: rather than left to the csproj.
    # Both properties in the csproj carry a Condition="'$(..)' == ''" guard, so
    # a command-line global property wins; without that guard the csproj value
    # would silently override this and the build would keep its old shape.
    # Assert-DeploymentShape reads the result back out of runtimeconfig.json.
    $dotnetArgs = @(
        'publish', $ProjectPath,
        '--configuration', $script:Configuration,
        '--runtime', $script:RuntimeIdentifier,
        ('-p:SelfContained=' + $script:SelfContained),
        ('-p:WindowsAppSDKSelfContained=' + $script:WindowsAppSdkSelfContained),
        '--output', $StageDir,
        # Parenthesised on purpose: the comma operator binds tighter than '+'
        # inside an array literal, so an unparenthesised concatenation would
        # split into two separate arguments.
        ('-p:Platform=' + $script:BuildPlatform),
        '-p:DebugType=none',
        '-p:DebugSymbols=false',
        '-p:PublishSingleFile=false',
        '-p:IncludeNativeLibrariesForSelfExtract=false',
        '--nologo',
        '--verbosity', 'minimal'
    )

    Write-Step "dotnet publish ($($script:Configuration) / $($script:RuntimeIdentifier) / $($script:DeploymentMode))"
    Write-Info "log: $logPath"

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $publishOutput = & dotnet @dotnetArgs 2>&1
    $code = $LASTEXITCODE
    $watch.Stop()

    $publishOutput | ForEach-Object { Write-Host "    $_" }
    $publishOutput | Out-File -LiteralPath $logPath -Encoding utf8
    if ($code -ne 0) {
        throw "dotnet publish failed with exit code $code (see $logPath)."
    }
    Write-Info ("publish finished in {0:N1}s" -f $watch.Elapsed.TotalSeconds)
}

# =============================================================================
# MAIN
# =============================================================================

Initialize-Console

try {
    $repoRoot = Get-RepoRoot
    $declaredVersion = Get-ProductVersion -RepoRootPath $repoRoot
    $releaseVersion = Resolve-ReleaseVersion -Requested $Version -Actual $declaredVersion

    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $OutputRoot = Join-Path $repoRoot 'artifacts\release'
    } else {
        $OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
    }
    if ([string]::IsNullOrWhiteSpace($StageRoot)) {
        $StageRoot = Join-Path $repoRoot 'artifacts\stage'
    } else {
        $StageRoot = [System.IO.Path]::GetFullPath($StageRoot)
    }

    $releaseDir = Join-Path (Join-Path $OutputRoot $releaseVersion) $script:RuntimeIdentifier
    $projectPath = Get-AppProjectPath -RepoRootPath $repoRoot

    Write-Step "Release $releaseVersion ($($script:Configuration) / $($script:RuntimeIdentifier) / $($script:DeploymentMode))"
    Write-Info "repo:       $repoRoot"
    Write-Info "project:    $projectPath"
    Write-Info "stage:      $StageRoot"
    Write-Info "release:    $releaseDir"

    # A release that cannot start is not a release: check the embedded
    # application manifest identity before spending a publish on it.
    Write-Step "Checking application manifest identity"
    $manifestVersion = Assert-ManifestIdentity -RepoRootPath $repoRoot
    Write-Info "assemblyIdentity version $manifestVersion (four-part, matches Version.props)"

    # The launcher is a static file pointing at a variable exe name and a
    # variable payload directory. Nothing else would notice if the two drifted.
    Write-Step "Checking launcher contract"
    $launcherSource = Assert-LauncherContract -RepoRootPath $repoRoot
    Write-Info ("{0} is ASCII and launches $($script:PayloadDir)\$(Get-MainExeName)" -f $script:LauncherName)

    # Only wipe the staging folder when this run actually publishes into it:
    # -SkipPublish means "reuse what is already there".
    if (-not $SkipPublish) {
        $stageDir = Reset-Directory -Path $StageRoot
        Invoke-StagePublish -RepoRootPath $repoRoot -ProjectPath $projectPath `
            -StageDir $stageDir -ReleaseVersion $releaseVersion
    } else {
        if (-not (Test-Path -LiteralPath $StageRoot -PathType Container)) {
            throw "Staging directory not found for -SkipPublish: $StageRoot"
        }
        $stageDir = (Resolve-Path -LiteralPath $StageRoot).Path
        Write-Step "Skipping dotnet publish (-SkipPublish); reusing $stageDir"
    }

    $stageStats = Get-DirectoryStats -Path $stageDir
    if ($stageStats.FileCount -eq 0) {
        throw "Staging directory is empty: $stageDir"
    }
    Write-Info ("staging directory: {0} files, {1}" -f $stageStats.FileCount, (Format-Bytes $stageStats.Bytes))

    Write-Step "Applying the release allow-list"
    $releaseDir = Reset-Directory -Path $releaseDir
    $dropLog = Join-Path $repoRoot ".tmp\build-logs\publish-drops-$releaseVersion-win-x64.txt"
    $result = Copy-ReleaseWhitelist -StageDir $stageDir -ReleaseDir $releaseDir -DropLogPath $dropLog
    Write-Host ("    kept {0} file(s) ({1}); dropped {2} file(s) and {3} directory(ies)" -f `
        $result.Kept, (Format-Bytes $result.CopiedBytes), $result.Dropped, $result.DroppedDirs)

    # Everything the allow-list accepted now goes into one subdirectory, and
    # the launcher takes the root. This is what makes an unzipped package show
    # two entries instead of the full payload listing.
    Write-Step "Moving the payload into '$($script:PayloadDir)\' and installing the launcher"
    $moved = Move-PayloadIntoSubdirectory -ReleaseDir $releaseDir -LauncherSourcePath $launcherSource
    Write-Host ("    moved {0} file(s) and {1} directory(ies) into {2}" -f `
        $moved.MovedFiles, $moved.MovedDirs, $script:PayloadDir)
    Write-Host ("    launcher: {0}" -f $moved.LauncherPath)

    Write-Step "Verifying release root layout"
    $rootEntries = Assert-ReleaseRootLayout -ReleaseDir $releaseDir
    Write-Host ("    root holds exactly {0} file(s) ({1}) and {2} directory(ies) ({3})" -f `
        $rootEntries.Files.Count, ($rootEntries.Files -join ', '),
        $rootEntries.Dirs.Count, ($rootEntries.Dirs -join ', '))

    Write-Step "Verifying deployment shape"
    $shape = Assert-DeploymentShape -ReleaseDir $releaseDir
    Write-Host ("    {0} (Microsoft.NETCore.App {1})" -f $shape.Shape, $shape.FrameworkVersion)

    Write-Step "Verifying required runtime files"
    Assert-RequiredFiles -ReleaseDir $releaseDir
    if ($script:DeploymentMode -eq 'SelfContained') {
        Write-Host ("    all {0} required file(s) present" -f $RequiredFiles.Count)
    } else {
        Write-Host ("    all {0} required file(s) present; {1} self-contained runtime file(s) correctly absent" -f `
            $RequiredFiles.Count, $script:SelfContainedRuntimeFiles.Count)
    }

    if (-not $SkipScan) {
        Invoke-SensitiveScan -RepoRootPath $repoRoot -TargetDir $releaseDir | Out-Null
    } else {
        Write-WarnLine "Sensitive-file scan skipped (-SkipScan)"
    }

    $stats = Get-DirectoryStats -Path $releaseDir
    Write-Step "Release directory ready"
    Write-Host ("    path:      {0}" -f $releaseDir)
    Write-Host ("    root:      {0} + {1}\ ({2} file(s) total)" -f `
        $script:LauncherName, $script:PayloadDir, $stats.FileCount)
    Write-Host ("    files:     {0}" -f $stats.FileCount)
    Write-Host ("    folders:   {0}" -f $stats.DirCount)
    Write-Host ("    size:      {0} ({1:N0} bytes)" -f (Format-Bytes $stats.Bytes), $stats.Bytes)
    exit $script:ExitOk

} catch {
    Fail -Message $_.Exception.Message -ExitCode $script:ExitFailure
}