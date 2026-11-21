using System;

class Program
{
    // Same live set as retained.cs (50 * 8000 nodes). Each round builds another
    // 25 buckets and drops them on the next assignment, before the new round
    // allocates. The final Sum keeps the live set reachable until the end, so
    // a garbage collector cannot treat it as unused.
    static void Main()
    {
        int heldBuckets = 50;
        int perBucket = 8000;
        int batchBuckets = 25;
        int rounds = 5;
        Bucket[] held = new Bucket[heldBuckets];
        long checksum = 0;
        for (int b = 0; b < heldBuckets; b = b + 1)
        {
            long part;
            held[b] = Fill(perBucket, out part);
            checksum = checksum + part;
        }

        Bucket[] batch = null;
        for (int round = 0; round < rounds; round = round + 1)
        {
            batch = new Bucket[batchBuckets];
            for (int b = 0; b < batchBuckets; b = b + 1)
            {
                long part;
                batch[b] = Fill(perBucket, out part);
                checksum = checksum + part;
            }
        }

        batch = null;
        checksum = checksum + Sum(held);
        Console.WriteLine("workload=churn");
        Console.WriteLine("checksum=" + checksum);
    }

    static Bucket Fill(int count, out long sum)
    {
        sum = 0;
        Bucket bucket = new Bucket();
        bucket.items = new Node[count];
        for (int i = 0; i < count; i = i + 1)
        {
            Node node = new Node();
            node.value = i;
            bucket.items[i] = node;
            sum = sum + node.value;
        }

        return bucket;
    }

    static long Sum(Bucket[] buckets)
    {
        long total = 0;
        for (int b = 0; b < buckets.Length; b = b + 1)
        {
            Node[] items = buckets[b].items;
            for (int i = 0; i < items.Length; i = i + 1)
            {
                total = total + items[i].value;
            }
        }

        return total;
    }
}
