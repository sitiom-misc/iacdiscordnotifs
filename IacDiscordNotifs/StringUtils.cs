using System;
using System.Linq;
using System.Collections.Generic;

namespace IacDiscordNotifs
{
    internal class StringUtils
    {
        // Split the string into chunks of the specified maximum size.
        // Only split between whitespaces if possible, and trim the start and end of the chunks.
        public static List<string> Split(string str, int chunkSize)
        {
            List<string> result = new List<string>();

            int index = 0;
            while (index < str.Length)
            {
                int length = Math.Min(chunkSize, str.Length - index);
                string chunk = str.Substring(index, length);

                if (chunk.Length == chunkSize && str.ElementAtOrDefault(index + length) != ' ' && str.ElementAtOrDefault(index + length) != '\0')
                {
                    int lastSpace = chunk.LastIndexOf(' ');
                    if (lastSpace != -1)
                    {
                        length = lastSpace + 1;
                    }
                }

                result.Add(str.Substring(index, length).Trim());
                index += length;
            }

            return result;
        }

    }
}
