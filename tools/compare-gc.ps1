<#
  Compare one ArcSharp executable with the same source built by the ordinary
  .NET garbage-collected runtime.

  The workloads live in bench/. They are valid for both compilers. This script
  is not part of tools/verify.ps1: the runs are long, and the memory numbers
  depend on the machine.

  Usage:
    powershell -ExecutionPolicy Bypass -File tools\compare-gc.ps1
    powershell -ExecutionPolicy Bypass -File tools\compare-gc.ps1 -Runs 1
    powershell -ExecutionPolicy Bypass -File tools\compare-gc.ps1 -Workload churn
#>
param(
    [string] $Clang = "C:/Program Files/LLVM/bin/clang.exe",
    [int] $Runs = 3,
    [ValidateSet("all", "baseline", "retained", "churn")]
    [string] $Workload = "all"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot/env.ps1"

if ($Runs -lt 1) {
    Write-Host "Runs must be at least 1"
    exit 1
}

if (-not (Test-Path $Clang)) {
    Write-Host "clang was not found at $Clang"
    exit 1
}

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class ArcPeak {
    [StructLayout(LayoutKind.Sequential)]
    public struct Counters {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool GetProcessMemoryInfo(IntPtr hProcess, out Counters counters, uint size);

    public static bool TryRead(IntPtr handle, out long peakWorkingSet, out long peakCommit) {
        peakWorkingSet = 0;
        peakCommit = 0;
        Counters counters = new Counters();
        counters.cb = (uint)Marshal.SizeOf(typeof(Counters));
        if (!GetProcessMemoryInfo(handle, out counters, counters.cb)) return false;
        peakWorkingSet = (long)counters.PeakWorkingSetSize.ToUInt64();
        peakCommit = (long)counters.PeakPagefileUsage.ToUInt64();
        return peakWorkingSet > 0 || peakCommit > 0;
    }
}
"@

function Invoke-Captured {
    param(
        [string] $FileName,
        [string[]] $Arguments,
        [hashtable] $Environment
    )

    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $FileName
    $start.WorkingDirectory = $root
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    if ($null -ne $Environment) {
        foreach ($key in $Environment.Keys) {
            $start.EnvironmentVariables[$key] = [string]$Environment[$key]
        }
    }
    $start.Arguments = ($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }) -join ' '

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    [void]$process.Start()
    $handle = $process.Handle
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    [void]$process.WaitForExit()
    $clock.Stop()
    [void]$stdout.Wait()
    [void]$stderr.Wait()

    $peakWorkingSet = [int64]0
    $peakCommit = [int64]0
    $memoryOk = [ArcPeak]::TryRead($handle, [ref]$peakWorkingSet, [ref]$peakCommit)
    return @{
        Out = $stdout.Result
        Err = $stderr.Result
        ExitCode = $process.ExitCode
        ElapsedMs = $clock.Elapsed.TotalMilliseconds
        PeakWorkingSet = $peakWorkingSet
        PeakCommit = $peakCommit
        MemoryOk = $memoryOk
    }
}

function Get-Median {
    param($Values)
    $sorted = @($Values | Sort-Object)
    $count = $sorted.Count
    if ($count -eq 0) { return 0 }
    if ($count % 2 -eq 1) { return [double]$sorted[($count - 1) / 2] }
    return ([double]$sorted[$count / 2 - 1] + [double]$sorted[$count / 2]) / 2
}

function Format-MiB {
    param([double] $Bytes)
    return ("{0:N1}" -f ($Bytes / 1MB))
}

$names = @("baseline", "retained", "churn")
if ($Workload -ne "all") { $names = @($Workload) }

$build = Join-Path $env:TEMP "arc-compare"
if (Test-Path $build) { Remove-Item -Recurse -Force $build }
New-Item -ItemType Directory -Force -Path $build | Out-Null

Push-Location $root
try {
    Write-Host "Building the compiler"
    $compilerBuild = Invoke-Captured -FileName "dotnet" -Arguments @("build", "-c", "Release")
    if ($compilerBuild.ExitCode -ne 0) {
        Write-Host $compilerBuild.Out
        Write-Host $compilerBuild.Err
        Write-Host "compiler build failed"
        exit 1
    }

    $dll = Join-Path $root "bin/Release/net11.0/arcsharp.dll"
    $runtime = Join-Path $root "runtime/arc_runtime.c"
    $sdkRun = Invoke-Captured -FileName "dotnet" -Arguments @("--version")
    $sdk = $sdkRun.Out.Trim()
    $clangRunInfo = Invoke-Captured -FileName $Clang -Arguments @("--version")
    $clangVersion = ($clangRunInfo.Out -split "`r?`n" | Select-Object -First 1)
    Write-Host "sdk=$sdk"
    Write-Host "clang=$clangVersion"
    Write-Host "os=$([Environment]::OSVersion.VersionString)"
    Write-Host "processors=$([Environment]::ProcessorCount)"

    $failed = $false
    $rows = @()

    foreach ($name in $names) {
        Write-Host "Building $name"
        $dotnetDir = Join-Path $build "dotnet/$name"
        New-Item -ItemType Directory -Force -Path $dotnetDir | Out-Null
        $dotnetBuild = Invoke-Captured -FileName "dotnet" -Arguments @(
            "build", (Join-Path $root "bench/dotnet/Bench.csproj"), "-c", "Release", "-p:Workload=$name", "-o", $dotnetDir)
        if ($dotnetBuild.ExitCode -ne 0) {
            Write-Host $dotnetBuild.Out
            Write-Host $dotnetBuild.Err
            Write-Host "dotnet build failed for $name"
            exit 1
        }
        $dotnetExe = Join-Path $dotnetDir "$name.exe"

        New-Item -ItemType Directory -Force -Path (Join-Path $build "arc") | Out-Null
        $sources = @(Join-Path $root "bench/$name.cs")
        if ($name -ne "baseline") {
            $sources = @(Join-Path $root "bench/types.cs") + $sources
        }
        $arcBase = Join-Path $build "arc/$name"
        $emit = Invoke-Captured -FileName "dotnet" -Arguments (@($dll) + $sources + @("-o", $arcBase, "--emit-llvm"))
        if ($emit.ExitCode -ne 0) {
            Write-Host $emit.Out
            Write-Host $emit.Err
            Write-Host "emit failed for $name"
            exit 1
        }
        $ll = $arcBase + ".ll"
        $arcExe = $arcBase + ".exe"
        $clangRun = Invoke-Captured -FileName $Clang -Arguments @("-O2", $ll, $runtime, "-o", $arcExe)
        if ($clangRun.ExitCode -ne 0) {
            Write-Host $clangRun.Err
            Write-Host "clang failed for $name"
            exit 1
        }

        foreach ($runtimeName in @("arcsharp", "dotnet")) {
            $times = @()
            $workingSets = @()
            $commits = @()
            # The first launches of a freshly built executable are slower (disk
            # and scanner). Discard two runs so the recorded times are steady.
            $warmEnv = @{}
            $warmExe = $arcExe
            if ($runtimeName -eq "arcsharp") { $warmEnv["ARC_STATS"] = "1" }
            else {
                $warmExe = $dotnetExe
                $warmEnv["ARC_GC_REPORT"] = Join-Path $build "$name-$runtimeName-warm.gc.txt"
                $warmEnv["DOTNET_TieredCompilation"] = "0"
                $warmEnv["DOTNET_TieredPGO"] = "0"
                $warmEnv["DOTNET_gcServer"] = "0"
                $warmEnv["DOTNET_gcConcurrent"] = "1"
            }
            for ($warmRun = 1; $warmRun -le 2; $warmRun++) {
                Write-Host "Warming $name $runtimeName $warmRun/2"
                $warm = Invoke-Captured -FileName $warmExe -Arguments @() -Environment $warmEnv
                if ($warm.ExitCode -ne 0) {
                    Write-Host $warm.Out
                    Write-Host $warm.Err
                    Write-Host "warmup failed for $name $runtimeName"
                    exit 1
                }
            }
            for ($run = 1; $run -le $Runs; $run++) {
                Write-Host "Running $name $runtimeName $run/$Runs"
                $gcReport = Join-Path $build "$name-$runtimeName-$run.gc.txt"
                $envVars = @{}
                $exe = $arcExe
                if ($runtimeName -eq "arcsharp") {
                    $envVars["ARC_STATS"] = "1"
                }
                else {
                    $exe = $dotnetExe
                    $envVars["ARC_GC_REPORT"] = $gcReport
                    $envVars["DOTNET_TieredCompilation"] = "0"
                    $envVars["DOTNET_TieredPGO"] = "0"
                    $envVars["DOTNET_gcServer"] = "0"
                    $envVars["DOTNET_gcConcurrent"] = "1"
                }

                $result = Invoke-Captured -FileName $exe -Arguments @() -Environment $envVars
                $times += [double]$result.ElapsedMs
                $workingSets += [double]$result.PeakWorkingSet
                $commits += [double]$result.PeakCommit

                $checksum = $null
                foreach ($line in ($result.Out -split "`r?`n")) {
                    if ($line -match '^checksum=(\d+)$') { $checksum = $Matches[1] }
                }

                $live = $null
                $alloc = $null
                $dead = $null
                $freed = $null
                $retain = $null
                $release = $null
                if ($runtimeName -eq "arcsharp") {
                    if ($result.Err -match '\[arc\] alloc=(\d+) dead=(\d+) freed=(\d+) live=(\d+)') {
                        $alloc = $Matches[1]
                        $dead = $Matches[2]
                        $freed = $Matches[3]
                        $live = $Matches[4]
                    }
                    if ($result.Err -match '\[arc-stats\] retain=(\d+) release=(\d+)') {
                        $retain = $Matches[1]
                        $release = $Matches[2]
                    }
                }

                $gc = $null
                if ($runtimeName -eq "dotnet" -and (Test-Path $gcReport)) {
                    $gc = [IO.File]::ReadAllText($gcReport).Trim()
                }

                $ok = $result.ExitCode -eq 0 -and $null -ne $checksum -and $result.MemoryOk
                if ($runtimeName -eq "arcsharp" -and $live -ne "0") { $ok = $false }
                if ($runtimeName -eq "dotnet" -and $null -eq $gc) { $ok = $false }
                if (-not $ok) { $failed = $true }

                $rows += [pscustomobject]@{
                    Workload = $name
                    Runtime = $runtimeName
                    Run = $run
                    ExitCode = $result.ExitCode
                    Checksum = $checksum
                    ElapsedMs = [math]::Round($result.ElapsedMs, 1)
                    PeakWorkingSet = $result.PeakWorkingSet
                    PeakCommit = $result.PeakCommit
                    MemoryOk = $result.MemoryOk
                    Live = $live
                    Alloc = $alloc
                    Dead = $dead
                    Freed = $freed
                    Retain = $retain
                    Release = $release
                    Gc = $gc
                    Ok = $ok
                }

                if (-not $ok) {
                    Write-Host "FAILED $name $runtimeName run $run exit=$($result.ExitCode) memory=$($result.MemoryOk)"
                    Write-Host "stdout: $($result.Out)"
                    Write-Host "stderr: $($result.Err)"
                }
            }

            $medianMs = Get-Median $times
            $medianWs = Get-Median $workingSets
            $medianCommit = Get-Median $commits
            Write-Host ("{0,-10} {1,-10} median {2,8:N1} ms   peak ws {3,8:N1} MiB   peak commit {4,8:N1} MiB" -f `
                $name, $runtimeName, $medianMs, ($medianWs / 1MB), ($medianCommit / 1MB))
        }

        $checksums = @($rows | Where-Object { $_.Workload -eq $name -and $_.Ok } | Select-Object -ExpandProperty Checksum -Unique)
        if ($checksums.Count -ne 1) {
            Write-Host "checksum mismatch for $name : $($checksums -join ', ')"
            $failed = $true
        }
    }

    $rows | Format-Table -AutoSize Workload, Runtime, Run, ExitCode, Checksum, ElapsedMs, PeakWorkingSet, PeakCommit, Live, Alloc, Retain, Release, Ok | Out-String -Width 220 | Write-Host
    foreach ($row in $rows) {
        if ($row.Gc) { Write-Host "$($row.Workload) dotnet run $($row.Run): $($row.Gc)" }
    }

    $summaryPath = Join-Path $build "summary.txt"
    $rows | Format-List | Out-File -FilePath $summaryPath -Encoding utf8
    Write-Host "summary: $summaryPath"
    if ($failed) { exit 1 }
    exit 0
}
finally {
    Pop-Location
}
