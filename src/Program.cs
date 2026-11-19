using ArcSharp.Driver;

var o = new Options();
for (var i = 0; i < args.Length; i++)
{
    var a = args[i];
    switch (a)
    {
        case "-o":
        {
            if (!TryTakeValue(args, ref i, a, out var output))
            {
                return 2;
            }

            o.Output = output;
            break;
        }
        case "--target":
        {
            if (!TryTakeValue(args, ref i, a, out var target))
            {
                return 2;
            }

            o.Target = target;
            break;
        }
        case "--emit-llvm": o.EmitLlvmOnly = true; break;
        case "--run": o.Run = true; break;
        case "--no-bounds": o.BoundsChecks = false; break;
        case "-Werror":
        case "--warn-as-error": o.WarnAsError = true; break;
        case "--runtime":
        {
            if (!TryTakeValue(args, ref i, a, out var runtime))
            {
                return 2;
            }

            o.Runtime = runtime;
            break;
        }
        case "--llc":
        {
            if (!TryTakeValue(args, ref i, a, out var llc))
            {
                return 2;
            }

            o.Llc = llc;
            break;
        }
        case "--cc":
        {
            if (!TryTakeValue(args, ref i, a, out var cc))
            {
                return 2;
            }

            o.Cc = cc;
            break;
        }
        case "--clang":
        {
            if (!TryTakeValue(args, ref i, a, out var clang))
            {
                return 2;
            }

            o.Clang = clang;
            break;
        }
        case "--frontend":
        {
            if (!TryTakeValue(args, ref i, a, out var frontend))
            {
                return 2;
            }

            if (frontend is not ("handwritten" or "roslyn"))
            {
                Console.Error.WriteLine("error: --frontend must be handwritten or roslyn");
                return 2;
            }

            o.Frontend = frontend;
            break;
        }
        case "-h":
        case "--help":
            Console.WriteLine(
                "usage: arcsharp <input.cs> [input2.cs ...] [-o name] [--target windows|host] [--frontend handwritten|roslyn] [--emit-llvm] [--run] [--no-bounds] [-Werror] [--runtime path] [--llc name] [--cc name] [--clang path]");
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

static bool TryTakeValue(string[] args, ref int i, string option, out string value)
{
    if (i + 1 >= args.Length)
    {
        Console.Error.WriteLine($"error: option {option} requires a value");
        value = "";
        return false;
    }

    i++;
    value = args[i];
    return true;
}
