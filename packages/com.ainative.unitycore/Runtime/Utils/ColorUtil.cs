using UnityEngine;

namespace AiNative.UnityCore.Utils
{
    public static class ColorUtil
    {
        #region ColorStr

        public const string White = "FFFFFF";
        public const string Black = "000000";
        public const string Red = "FF0000";
        public const string Green = "00FF18";
        public const string Orange = "FF9400";
        public const string Gray = "808080";
        public const string Exception = "FF00BD";
        public const string Blue = "0099BC";

        #endregion

        public static string ToColor(this string str, string color)
        {
            if (string.IsNullOrEmpty(str))
            {
                return str;
            }

            return $"<color=#{color}>{str}</color>";
        }

        public static Color ParseHtmlString(string htmlString)
        {
            var htmlStr = htmlString.StartsWith("#") ? htmlString : $"#{htmlString}";
            ColorUtility.TryParseHtmlString(htmlStr, out var color);
            return color;
        }
    }
}
