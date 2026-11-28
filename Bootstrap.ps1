<#
.SYNOPSIS
  Does the first-time work a clone needs before Visual Studio can build it.

.DESCRIPTION
  Checks the toolchain, initializes the submodules, finds or clones vcpkg, applies the tdjson patch
  to TDLib, builds TDLib for every requested triplet and builds wallet-engine. Each step is skipped
  when its output is already up to date, so running it again after pulling is cheap; -WhatIf shows
  what it would do without doing any of it.

  The vcpkg packages themselves are installed by the TDLib build, into the same per-triplet roots
  Visual Studio uses, so the first build from the IDE finds them ready.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Bootstrap.ps1

.EXAMPLE
  .\Bootstrap.ps1 -Arch x64,ARM64 -Target uwp
#>
[CmdletBinding(SupportsShouldProcess)]
param (
  # x64, ARM64 or both; this machine's architecture by default. A comma-separated string is accepted
  # as well, because powershell.exe -File hands a list over as a single string.
  [string[]]$Arch,
  # uwp is the TDLib Telegram.slnx and Telegram.Modern.slnx take, windows the one Telegram.WinUI.slnx takes.
  [string[]]$Target = @("uwp", "windows"),
  # A wallet-engine clone to build from; ..\wallet-engine next to this repository by default.
  [string]$WalletEngineRoot,
  [switch]$SkipTdlib,
  [switch]$SkipWalletEngine,
  # Rebuilds outputs that are up to date, or that were built outside this script and so carry no stamp.
  [switch]$Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3

# Telegram/Services/Wallet/Generated/wallet_engine.cs was generated from this commit. UniFFI checks
# every function's checksum against the library on first use, so a DLL from any other commit fails
# at runtime, not at build time: bump the two together.
$WalletEngineRepository = "https://github.com/i582/wallet-engine.git"
$WalletEngineCommit = "e59e0d89d7ee90c388bf36e3334c5b276f396697"

$VcpkgRepository = "https://github.com/microsoft/vcpkg.git"

$Root = $PSScriptRoot
$TdlibRoot = Join-Path $Root "Libraries\tdlib"
$TdjsonRoot = Join-Path $Root "Libraries\tdjson"
$WalletOutputRoot = Join-Path $Root "Libraries\wallet-engine"
$StampName = "bootstrap.stamp"

if (-not $WalletEngineRoot) {
  $WalletEngineRoot = Join-Path (Split-Path $Root -Parent) "wallet-engine"
}
$WalletEngineRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WalletEngineRoot)

function Split-List {
  param([string[]]$Values, [string[]]$Known, [string]$What)

  $list = New-Object System.Collections.Generic.List[string]
  foreach ($name in ($Values -split '[,|]')) {
    $trimmed = $name.Trim()
    if (-not $trimmed) {
      continue
    }

    # -eq is case-insensitive, so -Arch arm64 still reaches cmake and the triplet spelled as below.
    $canonical = $Known | Where-Object { $_ -eq $trimmed } | Select-Object -First 1
    if (-not $canonical) {
      throw "Unknown $What '$trimmed'. Expected one or more of $($Known -join ', ')."
    }
    if (-not $list.Contains($canonical)) {
      $list.Add($canonical)
    }
  }
  return ,$list
}

function Write-Step {
  param([string]$Text)
  Write-Host ""
  Write-Host "==> $Text" -ForegroundColor Cyan
}

function Write-Note {
  param([string]$Text)
  Write-Host "    $Text"
}

function Invoke-Native {
  param([string]$FilePath, [string[]]$Arguments)

  & $FilePath @Arguments
  if ($LASTEXITCODE -ne 0) {
    throw "$FilePath $($Arguments -join ' ') exited with code $LASTEXITCODE."
  }
}

# Windows PowerShell turns a native command's redirected stderr into a terminating error under
# ErrorActionPreference Stop, so a probe that is expected to fail has to lower it first.
function Test-Native {
  param([string]$FilePath, [string[]]$Arguments)

  $previous = $ErrorActionPreference
  $ErrorActionPreference = "Continue"
  try {
    & $FilePath @Arguments 2>&1 | Out-Null
    return $LASTEXITCODE
  } finally {
    $ErrorActionPreference = $previous
  }
}

function Get-NativeOutput {
  param([string]$FilePath, [string[]]$Arguments)

  $previous = $ErrorActionPreference
  $ErrorActionPreference = "Continue"
  try {
    $output = & $FilePath @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) {
      return $null
    }
    return ($output | Out-String).Trim()
  } finally {
    $ErrorActionPreference = $previous
  }
}

