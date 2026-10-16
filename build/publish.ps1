# publish.ps1
# Builds the whitelisted win-x64 release directory.
#
# Release shape is fixed by the handoff plan:
#   Release + win-x64 + self-contained + full-directory publish
#   (no single-file merge: it has never been validated on a real machine)
#
# Layout produced:
#   <OutputRoot>/<version>/win-x64/          <- the release directory
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
# Satellite resource folders are recognised by BCP-47 shape (language subtag
# plus a mandatory hyphen), so 'obj' / 'bin' / 'src' / 'tests' can never
# sneak in through the culture rule. Region and script subtags may contain
# digits ('zh-419', 'es-419'), so the later subtags allow 0-9.
$AllowedRootDirs   = @('Assets', 'Microsoft.UI.Xaml', 'NpuDetect')
$CultureDirPattern = '^[a-z]{2,3}-[A-Za-z0-9]{2,8}(-[A-Za-z0-9]{2,8})*$'

# Directories allowed below the release root, as posix relative paths.
$AllowedNestedDirs = @('Microsoft.UI.Xaml/Assets')

# A folder that matched $CultureDirPattern may only contain localized resource
# files. Without this, a stray folder such as 'obj-x64' would pass the culture
# shape test and smuggle any allow-listed binary through.
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

# Files that must be present in the release directory. Fail-closed check: it
# catches a runtime dependency silently lost to an over-eager filter rule.
$RequiredFiles = @(
    'StreamingAggregation.App.exe'
    'StreamingAggregation.App.dll'
    'StreamingAggregation.Core.dll'
    'StreamingAggregation.Infrastructure.dll'
    'StreamingAggregation.App.deps.json'
    'StreamingAggregation.App.runtimeconfig.json'
    'Assets/AppIcon.ico'
    'coreclr.dll'
    'hostfxr.dll'
    'hostpolicy.dll'
    'mscorlib.dll'
    'System.Private.CoreLib.dll'
    'System.Data.Common.dll'
    'e_sqlite3.dll'
    'Microsoft.Data.Sqlite.dll'
    'SQLitePCLRaw.core.dll'
    'SQLitePCLRaw.provider.e_sqlite3.dll'
    'Microsoft.ui.xaml.dll'
    'Microsoft.UI.Xaml.Controls.dll'
    'Microsoft.UI.Xaml.winmd'
    'Microsoft.UI.Xaml/Assets/map.html'
    'Microsoft.WindowsAppRuntime.Bootstrap.dll'
    'Microsoft.WindowsAppRuntime.Bootstrap.Net.dll'
    'WindowsAppRuntime.png'
    'WindowsAppSdk.AppxDeploymentExtensions.Desktop.dll'
    'WebView2Loader.dll'
    'H.NotifyIcon.WinUI.dll'
    'resources.pri'
    'NpuDetect/NPUDetect.dll'
)

# =============================================================================
# WHITELIST EVALUATION
# =============================================================================

function Test-AllowedDirectory {
    # $RelDir is a posix relative path ('' for the release root itself).
    param([string]$RelDir)
    if ([string]::IsNullOrEmpty($RelDir)) { return $true }

    $parts = $RelDir.Split('/')
    $head = $parts[0]
    $headOk = ($AllowedRootDirs -contains $head) -or ($head -cmatch $CultureDirPattern)
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
            $isCultureFolder = ($AllowedRootDirs -notcontains $topDir) -and ($topDir -cmatch $CultureDirPattern)
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

function Assert-RequiredFiles {
    param([string]$ReleaseDir)
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($rel in $RequiredFiles) {
        $path = Join-Path $ReleaseDir ($rel -replace '/', '\')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            $missing.Add($rel)
        }
    }
    if ($missing.Count -gt 0) {
        throw ("Release directory is incomplete, {0} required file(s) missing:`n    {1}" -f `
            $missing.Count, ($missing -join "`n    "))
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
    $dotnetArgs = @(
        'publish', $ProjectPath,
        '--configuration', $script:Configuration,
        '--runtime', $script:RuntimeIdentifier,
        '--self-contained', 'true',
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

    Write-Step "dotnet publish ($($script:Configuration) / $($script:RuntimeIdentifier) / self-contained)"
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

    Write-Step "Release $releaseVersion ($($script:Configuration) / $($script:RuntimeIdentifier) / self-contained)"
    Write-Info "repo:       $repoRoot"
    Write-Info "project:    $projectPath"
    Write-Info "stage:      $StageRoot"
    Write-Info "release:    $releaseDir"

    # A release that cannot start is not a release: check the embedded
    # application manifest identity before spending a publish on it.
    Write-Step "Checking application manifest identity"
    $manifestVersion = Assert-ManifestIdentity -RepoRootPath $repoRoot
    Write-Info "assemblyIdentity version $manifestVersion (four-part, matches Version.props)"

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

    Write-Step "Verifying required runtime files"
    Assert-RequiredFiles -ReleaseDir $releaseDir
    Write-Host ("    all {0} required file(s) present" -f $RequiredFiles.Count)

    if (-not $SkipScan) {
        Invoke-SensitiveScan -RepoRootPath $repoRoot -TargetDir $releaseDir | Out-Null
    } else {
        Write-WarnLine "Sensitive-file scan skipped (-SkipScan)"
    }

    $stats = Get-DirectoryStats -Path $releaseDir
    Write-Step "Release directory ready"
    Write-Host ("    path:      $releaseDir")
    Write-Host ("    files:     {0}" -f $stats.FileCount)
    Write-Host ("    folders:   {0}" -f $stats.DirCount)
    Write-Host ("    size:      {0} ({1:N0} bytes)" -f (Format-Bytes $stats.Bytes), $stats.Bytes)
    exit $script:ExitOk

} catch {
    Fail -Message $_.Exception.Message -ExitCode $script:ExitFailure
}