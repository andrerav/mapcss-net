using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MapCss.Styling;

internal static class ExpressionEvaluator
{
    public static string Evaluate(string exprText, MapCssQuery query)
    {
        if (string.IsNullOrWhiteSpace(exprText)) return string.Empty;
        var p = new Parser(exprText, query);
        var val = p.ParseExpr();
        return val.AsString();
    }

    private class Parser
    {
        private readonly string _s;
        private int _i;
        private readonly MapCssQuery _query;

        public Parser(string s, MapCssQuery query)
        {
            _s = s;
            _i = 0;
            _query = query;
        }

        public EvalResult ParseExpr()
        {
            SkipWs();
            var res = ParseEquality();
            SkipWs();
            return res;
        }

        private EvalResult ParseEquality()
        {
            var left = ParsePrimary();
            SkipWs();
            if (Match("==") || Match("="))
            {
                var right = ParsePrimary();
                return EvalEq(left, right);
            }
            if (Match("!="))
            {
                var right = ParsePrimary();
                return EvalNotEq(left, right);
            }
            if (Match(">="))
            {
                var right = ParsePrimary();
                return EvalCompare(left, right, (a, b) => a >= b);
            }
            if (Match("<="))
            {
                var right = ParsePrimary();
                return EvalCompare(left, right, (a, b) => a <= b);
            }
            if (Match(">"))
            {
                var right = ParsePrimary();
                return EvalCompare(left, right, (a, b) => a > b);
            }
            if (Match("<"))
            {
                var right = ParsePrimary();
                return EvalCompare(left, right, (a, b) => a < b);
            }
            return left;
        }

        private EvalResult ParsePrimary()
        {
            SkipWs();
            if (_i >= _s.Length) return EvalResult.EmptyString;
            if (_s[_i] == '"' || _s[_i] == '\'')
            {
                return ParseString();
            }
            if (char.IsDigit(_s[_i]))
            {
                return ParseNumber();
            }
            if (char.IsLetter(_s[_i]) || _s[_i] == '_' )
            {
                var ident = ParseIdent();
                SkipWs();
                if (Peek() == '(')
                {
                    // function call
                    _i++; // skip '('
                    var args = new List<EvalResult>();
                    SkipWs();
                    if (Peek() != ')')
                    {
                        while (true)
                        {
                            var arg = ParseExpr();
                            args.Add(arg);
                            SkipWs();
                            if (Peek() == ',')
                            {
                                _i++; SkipWs();
                                continue;
                            }
                            break;
                        }
                    }
                    if (Peek() == ')') _i++;
                    return EvalFunction(ident, args);
                }
                else
                {
                    // bare identifier -> return as string
                    return EvalResult.FromString(ident);
                }
            }

            if (Peek() == '(')
            {
                _i++; // skip
                var inner = ParseExpr();
                if (Peek() == ')') _i++;
                return inner;
            }

            // fallback: read until punctuation or whitespace
            var sb = new StringBuilder();
            while (_i < _s.Length && !char.IsWhiteSpace(_s[_i]) && ",():".IndexOf(_s[_i]) < 0)
            {
                sb.Append(_s[_i++]);
            }
            return EvalResult.FromString(sb.ToString());
        }

        private EvalResult EvalFunction(string name, List<EvalResult> args)
        {
            name = name.ToLowerInvariant();
            switch (name)
            {
                case "tag":
                    if (args.Count == 0) return EvalResult.EmptyString;
                    var key = args[0].AsString();
                    if (_query?.Context?.Element?.Tags != null && _query.Context.Element.Tags.TryGetValue(key, out var v)) return EvalResult.FromString(v);
                    return EvalResult.EmptyString;
                case "concat":
                    var sb = new StringBuilder();
                    foreach (var a in args) sb.Append(a.AsString());
                    return EvalResult.FromString(sb.ToString());
                case "any":
                    foreach (var a in args)
                    {
                        var s = a.AsString();
                        if (!string.IsNullOrEmpty(s)) return EvalResult.FromString(s);
                    }
                    return EvalResult.EmptyString;
                case "split":
                    if (args.Count < 2) return EvalResult.EmptyList;
                    var sep = args[0].AsString();
                    var text = args[1].AsString();
                    var parts = text.Split(new[] { sep }, StringSplitOptions.None);
                    return EvalResult.FromList(parts);
                case "get":
                    if (args.Count < 2) return EvalResult.EmptyString;
                    var list = args[0];
                    var idx = (int)args[1].AsNumber();
                    if (list.IsList && idx >= 0 && idx < list.List.Count) return EvalResult.FromString(list.List[idx].AsString());
                    return EvalResult.EmptyString;
                case "count":
                    if (args.Count < 1) return EvalResult.FromNumber(0);
                    var l = args[0];
                    if (l.IsList) return EvalResult.FromNumber(l.List.Count);
                    return EvalResult.FromNumber(0);
                case "cond":
                    if (args.Count < 3) return EvalResult.EmptyString;
                    var condition = args[0];
                    var condBool = condition.AsBool();
                    return condBool ? args[1] : args[2];
                case "eval":
                    if (args.Count < 1) return EvalResult.EmptyString;
                    // eval should evaluate inner expression if it's textual containing functions; here arguments already evaluated
                    return EvalResult.FromString(args[0].AsString());
                case "has_tag_key":
                    if (args.Count < 1) return EvalResult.FromBool(false);
                    var k = args[0].AsString();
                    var has = _query?.Context?.Element?.Tags != null && _query.Context.Element.Tags.ContainsKey(k);
                    return EvalResult.FromBool(has);
                default:
                    // unknown function: return empty
                    return EvalResult.EmptyString;
            }
        }