# Not Get-FileHash: Windows PowerShell's honours -WhatIf, and returns nothing under it.
function Get-FileSha {
  param([string]$Path)

  $sha = [System.Security.Cryptography.SHA256]::Create()
  $stream = [System.IO.File]::OpenRead($Path)
  try {
    return [System.BitConverter]::ToString($sha.ComputeHash($stream)).Replace("-", "").ToLowerInvariant()
  } finally {
    $stream.Dispose()
    $sha.Dispose()
  }
}

function Read-Stamp {
  param([string]$Folder)

  $path = Join-Path $Folder $StampName
  if (Test-Path -LiteralPath $path) {
    return ([System.IO.File]::ReadAllText($path)).Trim()
  }
  return $null
}

function Write-Stamp {
  param([string]$Folder, [string]$Value)
  [System.IO.File]::WriteAllText((Join-Path $Folder $StampName), $Value + "`r`n")
}

# Missing, stale, up to date, or present without a stamp. The last is a build made by hand, which is
# kept: rebuilding TDLib costs the better part of an hour, and whoever made it can pass -Force.
function Get-OutputState {
  param([string]$Folder, [string]$File, [string]$Expected)

  if (-not (Test-Path -LiteralPath (Join-Path $Folder $File))) {
    return "missing"
  }
  $stamp = Read-Stamp $Folder
  if ($null -eq $stamp) {
    return "unstamped"
  }
  if ($stamp -ne $Expected) {
    return "stale"
  }
  return "current"
}

function Test-ShouldBuild {
  param([string]$Name, [string]$State)

  switch ($State) {
    "current" {
      if ($Force) {
        Write-Note "$Name is up to date; rebuilding it because of -Force."
        return $true
      }
      Write-Note "$Name is up to date."
      return $false
    }
    "unstamped" {
      if ($Force) {
        Write-Note "$Name was built outside this script; rebuilding it because of -Force."
        return $true
      }
      Write-Warning "$Name exists but was built outside this script, so it is kept as it is. Pass -Force to rebuild it."
      return $false
    }
    "stale" {
      Write-Note "$Name is out of date."
      return $true
    }
    default {
      Write-Note "$Name has not been built."
      return $true
    }
  }
}

if (-not $Arch) {
  $Arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq "Arm64") { "ARM64" } else { "x64" }
}
$ArchList = Split-List $Arch @("x64", "ARM64") "architecture"
$TargetList = Split-List $Target @("uwp", "windows") "target"

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

# ---------------------------------------------------------------------------------------------------
Write-Step "Checking the toolchain"

$missing = New-Object System.Collections.Generic.List[string]

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
  $missing.Add("git, on PATH")
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$vs = $null
$msbuild = $null
if (Test-Path -LiteralPath $vswhere) {
  $vs = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -property installationPath | Select-Object -First 1
}
if (-not $vs) {
  $missing.Add("Visual Studio 2026 with MSBuild")
} else {
  Write-Note "Visual Studio: $vs"
  $msbuild = Join-Path $vs "MSBuild\Current\Bin\amd64\MSBuild.exe"

  $components = [ordered]@{
    "Microsoft.VisualStudio.Workload.ManagedDesktop"       = ".NET desktop development"
    "Microsoft.VisualStudio.Workload.NativeDesktop"        = "Desktop development with C++"
    "Microsoft.VisualStudio.Workload.Universal"            = "Universal Windows Platform development"
    "Microsoft.VisualStudio.Component.VC.Llvm.Clang"       = "C++ Clang Compiler for Windows (vcpkg builds libyuv with it)"
    "Microsoft.VisualStudio.Component.Windows11SDK.26100"  = "Windows 11 SDK (10.0.26100.0)"
  }
  if ($TargetList.Contains("uwp")) {
    $components["Microsoft.VisualStudio.ComponentGroup.UWP.VC"] = "C++ (v145) Universal Windows Platform tools"
  }
  if ($ArchList.Contains("ARM64")) {
    $components["Microsoft.VisualStudio.Component.VC.Tools.ARM64"] = "MSVC ARM64 build tools"
  }

  # Queried per component and matched against the instance above: vswhere -path fails on some
  # installer versions, and -requires with several ids does not say which one is absent.
  foreach ($id in $components.Keys) {
    $instances = @(& $vswhere -prerelease -products * -requires $id -property installationPath)
    if ($instances -notcontains $vs) {
      $missing.Add("Visual Studio component: $($components[$id]) [$id]")
    }
  }
}

