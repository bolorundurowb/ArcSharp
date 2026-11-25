<#
  End-to-end verification on Windows x64: builds the compiler, compiles every
  sample to a native .exe via clang (which consumes the generated LLVM IR
  directly), runs it, and checks the exit code, program output and ARC accounting
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
            [string[]] $Sources,
            [int]      $ExpectedExitCode = 0
        )
        $srcs = @($Sources | ForEach-Object { Join-Path $root "samples/$_.cs" })
        $outBase = Join-Path $build "out/$Name"
        $a = @() + $srcs + @("-o", $outBase, "--target", "windows", "--clang", $Clang, "--runtime", $runtime, "--run")
        $run = Invoke-Captured -Arguments (@($dll) + $a) -Environment @{ ARC_CHECK = "1" }
        $out = $run.Out
        $err = $run.Err
        $arc = ($err -split "`r?`n" | Where-Object { $_ -match '\[arc\] alloc=' }) -join ""
        $ok = ($run.ExitCode -eq $ExpectedExitCode) -and ($arc -ceq "[arc] $Accounting") -and ($out -match [regex]::Escape($Want)) -and ($err -notmatch '\[arc-check\]')
        if ($ok) {
            Write-Host "PASS  $Name  ($arc; output ok)"
            $err -split "`r?`n" | Where-Object { $_ -match '^\[arc-stats\]' } | ForEach-Object {
                Write-Host "      $Name  $_"
            }
            $script:pass++
        }
        else {
            Write-Host "FAIL  $Name  want_exit=$ExpectedExitCode got_exit=$($run.ExitCode) want='[arc] $Accounting' got='$arc' want_out='$Want' got_out='$out'"
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

    # A program that compiles and then aborts at run time with exit code 70 and
    # a message on stderr. The sources live in samples/negative. When
    # $Accounting is given, the complete [arc] line must match it as well.
    function Invoke-RuntimeAbort {
        param(
            [string]   $Name,
            [string]   $Want,
            [string[]] $Sources,
            [string]   $Accounting = ""
        )
        $srcs = @($Sources | ForEach-Object { Join-Path $root "samples/negative/$_.cs" })
        $outBase = Join-Path $build "out/abort_$Name"
        $a = @() + $srcs + @("-o", $outBase, "--target", "windows", "--clang", $Clang, "--runtime", $runtime, "--run")
        $run = Invoke-Captured -Arguments (@($dll) + $a) -Environment @{ ARC_CHECK = "1" }
        $ok = ($run.ExitCode -eq 70) -and ($run.Err -match [regex]::Escape($Want)) -and ($run.Out -notmatch "not reached")
        if ($ok -and $Accounting -ne "") {
            $arc = ($run.Err -split "`r?`n" | Where-Object { $_ -match '\[arc\] alloc=' }) -join ""
            $ok = ($arc -ceq "[arc] $Accounting")
        }

        if ($ok) {
            Write-Host "PASS  abort/$Name  ($Want)"
            $script:pass++
        }
        else {
            Write-Host "FAIL  abort/$Name  want='$Want' exit=$($run.ExitCode) output='$($run.Out)' errors='$($run.Err)'"
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
    Invoke-Sample statics       "alloc=3 dead=3 freed=3 live=0" "count=3"                    @("statics") -ExpectedExitCode 3
    Invoke-Sample fib           "alloc=22 dead=22 freed=22 live=0" "fib(10)=55"                 @("fib")
    Invoke-Sample floats        "alloc=7 dead=7 freed=7 live=0" "area=19.6349"               @("floats")
    Invoke-Sample weak_null     "alloc=4 dead=4 freed=4 live=0" "PASS"                       @("weak_null")
    Invoke-Sample cycle_weak    "alloc=4 dead=4 freed=4 live=0" "done (weak cycle)"          @("cycle_weak")
    Invoke-Sample cycle_weakref "alloc=4 dead=4 freed=4 live=0" "done (WeakReference cycle)" @("cycle_weakref")
    Invoke-Sample weakref_get   "alloc=4 dead=4 freed=4 live=0" "PASS under ARC"             @("weakref_get")
    Invoke-Sample cycle_strong  "alloc=3 dead=1 freed=1 live=2" "done (strong cycle)"        @("cycle_strong")   # intentional leak without weak
    Invoke-Sample lambdas       "alloc=14 dead=14 freed=14 live=0" "lambdas PASS"           @("lambdas")
    Invoke-Sample nested_closures "alloc=43 dead=43 freed=43 live=0" "nested closures ok" @("nested_closures")
    Invoke-Sample event_cycle "alloc=4 dead=1 freed=1 live=3" "event capture cycle" @("event_cycle")
    Invoke-Sample lambda_cycle  "alloc=4 dead=1 freed=1 live=3" "lambda capture cycle"     @("lambda_cycle")
    Invoke-Sample cycle_struct  "alloc=3 dead=1 freed=1 live=2" "done (struct cycle)"        @("cycle_struct")   # strong cycle through an inline struct field
    Invoke-Sample multifile     "alloc=4 dead=4 freed=4 live=0" "twice=42"                   @("mf_main", "mf_lib")   # two source files compiled together
    Invoke-Sample numerics      "alloc=4 dead=4 freed=4 live=0" "18446744073709551615"                        @("numerics")             # byte/sbyte/short/ushort/uint/ulong
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
    Invoke-Sample finaliser_order "alloc=40 dead=40 freed=40 live=0" "finaliser order PASS" @("finaliser_order")             # derived, then base, then children
    Invoke-Sample finaliser_release_point "alloc=17 dead=17 freed=17 live=0" "finaliser release points PASS" @("finaliser_release_point") # at the last release, not at scope exit
    Invoke-Sample finaliser_suppress "alloc=8 dead=8 freed=8 live=0" "suppress finalizer PASS finalised=1" @("finaliser_suppress") # GC.SuppressFinalize, and the Collect no-ops
    Invoke-Sample finaliser_graph "alloc=55 dead=55 freed=55 live=0" "finaliser graph PASS" @("finaliser_graph")           # weak refs, nested release, arrays, generics
    Invoke-Sample finaliser_deep "alloc=100006 dead=100006 freed=100006 live=0" "deep graph finalised 100000" @("finaliser_deep") # 100000 links: no recursion
    Invoke-Sample finaliser_cycle "alloc=5 dead=3 freed=3 live=2" "cycle finalised 0" @("finaliser_cycle")                # a strong cycle never finalises
    Invoke-Sample exceptions_basic "alloc=16 dead=16 freed=16 live=0" "outer caught inner" @("exceptions_basic")          # try, catch, finally, throw
    Invoke-Sample exceptions_unwind "alloc=97 dead=97 freed=97 live=0" "exceptions unwind PASS" @("exceptions_unwind")    # cleanup on every unwind path
    Invoke-Sample exceptions_abandoned_return "alloc=13 dead=13 freed=13 live=0" "abandoned returns PASS" @("exceptions_abandoned_return")
    Invoke-Sample exceptions_runtime "alloc=53 dead=53 freed=53 live=0" "exceptions runtime PASS" @("exceptions_runtime") # runtime checks raise catchable exceptions
    Invoke-Sample array_foreach "alloc=5 dead=5 freed=5 live=0" "array foreach PASS" @("array_foreach")
    Invoke-Sample variance "alloc=5 dead=5 freed=5 live=0" "variance PASS" @("variance")
    Invoke-Sample nullable_values "alloc=4 dead=4 freed=4 live=0" "nullable values PASS" @("nullable_values")
    Invoke-Sample operator_updates "alloc=9 dead=9 freed=9 live=0" "operator updates PASS" @("operator_updates")
    Invoke-Sample conditional_values "alloc=10 dead=10 freed=10 live=0" "conditional values PASS" @("conditional_values")
    Invoke-Sample patterns "alloc=6 dead=6 freed=6 live=0" "patterns PASS" @("patterns")
    Invoke-Sample events "alloc=9 dead=9 freed=9 live=0" "events PASS" @("events")
    Invoke-Sample delegates_basic "alloc=17 dead=17 freed=17 live=0" "delegates basic PASS" @("delegates_basic")           # method groups and explicit creation, static and instance
    Invoke-Sample delegates_multicast "alloc=127 dead=127 freed=127 live=0" "delegates multicast PASS" @("delegates_multicast") # combine, remove, equality, last result, null invoke
    Invoke-Sample delegates_targets "alloc=72 dead=72 freed=72 live=0" "delegates targets PASS" @("delegates_targets") # virtual, interface, ref, out, struct, generic, Func and Action
    Invoke-Sample delegates_lifetime "alloc=94 dead=94 freed=94 live=0" "after=finalised kept;" @("delegates_lifetime") # the delegate owns its target; handler exceptions; fields; arrays
Invoke-Sample delegates_lambdas "alloc=70 dead=70 freed=70 live=0" "delegates lambdas PASS" @("delegates_lambdas") # lambdas with captures, mixed with method groups in multicast lists
    Invoke-Sample struct_interfaces "alloc=9 dead=9 freed=9 live=0" "struct interfaces PASS" @("struct_interfaces")
    Invoke-Sample struct_storage "alloc=5 dead=5 freed=5 live=0" "struct storage PASS" @("struct_storage")
    Invoke-Sample struct_calls "alloc=6 dead=6 freed=6 live=0" "struct calls PASS" @("struct_calls")
    Invoke-Sample base_interfaces "alloc=2 dead=2 freed=2 live=0" "base interfaces PASS" @("base_interfaces")
    Invoke-Sample enums "alloc=2 dead=2 freed=2 live=0" "enums PASS" @("enums")
    Invoke-Sample static_constructor "alloc=11 dead=11 freed=11 live=0" "static constructor PASS" @("static_constructor")
    Invoke-Sample accessors "alloc=4 dead=4 freed=4 live=0" "accessors PASS" @("accessors")
    Invoke-Sample accessor_updates "alloc=13 dead=13 freed=13 live=0" "accessor updates PASS" @("accessor_updates")
    Invoke-Sample nullable_updates "alloc=3 dead=3 freed=3 live=0" "nullable updates PASS" @("nullable_updates") # lifted compound and increment, user-defined lifted operators and conversions
    Invoke-Sample nullable_struct_boxing "alloc=6 dead=6 freed=6 live=0" "nullable struct boxing PASS" @("nullable_struct_boxing") # boxing a nullable struct, as and casts back, interfaces
    Invoke-Sample decimals "alloc=292 dead=292 freed=292 live=0" "decimals PASS" @("decimals") # decimal arithmetic, scale, rounding, conversions, overflow, lifted decimal?
    Invoke-Sample object_initialisers "alloc=24 dead=24 freed=24 live=0" "object initialisers PASS" @("object_initialisers") # member initialisers, nested initialisers, evaluation order
    Invoke-Sample records "alloc=197 dead=197 freed=197 live=0" "records PASS" @("records") # record classes: equality, hashing, formatting, with, deconstruction, inheritance
    Invoke-Sample generic_dispatch "alloc=31 dead=31 freed=31 live=0" "generic dispatch PASS" @("generic_dispatch") # virtual, abstract and interface generic methods
    Invoke-Sample generic_inference "alloc=43 dead=43 freed=43 live=0" "generic inference PASS" @("generic_inference") # Roslyn inference for method type arguments
    Invoke-Sample static_abstract "alloc=21 dead=21 freed=21 live=0" "static abstract PASS" @("static_abstract")    # static abstract members called on a type parameter
    Invoke-Sample extensions "alloc=15 dead=15 freed=15 live=0" "extensions PASS" @("extensions_main", "extensions_lib") # extension methods and a partial class across two files
    Invoke-Sample arguments "alloc=9 dead=9 freed=9 live=0" "arguments PASS" @("arguments")
    Invoke-Sample operators_members "alloc=4 dead=4 freed=4 live=0" "operators and members PASS" @("operators_members")
    Invoke-Sample checked_arithmetic "alloc=13 dead=13 freed=13 live=0" "checked PASS" @("checked_arithmetic")
    Invoke-Sample bitwise "alloc=1 dead=1 freed=1 live=0" "bitwise PASS" @("bitwise")
    Invoke-Sample numeric_compound "alloc=10 dead=10 freed=10 live=0" "compound PASS" @("numeric_compound")
    Invoke-Sample increment "alloc=2 dead=2 freed=2 live=0" "increment PASS" @("increment")
    Invoke-Sample switch_statement "alloc=4 dead=4 freed=4 live=0" "switch PASS" @("switch_statement")
    Invoke-Sample loop_branches "alloc=3 dead=3 freed=3 live=0" "loop branches PASS" @("loop_branches")
    Invoke-Sample do_while "alloc=1 dead=1 freed=1 live=0" "do while PASS" @("do_while")
    Invoke-Sample exceptions_using "alloc=74 dead=74 freed=74 live=0" "exceptions using PASS" @("exceptions_using")       # Dispose runs while unwinding
    Invoke-Sample exceptions_finaliser "alloc=32 dead=32 freed=32 live=0" "exceptions finaliser PASS" @("exceptions_finaliser") # a throwing finaliser is reported and teardown continues

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

    Write-Host "--- unsupported constructs must be rejected (ARC3001 / CS / ARC2011 / ARC2012 / ARC2013 / ARC2015 / ARC2016) ---"
    Invoke-MustFail do_while_condition "CS0029" @("do_while_condition")
    Invoke-MustFail break           "CS0139" @("break")
    Invoke-MustFail exception_filter "ARC3001" @("exception_filter")
    Invoke-MustFail throw_expression "throw expressions are not supported" @("throw_expression")
    Invoke-MustFail throw_non_exception "CS0029" @("throw_non_exception")
    Invoke-MustFail list_pattern "ARC3001" @("list_pattern")
    Invoke-MustFail switch_pattern "ARC3001" @("switch_pattern")
    Invoke-MustFail accessor_update_struct "ARC3001" @("accessor_update_struct")
    Invoke-MustFail extension_scope "CS1061" @("extension_scope")
    Invoke-MustFail generic_interface_struct "ARC3001" @("generic_interface_struct")
    Invoke-MustFail initialiser_struct "ARC3001" @("initialiser_struct")
    Invoke-MustFail initialiser_collection "ARC3001" @("initialiser_collection")
    Invoke-MustFail record_struct "ARC3001" @("record_struct")
    Invoke-MustFail record_generic "ARC3001" @("record_generic")
    Invoke-MustFail record_struct_member "ARC3001" @("record_struct_member")
    Invoke-MustFail record_array_member "ARC3001" @("record_array_member")
    Invoke-MustFail decimal_constructor "ARC3001" @("decimal_constructor")
    Invoke-MustFail decimal_parse "CS0117" @("decimal_parse")
    Invoke-MustFail static_abstract_generic "ARC3001" @("static_abstract_generic")
    Invoke-MustFail foreach         "ARC3001" @("foreach")
    Invoke-MustFail lambda          "ARC3001" @("lambda")
    Invoke-MustFail event_accessors "ARC3001" @("event_accessors")
    Invoke-MustFail delegate_struct_target "ARC3001" @("delegate_struct_target")
    Invoke-MustFail delegate_library_method "ARC3001" @("delegate_library_method")
Invoke-MustFail lambda_ref_parameter "ARC3001" @("lambda_ref_parameter")
    Invoke-MustFail generic         "CS0246" @("generic")
    Invoke-MustFail generic_arity "CS0305" @("generic_arity")
    Invoke-MustFail generic_constraint "CS0452" @("generic_constraint")
    Invoke-MustFail generic_interface_constraint "CS0311" @("generic_interface_constraint")
    Invoke-MustFail generic_method_constraint "CS0453" @("generic_method_constraint")
    Invoke-MustFail generic_constructor_constraint "CS0310" @("generic_constructor_constraint")
    Invoke-MustFail generic_expansion "ARC2013" @("generic_expansion")
    Invoke-MustFail checked_constant "CS0220" @("checked_constant")
    Invoke-MustFail shift           "CS0019" @("shift")
    Invoke-MustFail readonly_field "CS0191" @("readonly_field")
    Invoke-MustFail nested_generic_outer "ARC3001" @("nested_generic_outer")
    Invoke-MustFail operator_update "ARC2001" @("operator_update")
    Invoke-MustFail expression_body "CS8701" @("expression_body")
    Invoke-MustFail named_arg       "CS1739" @("named_arg")
    Invoke-MustFail optional_param  "CS1737" @("optional_param")
    Invoke-MustFail base_interface_member "CS0535" @("base_interface_member")
    Invoke-MustFail enum_boxing "ARC3001" @("enum_boxing")
    Invoke-MustFail static_ctor_parameter "CS0132" @("static_ctor_parameter")
    Invoke-MustFail params_type "CS0225" @("params_type")
    Invoke-MustFail unmanaged_constraint "CS8377" @("unmanaged_constraint")
    Invoke-MustFail invalid_variance "CS1961" @("invalid_variance")
    Invoke-MustFail nullable_as "ARC3001" @("nullable_as")
    Invoke-MustFail nullable_boxing "ARC3001" @("nullable_boxing")
    Invoke-MustFail struct_boxing_missing_member "CS0535" @("struct_boxing_missing_member")
    Invoke-MustFail struct_array    "ARC3001" @("struct_array")
    Invoke-MustFail struct_self     "ARC3001" @("struct_self")
    Invoke-MustFail struct_return   "CS0165" @("struct_return")
    Invoke-MustFail using_bad       "ARC2011" @("using_bad")
    Invoke-MustFail bad_escape      "CS1009" @("bad_escape")
    Invoke-MustFail abstract_new    "ARC2012" @("abstract_new")
    Invoke-MustFail ref_escape      "ARC3001" @("ref_escape")
    Invoke-MustFail ref_owner       "ARC2015" @("ref_owner")
    Invoke-MustFail finaliser_resurrect "ARC2016" @("finaliser_resurrect")
    Invoke-MustFail gc_reregister   "ARC3001" @("gc_reregister")

    Write-Host "--- a finaliser that resurrects its object aborts the process ---"
    Invoke-RuntimeAbort finaliser_resurrect_call "ResurrectionException" @("finaliser_resurrect_call")

    Write-Host "--- an unhandled exception ends the program with exit code 70 ---"
    Invoke-RuntimeAbort exception_unhandled "Unhandled exception. System.InvalidOperationException: nobody catches this" @("exception_unhandled") "alloc=4 dead=4 freed=4 live=0"

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
    Invoke-Warns event_cycle "ARC3100" $true  @("event_cycle")
    Invoke-Warns events      "ARC3100" $false @("events")
    Invoke-Warns lambda_cycle  "ARC3100" $true  @("lambda_cycle")
    Invoke-Warns lambdas       "ARC3100" $false @("lambdas")
    Invoke-Warns cycle_struct  "ARC3100" $true  @("cycle_struct")
    Invoke-Warns cycle_weak    "ARC3100" $false @("cycle_weak")
    Invoke-Warns cycle_weakref "ARC3100" $false @("cycle_weakref")
    Invoke-Warns finaliser_cycle "ARC3100" $true @("finaliser_cycle")

    Write-Host "--- GC calls that do nothing (ARC3200) ---"
    Invoke-Warns finaliser_suppress "ARC3200" $true  @("finaliser_suppress")
    Invoke-Warns finaliser_order    "ARC3200" $false @("finaliser_order")

    Write-Host "=== $($script:pass) passed, $($script:fail) failed ==="
    if ($script:fail -ne 0) { exit 1 }
}
finally { Pop-Location }
