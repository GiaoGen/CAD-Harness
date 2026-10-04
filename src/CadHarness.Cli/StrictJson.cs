using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CadHarness.Cli
{
    // JSON parser rejects duplicate keys, trailing commas/text, comments, nonfinite numbers,
    // invalid escapes, and permissive JavaScript syntax before any intent deserialization.
    internal sealed class StrictJson
    {
        private readonly string source;
        private int position;
        private StrictJson(string source) { this.source = source; }
        internal static object Parse(string source)
        {
            if (source == null || source.Length > 1048576) throw Invalid();
            StrictJson parser = new StrictJson(source);
            object value = parser.Value(0);
            parser.White();
            if (parser.position != source.Length) throw Invalid();
            return value;
        }
        private object Value(int depth)
        {
            if (depth > 32) throw Invalid();
            White();
            if (position >= source.Length) throw Invalid();
            char c = source[position];
            if (c == '{') return Object(depth+1);
            if (c == '[') return Array(depth+1);
            if (c == '"') return String();
            if (c == 't') { Literal("true"); return true; }
            if (c == 'f') { Literal("false"); return false; }
            if (c == 'n') { Literal("null"); return null; }
            if (c == '-' || Digit(c)) return Number();
            throw Invalid();
        }
        private Dictionary<string, object> Object(int depth)
        {
            position++;
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            White(); if (Take('}')) return result;
            while (true)
            {
                White(); if (position >= source.Length || source[position] != '"') throw Invalid();
                string key = String(); White(); if (!Take(':') || result.ContainsKey(key)) throw Invalid();
                result.Add(key, Value(depth)); White();
                if (Take('}')) return result;
                if (!Take(',')) throw Invalid();
            }
        }
        private List<object> Array(int depth)
        {
            position++;
            List<object> result = new List<object>();
            White(); if (Take(']')) return result;
            while (true)
            {
                result.Add(Value(depth)); White();
                if (Take(']')) return result;
                if (!Take(',')) throw Invalid();
            }
        }
        private string String()
        {
            position++;
            StringBuilder result = new StringBuilder();
            while (position < source.Length)
            {
                char c = source[position++];
                if (c == '"') return result.ToString();
                if (c < 32) throw Invalid();
                if (c != '\\') { result.Append(c); continue; }
                if (position >= source.Length) throw Invalid();
                char escape = source[position++];
                switch (escape)
                {
                    case '"': case '\\': case '/': result.Append(escape); break;
                    case 'b': result.Append('\b'); break;
                    case 'f': result.Append('\f'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case 'u':
                        if (position+4 > source.Length) throw Invalid();
                        ushort code;
                        if (!UInt16.TryParse(source.Substring(position,4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code)) throw Invalid();
                        result.Append((char)code); position += 4; break;
                    default: throw Invalid();
                }
            }
            throw Invalid();
        }
        private object Number()
        {
            int start = position;
            Take('-');
            if (!Take('0'))
            {
                if (position >= source.Length || source[position] < '1' || source[position] > '9') throw Invalid();
                while (position < source.Length && Digit(source[position])) position++;
            }
            bool floating = false;
            if (Take('.')) { floating = true; Digits(); }
            if (position < source.Length && (source[position] == 'e' || source[position] == 'E'))
            {
                floating = true; position++; if (!Take('+')) Take('-'); Digits();
            }
            string token = source.Substring(start,position-start);
            long integer;
            if (!floating && Int64.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out integer)) return integer;
            double number;
            if (!Double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || Double.IsNaN(number) || Double.IsInfinity(number)) throw Invalid();
            return number;
        }
        private void Digits()
        {
            int start = position;
            while (position < source.Length && Digit(source[position])) position++;
            if (start == position) throw Invalid();
        }
        private void Literal(string value)
        {
            if (position+value.Length > source.Length || source.Substring(position,value.Length) != value) throw Invalid();
            position += value.Length;
        }
        private void White() { while (position < source.Length && (source[position]==' ' || source[position]=='\t' || source[position]=='\r' || source[position]=='\n')) position++; }
        private bool Take(char c) { if (position < source.Length && source[position] == c) { position++; return true; } return false; }
        private static bool Digit(char c) { return c >= '0' && c <= '9'; }
        private static PlannerFailure Invalid() { return new PlannerFailure("PLANNER_JSON_INVALID", "Planner output must be one strict JSON document with unique keys.", true, false); }
    }
}
