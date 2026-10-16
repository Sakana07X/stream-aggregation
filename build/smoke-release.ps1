# smoke-release.ps1
# Launch smoke test for the whitelisted release directory.
#
# WHAT THIS IS FOR
#
# A release can pass every static check in this folder -- allow-list complete,
# deployment shape correct, archive layout clean, sensitive scan clear -- and
# still never show a window. That failure mode is real and has been measured
# on this project: when the application payload is scattered across sibling
# directories instead of living in one subdirectory next to the exe,
# CreateProcess still succeeds. The process starts, reports Responding = True,
# owns a window handle, and never draws anything. It is a ghost process.
#
# Every one of those ghost signals looks healthy. The two that do not are:
#
#   MainWindowTitle == the exe name
#       Win32 reports the exe base name for a window that has no title of its
#       own. A real WinUI window carries the product name, so a title equal to
#       'StreamingAggregation.App' means the window never got past the shell.
#       Measured: ghost = 'StreamingAggregation.App', real = product name.
#
#   Threads.Count ~12 instead of ~74
#       The ghost never loads the Windows App SDK, so it never starts the XAML
#       and composition worker threads. 60 is the floor: it sits well clear of
#       the observed ghost value and well under the observed real value.
#
# HONESTY CONTRACT
#
# This script reports what it actually did and never lets a non-verdict read
# as a pass:
#
#   exit 0  PASS   the app launched and showed a real window
#   exit 1  FAIL   the app launched and did not show a real window, or the
#                  release directory is not usable at all
#   exit 2  USAGE  bad parameters
#   exit 3  SKIP   a precondition was absent, so no verdict was reached
#
# A caller MUST branch on the exit code. Exit 3 is deliberately not 0: a CI box
# without the .NET runtime would otherwise report a green smoke test that never
# launched anything. The final line of output is always a machine-readable
# 'SMOKE RESULT: PASS|FAIL|SKIP'.
#
# ASCII-only on purpose (PowerShell 5.1 reads BOM-less .ps1 as ANSI).
#
# Usage:
#   powershell -File build/smoke-release.ps1
#   powershell -File build/smoke-release.ps1 -ReleaseDir D:\somewhere\win-x64
#   powershell -File build/smoke-release.ps1 -StartupTimeoutSeconds 120
#
# Exit codes: 0 = pass, 1 = fail, 2 = usage error, 3 = skipped

param(
    [string]$ReleaseDir = '',
    [int]$StartupTimeoutSeconds = 90,
    [int]$MinThreadCount = 60
)

. (Join-Path $PSScriptRoot 'Common.ps1')

# =============================================================================
# PRECONDITION PROBE
# =============================================================================

# Installed versions of one .NET shared framework.
#
# The registry is read first: it is what the installer writes and it is present
# on a runtime-only machine, where the 'dotnet' CLI and thus
# `dotnet --list-runtimes` may not be. The on-disk layout is only a fallback.
# Both paths are probed because a portable / user-local install writes the
# layout and not the registry.
function Get-InstalledFrameworkVersions {
    param([string]$FrameworkName)

    $versions = New-Object System.Collections.Generic.List[version]

    $regRoots = @(
        'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx'
        'HKLM:\SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx'
    )
    foreach ($regRoot in $regRoots) {
        $key = Join-Path $regRoot $FrameworkName
        if (-not (Test-Path -LiteralPath $key)) { continue }
        $props = $null
        try { $props = Get-ItemProperty -LiteralPath $key } catch { continue }
        if ($null -eq $props) { continue }
        foreach ($prop in $props.PSObject.Properties) {
            $parsed = $null
            if ([version]::TryParse($prop.Name, [ref]$parsed)) { $versions.Add($parsed) }
        }
    }

    $layoutRoots = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) { $layoutRoots.Add($env:DOTNET_ROOT) }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $layoutRoots.Add((Join-Path $env:ProgramFiles 'dotnet'))
    }
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $layoutRoots.Add((Join-Path ${env:ProgramFiles(x86)} 'dotnet'))
    }
    foreach ($root in $layoutRoots) {
        $dir = Join-Path (Join-Path (Join-Path $root 'shared') $FrameworkName) ''
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) { continue }
        foreach ($sub in (Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue)) {
            $parsed = $null
            if ([version]::TryParse($sub.Name, [ref]$parsed)) { $versions.Add($parsed) }
        }
    }

    return $versions
}

