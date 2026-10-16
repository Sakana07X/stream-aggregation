# make-zip.ps1
# Packs the whitelisted release directory into a ZIP and writes SHA-256 sums.
#
# ZIP layout rule from the handoff plan: the archive root holds the main
# program and every runtime dependency directly. No wrapping folder.
#
# ASCII-only on purpose (PowerShell 5.1 reads BOM-less scripts as ANSI).
#
# Usage:
#   powershell -File build/make-zip.ps1
#   powershell -File build/make-zip.ps1 -Version 0.2.0
#   powershell -File build/make-zip.ps1 -OutputRoot D:\somewhere\release
#   powershell -File build/make-zip.ps1 -SkipScan
#
# Exit codes: 0 = ok, 1 = failure, 2 = usage error

param(
    [string]$Version = '',
    [string]$OutputRoot = '',
    [string]$ReleaseDir = '',
    [switch]$SkipScan
)

. (Join-Path $PSScriptRoot 'Common.ps1')

# =============================================================================
# ZIP POST-CONDITIONS
#
# These run AFTER the archive is written and are assertions, not filters:
# the build-time allow-list in publish.ps1 is what decides what ships. Here we
# only verify the archive really is clean, and fail the build otherwise.
# =============================================================================

# File types that must never appear in the archive.
$ForbiddenExtensions = @('.pdb', '.db', '.db-wal', '.db-shm', '.sqlite', '.sqlite3',
                         '.log', '.txt', '.cs', '.csproj', '.sln', '.slnx', '.xaml',
                         '.resx', '.user', '.suo', '.map', '.dat', '.bak', '.pfx',
                         '.p12', '.key', '.pem', '.zip')

# Path fragments that must never appear in an archive entry name.
$ForbiddenFragments = @('/SnowLuma', '/Steam', '/obj/', '/bin/', '/src/', '/tests/',
                        '/.tmp/', '/.git/', '/logs/', '/attachments/')

# =============================================================================
# ZIP CREATION
# =============================================================================

function New-ReleaseZip {
    # Entry names are the release-relative paths with forward slashes and no
    # wrapping folder. Entries are written in sorted order so two runs over an
    # unchanged release directory produce the same entry sequence.
    param([string]$SourceDir, [string]$ZipPath)

    Add-Type -AssemblyName System.IO.Compression | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

    $files = @(Get-ChildItem -LiteralPath $SourceDir -Recurse -Force -File |
               Sort-Object { Get-RelativePath -BasePath $SourceDir -FullName $_.FullName })

    $stream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::Create,
                                     [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive(
            $stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in $files) {
                $entryName = Get-RelativePath -BasePath $SourceDir -FullName $file.FullName
                $entry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
                try {
                    # Timestamp is cosmetic; a clock quirk must not fail the build.
                    try { $entry.LastWriteTime = $file.LastWriteTime } catch { }
                } catch { }
                $entryStream = $entry.Open()
                $fileStream = [System.IO.File]::OpenRead($file.FullName)
                try {
                    $fileStream.CopyTo($entryStream)
                } finally {
                    $fileStream.Dispose()
                    $entryStream.Dispose()
                }
            }
        } finally {
            $archive.Dispose()
        }
    } finally {
        $stream.Dispose()
    }

    return $files.Count
}

