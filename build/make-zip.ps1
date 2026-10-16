# make-zip.ps1
# Packs the whitelisted release directory into a ZIP and writes SHA-256 sums.
#
# ZIP layout rule from the handoff plan: the archive root holds the launcher
# and the app\ payload directory, and nothing else. No wrapping folder.
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
    param([string]$ZipPath, [string]$MainExeName, [string]$LauncherName, [string]$PayloadDir)

    Add-Type -AssemblyName System.IO.Compression | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

    $problems = New-Object System.Collections.Generic.List[string]
    $entryCount = 0

    $expectedMainEntry = "$PayloadDir/$MainExeName"
    $payloadPrefix = "$PayloadDir/"

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

            # --- the payload must be in one subdirectory, not at the root ---
            # This is the whole point of the layout: an unzipped package has to
            # show two entries, not a flat dump of every runtime dependency.
            if (-not $names.Contains($expectedMainEntry)) {
                $problems.Add("main program is not at '$expectedMainEntry'")
            }

            $rootFiles = @($names | Where-Object { $_ -notmatch '/' } | Sort-Object -Unique)
            $rootDirs = @($names | Where-Object { $_ -match '/' } |
                          ForEach-Object { $_.Split('/')[0] } | Sort-Object -Unique)

            foreach ($dir in $rootDirs) {
                if ($dir -cne $PayloadDir) {
                    $problems.Add("archive root holds an unexpected entry: $dir/")
                }
            }
            if ($rootDirs -notcontains $PayloadDir) {
                $problems.Add("archive root has no '$PayloadDir/' payload directory")
            }
            foreach ($file in $rootFiles) {
                if ($file -cne $LauncherName) {
                    $problems.Add("archive root holds an unexpected file: $file (only $LauncherName is allowed there)")
                }
            }
            if ($rootFiles -notcontains $LauncherName) {
                $problems.Add("archive root has no '$LauncherName' launcher")
            }

            $mainDirs = $rootDirs
            if ($mainDirs -contains 'win-x64') {
                $problems.Add("archive root is wrapped in an extra 'win-x64' folder")
            }

            # Every entry must live under the payload: a file that escaped the
            # subdirectory would be a file the loader can never resolve.
            $strayEntries = @($names | Where-Object {
                $_ -ne $LauncherName -and
                -not $_.StartsWith($payloadPrefix, [System.StringComparison]::OrdinalIgnoreCase)
            })
            foreach ($stray in $strayEntries) {
                $problems.Add("archive entry is outside '$payloadPrefix': $stray")
            }

            # Culture folders: only the allow-listed ones may appear, and
            # never more of them. This mirrors the publish.ps1 allow-list.
            # They sit one level below the payload directory, not at the root.
            $allowedCultures = @('zh-CN', 'zh-TW', 'en-us')
            $appTopDirs = @($names | Where-Object {
                    $_.StartsWith($payloadPrefix, [System.StringComparison]::OrdinalIgnoreCase)
                } |
                Where-Object { $_.Substring($payloadPrefix.Length) -match '/' } |
                ForEach-Object { $_.Substring($payloadPrefix.Length).Split('/')[0] } |
                Sort-Object -Unique)
            $cultureDirs = @($appTopDirs | Where-Object { $_ -cmatch '^[a-z]{2,3}-[A-Za-z0-9]{2,8}(-[A-Za-z0-9]{2,8})*$' })
            if ($cultureDirs.Count -gt $allowedCultures.Count) {
                $problems.Add("too many culture folders in archive: $($cultureDirs -join ', ')")
            }
            foreach ($culture in $cultureDirs) {
                if ($allowedCultures -notcontains $culture) {
                    $problems.Add("culture folder not on the allow-list: $culture")
                }
            }

            Write-Info ("entries: {0} ({1} under {2}\)" -f $entryCount, ($entryCount - $rootFiles.Count), $PayloadDir)
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

    $mainExePath = Get-MainExePath -ReleaseDir $ReleaseDir
    if (-not (Test-Path -LiteralPath $mainExePath -PathType Leaf)) {
        throw "Main program missing from the release payload: $mainExePath"
    }

    # The release layout is a precondition for packing: a root full of loose
    # dependencies would archive just fine and still be wrong.
    $rootEntries = Assert-ReleaseRootLayout -ReleaseDir $ReleaseDir
    Write-Info ("release root: {0} + {1}\" -f ($rootEntries.Files -join ', '), ($rootEntries.Dirs -join ', '))

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

    Write-Step "Writing ZIP (root holds the launcher and the app\ payload)"
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
    $entryCount = Assert-ZipContents -ZipPath $zipPath -MainExeName $mainExeName `
        -LauncherName $script:LauncherName -PayloadDir $script:PayloadDir
    Write-Host ("    {0} entries; root holds only '{1}' and '{2}\'; no PDB / dev material" -f `
        $entryCount, $script:LauncherName, $script:PayloadDir)

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