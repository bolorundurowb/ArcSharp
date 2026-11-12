// using statements: Dispose() runs at scope exit, then the resource is released.
// Covers the declaration form, the expression form, virtual and interface
// dispatch, a null resource, and cleanup on the return path.
class Count { public static int disposals; }

class Logger {
    public string name;
    public Logger(string n) { name = n; }
    public void Dispose() {
        Console.WriteLine("dispose " + name);
        Count.disposals = Count.disposals + 1;
    }
}
class File {
    public virtual void Dispose() { Console.WriteLine("file close"); }
}
class TmpFile : File {
    public override void Dispose() { Console.WriteLine("tmp delete"); }
}
interface IResource {
    void Dispose();
}
class Db : IResource {
    public void Dispose() { Console.WriteLine("db closed"); }
}
class Program {
    static string Scoped() {
        using (var x = new Logger("x")) {
            return "returned";   // cleanup still runs on the return path
        }
    }
    static void Main() {
        using (var a = new Logger("a")) {
            Console.WriteLine("body a");
        }
        using (File f = new TmpFile()) {
            Console.WriteLine("body f");      // virtual dispatch
        }
        using (IResource r = new Db()) {
            Console.WriteLine("body r");      // interface dispatch
        }
        using (new Logger("c")) {
            Console.WriteLine("body c");      // expression form
        }
        using (Logger n = null) {
            Console.WriteLine("body null");   // null resource: Dispose is skipped
        }
        string s = Scoped();
        Console.WriteLine(s);
        Console.WriteLine("disposals=" + Count.disposals);
    }
}
