using System;

namespace Scopa.Editor
{
    public static class StringUtility 
    {
        public static bool ContainsIgnoreCase(this string source, string str)
        {
            var comp = StringComparison.OrdinalIgnoreCase;
            return source?.IndexOf(str, comp) >= 0;
        }
    }
}