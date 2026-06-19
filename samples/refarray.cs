class Box { public int v; }
class Program {
    static void Main() {
        Box[] bs = new Box[3];
        for (int i = 0; i < bs.Length; i = i + 1) {
            Box b = new Box();
            b.v = i * i;
            bs[i] = b;
        }
        int sum = 0;
        for (int i = 0; i < bs.Length; i = i + 1) { sum = sum + bs[i].v; }
        Console.WriteLine("refarray sum=" + sum);
    }
}
