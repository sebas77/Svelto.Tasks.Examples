using System.Text;

public static class FastConcatUtility
{
    static readonly StringBuilder _stringBuilder = new StringBuilder(256);

    public static string FastConcat<T>(this string str1, T str2)
    {
        lock (_stringBuilder)
        {
            _stringBuilder.Length = 0;

            _stringBuilder.Append(str1);
            _stringBuilder.Append(str2);

            return _stringBuilder.ToString();
        }
    }

    public static string FastConcat(this string str1, string str2, string str3)
    {
        lock (_stringBuilder)
        {
            _stringBuilder.Length = 0;

            _stringBuilder.Append(str1);
            _stringBuilder.Append(str2);
            _stringBuilder.Append(str3);

            return _stringBuilder.ToString();
        }
    }

    public static string FastConcat(this string str1, string str2, string str3, string str4)
    {
        lock (_stringBuilder)
        {
            _stringBuilder.Length = 0;

            _stringBuilder.Append(str1);
            _stringBuilder.Append(str2);
            _stringBuilder.Append(str3);
            _stringBuilder.Append(str4);


            return _stringBuilder.ToString();
        }
    }

    public static string FastConcat(this string str1, string str2, string str3, string str4, string str5)
    {
        lock (_stringBuilder)
        {
            _stringBuilder.Length = 0;

            _stringBuilder.Append(str1);
            _stringBuilder.Append(str2);
            _stringBuilder.Append(str3);
            _stringBuilder.Append(str4);
            _stringBuilder.Append(str5);

            return _stringBuilder.ToString();
        }
    }
}
