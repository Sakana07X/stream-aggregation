# scan-release.ps1
# Release directory sensitive-file scanner.
# Verifies a release directory does not contain databases, logs, tokens,
# debug symbols, source code, or other development-only material.
#
# ASCII-only on purpose: PowerShell 5.1 reads a BOM-less .ps1 as ANSI,
# which corrupts non-ASCII characters and causes ParserError.
# Chinese filename patterns are expressed as \uXXXX regex escapes.
#
# Usage:
#   powershell -File build/scan-release.ps1
#   powershell -File build/scan-release.ps1 -Path "D:\path\to\release"
#
# Exit codes:
#   0 = clean (no hits)
#   1 = sensitive content found
#   2 = usage / path error

param(
    [string]$Path = ""
)

$ErrorActionPreference = 'SilentlyContinue'

# =============================================================================
# RULE DEFINITIONS -- single place to maintain. Add new rules here.
# =============================================================================

# File-name rules: matched against the file name (leaf) only.
# Use \uXXXX escapes for non-ASCII filename patterns.
$NameRules = [ordered]@{
    'DatabaseFile'  = '\.(db|db-wal|db-shm|sqlite|sqlite3)$'
    'SecretKeyFile'  = '(^|\/)secrets\.dat(\..*)?$'
    'CertFile'      = '\.(pfx|p12|key|pem)$'
    'LogFile'       = '\.log$'
    'DebugSymbol'   = '\.pdb$'
    'DevTempFile'   = '^[\u5F00\u59CB\u6D4B\u8BD5].*\.txt$'   # CJK temp files like "start testing"
    'BackupFile'    = '\.bak$'
}

# Directory rules: matched against the relative path (backslash or slash).
$DirRules = [ordered]@{
    'TempDir'       = '(^|[\/\\])\.tmp([\/\\]|$)'
    'BuildDir'      = '(^|[\/\\])(bin|obj)([\/\\]|$)'
    'SnowLumaDir'   = '(?i)(^|[\/\\])SnowLuma[^/\\]*([\/\\]|$)'
    'SteamDir'      = '(?i)(^|[\/\\])Steam[^/\\]*([\/\\]|$)'
    'SourceDir'     = '(^|[\/\\])(src|tests|test)([\/\\]|$)'
    'LogsDir'       = '(^|[\/\\])logs([\/\\]|$)'
}

# Content rules: matched against text-file contents (small files only).
$ContentRules = [ordered]@{
    'BearerToken'   = '(?i)Bearer\s+[A-Za-z0-9_\-\.]{24,}'
    'ApiKey'        = 'sk-[A-Za-z0-9_\-]{20,}'
    'SourceLink'    = '(?i)SourceLink'
}

# Extensions eligible for content scanning (text-like files only).
$TextExtensions = @('.cs', '.json', '.txt', '.config', '.xml', '.md',
                    '.yaml', '.yml', '.ps1', '.html', '.css', '.js',
                    '.ts', '.sql', '.manifest', '.targets', '.props')

# Skip content scan for files larger than this (binary/oversized guard).
$MaxContentSize = 2MB

# =============================================================================
# RESOLVE TARGET PATH
# =============================================================================

if ([string]::IsNullOrWhiteSpace($Path)) {
    # Default: newest directory under artifacts/release
    $defaultRoot = Join-Path $PSScriptRoot '..\artifacts\release'
    if (-not (Test-Path -LiteralPath $defaultRoot -PathType Container)) {
        Write-Host "ERROR: default path not found: $defaultRoot"
        Write-Host "Usage: powershell -File scan-release.ps1 -Path <directory>"
        exit 2
    }
    $releaseDirs = Get-ChildItem -LiteralPath $defaultRoot -Directory |
        Sort-Object Name -Descending
    if ($releaseDirs.Count -eq 0) {
        Write-Host "ERROR: no release directories under $defaultRoot"
        exit 2
    }
    $Path = $releaseDirs[0].FullName
    Write-Host "Using latest release dir: $Path"
}

if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
    Write-Host "ERROR: path not found: $Path"
    exit 2
}

$Path = (Resolve-Path -LiteralPath $Path).Path

# =============================================================================
# SCAN
# =============================================================================

Write-Host "Scanning: $Path"
Write-Host ""

$hits = New-Object System.Collections.Generic.List[string]
$fileCount = 0
$flaggedDirs = New-Object System.Collections.Generic.HashSet[string]

$allItems = Get-ChildItem -LiteralPath $Path -Recurse -Force

foreach ($item in $allItems) {
    # Compute relative path with forward slashes for display
    $rel = $item.FullName.Substring($Path.Length).TrimStart('\', '/')
    $rel = $rel -replace '\\', '/'

    # --- Directory rules ---
    if ($item.PSIsContainer) {
        # Only report the topmost violating directory; skip descendants of
        # already-flagged directories to keep output readable.
        $parts = $rel -split '/'
        $cumulative = ""
        $skip = $false
        for ($p = 0; $p -lt $parts.Count; $p++) {
            if ($p -eq 0) { $cumulative = $parts[0] }
            else { $cumulative = $cumulative + "/" + $parts[$p] }
            if ($flaggedDirs.Contains($cumulative)) { $skip = $true; break }
        }
        if ($skip) { continue }

        foreach ($rule in $DirRules.GetEnumerator()) {
            if ([regex]::IsMatch($rel, $rule.Value)) {
                $hits.Add("$rel/  [dir: $($rule.Key)]")
                [void]$flaggedDirs.Add($rel)
                break
            }
        }
        continue
    }

    $fileCount++

    # --- File-name rules ---
    foreach ($rule in $NameRules.GetEnumerator()) {
        if ([regex]::IsMatch($item.Name, $rule.Value)) {
            $hits.Add("$rel  [$($rule.Key)]")
            break   # one name hit per file is enough
        }
    }

    # --- Content rules ---
    if ($item.Length -le $MaxContentSize) {
        $ext = $item.Extension.ToLower()
        if ($TextExtensions -contains $ext) {
            $lines = Get-Content -LiteralPath $item.FullName
            if ($lines) {
                for ($i = 0; $i -lt $lines.Count; $i++) {
                    $line = $lines[$i]
                    foreach ($rule in $ContentRules.GetEnumerator()) {
                        if ([regex]::IsMatch($line, $rule.Value)) {
                            $snippet = $line.Trim()
                            if ($snippet.Length -gt 100) {
                                $snippet = $snippet.Substring(0, 100) + '...'
                            }
                            $hits.Add("${rel}:$($i + 1)  [$($rule.Key)]  $snippet")
                        }
                    }
                }
            }
        }
    }
}

# =============================================================================
# REPORT
# =============================================================================

Write-Host "----------------------------------------"
if ($hits.Count -eq 0) {
    Write-Host "Scan PASSED: $fileCount files, 0 hits"
    exit 0
} else {
    Write-Host "Scan FAILED: $fileCount files, $($hits.Count) hit(s)"
    Write-Host ""
    foreach ($h in $hits) {
        Write-Host "  $h"
    }
    exit 1
}