if (-not $SkipTdlib) {
  $cmake = Get-Command cmake -ErrorAction SilentlyContinue
  if (-not $cmake) {
    $missing.Add("CMake 4.4 or later, on PATH")
  } else {
    $line = (& cmake --version | Select-Object -First 1)
    if ($line -match '(\d+\.\d+(\.\d+)?)') {
      # Older versions have no Visual Studio 18 generator and silently fall back to an older toolset.
      if ([version]$Matches[1] -lt [version]"4.4") {
        $missing.Add("CMake 4.4 or later (found $($Matches[1]))")
      }
    }
  }
  if (-not (Get-Command php -ErrorAction SilentlyContinue)) {
    $missing.Add("PHP, with php.exe on PATH (TDLib's code generation runs it)")
  }
}

# Asked even with -SkipWalletEngine: Telegram.Native.Widgets builds the Nova engine with cargo in every solution.
if (-not (Get-Command rustup -ErrorAction SilentlyContinue) -or -not (Get-Command cargo -ErrorAction SilentlyContinue)) {
  $missing.Add("Rust, through rustup (https://rustup.rs), with cargo on PATH")
}

if ($missing.Count -gt 0) {
  throw "Install these first:`n  - $($missing -join "`n  - ")`nSee Documentation\Build-instructions.md."
}
Write-Note "Everything needed is installed."

# ---------------------------------------------------------------------------------------------------
Write-Step "Initializing submodules"

# Only the ones never checked out. An initialized submodule is left wherever it is: moving it back to
# the recorded commit would discard work in progress, TDLib's included.
$uninitialized = @(& git -C $Root submodule status | Where-Object { $_.StartsWith("-") } | ForEach-Object { ($_.Substring(1) -split '\s+')[1] })
if ($uninitialized.Count -eq 0) {
  Write-Note "All submodules are checked out."
} elseif ($PSCmdlet.ShouldProcess(($uninitialized -join ", "), "git submodule update --init --recursive")) {
  Invoke-Native git (@("-C", $Root, "submodule", "update", "--init", "--recursive", "--") + $uninitialized)
}

# ---------------------------------------------------------------------------------------------------
Write-Step "Finding vcpkg"

# Asked of Directory.Build.props rather than repeated here, so that this finds the same vcpkg the
# build does: an explicit VcpkgRoot, VCPKG_ROOT, ..\vcpkg, then Visual Studio's own component.
function Get-VcpkgRoot {
  $props = Join-Path $Root "Directory.Build.props"
  $stub = Join-Path ([System.IO.Path]::GetTempPath()) "bootstrap-vcpkg-root-$PID.proj"
  Set-Content -LiteralPath $stub -Value "<Project><Import Project=`"$props`" /></Project>" -WhatIf:$false
  try {
    $json = & $msbuild $stub -nologo "-p:TelegramUsesVcpkg=true" "-p:Platform=x64" "-getProperty:VcpkgRoot" | Out-String
    if ($LASTEXITCODE -ne 0) {
      throw "MSBuild could not evaluate Directory.Build.props: $json"
    }
  } finally {
    Remove-Item -LiteralPath $stub -Force -ErrorAction SilentlyContinue -WhatIf:$false
  }

  # A single -getProperty prints the bare value, not JSON.
  $value = $json.Trim()
  if ($value) {
    return $value.TrimEnd('\')
  }
  return $null
}

$vcpkgRoot = Get-VcpkgRoot
if (-not $vcpkgRoot) {
  $vcpkgRoot = Join-Path (Split-Path $Root -Parent) "vcpkg"
  Write-Note "No vcpkg found; cloning one to $vcpkgRoot."
  # A full clone: manifest mode checks every port out of vcpkg's history, which --depth would cut.
  if ($PSCmdlet.ShouldProcess($vcpkgRoot, "git clone $VcpkgRepository")) {
    Invoke-Native git @("clone", $VcpkgRepository, $vcpkgRoot)
  }
} else {
  Write-Note "vcpkg: $vcpkgRoot"
}

if (Test-Path -LiteralPath (Join-Path $vcpkgRoot ".git")) {
  $manifest = [System.IO.File]::ReadAllText((Join-Path $Root "vcpkg.json"))
  if ($manifest -match '"builtin-baseline"\s*:\s*"([0-9a-f]{40})"') {
    $baseline = $Matches[1]
    # vcpkg reads its version database from the working tree, so a checkout older than the pin
    # fails much later with "no version database entry". Exit code 1 alone means "not an ancestor".
    if ((Test-Native git @("-C", $vcpkgRoot, "merge-base", "--is-ancestor", $baseline, "HEAD")) -eq 1) {
      throw "The vcpkg checkout at $vcpkgRoot is older than the commit pinned in vcpkg.json. Update it:`n  git -C `"$vcpkgRoot`" fetch`n  git -C `"$vcpkgRoot`" checkout $baseline`n  `"$vcpkgRoot\bootstrap-vcpkg.bat`""
    }
  }

  if (-not (Test-Path -LiteralPath (Join-Path $vcpkgRoot "vcpkg.exe"))) {
    if ($PSCmdlet.ShouldProcess($vcpkgRoot, "bootstrap-vcpkg.bat")) {
      Invoke-Native (Join-Path $vcpkgRoot "bootstrap-vcpkg.bat") @("-disableMetrics")
    }
  }
}

