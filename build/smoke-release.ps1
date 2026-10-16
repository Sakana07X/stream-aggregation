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
# WHAT IS LAUNCHED
#
# The launcher executable at the package root -- the file the user actually
# double-clicks -- and never the payload exe directly. Measuring the payload
# exe directly would skip the whole indirection this script exists to cover:
# a launcher that resolved the wrong path, or started the child with the wrong
# working directory, or refused to start anything at all, would still pass.
#
# CASES
#
#   1  from-package-root      double-click simulation: cwd is the package root
#   2  from-other-cwd         cwd is somewhere unrelated, as it is when the
#                             user starts it from a shortcut or a file manager
#                             whose shell has its own working directory
#   3  moved-package          the whole package copied to a different path
#                             first, proving nothing is resolved absolutely
#   4  single-instance        a second launch must activate the running app
#                             instead of starting a second one
#
# HONESTY CONTRACT
#
# This script reports what it actually did and never lets a non-verdict read
# as a pass:
#
#   exit 0  PASS   every case reached a verdict and every verdict was pass
#   exit 1  FAIL   a case failed, or the release directory is not usable at all
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
#   powershell -File build/smoke-release.ps1 -SkipMovedPackage
#
# Exit codes: 0 = pass, 1 = fail, 2 = usage error, 3 = skipped

