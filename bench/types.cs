// Nodes are independent. A linked chain would release recursively, and a long
// chain overflows the stack. The array destructor releases elements in a loop.
//
// Each bucket holds 8000 nodes so the reference array stays under the .NET
// large-object threshold (85,000 bytes). A bigger array would be collected on
// a different schedule from the nodes, and the comparison would be about that
// threshold rather than about ordinary objects.
class Node
{
    public int value;
}

class Bucket
{
    public Node[] items;
}