# ---------------------------------------------------------------------------------------------------
if (-not $SkipTdlib) {
  Write-Step "Patching TDLib"

  # Applied as LF: git matches a patch against the normalized form of a text file, whatever the
  # working tree's line endings, whereas a CRLF copy only applies to a CRLF checkout.
  $patchSource = Join-Path $TdjsonRoot "tdjson.patch"
  $patch = Join-Path ([System.IO.Path]::GetTempPath()) "bootstrap-tdjson-$PID.patch"
  $text = [System.IO.File]::ReadAllText($patchSource).Replace("`r`n", "`n")
  [System.IO.File]::WriteAllText($patch, $text, (New-Object System.Text.UTF8Encoding $false))

  try {
    $tdlibHead = Get-NativeOutput git @("-C", $TdlibRoot, "rev-parse", "HEAD")
    if ((Test-Native git @("-C", $TdlibRoot, "apply", "--reverse", "--check", $patch)) -eq 0) {
      Write-Note "Already applied."
    } elseif ((Test-Native git @("-C", $TdlibRoot, "apply", "--check", $patch)) -eq 0) {
      if ($PSCmdlet.ShouldProcess($TdlibRoot, "git apply tdjson.patch")) {
        Invoke-Native git @("-C", $TdlibRoot, "apply", $patch)
        Write-Note "Applied to $tdlibHead."
      }
    } else {
      throw "Libraries\tdjson\tdjson.patch does not apply to Libraries\tdlib at $tdlibHead. Either TDLib has moved past what the patch was written against, or its working tree has conflicting changes; see git -C Libraries\tdlib status."
    }
  } finally {
    Remove-Item -LiteralPath $patch -Force -ErrorAction SilentlyContinue -WhatIf:$false
  }

  # -------------------------------------------------------------------------------------------------
  Write-Step "Building TDLib"

  # Everything that changes tdjson.dll: the TDLib commit, its working tree (the patch and any local
  # edits), the openssl and zlib vcpkg.json resolves, and the flags build.ps1 passes.
  $diff = Join-Path ([System.IO.Path]::GetTempPath()) "bootstrap-tdlib-$PID.diff"
  try {
    Invoke-Native git @("-C", $TdlibRoot, "diff", "HEAD", "--binary", "--output=$diff")
    $tdlibStamp = @(
      "tdlib=$(Get-NativeOutput git @("-C", $TdlibRoot, "rev-parse", "HEAD"))",
      "worktree=$(Get-FileSha $diff)",
      "vcpkg.json=$(Get-FileSha (Join-Path $Root "vcpkg.json"))",
      "build.ps1=$(Get-FileSha (Join-Path $TdjsonRoot "build.ps1"))"
    ) -join "`r`n"
  } finally {
    Remove-Item -LiteralPath $diff -Force -ErrorAction SilentlyContinue -WhatIf:$false
  }

  foreach ($t in $TargetList) {
    $needed = New-Object System.Collections.Generic.List[string]
    foreach ($a in $ArchList) {
      $triplet = "$($a.ToLowerInvariant())-$t"
      $state = Get-OutputState (Join-Path $TdjsonRoot $triplet) "tdjson.dll" $tdlibStamp
      if (Test-ShouldBuild "tdjson $triplet" $state) {
        $needed.Add($a)
      }
    }

    if ($needed.Count -eq 0) {
      continue
    }

    # Tens of minutes per triplet from cold, most of it vcpkg installing the app's packages into the
    # same roots Visual Studio uses, which is what makes its first build fast.
    if ($PSCmdlet.ShouldProcess("$($needed -join ', ') for $t", "Libraries\tdjson\build.ps1")) {
      # build.ps1 resolves the TDLib and manifest roots relative to its own folder.
      Push-Location $TdjsonRoot
      try {
        & (Join-Path $TdjsonRoot "build.ps1") -arch $needed.ToArray() -target $t
      } finally {
        Pop-Location
      }

      foreach ($a in $needed) {
        Write-Stamp (Join-Path $TdjsonRoot "$($a.ToLowerInvariant())-$t") $tdlibStamp
      }
    }
  }

  # Gitignored and written by build.ps1's export, so a tree whose DLLs were all kept may lack it.
  $schema = Join-Path $TdjsonRoot "td_api.tl"
  if (-not (Test-Path -LiteralPath $schema)) {
    if ($PSCmdlet.ShouldProcess($schema, "Copy the TDLib schema")) {
      Copy-Item -LiteralPath (Join-Path $TdlibRoot "td\generate\scheme\td_api.tl") -Destination $schema
    }
  }
}

