class Program {
    static long Fib(int n) {
        if (n < 2) return n;
        return Fib(n - 1) + Fib(n - 2);
    }
    static void Main() {
        int i = 0;
        while (i <= 10) {
            Console.Write("fib(");
            Console.Write(i);
            Console.Write(")=");
            Console.WriteLine(Fib(i));
            i = i + 1;
        }
    }
}
