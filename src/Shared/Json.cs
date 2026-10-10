using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ClaudeGuardian
{
    /// <summary>
    /// Minimal JSON parser and writer, with no dependencies.
    ///
    /// .NET Framework does not include System.Text.Json; JavaScriptSerializer has length
    /// limits and rewrites non-ASCII characters, which would mangle settings.json.
    ///
    /// Objects → Dictionary&lt;string, object&gt; (key order is preserved),
    /// arrays → List&lt;object&gt;, numbers → long or double, plus string, bool and null.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var parser = new Parser(text);
            parser.SkipWhitespace();
            var value = parser.ReadValue();
            parser.SkipWhitespace();
            if (!parser.AtEnd) throw new FormatException("Unexpected data after JSON value");
            return value;
        }

        /// <summary>Like Parse, but returns null if the text is not a valid JSON object.</summary>
        public static Dictionary<string, object> TryParseObject(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try { return Parse(text) as Dictionary<string, object>; }
            catch (FormatException) { return null; }
        }

        public static string Serialize(object value, bool indented = false)
        {
            var sb = new StringBuilder();
            Write(sb, value, indented, 0);
            return sb.ToString();
        }

        // MARK: - Field access

        public static string GetString(this Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.TryGetValue(key, out var value) || value == null) return null;
            if (value is string s) return s;
            if (value is bool b) return b ? "true" : "false";
            if (value is long l) return l.ToString(CultureInfo.InvariantCulture);
            if (value is double d) return d.ToString("R", CultureInfo.InvariantCulture);
            return null;
        }

        public static long GetLong(this Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.TryGetValue(key, out var value) || value == null) return 0;
            if (value is long l) return l;
            if (value is double d) return (long)d;
            if (value is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            return 0;
        }

        public static bool GetBool(this Dictionary<string, object> obj, string key)
        {
            return obj != null && obj.TryGetValue(key, out var value) && value is bool b && b;
        }

        // MARK: - Writer

        private static void Write(StringBuilder sb, object value, bool indented, int depth)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    return;
                case string s:
                    WriteString(sb, s);
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    return;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    return;
                case double d:
                    sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case IDictionary<string, object> dict:
                    if (dict.Count == 0) { sb.Append("{}"); return; }
                    sb.Append('{');
                    var first = true;
                    foreach (var pair in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        NewLine(sb, indented, depth + 1);
                        WriteString(sb, pair.Key);
                        sb.Append(indented ? ": " : ":");
                        Write(sb, pair.Value, indented, depth + 1);
                    }
                    NewLine(sb, indented, depth);
                    sb.Append('}');
                    return;
                case IEnumerable list:
                    var items = new List<object>();
                    foreach (var item in list) items.Add(item);
                    if (items.Count == 0) { sb.Append("[]"); return; }
                    sb.Append('[');
                    for (var n = 0; n < items.Count; n++)
                    {
                        if (n > 0) sb.Append(',');
                        NewLine(sb, indented, depth + 1);
                        Write(sb, items[n], indented, depth + 1);
                    }
                    NewLine(sb, indented, depth);
                    sb.Append(']');
                    return;
                default:
                    WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
            }
        }

        private static void NewLine(StringBuilder sb, bool indented, int depth)
        {
            if (!indented) return;
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // MARK: - Parser

        private sealed class Parser
        {
            private readonly string _text;
            private int _pos;

            public Parser(string text) { _text = text ?? ""; }

            public bool AtEnd => _pos >= _text.Length;

            public void SkipWhitespace()
            {
                while (_pos < _text.Length && (_text[_pos] == ' ' || _text[_pos] == '\t' || _text[_pos] == '\n' || _text[_pos] == '\r' || _text[_pos] == '﻿'))
                    _pos++;
            }

            public object ReadValue()
            {
                if (AtEnd) throw new FormatException("Unexpected end of JSON");
                var c = _text[_pos];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw new FormatException("Unexpected character '" + c + "' at " + _pos);
                }
            }

            private Dictionary<string, object> ReadObject()
            {
                var result = new Dictionary<string, object>();
                _pos++; // {
                SkipWhitespace();
                if (Peek() == '}') { _pos++; return result; }
                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"') throw new FormatException("Expected property name at " + _pos);
                    var key = ReadString();
                    SkipWhitespace();
                    if (Peek() != ':') throw new FormatException("Expected ':' at " + _pos);
                    _pos++;
                    SkipWhitespace();
                    result[key] = ReadValue();
                    SkipWhitespace();
                    var next = Peek();
                    _pos++;
                    if (next == ',') continue;
                    if (next == '}') return result;
                    throw new FormatException("Expected ',' or '}' at " + (_pos - 1));
                }
            }

            private List<object> ReadArray()
            {
                var result = new List<object>();
                _pos++; // [
                SkipWhitespace();
                if (Peek() == ']') { _pos++; return result; }
                while (true)
                {
                    SkipWhitespace();
                    result.Add(ReadValue());
                    SkipWhitespace();
                    var next = Peek();
                    _pos++;
                    if (next == ',') continue;
                    if (next == ']') return result;
                    throw new FormatException("Expected ',' or ']' at " + (_pos - 1));
                }
            }

            private string ReadString()
            {
                _pos++; // "
                var sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd) throw new FormatException("Unterminated string");
                    var c = _text[_pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (AtEnd) throw new FormatException("Unterminated escape");
                    var e = _text[_pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_pos + 4 > _text.Length) throw new FormatException("Bad unicode escape");
                            sb.Append((char)int.Parse(_text.Substring(_pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _pos += 4;
                            break;
                        default: throw new FormatException("Bad escape '\\" + e + "'");
                    }
                }
            }

            private object ReadNumber()
            {
                var start = _pos;
                if (Peek() == '-') _pos++;
                while (!AtEnd && "0123456789+-.eE".IndexOf(_text[_pos]) >= 0) _pos++;
                var token = _text.Substring(start, _pos - start);
                if (long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                throw new FormatException("Bad number '" + token + "'");
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0)
                    throw new FormatException("Expected '" + word + "' at " + _pos);
                _pos += word.Length;
            }

            private char Peek() => AtEnd ? '\0' : _text[_pos];
        }
    }
}