# Decides whether this machine can run the release at all.
#
# Returns $null when it can, or a human-readable reason when it cannot. The
# caller turns a reason into SKIP; it never turns one into PASS.
function Test-RuntimePrecondition {
    param([string]$Dir)

    $shape = Get-DeploymentShape -ReleaseDir $Dir
    if ($shape.Shape -eq 'SelfContained') {
        Write-Info "deployment shape is SelfContained; no installed runtime is needed"
        return $null
    }
    if ($shape.Shape -ne 'FrameworkDependent') {
        return ("cannot determine the deployment shape from {0} (read '{1}')" -f `
                $shape.RuntimeConfigPath, $shape.Shape)
    }

    $requiredName = 'Microsoft.NETCore.App'
    if ([string]::IsNullOrWhiteSpace($shape.FrameworkVersion)) {
        return "the runtimeconfig declares no $requiredName version to compare against"
    }
    $required = $null
    if (-not [version]::TryParse($shape.FrameworkVersion, [ref]$required)) {
        return ("the runtimeconfig declares an unparseable $requiredName version '{0}'" -f $shape.FrameworkVersion)
    }

    $installed = Get-InstalledFrameworkVersions -FrameworkName $requiredName
    if ($installed.Count -eq 0) {
        return ("no installed $requiredName was found (registry or dotnet root)")
    }

    $best = $null
    foreach ($v in $installed) {
        if ($null -eq $best -or $v -gt $best) { $best = $v }
    }

    # The host rolls forward across patch releases, so any installed version at
    # or above the declared one satisfies the app.
    if ($best -lt $required) {
        return ("installed $requiredName is $best but the release requires at least $required")
    }

    Write-Info ("$requiredName $best installed (release requires $required or newer)")
    return $null
}

# =============================================================================
# LAUNCH AND VERDICT
# =============================================================================

function Stop-ReleaseProcesses {
    # Kills what the smoke test started, and nothing else: the Windows App SDK
    # can leave a WebView2 helper behind, which also runs out of the release
    # directory, so anything loaded from there is ours. A pre-existing instance
    # started from somewhere else has a different path and is left alone.
    param([System.Diagnostics.Process]$Process, [string]$ReleaseDir)

    if ($null -ne $Process) {
        try {
            if (-not $Process.HasExited) {
                Stop-Process -Id $Process.Id -Force -ErrorAction Stop
                $Process.WaitForExit(10000) | Out-Null
            }
        } catch { }
    }

    # Only reached when something was actually launched, so the release
    # directory is known to exist here -- but Test-Path first anyway: this runs
    # from a finally block after 'exit', and an unhandled Resolve-Path error
    # there would print a raw stack trace over the machine-readable verdict.
    if (-not (Test-Path -LiteralPath $ReleaseDir -PathType Container)) { return }
    try {
        $root = (Resolve-Path -LiteralPath $ReleaseDir -ErrorAction Stop).Path
    } catch {
        return
    }

    foreach ($p in (Get-Process -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $p.Path } catch { continue }
        if ([string]::IsNullOrEmpty($path)) { continue }
        if ($path.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { }
        }
    }
}

# Waits for the process to either prove itself or fail to.
#
# Returns a result object; never exits, so the caller controls cleanup and the
# exit code in exactly one place.
function Measure-Launch {
    param(
        [System.Diagnostics.Process]$Process,
        [int]$TimeoutSeconds,
        [int]$ThreadFloor,
        [string]$PlaceholderTitle
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $seen = [pscustomobject]@{
        Title = ''
        Handle = [IntPtr]::Zero
        Threads = 0
        Responding = $false
    }
    $reason = "no window appeared within $TimeoutSeconds second(s)"

    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        try {
            $Process.Refresh()
            if ($Process.HasExited) {
                $code = $null
                try { $code = $Process.ExitCode } catch { }
                return [pscustomobject]@{
                    Passed = $false
                    Reason = ("the process exited during startup (exit code {0})" -f `
                            $(if ($null -eq $code) { 'unknown' } else { $code }))
                    Observed = $seen
                    Exited = $true
                }
            }
            $seen.Title       = $Process.MainWindowTitle
            $seen.Handle      = $Process.MainWindowHandle
            $seen.Threads     = $Process.Threads.Count
            $seen.Responding  = $Process.Responding
        } catch {
            # The process object went away between the refresh and the reads.
            return [pscustomobject]@{
                Passed   = $false
                Reason   = 'the process disappeared while being inspected'
                Observed = $seen
                Exited   = $true
            }
        }

        if ([string]::IsNullOrWhiteSpace($seen.Title)) { continue }

        # Ghost signature 1: the shell never got a title, so Win32 falls back to
        # reporting the image name. Keep waiting: a real window gets its title
        # from the XAML after the first frame is composed.
        if ($seen.Title -eq $PlaceholderTitle) { continue }

        # Ghost signature 2: the Windows App SDK never loaded, so the XAML and
        # composition thread pool never started.
        if ($seen.Threads -lt $ThreadFloor) {
            $reason = ("a titled window appeared but only {0} thread(s) are running; a real session has about 74 (floor is {1})" -f `
                    $seen.Threads, $ThreadFloor)
            continue
        }

        return [pscustomobject]@{
            Passed   = $true
            Reason   = 'a titled window with a full thread pool is up'
            Observed = $seen
            Exited   = $false
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($seen.Title) -and $seen.Title -eq $PlaceholderTitle) {
        $reason = ("the window title is still '{0}', which is what Win32 reports for a window that never got one; the payload is not loading" -f $PlaceholderTitle)
    } elseif ($seen.Threads -gt 0 -and $seen.Threads -lt $ThreadFloor) {
        $reason = ("the process is alive with a window handle but only {0} thread(s); floor is {1}" -f `
                $seen.Threads, $ThreadFloor)
    }

    return [pscustomobject]@{
        Passed   = $false
        Reason   = $reason
        Observed = $seen
        Exited   = $false
    }
}

