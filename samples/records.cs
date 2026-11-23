enum Tier
{
    Basic,
    Gold = 5
}

record Point(int X, int Y);

record Label(string Text, Point Origin);

record Person(string Name, int Age)
{
    public string City { get; init; } = "none";
}

record Student(string Name, int Age, int Grade) : Person(Name, Age);

record Member(string Name, Tier Level, bool Active, long Id, char Mark);

sealed record Empty;

class Plain
{
    public int Value = 1;
}

record Holder(Plain Item, int Count);

static class Program
{
    static string Describe(Point point)
    {
        if (point is (0, 0))
        {
            return "origin";
        }

        if (point is { X: > 0 } && point is (_, var y))
        {
            return "right " + y;
        }

        return "other";
    }

    static void Main()
    {
        Point a = new Point(1, 2);
        Point b = new Point(1, 2);
        Point c = new Point(3, 4);
        bool equality = a == b && a != c && a.Equals(b) && !a.Equals(c) && !(a == null) && a != null;
        bool sameHash = a.GetHashCode() == b.GetHashCode() && a.GetHashCode() != c.GetHashCode();

        Point moved = a with { X = 10 };
        bool copying = moved.X == 10 && moved.Y == 2 && a.X == 1 && moved != a;
        Point same = a with { };
        bool cloned = same == a && (object)same != (object)a;

        bool text = a.ToString() == "Point { X = 1, Y = 2 }" && ("" + c) == "Point { X = 3, Y = 4 }";
        Console.WriteLine(a);

        var (px, py) = c;
        Point origin = new Point(0, 0);
        bool deconstruct = px == 3 && py == 4 && Describe(origin) == "origin" && Describe(a) == "right 2"
            && Describe(new Point(-1, 5)) == "other";

        Label first = new Label("start", a);
        Label second = new Label("start", new Point(1, 2));
        Label third = new Label("start", c);
        bool nested = first == second && first != third && first.GetHashCode() == second.GetHashCode()
            && first.ToString() == "Label { Text = start, Origin = Point { X = 1, Y = 2 } }";

        Person person = new Person("Ada", 36);
        Person grown = person with { Age = 37, City = "London" };
        bool members = person.City == "none" && grown.City == "London" && grown.Age == 37
            && person.ToString() == "Person { Name = Ada, Age = 36, City = none }";

        Student student = new Student("Bo", 20, 3);
        Student sibling = new Student("Bo", 20, 3);
        Student other = new Student("Bo", 20, 4);
        Person asPerson = new Person("Bo", 20);
        Person viaBase = student;
        bool derived = student == sibling && student != other && !student.Equals(asPerson)
            && !asPerson.Equals(student) && viaBase.Equals(sibling) && !viaBase.Equals(other)
            && student.GetHashCode() == sibling.GetHashCode()
            && student.ToString() == "Student { Name = Bo, Age = 20, City = none, Grade = 3 }";
        Student promoted = student with { Grade = 4, Name = "Cy" };
        bool derivedWith = promoted.Grade == 4 && promoted.Name == "Cy" && promoted.Age == 20 && student.Grade == 3;

        Member gold = new Member("M", Tier.Gold, true, 7, 'x');
        Member basic = new Member("M", Tier.Basic, true, 7, 'x');
        bool values = gold != basic && gold == (basic with { Level = Tier.Gold })
            && gold.ToString() == "Member { Name = M, Level = Gold, Active = True, Id = 7, Mark = x }"
            && basic.ToString() == "Member { Name = M, Level = Basic, Active = True, Id = 7, Mark = x }";

        Plain shared = new Plain();
        Holder left = new Holder(shared, 1);
        Holder right = new Holder(shared, 1);
        Holder apart = new Holder(new Plain(), 1);
        bool references = left == right && left != apart;

        Empty none = new Empty();
        bool empty = none == new Empty() && none.ToString() == "Empty { }";

        Point missing = null;
        bool nulls = missing == null && !(a == missing) && a != missing && null == missing;

        if (equality && sameHash && copying && cloned && text && deconstruct && nested && members
            && derived && derivedWith && values && references && empty && nulls)
        {
            Console.WriteLine("records PASS");
        }
    }
}
