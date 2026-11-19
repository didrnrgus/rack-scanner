using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RackScanner.Shared.IO
{
    /// <summary>
    /// 의존성 없는 최소 JSON 작성기. (Shared 는 UnityEngine/외부 패키지를 참조하지 않는다.)
    /// </summary>
    public sealed class JsonWriter
    {
        readonly StringBuilder _sb = new StringBuilder();
        readonly Stack<bool> _first = new Stack<bool>();
        int _indent;
        bool _afterKey;

        public JsonWriter BeginObject() { Value(); _sb.Append('{'); Push(); return this; }
        public JsonWriter EndObject() { Pop('}'); return this; }
        public JsonWriter BeginArray() { Value(); _sb.Append('['); Push(); return this; }
        public JsonWriter EndArray() { Pop(']'); return this; }

        public JsonWriter Key(string k)
        {
            Comma();
            NewLine();
            WriteString(k);
            _sb.Append(": ");
            _afterKey = true;
            return this;
        }

        public JsonWriter Str(string v) { Value(); if (v == null) _sb.Append("null"); else WriteString(v); return this; }
        public JsonWriter Num(double v) { Value(); _sb.Append(double.IsFinite(v) ? Math.Round(v, 6).ToString("R", CultureInfo.InvariantCulture) : "null"); return this; }
        public JsonWriter Num(long v) { Value(); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
        public JsonWriter Bool(bool v) { Value(); _sb.Append(v ? "true" : "false"); return this; }
        public JsonWriter Null() { Value(); _sb.Append("null"); return this; }

        public JsonWriter Prop(string k, string v) => Key(k).Str(v);
        public JsonWriter Prop(string k, double v) => Key(k).Num(v);
        public JsonWriter Prop(string k, long v) => Key(k).Num(v);
        public JsonWriter Prop(string k, bool v) => Key(k).Bool(v);

        public JsonWriter NumArray(IEnumerable<double> vs)
        {
            Value();
            _sb.Append('[');
            bool first = true;
            foreach (var v in vs)
            {
                if (!first) _sb.Append(", ");
                first = false;
                _sb.Append(Math.Round(v, 6).ToString("R", CultureInfo.InvariantCulture));
            }
            _sb.Append(']');
            return this;
        }

        public override string ToString() => _sb.ToString();

        void Push() { _first.Push(true); _indent++; }
        void Pop(char c)
        {
            bool empty = _first.Pop();
            _indent--;
            if (!empty) NewLine();
            _sb.Append(c);
        }

        void Value()
        {
            if (_afterKey) { _afterKey = false; return; }
            if (_first.Count > 0) { Comma(); NewLine(); }
        }

        void Comma()
        {
            if (_first.Count == 0) return;
            if (_first.Peek()) { _first.Pop(); _first.Push(false); }
            else _sb.Append(',');
        }

        void NewLine() { _sb.Append('\n').Append(' ', _indent * 2); }

        void WriteString(string s)
        {
            _sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) _sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }
    }

    /// <summary>
    /// 최소 JSON 파서. 객체 → Dictionary&lt;string, object&gt;, 배열 → List&lt;object&gt;,
    /// 숫자 → double, 문자열 → string, true/false → bool, null → null.
    /// </summary>
    public static class JsonParser
    {
        public static object Parse(string json)
        {
            int i = 0;
            var v = ParseValue(json, ref i);
            SkipWs(json, ref i);
            if (i != json.Length) throw new FormatException($"trailing characters at {i}");
            return v;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (Match(s, ref i, "true")) return true;
            if (Match(s, ref i, "false")) return false;
            if (Match(s, ref i, "null")) return null;
            return ParseNumber(s, ref i);
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++;
            SkipWs(s, ref i);
            if (s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                var k = ParseString(s, ref i);
                SkipWs(s, ref i);
                Expect(s, ref i, ':');
                d[k] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                Expect(s, ref i, '}');
                return d;
            }
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var l = new List<object>();
            i++;
            SkipWs(s, ref i);
            if (s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                Expect(s, ref i, ']');
                return l;
            }
        }

        static string ParseString(string s, ref int i)
        {
            Expect(s, ref i, '"');
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
        }

        static double ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (start == i) throw new FormatException($"unexpected character '{s[i]}' at {i}");
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        static bool Match(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new FormatException($"expected '{c}' at {i}");
            i++;
        }

        static void SkipWs(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        // 편의 접근자
        public static double GetNumber(this Dictionary<string, object> d, string key, double fallback)
            => d != null && d.TryGetValue(key, out var v) && v is double x ? x : fallback;
        public static string GetString(this Dictionary<string, object> d, string key, string fallback = null)
            => d != null && d.TryGetValue(key, out var v) && v is string x ? x : fallback;
        public static Dictionary<string, object> GetObject(this Dictionary<string, object> d, string key)
            => d != null && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;
        public static List<object> GetArray(this Dictionary<string, object> d, string key)
            => d != null && d.TryGetValue(key, out var v) ? v as List<object> : null;
    }
}
