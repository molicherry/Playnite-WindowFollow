using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SunshineWindowFollow
{
    /// <summary>JSON 值的类型。</summary>
    public enum JsonKind
    {
        Null,
        Boolean,
        Number,
        String,
        Object,
        Array
    }

    /// <summary>
    /// 只读 JSON 值。插件只需要解析 Sunshine 端点返回的小型状态对象，
    /// 为了避免引入 Newtonsoft 等外部依赖（Playnite 的序列化器又依赖宿主初始化），
    /// 这里实现了一个最小但完整的 JSON 解析器，请求与响应两端都不依赖任何 JSON 库。
    /// </summary>
    public sealed class JsonValue
    {
        private Dictionary<string, JsonValue> members;
        private List<JsonValue> items;

        public JsonKind Kind { get; private set; }

        public bool BooleanValue { get; private set; }

        public double NumberValue { get; private set; }

        public string StringValue { get; private set; }

        private JsonValue()
        {
        }

        #region 构造

        internal static JsonValue FromNull()
        {
            return new JsonValue { Kind = JsonKind.Null };
        }

        internal static JsonValue FromBoolean(bool value)
        {
            return new JsonValue { Kind = JsonKind.Boolean, BooleanValue = value };
        }

        internal static JsonValue FromNumber(double value)
        {
            return new JsonValue { Kind = JsonKind.Number, NumberValue = value };
        }

        internal static JsonValue FromString(string value)
        {
            return new JsonValue { Kind = JsonKind.String, StringValue = value };
        }

        internal static JsonValue FromObject(Dictionary<string, JsonValue> value)
        {
            return new JsonValue { Kind = JsonKind.Object, members = value };
        }

        internal static JsonValue FromArray(List<JsonValue> value)
        {
            return new JsonValue { Kind = JsonKind.Array, items = value };
        }

        #endregion

        #region 读取帮助方法

        /// <summary>对象模式下按名称取值；不存在返回 null。</summary>
        public JsonValue Get(string name)
        {
            if (Kind != JsonKind.Object || members == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            JsonValue value;
            return members.TryGetValue(name, out value) ? value : null;
        }

        /// <summary>对象模式下取数组元素；非数组或越界返回 null。</summary>
        public JsonValue GetAt(int index)
        {
            if (Kind != JsonKind.Array || items == null || index < 0 || index >= items.Count)
            {
                return null;
            }

            return items[index];
        }

        public int Count
        {
            get
            {
                if (Kind == JsonKind.Array && items != null)
                {
                    return items.Count;
                }

                if (Kind == JsonKind.Object && members != null)
                {
                    return members.Count;
                }

                return 0;
            }
        }

        public string GetString(string name)
        {
            var value = Get(name);
            return value != null && value.Kind == JsonKind.String ? value.StringValue : null;
        }

        public bool GetBool(string name)
        {
            var value = Get(name);
            if (value == null)
            {
                return false;
            }

            if (value.Kind == JsonKind.Boolean)
            {
                return value.BooleanValue;
            }

            return value.Kind == JsonKind.Number && Math.Abs(value.NumberValue) > double.Epsilon;
        }

        public int GetInt(string name)
        {
            var value = Get(name);
            if (value == null)
            {
                return 0;
            }

            if (value.Kind == JsonKind.Number)
            {
                return (int)Math.Round(value.NumberValue, MidpointRounding.AwayFromZero);
            }

            int parsed;
            if (value.Kind == JsonKind.String &&
                int.TryParse(value.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }

            return 0;
        }

        public JsonValue GetObject(string name)
        {
            var value = Get(name);
            return value != null && value.Kind == JsonKind.Object ? value : null;
        }

        /// <summary>把简单标量转成展示用字符串（对象/数组只返回类型说明）。</summary>
        public string ToDisplayString()
        {
            switch (Kind)
            {
                case JsonKind.Null:
                    return string.Empty;
                case JsonKind.Boolean:
                    return BooleanValue ? "true" : "false";
                case JsonKind.Number:
                    return NumberValue.ToString(CultureInfo.InvariantCulture);
                case JsonKind.String:
                    return StringValue;
                case JsonKind.Object:
                    return "<object>";
                case JsonKind.Array:
                    return "<array>";
                default:
                    return string.Empty;
            }
        }

        #endregion
    }

    /// <summary>
    /// 最小 JSON 解析器（只实现解析，不实现序列化）。
    /// </summary>
    public static class JsonLite
    {
        private const int MaxDepth = 64;

        public static bool TryParse(string text, out JsonValue value)
        {
            try
            {
                value = Parse(text);
                return true;
            }
            catch (Exception)
            {
                value = null;
                return false;
            }
        }

        public static JsonValue Parse(string text)
        {
            if (text == null)
            {
                throw new FormatException("JSON 文本为空");
            }

            var parser = new JsonParser(text);
            return parser.ParseDocument();
        }

        private sealed class JsonParser
        {
            private readonly string text;
            private int position;

            public JsonParser(string text)
            {
                this.text = text;
            }

            public JsonValue ParseDocument()
            {
                if (text.Length > 0 && text[0] == '\uFEFF')
                {
                    position = 1;
                }

                SkipWhitespace();
                var value = ParseValue(0);
                SkipWhitespace();
                if (position < text.Length)
                {
                    throw new FormatException("JSON 结尾存在多余内容（位置 " + position + "）");
                }

                return value;
            }

            private JsonValue ParseValue(int depth)
            {
                if (depth > MaxDepth)
                {
                    throw new FormatException("JSON 嵌套层级过深");
                }

                SkipWhitespace();
                if (position >= text.Length)
                {
                    throw new FormatException("JSON 意外结束");
                }

                var current = text[position];
                switch (current)
                {
                    case '{':
                        return ParseObject(depth);
                    case '[':
                        return ParseArray(depth);
                    case '"':
                        return JsonValue.FromString(ParseString());
                    case 't':
                        ExpectLiteral("true");
                        return JsonValue.FromBoolean(true);
                    case 'f':
                        ExpectLiteral("false");
                        return JsonValue.FromBoolean(false);
                    case 'n':
                        ExpectLiteral("null");
                        return JsonValue.FromNull();
                    default:
                        if (current == '-' || (current >= '0' && current <= '9'))
                        {
                            return ParseNumber();
                        }

                        throw new FormatException("JSON 中出现非法字符：" + current + "（位置 " + position + "）");
                }
            }

            private JsonValue ParseObject(int depth)
            {
                position++; // '{'
                var members = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                SkipWhitespace();
                if (Peek() == '}')
                {
                    position++;
                    return JsonValue.FromObject(members);
                }

                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"')
                    {
                        throw new FormatException("JSON 对象的键必须是字符串（位置 " + position + "）");
                    }

                    var name = ParseString();
                    SkipWhitespace();
                    if (Peek() != ':')
                    {
                        throw new FormatException("JSON 对象缺少冒号（位置 " + position + "）");
                    }

                    position++; // ':'
                    var value = ParseValue(depth + 1);
                    members[name] = value;
                    SkipWhitespace();

                    var separator = Next();
                    if (separator == '}')
                    {
                        return JsonValue.FromObject(members);
                    }

                    if (separator != ',')
                    {
                        throw new FormatException("JSON 对象缺少逗号或右花括号（位置 " + position + "）");
                    }
                }
            }

            private JsonValue ParseArray(int depth)
            {
                position++; // '['
                var items = new List<JsonValue>();
                SkipWhitespace();
                if (Peek() == ']')
                {
                    position++;
                    return JsonValue.FromArray(items);
                }

                while (true)
                {
                    items.Add(ParseValue(depth + 1));
                    SkipWhitespace();

                    var separator = Next();
                    if (separator == ']')
                    {
                        return JsonValue.FromArray(items);
                    }

                    if (separator != ',')
                    {
                        throw new FormatException("JSON 数组缺少逗号或右方括号（位置 " + position + "）");
                    }
                }
            }

            private string ParseString()
            {
                position++; // '"'
                var builder = new StringBuilder();
                while (position < text.Length)
                {
                    var current = text[position++];
                    if (current == '"')
                    {
                        return builder.ToString();
                    }

                    if (current != '\\')
                    {
                        builder.Append(current);
                        continue;
                    }

                    if (position >= text.Length)
                    {
                        throw new FormatException("JSON 字符串中的转义序列不完整");
                    }

                    var escaped = text[position++];
                    switch (escaped)
                    {
                        case '"':
                            builder.Append('"');
                            break;
                        case '\\':
                            builder.Append('\\');
                            break;
                        case '/':
                            builder.Append('/');
                            break;
                        case 'b':
                            builder.Append('\b');
                            break;
                        case 'f':
                            builder.Append('\f');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'u':
                            if (position + 4 > text.Length)
                            {
                                throw new FormatException("JSON \\u 转义序列不完整");
                            }

                            int code;
                            if (!int.TryParse(text.Substring(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            {
                                throw new FormatException("JSON \\u 转义序列不是合法的十六进制");
                            }

                            builder.Append((char)code);
                            position += 4;
                            break;
                        default:
                            throw new FormatException("JSON 字符串中存在不支持的转义字符：" + escaped);
                    }
                }

                throw new FormatException("JSON 字符串未闭合");
            }

            private JsonValue ParseNumber()
            {
                var start = position;
                if (Peek() == '-')
                {
                    position++;
                }

                while (position < text.Length && text[position] >= '0' && text[position] <= '9')
                {
                    position++;
                }

                if (position < text.Length && text[position] == '.')
                {
                    position++;
                    while (position < text.Length && text[position] >= '0' && text[position] <= '9')
                    {
                        position++;
                    }
                }

                if (position < text.Length && (text[position] == 'e' || text[position] == 'E'))
                {
                    position++;
                    if (position < text.Length && (text[position] == '+' || text[position] == '-'))
                    {
                        position++;
                    }

                    while (position < text.Length && text[position] >= '0' && text[position] <= '9')
                    {
                        position++;
                    }
                }

                var raw = text.Substring(start, position - start);
                double number;
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    throw new FormatException("JSON 数字无效：" + raw);
                }

                return JsonValue.FromNumber(number);
            }

            private void ExpectLiteral(string literal)
            {
                if (position + literal.Length > text.Length ||
                    string.CompareOrdinal(text, position, literal, 0, literal.Length) != 0)
                {
                    throw new FormatException("JSON 中期望字面量 " + literal + "（位置 " + position + "）");
                }

                position += literal.Length;
            }

            private char Peek()
            {
                return position < text.Length ? text[position] : '\0';
            }

            private char Next()
            {
                if (position >= text.Length)
                {
                    throw new FormatException("JSON 意外结束");
                }

                return text[position++];
            }

            private void SkipWhitespace()
            {
                while (position < text.Length)
                {
                    var current = text[position];
                    if (current == ' ' || current == '\t' || current == '\r' || current == '\n')
                    {
                        position++;
                        continue;
                    }

                    break;
                }
            }
        }
    }
}
