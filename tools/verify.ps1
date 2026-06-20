<#
  End-to-end verification on Windows x64: builds the compiler, compiles every
  sample to a native .exe via clang (which consumes the generated LLVM IR
  directly), runs it, and checks both program output and ARC accounting
  (the `[arc] alloc=.. dead=.. freed=.. live=..` line printed to stderr).

  Usage:
    powershell -ExecutionPolicy Bypass -File tools\verify.ps1
    powershell -ExecutionPolicy Bypass -File tools\verify.ps1 -Clang "C:/Program Files/LLVM/bin/clang.exe"
#>
param(
  [string] $Clang = "C:/Program Files/LLVM/bin/clang.exe"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot/env.ps1"

Push-Location $root
try {
    $build = Join-Path $env:TEMP "arc-verify"
    if (Test-Path $build) { Remove-Item -Recurse -Force $build }
    New-Item -ItemType Directory -Force -Path (Join-Path $build "out") | Out-Null

    dotnet build -c Release | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED"; exit 1 }

    $dll = Join-Path $root "bin/Release/net8.0/arcsharp.dll"
    $runtime = Join-Path $root "runtime/arc_runtime.c"
    $errFile = Join-Path $build "stderr.txt"
    $script:pass = 0
    $script:fail = 0

    function Invoke-Sample {
        param(
            [string]   $Name,
            [int]      $Live,
            [string]   $Want,
            [string[]] $Sources
        )
        $srcs = @($Sources | ForEach-Object { Join-Path $root "samples/$_.cs" })
        $outBase = Join-Path $build "out/$Name"
        $a = @() + $srcs + @("-o", $outBase, "--target", "windows", "--clang", $Clang, "--runtime", $runtime, "--run")
        $out = (& dotnet $dll @a 2> $errFile) -join "`n"
        $err = if (Test-Path $errFile) { Get-Content $errFile -Raw } else { "" }
        $arc = ($err -split "`r?`n" | Where-Object { $_ -match '\[arc\]' }) -join ""
        $gotLive = if ($arc -match 'live=(\d+)') { [int]$Matches[1] } else { -1 }
        $ok = ($gotLive -eq $Live) -and ($out -match [regex]::Escape($Want))
        if ($ok) {
            Write-Host "PASS  $Name  ($arc; output ok)"
            $script:pass++
        }
        else {
            Write-Host "FAIL  $Name  want_live=$Live got='$arc' want_out='$Want' got_out='$out'"
            $script:fail++
        }
    }

    Write-Host "=== ArcSharp verification (Windows x64) ==="
    Invoke-Sample inherit       0 "sum=42"                     @("inherit")
    Invoke-Sample interface     0 "circle=75"                  @("interface")
    Invoke-Sample refarray      0 "refarray sum=5"             @("refarray")
    Invoke-Sample statics       0 "count=3"                    @("statics")
    Invoke-Sample fib           0 "fib(10)=55"                 @("fib")
    Invoke-Sample floats        0 "area=19.6349"               @("floats")
    Invoke-Sample weak_null     0 "PASS"                       @("weak_null")
    Invoke-Sample cycle_weak    0 "done (weak cycle)"          @("cycle_weak")
    Invoke-Sample cycle_weakref 0 "done (WeakReference cycle)" @("cycle_weakref")
    Invoke-Sample weakref_get   0 "PASS under ARC"             @("weakref_get")
    Invoke-Sample cycle_strong  2 "done (strong cycle)"        @("cycle_strong")   # intentional leak without weak
    Invoke-Sample multifile     0 "twice=42"                   @("mf_main", "mf_lib")   # two source files compiled together
    Invoke-Sample numerics      0 "150"                        @("numerics")             # byte/sbyte/short/ushort/uint/ulong
    Invoke-Sample byref         0 "3"                          @("byref")                # ref/out/in parameters
    Invoke-Sample properties    0 "Alice"                      @("properties")           # auto-properties
    Invoke-Sample isas          0 "as Cat: null"               @("isas")                 # is/as runtime type checks
    Invoke-Sample covariance      0 "stored Dog"                 @("covariance")           # array covariance runtime check

    Write-Host "=== $($script:pass) passed, $($script:fail) failed ==="
    if ($script:fail -ne 0) { exit 1 }
}
finally { Pop-Location }