# =============================================================================
# MAIN
# =============================================================================

Initialize-Console

$exitCode = $script:ExitUsage
$process = $null

try {
    if ($StartupTimeoutSeconds -lt 5) {
        throw "-StartupTimeoutSeconds must be at least 5 (got $StartupTimeoutSeconds)."
    }
    if ($MinThreadCount -lt 1) {
        throw "-MinThreadCount must be at least 1 (got $MinThreadCount)."
    }

    $repoRoot = Get-RepoRoot
    if ([string]::IsNullOrWhiteSpace($ReleaseDir)) {
        $releaseVersion = Get-ProductVersion -RepoRootPath $repoRoot
        $ReleaseDir = Join-Path (Join-Path (Join-Path (Join-Path $repoRoot 'artifacts\release') $releaseVersion) $script:RuntimeIdentifier) ''
    } else {
        $ReleaseDir = [System.IO.Path]::GetFullPath($ReleaseDir)
    }

    Write-Step "Release smoke test"
    Write-Info "release: $ReleaseDir"

    # --- structural preconditions -------------------------------------------
    if (-not (Test-Path -LiteralPath $ReleaseDir -PathType Container)) {
        # Deliberately a FAIL, not a SKIP: the caller pointed at a release that
        # does not exist, and quietly reporting 'skipped' would hide a broken
        # build step behind a green run.
        Write-WarnLine "release directory not found: $ReleaseDir"
        Write-Host 'SMOKE RESULT: FAIL (release directory not found)'
        exit $script:ExitFailure
    }
    $ReleaseDir = (Resolve-Path -LiteralPath $ReleaseDir).Path

    $rootEntries = Assert-ReleaseRootLayout -ReleaseDir $ReleaseDir
    Write-Info ("root: {0} + {1}\" -f ($rootEntries.Files -join ', '), ($rootEntries.Dirs -join ', '))

    $exePath = Get-MainExePath -ReleaseDir $ReleaseDir
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        Write-WarnLine "main program not found in the payload: $exePath"
        Write-Host 'SMOKE RESULT: FAIL (main program missing)'
        exit $script:ExitFailure
    }
    Write-Info "payload:  $exePath"

    # --- runtime precondition -----------------------------------------------
    Write-Step "Probing launch precondition"
    $skipReason = Test-RuntimePrecondition -Dir $ReleaseDir
    if ($null -ne $skipReason) {
        Write-WarnLine "cannot launch on this machine: $skipReason"
        Write-Host 'SKIP: no launch was attempted and no verdict was reached.'
        Write-Host 'SMOKE RESULT: SKIP'
        exit $script:ExitSkipped
    }

    # --- launch --------------------------------------------------------------
    $placeholderTitle = [System.IO.Path]::GetFileNameWithoutExtension($exePath)
    Write-Step "Launching and watching for a real window"
    Write-Info ("criteria: title != '{0}', threads >= {1}, within {2}s" -f `
            $placeholderTitle, $MinThreadCount, $StartupTimeoutSeconds)

    $process = Start-Process -FilePath $exePath `
        -WorkingDirectory (Get-PayloadDirPath -ReleaseDir $ReleaseDir) `
        -PassThru -ErrorAction Stop
    Write-Host ("    pid {0}" -f $process.Id)

    $result = Measure-Launch -Process $process -TimeoutSeconds $StartupTimeoutSeconds `
        -ThreadFloor $MinThreadCount -PlaceholderTitle $placeholderTitle

    Write-Info ("window title: '{0}'" -f $result.Observed.Title)
    Write-Info ("window handle: {0}; threads: {1}; responding: {2}" -f `
            $result.Observed.Handle, $result.Observed.Threads, $result.Observed.Responding)

    if ($result.Passed) {
        Write-Step "Smoke test PASSED"
        Write-Host ("    {0}" -f $result.Reason)
        Write-Host 'SMOKE RESULT: PASS'
        $exitCode = $script:ExitOk
    } else {
        Write-Step "Smoke test FAILED"
        Write-Host ("    {0}" -f $result.Reason)
        Write-Host 'SMOKE RESULT: FAIL'
        $exitCode = $script:ExitFailure
    }

} catch {
    Write-Host "ERROR: $($_.Exception.Message)"
    Write-Host 'SMOKE RESULT: FAIL'
    $exitCode = $script:ExitFailure

} finally {
    # Only when a process was actually started. A run that bailed out before
    # launching has nothing of its own to clean up, and a wrong ReleaseDir must
    # not turn the cleanup path into a second, noisier failure.
    if ($null -ne $process) {
        Stop-ReleaseProcesses -Process $process -ReleaseDir $ReleaseDir
    }
}

exit $exitCode