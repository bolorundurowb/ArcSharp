using System;

class Program
{
    // 50 * 8000 live nodes, held until Main returns. churn.cs keeps the same set.
    static void Main()
    {
        int heldBuckets = 50;
        int perBucket = 8000;
        Bucket[] held = new Bucket[heldBuckets];
        long checksum = 0;
        for (int b = 0; b < heldBuckets; b = b + 1)
        {
            long part;
            held[b] = Fill(perBucket, out part);
            checksum = checksum + part;
        }

        checksum = checksum + Sum(held);
        Console.WriteLine("workload=retained");
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
