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
    $script:pass = 0
    $script:fail = 0

    # Windows PowerShell turns a native command's stderr into an error record,
    # and PowerShell 7 does the same while ErrorActionPreference is Stop.
    # The compiler writes progress lines such as "[arcsharp] wrote ..." there,
    # so read both streams from the process instead of calling dotnet directly.
    function Invoke-Captured {
        param([string[]] $Arguments)
        $start = New-Object System.Diagnostics.ProcessStartInfo
        $start.FileName = "dotnet"
        $start.WorkingDirectory = $root
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.CreateNoWindow = $true
        $start.Arguments = ($Arguments | ForEach-Object {
            if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
        }) -join ' '

        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $start
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        [void]$process.WaitForExit()
        [void]$stdout.Wait()
        [void]$stderr.Wait()
        return @{ Out = $stdout.Result; Err = $stderr.Result; ExitCode = $process.ExitCode }
    }

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
        $run = Invoke-Captured (@($dll) + $a)
        $out = $run.Out
        $err = $run.Err
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

    # A program that must be rejected. The compiler stops after emitting IR,
    # so this needs no clang. The expected code has to appear in the output.
    function Invoke-MustFail {
        param(
            [string]   $Name,
            [string]   $Code,
            [string[]] $Sources
        )
        $srcs = @($Sources | ForEach-Object { Join-Path $root "samples/negative/$_.cs" })
        $outBase = Join-Path $build "out/neg_$Name"
        $a = @() + $srcs + @("-o", $outBase, "--target", "windows", "--emit-llvm")
        $run = Invoke-Captured (@($dll) + $a)
        $text = $run.Out + $run.Err
        $ok = ($run.ExitCode -ne 0) -and ($text -match [regex]::Escape($Code))
        if ($ok) {
            Write-Host "PASS  neg/$Name  ($Code)"
            $script:pass++
        }
        else {
            Write-Host "FAIL  neg/$Name  want_code=$Code exit=$($run.ExitCode) output='$text'"
            $script:fail++
        }
    }

    # A program that compiles, with a diagnostic either present or absent.
    function Invoke-Warns {
        param(
            [string]   $Name,
            [string]   $Code,
            [bool]     $Expected,
            [string[]] $Sources
        )
        $srcs = @($Sources | ForEach-Object { Join-Path $root "samples/$_.cs" })
        $outBase = Join-Path $build "out/warn_$Name"
        $a = @() + $srcs + @("-o", $outBase, "--target", "windows", "--emit-llvm")
        $run = Invoke-Captured (@($dll) + $a)
        $text = $run.Out + $run.Err
        $found = $text -match [regex]::Escape($Code)
        $ok = ($run.ExitCode -eq 0) -and ($found -eq $Expected)
        if ($ok) {
            $want = if ($Expected) { "warns" } else { "clean" }
            Write-Host "PASS  warn/$Name  ($Code $want)"
            $script:pass++
        }
        else {
            Write-Host "FAIL  warn/$Name  want_code=$Code expected=$Expected exit=$($run.ExitCode) output='$text'"
            $script:fail++
        }
    }

    Write-Host "=== ArcSharp verification (Windows x64) ==="

    # The analyser ships with its own Roslyn tests. They run here so the
    # harness stays the single gate; the samples below do the end-to-end work.
    Write-Host "--- analyser unit tests ---"
    $testProject = Join-Path $root "analyzers/ArcSharp.Analyzers.Tests/ArcSharp.Analyzers.Tests.csproj"
    $tests = Invoke-Captured @("test", $testProject, "-c", "Release", "--nologo", "-v", "q")
    if ($tests.ExitCode -eq 0) {
        Write-Host "PASS  analyser tests"
        $script:pass++
    }
    else {
        Write-Host "FAIL  analyser tests"
        Write-Host ($tests.Out + $tests.Err)
        $script:fail++
    }

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
    Invoke-Sample cycle_struct  2 "done (struct cycle)"        @("cycle_struct")   # strong cycle through an inline struct field
    Invoke-Sample multifile     0 "twice=42"                   @("mf_main", "mf_lib")   # two source files compiled together
    Invoke-Sample numerics      0 "150"                        @("numerics")             # byte/sbyte/short/ushort/uint/ulong
    Invoke-Sample byref         0 "3"                          @("byref")                # ref/out/in parameters
    Invoke-Sample properties    0 "Alice"                      @("properties")           # auto-properties
    Invoke-Sample isas          0 "as Cat: null"               @("isas")                 # is/as runtime type checks
    Invoke-Sample covariance      0 "stored Dog"                 @("covariance")           # array covariance runtime check
    Invoke-Sample longconcat      0 "concat=20000000000 uint=3000000000" @("longconcat") # long and uint to string; C long is 32-bit on Windows
    Invoke-Sample using_stmt     0 "disposals=3"                @("using_stmt")           # using/IDisposable: declaration, expression, virtual, interface, null, return
    Invoke-Sample structs        0 "card=card copy tags=1,2"    @("structs")              # struct codegen: value semantics, reference fields, ref/out, nesting

    Write-Host "--- unsupported constructs must be rejected (ARC3001 / ARC0003 / ARC2011) ---"
    Invoke-MustFail break           "ARC3001" @("break")
    Invoke-MustFail try             "ARC3001" @("try")
    Invoke-MustFail foreach         "ARC3001" @("foreach")
    Invoke-MustFail lambda          "ARC3001" @("lambda")
    Invoke-MustFail generic         "ARC3001" @("generic")
    Invoke-MustFail shift           "ARC0003" @("shift")
    Invoke-MustFail expression_body "ARC3001" @("expression_body")
    Invoke-MustFail named_arg       "ARC3001" @("named_arg")
    Invoke-MustFail optional_param  "ARC3001" @("optional_param")
    Invoke-MustFail struct_array    "ARC3001" @("struct_array")
    Invoke-MustFail struct_self     "ARC3001" @("struct_self")
    Invoke-MustFail struct_return   "ARC3001" @("struct_return")
    Invoke-MustFail using_bad       "ARC2011" @("using_bad")

    Write-Host "--- cycle analysis (ARC3100) ---"
    Invoke-Warns cycle_strong  "ARC3100" $true  @("cycle_strong")
    Invoke-Warns cycle_struct  "ARC3100" $true  @("cycle_struct")
    Invoke-Warns cycle_weak    "ARC3100" $false @("cycle_weak")
    Invoke-Warns cycle_weakref "ARC3100" $false @("cycle_weakref")

    Write-Host "=== $($script:pass) passed, $($script:fail) failed ==="
    if ($script:fail -ne 0) { exit 1 }
}
finally { Pop-Location }