param(
    [string]$ReleaseDir = '',
    [int]$StartupTimeoutSeconds = 90,
    [int]$MinThreadCount = 60,
    [switch]$SkipMovedPackage
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
    #
    # That includes the launcher, which lives at the package root and stays
    # alive for exactly as long as the app it started.
    param([string[]]$Directories)

    foreach ($dir in $Directories) {
        if ([string]::IsNullOrWhiteSpace($dir)) { continue }
        # Only reached when something was actually launched, so the release
        # directory is known to exist here -- but Test-Path first anyway: this
        # runs from a finally block after 'exit', and an unhandled Resolve-Path
        # error there would print a raw stack trace over the machine-readable
        # verdict.
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) { continue }
        try {
            $root = (Resolve-Path -LiteralPath $dir -ErrorAction Stop).Path
        } catch {
            continue
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
}

# Every running process whose image is exactly $PayloadExePath.
#
# Matched on PATH, not on name. The app is framework-dependent and unpacked,
# so a developer running the same exe out of their bin directory has the same
# process name; counting those in would let a smoke test pass on somebody
# else's window.
function Get-PayloadProcesses {
    param([string]$PayloadExePath)

    $wanted = [System.IO.Path]::GetFullPath($PayloadExePath).TrimEnd('\')
    $name = [System.IO.Path]::GetFileNameWithoutExtension($PayloadExePath)

    $found = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
    foreach ($p in (Get-Process -Name $name -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $p.Path } catch { continue }
        if ([string]::IsNullOrEmpty($path)) { continue }
        if ($path.TrimEnd('\').Equals($wanted, [System.StringComparison]::OrdinalIgnoreCase)) {
            $found.Add($p)
        }
    }
    return $found
}

function New-PidSet {
    param([System.Diagnostics.Process[]]$Processes)
    $set = New-Object 'System.Collections.Generic.HashSet[int]'
    foreach ($p in @($Processes)) {
        if ($null -ne $p) { [void]$set.Add([int]$p.Id) }
    }
    return $set
}

# The launcher is not the thing worth measuring -- the app it started is. This
# waits for a payload process that was not already running, so a case can never
# latch onto a leftover from the previous one.
function Wait-ForNewPayloadProcess {
    param(
        [string]$PayloadExePath,
        [int]$TimeoutSeconds,
        [System.Collections.Generic.HashSet[int]]$KnownPids
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        foreach ($p in (Get-PayloadProcesses -PayloadExePath $PayloadExePath)) {
            if ($null -ne $KnownPids -and $KnownPids.Contains([int]$p.Id)) { continue }
            return $p
        }
    }
    return $null
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
# CASES
# =============================================================================

# One line per case, always. A case that was skipped must still print a line:
# a missing line reads as "did not happen" rather than "did not pass".
function Write-CaseResult {
    param($Result)
    $verdict = $(if ($Result.Passed) { 'PASS' } else { 'FAIL' })
    Write-Host ("    [{0}] {1}: {2}" -f $verdict, $Result.Name, $Result.Reason)
    if ($null -ne $Result.Observed) {
        Write-Host ("           window title '{0}'; handle {1}; threads {2}; responding {3}" -f `
                $Result.Observed.Title, $Result.Observed.Handle,
                $Result.Observed.Threads, $Result.Observed.Responding)
    }
    if ($Result.AppPid -gt 0) {
        Write-Host ("           launcher pid {0}; app pid {1}" -f $Result.LauncherPid, $Result.AppPid)
    }
}

# Launches the package launcher and judges the app it started.
#
# Returns a result object with the same shape Measure-Launch produces, plus the
# launcher pid, so the caller can report which case produced which verdict.
function Invoke-LauncherCase {
    param(
        [string]$Name,
        [string]$LauncherPath,
        [string]$LauncherWorkingDirectory,
        [string]$PayloadExePath,
        [string]$PlaceholderTitle,
        [int]$TimeoutSeconds,
        [int]$ThreadFloor
    )

    # Each case starts from a clean slate. Without this the second case would
    # find the first case's app still running, the launcher's single-instance
    # lock would hand the click to that running app, no NEW payload process
    # would appear, and the case would fail for having worked.
    Stop-ReleaseProcesses -Directories @([System.IO.Path]::GetDirectoryName($LauncherPath))

    $known = New-PidSet -Processes @(Get-PayloadProcesses -PayloadExePath $PayloadExePath)

    $launcher = $null
    try {
        $launcher = Start-Process -FilePath $LauncherPath `
            -WorkingDirectory $LauncherWorkingDirectory `
            -PassThru -ErrorAction Stop
    } catch {
        return [pscustomobject]@{
            Name     = $Name
            Passed   = $false
            Reason   = ("could not start the launcher: {0}" -f $_.Exception.Message)
            Observed = $null
            LauncherPid = 0
            AppPid   = 0
        }
    }

    # The launcher resolving the wrong path is a silent failure by design: it
    # would pop a MessageBox, not exit with an error the caller can read. Give
    # it a bounded window to produce a child before calling it dead.
    $app = Wait-ForNewPayloadProcess -PayloadExePath $PayloadExePath `
                                     -TimeoutSeconds $TimeoutSeconds -KnownPids $known
    if ($null -eq $app) {
        $launcherExited = $false
        $launcherCode = 'still running'
        try {
            $launcherExited = $launcher.WaitForExit(2000)
            if ($launcherExited) { $launcherCode = "exit code $($launcher.ExitCode)" }
        } catch { }
        return [pscustomobject]@{
            Name     = $Name
            Passed   = $false
            Reason   = ("the launcher started no '{0}' within {1}s ({2})" -f `
                        [System.IO.Path]::GetFileName($PayloadExePath), $TimeoutSeconds, $launcherCode)
            Observed = $null
            LauncherPid = $launcher.Id
            AppPid   = 0
        }
    }

    $measured = Measure-Launch -Process $app -TimeoutSeconds $TimeoutSeconds `
        -ThreadFloor $ThreadFloor -PlaceholderTitle $PlaceholderTitle
    return [pscustomobject]@{
        Name       = $Name
        Passed     = $measured.Passed
        Reason     = $measured.Reason
        Observed   = $measured.Observed
        LauncherPid = $launcher.Id
        AppPid     = $app.Id
    }
}

# The second double-click. Asserts that no second payload process appeared and
# that the second launcher returned on its own rather than waiting on one.
#
# "Exited" matters as much as the count: a launcher that had actually started
# a second app would still be sitting there waiting for it, and a launcher that
# started nothing but never returned would hang the user on a spinner.
function Invoke-SingleInstanceCase {
    param(
        [string]$LauncherPath,
        [string]$LauncherWorkingDirectory,
        [string]$PayloadExePath,
        [int]$TimeoutSeconds
    )

    $before = @(Get-PayloadProcesses -PayloadExePath $PayloadExePath)
    $expected = $before.Count

    $second = $null
    try {
        $second = Start-Process -FilePath $LauncherPath `
            -WorkingDirectory $LauncherWorkingDirectory `
            -PassThru -ErrorAction Stop
    } catch {
        return [pscustomobject]@{
            Name   = 'single-instance'
            Passed = $false
            Reason = ("could not start the second launcher: {0}" -f $_.Exception.Message)
        }
    }

    $exited = $false
    $code = 'unknown'
    try {
        $exited = $second.WaitForExit([Math]::Min($TimeoutSeconds, 20000))
        if ($exited) { $code = "exit code $($second.ExitCode)" }
    } catch { }

    # Give a wrongly-started second app a moment to appear before declaring the
    # count unchanged. 2s is generous: CreateProcess is immediate, and the
    # window check would need several more seconds after that.
    if ($exited) { Start-Sleep -Milliseconds 2000 }

    $after = @(Get-PayloadProcesses -PayloadExePath $PayloadExePath)
    $problems = New-Object System.Collections.Generic.List[string]
    if ($after.Count -ne $expected) {
        $problems.Add(("payload process count went from {0} to {1}" -f $expected, $after.Count))
    }
    if (-not $exited) {
        $problems.Add("the second launcher did not return on its own")
    } elseif ($second.ExitCode -ne 0) {
        $problems.Add(("the second launcher reported {0}" -f $code))
    }
    if ($problems.Count -gt 0) {
        return [pscustomobject]@{
            Name   = 'single-instance'
            Passed = $false
            Reason = ($problems -join '; ')
        }
    }
    return [pscustomobject]@{
        Name   = 'single-instance'
        Passed = $true
        Reason = ("a second launch returned {0} and left the running instance as the only payload process" -f $code)
    }
}

# =============================================================================
# MAIN
# =============================================================================

Initialize-Console

$exitCode = $script:ExitUsage
$launched = $false
$scratchDirs = New-Object System.Collections.Generic.List[string]

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

    $launcherPath = Get-LauncherPath -ReleaseDir $ReleaseDir
    if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
        Write-WarnLine "launcher executable not found at the package root: $launcherPath"
        Write-Host 'SMOKE RESULT: FAIL (launcher missing)'
        exit $script:ExitFailure
    }
    Write-Info "launcher: $launcherPath"

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

    # --- launch cases --------------------------------------------------------
    $placeholderTitle = [System.IO.Path]::GetFileNameWithoutExtension($exePath)
    Write-Step "Launching the package launcher and watching for a real window"
    Write-Info ("criteria: title != '{0}', threads >= {1}, within {2}s" -f `
            $placeholderTitle, $MinThreadCount, $StartupTimeoutSeconds)

    # Case 2 runs the same package from an unrelated working directory. A
    # shell never guarantees the current directory, and a launcher that leaned
    # on it would look perfectly correct when double-clicked from Explorer.
    $otherCwd = [System.Environment]::GetFolderPath('UserProfile')
    if ([string]::IsNullOrWhiteSpace($otherCwd) -or
        -not (Test-Path -LiteralPath $otherCwd -PathType Container)) {
        $otherCwd = $env:SystemRoot
    }

    $results = New-Object System.Collections.Generic.List[object]

    $case1 = Invoke-LauncherCase -Name 'from-package-root' `
        -LauncherPath $launcherPath -LauncherWorkingDirectory $ReleaseDir `
        -PayloadExePath $exePath -PlaceholderTitle $placeholderTitle `
        -TimeoutSeconds $StartupTimeoutSeconds -ThreadFloor $MinThreadCount
    $launched = $true
    $results.Add($case1)
    Write-CaseResult -Result $case1

    # Case 4 needs the instance from case 1 still alive, so it runs immediately
    # after it rather than at the end of the sequence.
    if ($case1.Passed) {
        $case4 = Invoke-SingleInstanceCase -LauncherPath $launcherPath `
            -LauncherWorkingDirectory $otherCwd -PayloadExePath $exePath `
            -TimeoutSeconds $StartupTimeoutSeconds
        $results.Add($case4)
        Write-CaseResult -Result $case4
    } else {
        $skipped = [pscustomobject]@{
            Name   = 'single-instance'
            Passed = $false
            Reason = 'not attempted: the first launch did not reach a verdict'
        }
        $results.Add($skipped)
        Write-CaseResult -Result $skipped
    }

    $case2 = Invoke-LauncherCase -Name 'from-other-cwd' `
        -LauncherPath $launcherPath -LauncherWorkingDirectory $otherCwd `
        -PayloadExePath $exePath -PlaceholderTitle $placeholderTitle `
        -TimeoutSeconds $StartupTimeoutSeconds -ThreadFloor $MinThreadCount
    $results.Add($case2)
    Write-CaseResult -Result $case2

    # Case 3 copies the whole package to a different path first. Anything the
    # launcher resolved absolutely -- a build-time path, a registry entry, the
    # repository location -- only shows up here.
    if ($SkipMovedPackage) {
        Write-WarnLine 'moved-package case skipped (-SkipMovedPackage)'
    } else {
        $movedRoot = Join-Path (Join-Path $repoRoot '.tmp') 'smoke-moved-package'
        Write-Step "Copying the package to $movedRoot"
        Reset-Directory -Path $movedRoot | Out-Null
        Copy-Item -Path (Join-Path $ReleaseDir '*') -Destination $movedRoot -Recurse -Force
        $scratchDirs.Add($movedRoot)

        $movedLauncher = Get-LauncherPath -ReleaseDir $movedRoot
        $movedPayload = Get-MainExePath -ReleaseDir $movedRoot
        $case3 = Invoke-LauncherCase -Name 'moved-package' `
            -LauncherPath $movedLauncher -LauncherWorkingDirectory $otherCwd `
            -PayloadExePath $movedPayload -PlaceholderTitle $placeholderTitle `
            -TimeoutSeconds $StartupTimeoutSeconds -ThreadFloor $MinThreadCount
        $results.Add($case3)
        Write-CaseResult -Result $case3
    }

    # --- verdict -------------------------------------------------------------
    $failed = @($results | Where-Object { -not $_.Passed })
    $notRun = $(if ($SkipMovedPackage) { 1 } else { 0 })
    Write-Step ("Smoke test: {0} case(s) run, {1} failed, {2} not run" -f `
            $results.Count, $failed.Count, $notRun)

    if ($failed.Count -eq 0) {
        Write-Step "Smoke test PASSED"
        Write-Host 'SMOKE RESULT: PASS'
        $exitCode = $script:ExitOk
    } else {
        Write-Step "Smoke test FAILED"
        foreach ($f in $failed) {
            Write-Host ("    {0}: {1}" -f $f.Name, $f.Reason)
        }
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
    if ($launched) {
        Stop-ReleaseProcesses -Directories (@($ReleaseDir) + $scratchDirs.ToArray())
    }
}

exit $exitCode