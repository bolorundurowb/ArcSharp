using System.ComponentModel;
using System.Diagnostics;
using ArcSharp.Binding;
using ArcSharp.CodeGen;
using ArcSharp.Frontend;
using ArcSharp.Lexing;
using ArcSharp.Syntax;

namespace ArcSharp.Driver;

public sealed class Options
{
    public List<string> Inputs = new(); // one or more .cs source files
    public string Output = "";
    public string Target = "windows"; // windows | host
    public bool EmitLlvmOnly;
    public bool Run;
    public bool BoundsChecks = true;
    public bool WarnAsError; // treat warnings as errors
    public string Runtime = "runtime/arc_runtime.c";
    public string Llc = "llc";
    public string Cc = "gcc";
    public string Clang = ""; // if set, used as a one-shot .ll+runtime -> exe driver
    public string Frontend = "handwritten"; // handwritten | roslyn; roslyn is temporary until it replaces the other
}

public static class Compilation
{
    public static int Run(Options o)
    {
        if (o.Inputs.Count == 0)
        {
            Console.Error.WriteLine("error: no input file");
            return 2;
        }

        var diags = new List<(string file, Diagnostic d)>();
        foreach (var input in o.Inputs)
        {
            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"error: input not found: {input}");
                return 2;
            }
        }

        BoundProgram program;
        if (o.Frontend == "roslyn")
        {
            program = RoslynFrontend.Bind(o.Inputs, diags);
        }
        else
        {
            program = BindHandwritten(o.Inputs, diags);
        }

        foreach (var (file, d) in diags)
        {
            Console.Error.WriteLine($"{file}{d}");
        }

        var errors = diags.Count(x => x.d.Severity == DiagnosticSeverity.Error
                                      || (o.WarnAsError && x.d.Severity == DiagnosticSeverity.Warning));
        var warnings = diags.Count(x => x.d.Severity == DiagnosticSeverity.Warning);
        if (errors > 0)
        {
            Console.Error.WriteLine($"compilation failed: {errors} error(s), {warnings} warning(s)");
            return 1;
        }

        if (warnings > 0)
        {
            Console.Error.WriteLine($"[arcsharp] {warnings} warning(s)");
        }

        var triple = o.Target == "windows" ? "x86_64-pc-windows-msvc" : "x86_64-pc-linux-gnu";
        var emitter = new Emitter(program, triple, o.BoundsChecks);
        var ir = emitter.Emit();

        var baseName = string.IsNullOrEmpty(o.Output)
            ? Path.GetFileNameWithoutExtension(o.Inputs[0])
            : o.Output;
        var llPath = baseName + ".ll";
        File.WriteAllText(llPath, ir);
        Console.Error.WriteLine($"[arcsharp] wrote {llPath}");

        if (o.EmitLlvmOnly)
        {
            return 0;
        }

        // ---- one-shot clang path: clang compiles .ll + runtime and links --------
        if (!string.IsNullOrEmpty(o.Clang))
        {
            var exe = baseName + (o.Target == "windows" ? ".exe" : "");
            var crc = Exec(o.Clang, [llPath, o.Runtime, "-o", exe]);
            if (crc != 0)
            {
                Console.Error.WriteLine("error: clang build failed");
                return crc;
            }

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
        if (rc != 0)
        {
            Console.Error.WriteLine("error: llc failed");
            return rc;
        }

        Console.Error.WriteLine($"[arcsharp] wrote {objPath}");

        if (o.Target == "windows")
        {
            Console.Error.WriteLine("[arcsharp] Windows object emitted. Link on Windows with, e.g.:");
            Console.Error.WriteLine($"           clang {objPath} {o.Runtime} -o {baseName}.exe");
            Console.Error.WriteLine(
                "           (or pass --clang \"C:/Program Files/LLVM/bin/clang.exe\" to build the .exe directly)");
            return 0;
        }

        var exePath = baseName;
        rc = Exec(o.Cc, [objPath, o.Runtime, "-o", exePath]);
        if (rc != 0)
        {
            Console.Error.WriteLine("error: link failed");
            return rc;
        }

        Console.Error.WriteLine($"[arcsharp] wrote {exePath}");

        if (o.Run)
        {
            Console.Error.WriteLine($"[arcsharp] running {exePath}");
            return Exec(Path.IsPathRooted(exePath) ? exePath : "./" + exePath, []);
        }

        return 0;
    }

    // The handwritten frontend does not know System.IDisposable. The prelude
    // keeps samples that implement it compiling on this path. The Roslyn path
    // binds the interface from the reference assembly instead.
    private static BoundProgram BindHandwritten(List<string> inputs, List<(string file, Diagnostic d)> diags)
    {
        var units = new List<CompilationUnit>();
        var prelude = new Lexer("interface IDisposable { void Dispose(); }");
        var preludeParser = new Parser(prelude.Lex());
        var preludeUnit = preludeParser.ParseCompilationUnit();
        preludeUnit.File = "";
        units.Add(preludeUnit);
        foreach (var d in prelude.Diagnostics.Concat(preludeParser.Diagnostics))
        {
            diags.Add(("", d));
        }

        foreach (var input in inputs)
        {
            var src = File.ReadAllText(input);
            var lexer = new Lexer(src);
            var parser = new Parser(lexer.Lex());
            var unit = parser.ParseCompilationUnit();
            unit.File = Path.GetFileName(input);
            units.Add(unit);
            foreach (var d in lexer.Diagnostics.Concat(parser.Diagnostics))
            {
                diags.Add((input, d));
            }
        }

        var binder = new Binder();
        var program = binder.Bind(units);
        foreach (var d in program.Diagnostics)
        {
            diags.Add(("", d));
        }

        return program;
    }

    private static int Exec(string file, string[] args)
    {
        var psi = new ProcessStartInfo { FileName = file, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                Console.Error.WriteLine($"error: could not run '{file}'");
                return 2;
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception ex)
        {
            Console.Error.WriteLine($"error: could not run '{file}': {ex.Message}");
            return 2;
        }
    }
}
