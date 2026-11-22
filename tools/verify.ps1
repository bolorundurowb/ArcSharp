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

    $dll = Join-Path $root "bin/Release/net11.0/arcsharp.dll"
    $runtime = Join-Path $root "runtime/arc_runtime.c"
    $script:pass = 0
    $script:fail = 0

    # Windows PowerShell turns a native command's stderr into an error record,
    # and PowerShell 7 does the same while ErrorActionPreference is Stop.
    # The compiler writes progress lines such as "[arcsharp] wrote ..." there,
    # so read both streams from the process instead of calling dotnet directly.
    function Invoke-Captured {
        param([string[]] $Arguments, [hashtable] $Environment)
        $start = New-Object System.Diagnostics.ProcessStartInfo
        $start.FileName = "dotnet"
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
            [string]   $Accounting,
            [string]   $Want,
            [string[]] $Sources
        )
        $srcs = @($Sources | ForEach-Object { Join-Path $root "samples/$_.cs" })
        $outBase = Join-Path $build "out/$Name"
        $a = @() + $srcs + @("-o", $outBase, "--target", "windows", "--clang", $Clang, "--runtime", $runtime, "--run")
        $run = Invoke-Captured -Arguments (@($dll) + $a) -Environment @{ ARC_CHECK = "1" }
        $out = $run.Out
        $err = $run.Err
        $arc = ($err -split "`r?`n" | Where-Object { $_ -match '\[arc\] alloc=' }) -join ""
        $ok = ($arc -ceq "[arc] $Accounting") -and ($out -match [regex]::Escape($Want)) -and ($err -notmatch '\[arc-check\]')
        if ($ok) {
            Write-Host "PASS  $Name  ($arc; output ok)"
            $err -split "`r?`n" | Where-Object { $_ -match '^\[arc-stats\]' } | ForEach-Object {
                Write-Host "      $Name  $_"
            }
            $script:pass++
        }
        else {
            Write-Host "FAIL  $Name  want='[arc] $Accounting' got='$arc' want_out='$Want' got_out='$out'"
            Write-Host $err
            $script:fail++
        }
    }

    # Same as Invoke-Sample, with ARC_LEAKS=1. Patterns are .NET regexes matched
    # against the whole stderr. Absent patterns must not match.
    function Invoke-LeakCheck {
        param(
            [string]   $Name,
            [int]      $Live,
            [string]   $Want,
            [string[]] $Sources,
            [string[]] $Patterns,
            [string[]] $Absent
        )
        $srcs = @($Sources | ForEach-Object { Join-Path $root "samples/$_.cs" })
        $outBase = Join-Path $build "out/leak_$Name"
        $a = @() + $srcs + @("-o", $outBase, "--target", "windows", "--clang", $Clang, "--runtime", $runtime, "--run")
        $run = Invoke-Captured -Arguments (@($dll) + $a) -Environment @{ ARC_LEAKS = "1"; ARC_CHECK = "1" }
        $out = $run.Out
        $err = $run.Err
        $arc = ($err -split "`r?`n" | Where-Object { $_ -match '\[arc\] alloc=' }) -join ""
        $gotLive = if ($arc -match 'live=(\d+)') { [int]$Matches[1] } else { -1 }
        $missing = @($Patterns | Where-Object { $err -notmatch $_ })
        $present = @($Absent | Where-Object { $err -match $_ })
        $ok = ($gotLive -eq $Live) -and ($out -match [regex]::Escape($Want)) -and ($err -notmatch '\[arc-check\]') -and ($missing.Count -eq 0) -and ($present.Count -eq 0)
        if ($ok) {
            Write-Host "PASS  leak/$Name  ($arc)"
            $script:pass++
        }
        else {
            Write-Host "FAIL  leak/$Name  want_live=$Live got='$arc' missing='$($missing -join ' | ')' unexpected='$($present -join ' | ')'"
            Write-Host $err
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

    # A command line that must be rejected without crashing the compiler.
    function Invoke-Usage {
        param(
            [string]   $Name,
            [string]   $Want,
            [string[]] $Arguments
        )
        $run = Invoke-Captured (@($dll) + $Arguments)
        $text = $run.Out + $run.Err
        $ok = ($run.ExitCode -eq 2) -and ($text -match [regex]::Escape($Want))
        if ($ok) {
            Write-Host "PASS  usage/$Name  ($Want)"
            $script:pass++
        }
        else {
            Write-Host "FAIL  usage/$Name  want='$Want' exit=$($run.ExitCode) output='$text'"
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

    Write-Host "--- reference surface ---"
    $surfaceDll = Join-Path $root "ref/ArcSharp.References/bin/Release/net11.0/ArcSharp.References.dll"
    $surfaceProject = Join-Path $root "ref/ArcSharp.References.Check/ArcSharp.References.Check.csproj"
    $surface = Invoke-Captured @("run", "--project", $surfaceProject, "-c", "Release", "-v", "q", "--", $surfaceDll)
    if ($surface.ExitCode -eq 0) {
        Write-Host "PASS  reference surface"
        $script:pass++
    }
    else {
        Write-Host "FAIL  reference surface"
        Write-Host ($surface.Out + $surface.Err)
        $script:fail++
    }

    Invoke-Sample generics "alloc=72 dead=72 freed=72 live=0" "generics PASS" @("generics", "generics_lib")
    Invoke-Sample generic_structs "alloc=28 dead=28 freed=28 live=0" "layout=20,30,99,40" @("generic_structs")
    Invoke-Sample inherit       "alloc=5 dead=5 freed=5 live=0" "sum=42"                     @("inherit")
    Invoke-Sample interface     "alloc=8 dead=8 freed=8 live=0" "circle=75"                  @("interface")
    Invoke-Sample refarray      "alloc=7 dead=7 freed=7 live=0" "refarray sum=5"             @("refarray")
    Invoke-Sample statics       "alloc=3 dead=3 freed=3 live=0" "count=3"                    @("statics")
    Invoke-Sample fib           "alloc=22 dead=22 freed=22 live=0" "fib(10)=55"                 @("fib")
    Invoke-Sample floats        "alloc=7 dead=7 freed=7 live=0" "area=19.6349"               @("floats")
    Invoke-Sample weak_null     "alloc=4 dead=4 freed=4 live=0" "PASS"                       @("weak_null")
    Invoke-Sample cycle_weak    "alloc=4 dead=4 freed=4 live=0" "done (weak cycle)"          @("cycle_weak")
    Invoke-Sample cycle_weakref "alloc=4 dead=4 freed=4 live=0" "done (WeakReference cycle)" @("cycle_weakref")
    Invoke-Sample weakref_get   "alloc=4 dead=4 freed=4 live=0" "PASS under ARC"             @("weakref_get")
    Invoke-Sample cycle_strong  "alloc=3 dead=1 freed=1 live=2" "done (strong cycle)"        @("cycle_strong")   # intentional leak without weak
    Invoke-Sample cycle_struct  "alloc=3 dead=1 freed=1 live=2" "done (struct cycle)"        @("cycle_struct")   # strong cycle through an inline struct field
    Invoke-Sample multifile     "alloc=4 dead=4 freed=4 live=0" "twice=42"                   @("mf_main", "mf_lib")   # two source files compiled together
    Invoke-Sample numerics      "alloc=0 dead=0 freed=0 live=0" "150"                        @("numerics")             # byte/sbyte/short/ushort/uint/ulong
    Invoke-Sample byref         "alloc=0 dead=0 freed=0 live=0" "3"                          @("byref")                # ref/out/in parameters
    Invoke-Sample properties    "alloc=2 dead=2 freed=2 live=0" "Alice"                      @("properties")           # auto-properties
    Invoke-Sample isas          "alloc=5 dead=5 freed=5 live=0" "as Cat: null"               @("isas")                 # is/as runtime type checks
    Invoke-Sample covariance      "alloc=3 dead=3 freed=3 live=0" "stored Dog"                 @("covariance")           # array covariance runtime check
    Invoke-Sample longconcat      "alloc=7 dead=7 freed=7 live=0" "concat=20000000000 uint=3000000000" @("longconcat") # long and uint to string; C long is 32-bit on Windows
    Invoke-Sample using_stmt     "alloc=25 dead=25 freed=25 live=0" "disposals=3"                @("using_stmt")           # using/IDisposable: declaration, expression, virtual, interface, null, return
    Invoke-Sample structs        "alloc=66 dead=66 freed=66 live=0" "card=card copy tags=1,2"    @("structs")              # struct codegen: value semantics, reference fields, ref/out, nesting
    Invoke-Sample boolprint      "alloc=9 dead=9 freed=9 live=0" "cmp=False/True direct=False" @("boolprint")       # false comparison printed with WriteLine, and bool concatenation
    Invoke-Sample unicode        "alloc=9 dead=9 freed=9 live=0" "letter=A match=True bytes=4" @("unicode")         # \u and \U escapes
    Invoke-Sample overloads      "alloc=19 dead=19 freed=19 live=0" "int=7 str=ok 4 4"           @("overloads")        # same-arity overloads, static and instance
    Invoke-Sample charprint      "alloc=5 dead=5 freed=5 live=0" "concat=B direct=A"          @("charprint")        # char prints as a character
    Invoke-Sample staticprop     "alloc=3 dead=3 freed=3 live=0" "count=4"                    @("staticprop")       # static auto-property
    Invoke-Sample leaks          "alloc=7 dead=1 freed=1 live=6" "leaks reported"             @("leaks")            # static root plus unreachable cycles; ARC_LEAKS report checked below
    Invoke-Sample owners         "alloc=33 dead=33 freed=33 live=0" "owners=4,7,5,6,5,2"         @("owners")           # interior refs stay valid when the callee drops the owner
    Invoke-Sample destruction_points "alloc=3 dead=3 freed=3 live=0" "destruction points PASS" @("destruction_points")
    Invoke-Sample m2_dispatch_logic "alloc=6 dead=6 freed=6 live=0" "dispatch and logic PASS" @("m2_dispatch_logic")
    Invoke-Sample m2_initialisers "alloc=7 dead=7 freed=7 live=0" "initialisers PASS" @("m2_initialisers")
    Invoke-Sample m2_properties_narrowing "alloc=5 dead=5 freed=5 live=0" "properties and narrowing PASS" @("m2_properties_narrowing")
    Invoke-Sample lifetime_reads_and_moves "alloc=10 dead=10 freed=10 live=0" "lifetime reads and moves PASS" @("lifetime_reads_and_moves")
    Invoke-Sample lifetime_confined "alloc=6 dead=6 freed=6 live=0" "confined lifetimes PASS" @("lifetime_confined")

    Write-Host "--- leak detector (ARC_LEAKS=1) ---"
    Invoke-LeakCheck leaks 6 "leaks reported" @("leaks") @(
        "type=Node .*root=static",
        "type=Parent .*root=unreachable",
        "type=Child .*root=unreachable",
        "type=Cell .*root=unreachable",
        "type=<ref\[\]> .*root=unreachable",
        "site=leaks\.cs:",
        "\[arc\] cycle (Parent -> Child -> Parent|Child -> Parent -> Child)",
        "\[arc\] cycle (<ref\[\]> -> Cell -> <ref\[\]>|Cell -> <ref\[\]> -> Cell)"
    ) @()
    Invoke-LeakCheck cycle_strong 2 "done (strong cycle)" @("cycle_strong") @(
        "type=Parent .*root=unreachable",
        "type=Child .*root=unreachable",
        "site=cycle_strong\.cs:",
        "\[arc\] cycle (Parent -> Child -> Parent|Child -> Parent -> Child)"
    ) @("root=static")
    Invoke-LeakCheck cycle_struct 2 "done (struct cycle)" @("cycle_struct") @(
        "type=Owner .*root=unreachable",
        "site=cycle_struct\.cs:",
        "\[arc\] cycle Owner -> Owner -> Owner"
    ) @("root=static")
    Invoke-LeakCheck cycle_weak 0 "done (weak cycle)" @("cycle_weak") @() @("\[arc\] survivor")

    Write-Host "--- unsupported constructs must be rejected (ARC3001 / CS / ARC2011 / ARC2012 / ARC2013 / ARC2015) ---"
    Invoke-MustFail break           "ARC3001" @("break")
    Invoke-MustFail try             "ARC3001" @("try")
    Invoke-MustFail foreach         "ARC3001" @("foreach")
    Invoke-MustFail lambda          "ARC3001" @("lambda")
    Invoke-MustFail generic         "CS0246" @("generic")
    Invoke-MustFail generic_arity "CS0305" @("generic_arity")
    Invoke-MustFail generic_constraint "CS0452" @("generic_constraint")
    Invoke-MustFail generic_interface_constraint "CS0311" @("generic_interface_constraint")
    Invoke-MustFail generic_method_constraint "CS0453" @("generic_method_constraint")
    Invoke-MustFail generic_constructor_constraint "CS0310" @("generic_constructor_constraint")
    Invoke-MustFail generic_expansion "ARC2013" @("generic_expansion")
    Invoke-MustFail generic_virtual_method "ARC3001" @("generic_virtual_method")
    Invoke-MustFail shift_right "ARC3001" @("shift_right")
    Invoke-MustFail shift           "ARC3001" @("shift")
    Invoke-MustFail expression_body "ARC3001" @("expression_body")
    Invoke-MustFail named_arg       "ARC3001" @("named_arg")
    Invoke-MustFail optional_param  "ARC3001" @("optional_param")
    Invoke-MustFail struct_array    "ARC3001" @("struct_array")
    Invoke-MustFail struct_self     "ARC3001" @("struct_self")
    Invoke-MustFail struct_return   "ARC3001" @("struct_return")
    Invoke-MustFail using_bad       "ARC2011" @("using_bad")
    Invoke-MustFail bad_escape      "CS1009" @("bad_escape")
    Invoke-MustFail abstract_new    "ARC2012" @("abstract_new")
    Invoke-MustFail ref_escape      "ARC3001" @("ref_escape")
    Invoke-MustFail ref_owner       "ARC2015" @("ref_owner")

    Write-Host "--- command line must report an error instead of crashing ---"
    Invoke-Usage missing_option "requires a value" @((Join-Path $root "samples/fib.cs"), "-o")
    $missingTool = Join-Path $build "no-such-tool.exe"
    Invoke-Usage missing_clang "could not run" @(
        (Join-Path $root "samples/fib.cs"),
        "-o", (Join-Path $build "out/missing_clang"),
        "--target", "windows",
        "--clang", $missingTool,
        "--runtime", $runtime)
    Invoke-Usage missing_llc "could not run" @(
        (Join-Path $root "samples/fib.cs"),
        "-o", (Join-Path $build "out/missing_llc"),
        "--target", "windows",
        "--llc", $missingTool)

    Write-Host "--- cycle analysis (ARC3100) ---"
    Invoke-Warns cycle_strong  "ARC3100" $true  @("cycle_strong")
    Invoke-Warns cycle_struct  "ARC3100" $true  @("cycle_struct")
    Invoke-Warns cycle_weak    "ARC3100" $false @("cycle_weak")
    Invoke-Warns cycle_weakref "ARC3100" $false @("cycle_weakref")

    Write-Host "=== $($script:pass) passed, $($script:fail) failed ==="
    if ($script:fail -ne 0) { exit 1 }
}
finally { Pop-Location }
