<#
  Builds the ArcSharp compiler and compiles a C# source file to a native
  Windows x64 .exe using clang (which consumes the generated LLVM IR directly).

  Usage:
    powershell -ExecutionPolicy Bypass -File tools\build-windows.ps1 samples\inherit.cs -Run
    powershell -ExecutionPolicy Bypass -File tools\build-windows.ps1 samples\fib.cs -Out build\fib

  Requires: .NET 8 SDK, LLVM/clang, and a Windows linker reachable by clang
  (Visual Studio Build Tools / Windows SDK, or lld via -fuse-ld=lld).
#>
param(
  [Parameter(Mandatory = $true, Position = 0)] [string] $Source,
  [string] $Clang = "C:/Program Files/LLVM/bin/clang.exe",
  [string] $Out = "",
  [switch] $Run
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet build -c Release | Out-Host
    $dll = "bin/Release/net8.0/arcsharp.dll"
    $a = @($Source, "--target", "windows", "--clang", $Clang, "--runtime", "runtime/arc_runtime.c")
    if ($Out) { $a += @("-o", $Out) }
    if ($Run) { $a += "--run" }
    & dotnet $dll @a
} finally { Pop-Location }
