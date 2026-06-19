class Counter { public static int count; }
class Program {
    static void Bump() { Counter.count = Counter.count + 1; }
    static int Main() {
        Bump(); Bump(); Bump();
        Console.WriteLine("count=" + Counter.count);
        return Counter.count;
    }
}