        private bool Match(string s)
        {
            SkipWs();
            if (_s.Substring(_i).StartsWith(s, StringComparison.Ordinal))
            {
                _i += s.Length; return true;
            }
            return false;
        }

        private string ParseIdent()
        {
            var sb = new StringBuilder();
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == ':' || _s[_i] == '-'))
            {
                sb.Append(_s[_i++]);
            }
            return sb.ToString();
        }

        private EvalResult ParseString()
        {
            var quote = _s[_i++];
            var sb = new StringBuilder();
            while (_i < _s.Length)
            {
                var c = _s[_i++];
                if (c == quote) break;
                if (c == '\\' && _i < _s.Length)
                {
                    var n = _s[_i++];
                    sb.Append(n);
                }
                else sb.Append(c);
            }
            return EvalResult.FromString(sb.ToString());
        }

        private EvalResult ParseNumber()
        {
            var sb = new StringBuilder();
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) sb.Append(_s[_i++]);
            if (double.TryParse(sb.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return EvalResult.FromNumber(d);
            return EvalResult.FromNumber(0);
        }

        private char Peek() => _i < _s.Length ? _s[_i] : '\0';

        private void SkipWs() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }

        private static EvalResult EvalEq(EvalResult a, EvalResult b)
        {
            if (a.IsNumber && b.IsNumber)
            {
                return EvalResult.FromBool(Math.Abs(a.Number - b.Number) < 1e-9);
            }
            return EvalResult.FromBool(string.Equals(a.AsString(), b.AsString(), StringComparison.Ordinal));
        }

        private static EvalResult EvalNotEq(EvalResult a, EvalResult b)
        {
            var eq = EvalEq(a, b);
            return EvalResult.FromBool(!eq.AsBool());
        }

        private static EvalResult EvalCompare(EvalResult a, EvalResult b, Func<double, double, bool> cmp)
        {
            if (a.IsNumber && b.IsNumber) return EvalResult.FromBool(cmp(a.Number, b.Number));
            // fallback: compare as numbers if possible
            if (double.TryParse(a.AsString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var na) && double.TryParse(b.AsString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var nb))
            {
                return EvalResult.FromBool(cmp(na, nb));
            }
            return EvalResult.FromBool(false);
        }
    }

    private class EvalResult
    {
        public bool IsString { get; private set; }
        public bool IsNumber { get; private set; }
        public bool IsBool { get; private set; }
        public bool IsList { get; private set; }

        public string Str { get; private set; } = string.Empty;
        public double Number { get; private set; }
        public bool Bool { get; private set; }
        public List<EvalResult> List { get; private set; } = new();

        public static EvalResult FromString(string s) => new EvalResult { IsString = true, Str = s };
        public static EvalResult FromNumber(double n) => new EvalResult { IsNumber = true, Number = n };
        public static EvalResult FromBool(bool b) => new EvalResult { IsBool = true, Bool = b };
        public static EvalResult FromList(IEnumerable<string> values)
        {
            var r = new EvalResult { IsList = true, List = new List<EvalResult>() };
            foreach (var v in values) r.List.Add(FromString(v));
            return r;
        }

        public static EvalResult EmptyString => FromString(string.Empty);
        public static EvalResult EmptyList => FromList(Array.Empty<string>());

        public string AsString()
        {
            if (IsString) return Str;
            if (IsNumber) return Number.ToString(CultureInfo.InvariantCulture);
            if (IsBool) return Bool ? "true" : "false";
            if (IsList) return string.Join(";", List.ConvertAll(x => x.AsString()));
            return string.Empty;
        }

        public double AsNumber()
        {
            if (IsNumber) return Number;
            if (IsString && double.TryParse(Str, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
            return 0;
        }

        public bool AsBool()
        {
            if (IsBool) return Bool;
            var s = AsString();
            return !string.IsNullOrWhiteSpace(s) && s != "0" 
                && !string.Equals(s, "false", StringComparison.InvariantCultureIgnoreCase) 
                && !string.Equals(s, "no", StringComparison.InvariantCultureIgnoreCase);
        }
    }
}
