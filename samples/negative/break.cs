// Expected: ARC3001 'break' is not supported.
class Program {
    static void Main() {
        int i = 0;
        while (i < 3) {
            i = i + 1;
            break;
        }
    }
}
