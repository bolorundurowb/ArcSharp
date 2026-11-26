using System.Text;

class Program
{
    static int Main()
    {
        string text = "\u3000A\U0001F600B\t";
        string trimmed = text.Trim();
        if (trimmed.Length != 4 || trimmed[1] != '\uD83D' || trimmed[2] != '\uDE00'
            || !trimmed.Substring(length: 2, startIndex: 1).Equals("\U0001F600")
            || !trimmed.Substring(3).Equals("B"))
        {
            return 1;
        }

        if (!trimmed.StartsWith("A") || !trimmed.EndsWith("B") || !trimmed.Contains('\uDE00')
            || !trimmed.Contains("\U0001F600") || trimmed.IndexOf("B") != 3
            || trimmed.IndexOf('x') != -1 || !string.IsNullOrEmpty(null)
            || !string.IsNullOrWhiteSpace("\u3000\t") || string.IsNullOrWhiteSpace(trimmed)
            || !string.Equals(trimmed.ToString(), string.Concat(str1: "B", str0: "A\U0001F600"))
            || !trimmed.Replace('A', 'B').Replace("\U0001F600", null).Equals("BB"))
        {
            return 2;
        }

        var builder = new StringBuilder(null);
        string snapshot = builder.Append('\uD83D').Append('\uDE00').ToString();
        builder.Append(42).Append(true).Append(1.25m).AppendLine();
        if (snapshot.Length != 2 || !snapshot.Equals("\U0001F600")
            || !builder.ToString().StartsWith("\U0001F60042True1.25"))
        {
            return 3;
        }

        if (!builder.Clear().AppendLine("reset").ToString().Equals("reset" + Environment.NewLine)
            || !((sbyte)-1).ToString("X").Equals("FF")
            || !18446744073709551615UL.ToString().Equals("18446744073709551615"))
        {
            return 4;
        }

        string missing = null;
        int value = 42;
        string formatted = $"{{{value,5:D3}}}|{1.25,-6:F2}|{missing,2}|{false}|{'!'}";
        if (!formatted.Equals("{  042}|1.25  |  |False|!"))
        {
            Console.WriteLine(formatted);
            return 5;
        }

        string pattern = "{{{1,5:D3}}}|{0,-6:F2}|{2,2}|{3}|{4}|{1:X}";
        string composite = string.Format(pattern, 1.25, value, missing, false, '!');
        if (!composite.Equals("{  042}|1.25  |  |False|!|2A")
            || !string.Format(args: new object[] { value, "a" }, format: "{1}{0}").Equals("a42"))
        {
            Console.WriteLine(composite);
            return 7;
        }

        int errors = 0;
        try { Console.WriteLine(trimmed[4]); }
        catch (IndexOutOfRangeException) { errors++; }
        try { Console.WriteLine(trimmed.Substring(-1)); }
        catch (ArgumentOutOfRangeException) { errors++; }
        try { Console.WriteLine(trimmed.Contains(null)); }
        catch (ArgumentNullException) { errors++; }
        try { Console.WriteLine(value.ToString("Q")); }
        catch (FormatException) { errors++; }
        try { Console.WriteLine(missing.ToString()); }
        catch (NullReferenceException) { errors++; }
        try { Console.WriteLine(string.Format("{1}", value)); }
        catch (FormatException) { errors++; }
        try { Console.WriteLine(string.Format("{0} {1:Q}", value, value)); }
        catch (FormatException) { errors++; }
        if (errors != 7)
        {
            return 6;
        }

        Console.WriteLine("string library PASS");
        return 0;
    }
}
