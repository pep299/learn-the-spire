using System.Text;
using System.Text.RegularExpressions;

namespace Godot;

public static class StringExtensions
{
    public static string ToSnakeCase(this string text)
    {
        string words = Regex.Replace(text, "([A-Z])([A-Z][a-z])", "$1_$2");
        words = Regex.Replace(words, "([a-z0-9])([A-Z])", "$1_$2");
        return words.Replace(' ', '_').Replace('-', '_').ToLowerInvariant();
    }

    public static string PathJoin(this string path, string file)
    {
        if (path.Length == 0) return file;
        return path[^1] == '/' || file.Length > 0 && file[0] == '/'
            ? path + file : path + "/" + file;
    }

    internal static string HexEncode(this byte b)
    {
        return b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string HexEncode(this byte[] bytes)
    {
        StringBuilder builder = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            builder.Append(b.HexEncode());
        }

        return builder.ToString();
    }
}
