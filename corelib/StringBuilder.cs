namespace System.Text;

public sealed class StringBuilder
{
    private string _text;

    public StringBuilder()
    {
        _text = "";
    }

    public StringBuilder(string value)
    {
        _text = value ?? "";
    }

    public int Length => _text.Length;

    public StringBuilder Append(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _text = _text + value;
        }

        return this;
    }

    public StringBuilder Append(char value) => Append(value.ToString());
    public StringBuilder Append(bool value) => Append(value.ToString());
    public StringBuilder Append(sbyte value) => Append(value.ToString());
    public StringBuilder Append(byte value) => Append(value.ToString());
    public StringBuilder Append(short value) => Append(value.ToString());
    public StringBuilder Append(ushort value) => Append(value.ToString());
    public StringBuilder Append(int value) => Append(value.ToString());
    public StringBuilder Append(uint value) => Append(value.ToString());
    public StringBuilder Append(long value) => Append(value.ToString());
    public StringBuilder Append(ulong value) => Append(value.ToString());
    public StringBuilder Append(float value) => Append(value.ToString());
    public StringBuilder Append(double value) => Append(value.ToString());
    public StringBuilder Append(decimal value) => Append(value.ToString());

    public StringBuilder AppendLine() => Append(Environment.NewLine);
    public StringBuilder AppendLine(string value) => Append(value).AppendLine();

    public StringBuilder Clear()
    {
        _text = "";
        return this;
    }

    public override string ToString() => _text;
}
