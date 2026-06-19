#!/usr/bin/env bash
# End-to-end verification: builds the compiler, compiles every sample to LLVM IR,
# links it against the ARC runtime, runs it, and checks output + ARC accounting.
# Verification compiles for the Linux host (same x86-64 IR as the Windows target,
# only the triple/datalayout differ) so it can run in CI.
set -u
HERE="$(cd "$(dirname "$0")/.." && pwd)"
source "$HERE/tools/env.sh"

BUILD=/tmp/arc-verify
rm -rf "$BUILD"; mkdir -p "$BUILD/out"
cp -r "$HERE/src" "$HERE/runtime" "$HERE/samples" "$HERE/ArcSharp.csproj" "$HERE/nuget.config" "$BUILD/"
cd "$BUILD"
dotnet build -c Release >/tmp/arc-build.log 2>&1 || { echo "BUILD FAILED"; tail -20 /tmp/arc-build.log; exit 1; }
ASC="dotnet $BUILD/bin/Release/net8.0/arcsharp.dll"

pass=0; fail=0
run() { # name  expect_live  expect_substring
  local name="$1" live="$2" want="$3"
  local o; o="$($ASC samples/$name.cs -o out/$name --target host --run --runtime runtime/arc_runtime.c 2>/tmp/e)"
  local arc; arc="$(grep '\[arc\]' /tmp/e)"
  local gotlive; gotlive="$(echo "$arc" | sed -n 's/.*live=\([0-9]*\).*/\1/p')"
  local ok=1
  [ "$gotlive" = "$live" ] || ok=0
  echo "$o" | grep -qF "$want" || ok=0
  if [ "$ok" = 1 ]; then echo "PASS  $name  ($arc; output ok)"; pass=$((pass+1));
  else echo "FAIL  $name  want_live=$live got='$arc' want_out='$want' got_out='$o'"; fail=$((fail+1)); fi
}

echo "=== ArcSharp verification ==="
run inherit       0 "sum=42"
run interface     0 "circle=75"
run refarray      0 "refarray sum=5"
run statics       0 "count=3"
run fib           0 "fib(10)=55"
run weak_null     0 "PASS"
run cycle_weak    0 "done (weak cycle)"
run cycle_weakref 0 "done (WeakReference cycle)"
run weakref_get   0 "PASS under ARC"
run cycle_strong  2 "done (strong cycle)"   # intentional leak without weak
echo "=== $pass passed, $fail failed ==="
[ "$fail" = 0 ]
