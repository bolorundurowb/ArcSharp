using System.Diagnostics;
using ArcSharp.Lexing;
using ArcSharp.Syntax;
using ArcSharp.Binding;
using ArcSharp.CodeGen;

namespace ArcSharp.Driver;

public sealed class Options
{
    public string Input = "";
    public string Output = "";
    public string Target = "windows";     // windows | host
    public bool EmitLlvmOnly;
    public bool Run;
    public bool BoundsChecks = true;
    public string Runtime = "runtime/arc_runtime.c";
    public string Llc = "llc";
    public string Cc = "gcc";
    public string Clang = "";              // if set, used as a one-shot .ll+runtime -> exe driver
}

public static class Compilation
{
    public static int Run(Options o)
    {
        if (!File.Exists(o.Input)) { Console.Error.WriteLine($"error: input not found: {o.Input}"); return 2; }
        var src = File.ReadAllText(o.Input);

        var lexer = new Lexer(src);
        var tokens = lexer.Lex();
        var parser = new Parser(tokens);
        var cu = parser.ParseCompilationUnit();
        var binder = new Binder();
        var program = binder.Bind(cu);

        var diags = lexer.Diagnostics.Concat(parser.Diagnostics).Concat(program.Diagnostics).ToList();
        foreach (var d in diags) Console.Error.WriteLine($"{o.Input}{d}");
        if (diags.Count > 0) { Console.Error.WriteLine($"compilation failed: {diags.Count} diagnostic(s)"); return 1; }

        var triple = o.Target == "windows" ? "x86_64-pc-windows-msvc" : "x86_64-pc-linux-gnu";
        var emitter = new Emitter(program, triple, o.BoundsChecks);
        var ir = emitter.Emit();

        var baseName = string.IsNullOrEmpty(o.Output)
            ? Path.GetFileNameWithoutExtension(o.Input)
            : o.Output;
        var llPath = baseName + ".ll";
        File.WriteAllText(llPath, ir);
        Console.Error.WriteLine($"[arcsharp] wrote {llPath}");

        if (o.EmitLlvmOnly) return 0;

        // ---- one-shot clang path: clang compiles .ll + runtime and links --------
        if (!string.IsNullOrEmpty(o.Clang))
        {
            var exe = baseName + (o.Target == "windows" ? ".exe" : "");
            var crc = Exec(o.Clang, [llPath, o.Runtime, "-o", exe]);
            if (crc != 0) { Console.Error.WriteLine("error: clang build failed"); return crc; }
            Console.Error.WriteLine($"[arcsharp] wrote {exe}");
            if (o.Run)
            {
                Console.Error.WriteLine($"[arcsharp] running {exe}");
                return Exec(Path.IsPathRooted(exe) || exe.Contains('/') || exe.Contains('\\') ? exe : "./" + exe, []);
            }
            return 0;
        }

        // ---- llc + linker path ---------------------------------------------------
        var objPath = baseName + (o.Target == "windows" ? ".obj" : ".o");
        var rc = Exec(o.Llc, ["-filetype=obj", "-relocation-model=pic", llPath, "-o", objPath]);
        if (rc != 0) { Console.Error.WriteLine("error: llc failed"); return rc; }
        Console.Error.WriteLine($"[arcsharp] wrote {objPath}");

        if (o.Target == "windows")
        {
            Console.Error.WriteLine("[arcsharp] Windows object emitted. Link on Windows with, e.g.:");
            Console.Error.WriteLine($"           clang {objPath} {o.Runtime} -o {baseName}.exe");
            Console.Error.WriteLine("           (or pass --clang \"C:/Program Files/LLVM/bin/clang.exe\" to build the .exe directly)");
            return 0;
        }

        var exePath = baseName;
        rc = Exec(o.Cc, [objPath, o.Runtime, "-o", exePath]);
        if (rc != 0) { Console.Error.WriteLine("error: link failed"); return rc; }
        Console.Error.WriteLine($"[arcsharp] wrote {exePath}");

        if (o.Run)
        {
            Console.Error.WriteLine($"[arcsharp] running {exePath}");
            return Exec(Path.IsPathRooted(exePath) ? exePath : "./" + exePath, []);
        }
        return 0;
    }

    private static int Exec(string file, string[] args)
    {
        var psi = new ProcessStartInfo { FileName = file, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