# ---------------------------------------------------------------------------------------------------
if (-not $SkipWalletEngine) {
  Write-Step "Building wallet-engine"

  $walletStamp = "wallet-engine=$WalletEngineCommit"
  $triples = @{ "x64" = "x86_64-pc-windows-msvc"; "ARM64" = "aarch64-pc-windows-msvc" }

  $needed = New-Object System.Collections.Generic.List[string]
  foreach ($a in $ArchList) {
    $state = Get-OutputState (Join-Path $WalletOutputRoot $a) "wallet_engine.dll" $walletStamp
    if (Test-ShouldBuild "wallet_engine $a" $state) {
      $needed.Add($a)
    }
  }

  if ($needed.Count -gt 0) {
    if (-not (Test-Path -LiteralPath $WalletEngineRoot)) {
      if ($PSCmdlet.ShouldProcess($WalletEngineRoot, "git clone $WalletEngineRepository")) {
        Invoke-Native git @("clone", $WalletEngineRepository, $WalletEngineRoot)
        Invoke-Native git @("-C", $WalletEngineRoot, "-c", "advice.detachedHead=false", "checkout", "--detach", $WalletEngineCommit)
      }
    } else {
      # A clone that already exists is somebody's, so it is never moved, only checked.
      $head = Get-NativeOutput git @("-C", $WalletEngineRoot, "rev-parse", "HEAD")
      if ($head -ne $WalletEngineCommit) {
        throw "$WalletEngineRoot is at $head, but the bindings in Telegram\Services\Wallet\Generated were generated from $WalletEngineCommit. Check that commit out there, or pass -WalletEngineRoot with another clone."
      }
      if (Get-NativeOutput git @("-C", $WalletEngineRoot, "status", "--porcelain", "--untracked-files=no")) {
        Write-Warning "$WalletEngineRoot has local changes, which go into the build."
      }
      Write-Note "wallet-engine: $WalletEngineRoot"
    }

    if ($PSCmdlet.ShouldProcess("$($needed -join ', ')", "cargo build wallet-engine")) {
      # The target has to be added to the toolchain wallet-engine pins, not to the default one:
      # otherwise the cross build fails with "can't find crate for core" across unrelated crates.
      $toolchainFile = [System.IO.File]::ReadAllText((Join-Path $WalletEngineRoot "rust-toolchain.toml"))
      if ($toolchainFile -notmatch 'channel\s*=\s*"([^"]+)"') {
        throw "No toolchain channel in $WalletEngineRoot\rust-toolchain.toml."
      }
      $toolchain = $Matches[1]
      Invoke-Native rustup @("toolchain", "install", $toolchain, "--profile", "minimal")

      Push-Location $WalletEngineRoot
      try {
        foreach ($a in $needed) {
          $triple = $triples[$a]
          Invoke-Native rustup @("target", "add", $triple, "--toolchain", $toolchain)
          Invoke-Native cargo @("build", "--locked", "--release", "-p", "wallet-engine", "--lib", "--target", $triple)

          $folder = Join-Path $WalletOutputRoot $a
          New-Item -ItemType Directory -Force -Path $folder | Out-Null
          Copy-Item -LiteralPath (Join-Path $WalletEngineRoot "target\$triple\release\wallet_engine.dll") -Destination $folder -Force
          Write-Stamp $folder $walletStamp
        }
      } finally {
        Pop-Location
      }
    }
  }
}

# ---------------------------------------------------------------------------------------------------
Write-Step "Done in $([int]$stopwatch.Elapsed.TotalMinutes) min"

if (-not (Test-Path -LiteralPath (Join-Path $Root "Telegram\Constants.Secret.cs"))) {
  Write-Warning "Telegram\Constants.Secret.cs is missing, and nothing compiles without it. Create it with your api_id and api_hash from https://my.telegram.org/apps, as Documentation\Build-instructions.md shows."
}

Write-Note "Open Telegram.slnx for the UWP app, or Telegram.WinUI.slnx for the WinUI one, and build."