function Assert-ZipContents {
    param([string]$ZipPath, [string]$MainExeName)

    Add-Type -AssemblyName System.IO.Compression | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

    $problems = New-Object System.Collections.Generic.List[string]
    $entryCount = 0

    $stream = [System.IO.File]::OpenRead($ZipPath)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive(
            $stream, [System.IO.Compression.ZipArchiveMode]::Read)
        try {
            $names = New-Object System.Collections.Generic.List[string]
            foreach ($entry in $archive.Entries) {
                $entryCount++
                $name = $entry.FullName
                $names.Add($name)

                if ($entry.FullName -match '\\') {
                    $problems.Add("entry name is not posix-style: $name")
                }
                $ext = [System.IO.Path]::GetExtension($name).ToLower()
                if ($ForbiddenExtensions -contains $ext) {
                    $problems.Add("forbidden file type in archive: $name")
                }
                foreach ($fragment in $ForbiddenFragments) {
                    if ($name.IndexOf($fragment, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                        $problems.Add("forbidden path fragment '$fragment' in archive: $name")
                        break
                    }
                }
                $devNote = [regex]::Match($name, '^[\u5F00\u59CB\u6D4B\u8BD5]')
                if ($devNote.Success) {
                    $problems.Add("development test material in archive: $name")
                }
            }

            if (-not $names.Contains($MainExeName)) {
                $problems.Add("main program '$MainExeName' is not at the archive root")
            }
            $rootDepth = @($names | Where-Object { $_ -match '/' }).Count
            $mainDirs = @($names | Where-Object { $_ -match '/' } |
                          ForEach-Object { $_.Split('/')[0] } | Sort-Object -Unique)
            if ($mainDirs -contains 'win-x64') {
                $problems.Add("archive root is wrapped in an extra 'win-x64' folder")
            }

            # Culture folders: only the allow-listed ones may appear, and
            # never more of them. This mirrors the publish.ps1 allow-list.
            $allowedCultures = @('zh-CN', 'zh-TW', 'en-us')
            $cultureDirs = @($mainDirs | Where-Object { $_ -cmatch '^[a-z]{2,3}-[A-Za-z0-9]{2,8}(-[A-Za-z0-9]{2,8})*$' })
            if ($cultureDirs.Count -gt $allowedCultures.Count) {
                $problems.Add("too many culture folders in archive: $($cultureDirs -join ', ')")
            }
            foreach ($culture in $cultureDirs) {
                if ($allowedCultures -notcontains $culture) {
                    $problems.Add("culture folder not on the allow-list: $culture")
                }
            }

            Write-Info ("entries: {0} ({1} below the root)" -f $entryCount, $rootDepth)
        } finally {
            $archive.Dispose()
        }
    } finally {
        $stream.Dispose()
    }

    if ($problems.Count -gt 0) {
        throw ("ZIP content check failed with {0} problem(s):`n    {1}" -f `
            $problems.Count, ($problems -join "`n    "))
    }
    return $entryCount
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
    $productName = Get-ProductName
    $mainExeName = Get-MainExeName

    if ([string]::IsNullOrWhiteSpace($ReleaseDir)) {
        $ReleaseDir = Join-Path (Join-Path $OutputRoot $releaseVersion) $script:RuntimeIdentifier
    } else {
        $ReleaseDir = [System.IO.Path]::GetFullPath($ReleaseDir)
    }

    if (-not (Test-Path -LiteralPath $ReleaseDir -PathType Container)) {
        throw ("Release directory not found: {0}`nRun build/publish.ps1 first." -f $ReleaseDir)
    }
    $ReleaseDir = (Resolve-Path -LiteralPath $ReleaseDir).Path

    $mainExePath = Join-Path $ReleaseDir $mainExeName
    if (-not (Test-Path -LiteralPath $mainExePath -PathType Leaf)) {
        throw "Main program missing from the release directory: $mainExePath"
    }

    $zipName = "$productName-$releaseVersion-$($script:RuntimeIdentifier).zip"
    $zipPath = Join-Path $OutputRoot $zipName
    $sumsPath = Join-Path $OutputRoot 'SHA256SUMS.txt'

    Write-Step "Packing release $releaseVersion"
    Write-Info "source:  $ReleaseDir"
    Write-Info "archive: $zipPath"

    if (-not $SkipScan) {
        Invoke-SensitiveScan -RepoRootPath $repoRoot -TargetDir $ReleaseDir | Out-Null
    } else {
        Write-WarnLine "Sensitive-file scan skipped (-SkipScan)"
    }

    $stats = Get-DirectoryStats -Path $ReleaseDir
    Write-Info ("packing {0} file(s), {1}" -f $stats.FileCount, (Format-Bytes $stats.Bytes))

    Write-Step "Writing ZIP (root holds the main program directly)"
    # -OutputRoot is honoured by the usage banner but was never created, so a
    # caller-supplied (or freshly cleaned) root made ZipFile.Open fail with
    # "part of the path could not be found" instead of producing the archive.
    if (-not (Test-Path -LiteralPath (Split-Path -Parent $zipPath) -PathType Container)) {
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $zipPath) -Force)
    }
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $fileCount = New-ReleaseZip -SourceDir $ReleaseDir -ZipPath $zipPath
    $watch.Stop()
    Write-Info ("archive written in {0:N1}s" -f $watch.Elapsed.TotalSeconds)

    Write-Step "Verifying archive layout and contents"
    $entryCount = Assert-ZipContents -ZipPath $zipPath -MainExeName $mainExeName
    Write-Host ("    {0} entries; '{1}' at the root; no PDB / dev material" -f $entryCount, $mainExeName)

    Write-Step "Writing SHA-256 sums"
    $hash = Get-Sha256 -Path $zipPath
    $sumLine = "$hash  $zipName"
    [System.IO.File]::WriteAllText($sumsPath, "$sumLine`r`n", (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "    $sumsPath"

    $zipInfo = Get-Item -LiteralPath $zipPath
    Write-Step "Done"
    Write-Host ("    zip:        {0}" -f $zipInfo.FullName)
    Write-Host ("    size:       {0} ({1:N0} bytes)" -f (Format-Bytes $zipInfo.Length), $zipInfo.Length)
    Write-Host ("    sha-256:    {0}" -f $hash)
    Write-Host ("    file count: {0} in release dir, {1} in archive" -f $fileCount, $entryCount)
    exit $script:ExitOk

} catch {
    Fail -Message $_.Exception.Message -ExitCode $script:ExitFailure
}