#!/usr/bin/env bash
# Sources the locally-provisioned .NET + LLVM toolchain into the current shell.
# Usage:  source tools/env.sh
PREFIX="$HOME/local"
export DOTNET_ROOT="$PREFIX/usr/lib/dotnet"
export PATH="$DOTNET_ROOT:$PREFIX/usr/lib/llvm-14/bin:${PATH:-}"
export LD_LIBRARY_PATH="$PREFIX/usr/lib/x86_64-linux-gnu:$PREFIX/usr/lib/llvm-14/lib:${LD_LIBRARY_PATH:-}"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
