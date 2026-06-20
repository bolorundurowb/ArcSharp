using ArcSharp.Driver;

var o = new Options();
for (var i = 0; i < args.Length; i++)
{
    var a = args[i];
    switch (a)
    {
        case "-o": o.Output = args[++i]; break;
        case "--target": o.Target = args[++i]; break;
        case "--emit-llvm": o.EmitLlvmOnly = true; break;
        case "--run": o.Run = true; break;
        case "--no-bounds": o.BoundsChecks = false; break;
        case "-Werror":
        case "--warn-as-error": o.WarnAsError = true; break;
        case "--runtime": o.Runtime = args[++i]; break;
        case "--llc": o.Llc = args[++i]; break;
        case "--cc": o.Cc = args[++i]; break;
        case "--clang": o.Clang = args[++i]; break;
        case "-h":
        case "--help":
            Console.WriteLine(
                "usage: arcsharp <input.cs> [input2.cs ...] [-o name] [--target windows|host] [--emit-llvm] [--run] [--no-bounds] [-Werror] [--runtime path] [--llc name] [--cc name] [--clang path]");
            return 0;
        default:
            if (a.StartsWith('-'))
            {
                Console.Error.WriteLine($"unknown option {a}");
                return 2;
            }

            o.Inputs.Add(a);
            break;
    }
}

if (o.Inputs.Count == 0)
{
    Console.Error.WriteLine("error: no input file");
    return 2;
}

return Compilation.Run(o);
