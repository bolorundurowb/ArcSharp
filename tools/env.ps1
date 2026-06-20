<#
  Sets the ArcSharp build environment for Windows x64. Dot-source it:

    . tools\env.ps1

  Assumes a system .NET 8 SDK and LLVM (clang/llc) install. If LLVM is in the
  default location it is added to PATH so `clang` and `llc` are reachable.
#>
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = "1"

# Add LLVM to PATH if present so `clang` / `llc` are reachable.
$llvmBin = "C:/Program Files/LLVM/bin"
if ((Test-Path $llvmBin) -and ($env:PATH -notlike "*$llvmBin*")) {
    $env:PATH = "$llvmBin;$env:PATH"
}
