using System.Globalization;
using System.Text;

namespace ClickZen.Core.Variables;

/// <summary>Raised for syntax errors (with position) and evaluation errors.</summary>
public sealed class ExpressionException : Exception
{
    public ExpressionException(string message, int position = -1) : base(message) => Position = position;

    public ExpressionException() { }

    public ExpressionException(string message) : base(message) { }

    public ExpressionException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Character index in the source, or -1.</summary>
    public int Position { get; } = -1;
}

/// <summary>Context an expression is evaluated in.</summary>
public interface IExpressionContext
{
    bool TryGetVariable(string name, out VariableValue value);

    /// <summary>Random source for rand(); injectable for tests.</summary>
    Random Random { get; }
}

/// <summary>
/// A compiled expression. Grammar (lowest to highest precedence):
/// <code>
///   expr     := or
///   or       := and ( ("||" | "or") and )*
///   and      := equality ( ("&amp;&amp;" | "and") equality )*
///   equality := compare ( ("==" | "!=") compare )*
///   compare  := additive ( ("&lt;" | "&lt;=" | "&gt;" | "&gt;=") additive )*
///   additive := term ( ("+" | "-") term )*
///   term     := unary ( ("*" | "/" | "//" | "%") unary )*
///   unary    := ("!" | "not" | "-" | "+") unary | primary
///   primary  := number | string | true | false | identifier | $match.x-style identifier
///             | identifier "(" args ")" | "(" expr ")"
/// </code>
/// Undefined variables evaluate to 0 when <see cref="Expression.StrictVariables"/> is false,
/// otherwise they throw. Functions: abs, min, max, round, floor, ceil, rand(a,b), int, str, len, clamp(v,lo,hi), defined(name).
/// </summary>
public sealed class Expression
{
    private readonly Node _root;

    private Expression(string source, Node root, IReadOnlyList<string> variables)
    {
        Source = source;
        _root = root;
        Variables = variables;
    }

    public string Source { get; }

    /// <summary>Variable names referenced by the expression.</summary>
    public IReadOnlyList<string> Variables { get; }

    /// <summary>When true, reading an undefined variable throws instead of yielding 0.</summary>
    public bool StrictVariables { get; private init; }

    public static Expression Parse(string source, bool strictVariables = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        var parser = new Parser(source);
        var node = parser.ParseExpression();
        parser.ExpectEnd();
        return new Expression(source, node, parser.Variables.ToArray()) { StrictVariables = strictVariables };
    }

    public static bool TryParse(string source, out Expression? expression, out string? error)
    {
        try
        {
            expression = Parse(source);
            error = null;
            return true;
        }
        catch (ExpressionException ex)
        {
            expression = null;
            error = ex.Position >= 0 ? $"{ex.Message} (at {ex.Position + 1})" : ex.Message;
            return false;
        }
    }

    public VariableValue Evaluate(IExpressionContext ctx) => _root.Eval(ctx, StrictVariables);

    public override string ToString() => Source;

    // ---------------------------------------------------------------- AST

    private abstract class Node
    {
        public abstract VariableValue Eval(IExpressionContext ctx, bool strict);
    }

    private sealed class Literal(VariableValue value) : Node
    {
        public override VariableValue Eval(IExpressionContext ctx, bool strict) => value;
    }

    private sealed class VarRef(string name, int pos) : Node
    {
        public string Name => name;

        public override VariableValue Eval(IExpressionContext ctx, bool strict)
        {
            if (ctx.TryGetVariable(name, out var v))
            {
                return v;
            }

            if (strict)
            {
                throw new ExpressionException($"Variable '{name}' is not defined.", pos);
            }

            return VariableValue.Zero;
        }
    }

    private sealed class Unary(string op, Node operand) : Node
    {
        public override VariableValue Eval(IExpressionContext ctx, bool strict)
        {
            var v = operand.Eval(ctx, strict);
            return op switch
            {
                "!" => VariableValue.FromBool(!v.AsBool),
                "-" => v.Type == VariableType.Double ? VariableValue.FromDouble(-v.AsDouble) : VariableValue.FromInt(-v.AsInt),
                _ => v.Type == VariableType.Double ? VariableValue.FromDouble(v.AsDouble) : VariableValue.FromInt(v.AsInt),
            };
        }
    }

    private sealed class Logical(bool isAnd, Node left, Node right) : Node
    {
        public override VariableValue Eval(IExpressionContext ctx, bool strict)
        {
            var l = left.Eval(ctx, strict).AsBool;
            if (isAnd ? !l : l)
            {
                return VariableValue.FromBool(l);
            }

            return VariableValue.FromBool(right.Eval(ctx, strict).AsBool);
        }
    }

    private sealed class Binary(string op, Node left, Node right, int pos) : Node
    {
        public override VariableValue Eval(IExpressionContext ctx, bool strict)
        {
            var a = left.Eval(ctx, strict);
            var b = right.Eval(ctx, strict);
            switch (op)
            {
                case "==": return VariableValue.FromBool(a.Equals(b));
                case "!=": return VariableValue.FromBool(!a.Equals(b));
                case "<": return VariableValue.FromBool(a.CompareTo(b) < 0);
                case "<=": return VariableValue.FromBool(a.CompareTo(b) <= 0);
                case ">": return VariableValue.FromBool(a.CompareTo(b) > 0);
                case ">=": return VariableValue.FromBool(a.CompareTo(b) >= 0);
                case "+" when a.Type == VariableType.String || b.Type == VariableType.String:
                    return VariableValue.FromString(a.AsString + b.AsString);
            }

            if (!a.IsNumeric && !double.TryParse(a.AsString, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                throw new ExpressionException($"Cannot apply '{op}' to text \"{a.AsString}\".", pos);
            }

            if (!b.IsNumeric && !double.TryParse(b.AsString, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                throw new ExpressionException($"Cannot apply '{op}' to text \"{b.AsString}\".", pos);
            }

            var useDouble = a.Type == VariableType.Double || b.Type == VariableType.Double
                            || (a.Type == VariableType.String && a.AsString.Contains('.', StringComparison.Ordinal))
                            || (b.Type == VariableType.String && b.AsString.Contains('.', StringComparison.Ordinal));
            if (op == "/" && !useDouble)
            {
                // "/" on two integers is exact division when it divides evenly, else a double –
                // users writing 7/2 expect 3.5. Use "//" for integer division.
                if (b.AsInt == 0)
                {
                    throw new ExpressionException("Division by zero.", pos);
                }

                return a.AsInt % b.AsInt == 0 ? VariableValue.FromInt(a.AsInt / b.AsInt) : VariableValue.FromDouble((double)a.AsInt / b.AsInt);
            }

            if (useDouble)
            {
                double x = a.AsDouble, y = b.AsDouble;
                return op switch
                {
                    "+" => x + y,
                    "-" => x - y,
                    "*" => x * y,
                    "/" => y == 0 ? throw new ExpressionException("Division by zero.", pos) : x / y,
                    "//" => y == 0 ? throw new ExpressionException("Division by zero.", pos) : Math.Floor(x / y),
                    "%" => y == 0 ? throw new ExpressionException("Division by zero.", pos) : x % y,
                    _ => throw new ExpressionException($"Unknown operator '{op}'.", pos),
                };
            }

            long i = a.AsInt, j = b.AsInt;
            return op switch
            {
                "+" => VariableValue.FromInt(unchecked(i + j)),
                "-" => VariableValue.FromInt(unchecked(i - j)),
                "*" => VariableValue.FromInt(unchecked(i * j)),
                "//" => j == 0 ? throw new ExpressionException("Division by zero.", pos) : VariableValue.FromInt(FloorDiv(i, j)),
                "%" => j == 0 ? throw new ExpressionException("Division by zero.", pos) : VariableValue.FromInt(i % j),
                _ => throw new ExpressionException($"Unknown operator '{op}'.", pos),
            };
        }

        private static long FloorDiv(long a, long b)
        {
            var q = a / b;
            return (a % b != 0) && ((a < 0) ^ (b < 0)) ? q - 1 : q;
        }
    }

    private sealed class Call(string name, IReadOnlyList<Node> args, int pos) : Node
    {
        public override VariableValue Eval(IExpressionContext ctx, bool strict)
        {
            if (name == "defined")
            {
                // defined(x) takes the variable name unevaluated.
                return args is [VarRef vr]
                    ? VariableValue.FromBool(ctx.TryGetVariable(vr.Name, out _))
                    : throw new ExpressionException("defined() takes a variable name.", pos);
            }

            var v = args.Select(a => a.Eval(ctx, strict)).ToArray();
            switch (name)
            {
                case "abs": Arity(1); return v[0].Type == VariableType.Double ? Math.Abs(v[0].AsDouble) : VariableValue.FromInt(Math.Abs(v[0].AsInt));
                case "min": AtLeast(1); return v.Aggregate((x, y) => x.CompareTo(y) <= 0 ? x : y);
                case "max": AtLeast(1); return v.Aggregate((x, y) => x.CompareTo(y) >= 0 ? x : y);
                case "round": Arity(1); return VariableValue.FromInt((long)Math.Round(v[0].AsDouble, MidpointRounding.AwayFromZero));
                case "floor": Arity(1); return VariableValue.FromInt((long)Math.Floor(v[0].AsDouble));
                case "ceil": Arity(1); return VariableValue.FromInt((long)Math.Ceiling(v[0].AsDouble));
                case "int": Arity(1); return VariableValue.FromInt(v[0].AsInt);
                case "str": Arity(1); return VariableValue.FromString(v[0].AsString);
                case "len": Arity(1); return VariableValue.FromInt(v[0].AsString.Length);
                case "clamp":
                    Arity(3);
                    return v[0].CompareTo(v[1]) < 0 ? v[1] : v[0].CompareTo(v[2]) > 0 ? v[2] : v[0];
                case "rand":
                    if (v.Length == 0)
                    {
                        return VariableValue.FromDouble(ctx.Random.NextDouble());
                    }

                    Arity(2);
                    var lo = v[0].AsInt;
                    var hi = v[1].AsInt;
                    if (hi < lo)
                    {
                        (lo, hi) = (hi, lo);
                    }

                    return VariableValue.FromInt(ctx.Random.NextInt64(lo, hi + 1));
                default:
                    throw new ExpressionException($"Unknown function '{name}'.", pos);
            }

            void Arity(int n)
            {
                if (v.Length != n)
                {
                    throw new ExpressionException($"{name}() takes {n} argument(s).", pos);
                }
            }

            void AtLeast(int n)
            {
                if (v.Length < n)
                {
                    throw new ExpressionException($"{name}() takes at least {n} argument(s).", pos);
                }
            }
        }
    }

    // ---------------------------------------------------------------- Parser

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s) => _s = s;

        public List<string> Variables { get; } = [];

        public Node ParseExpression() => ParseOr();

        public void ExpectEnd()
        {
            SkipWs();
            if (_i < _s.Length)
            {
                throw new ExpressionException($"Unexpected '{_s[_i]}'.", _i);
            }
        }

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (Match("||") || MatchWord("or"))
            {
                left = new Logical(false, left, ParseAnd());
            }

            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseEquality();
            while (Match("&&") || MatchWord("and"))
            {
                left = new Logical(true, left, ParseEquality());
            }

            return left;
        }

        private Node ParseEquality()
        {
            var left = ParseCompare();
            while (true)
            {
                var pos = Pos();
                if (Match("=="))
                {
                    left = new Binary("==", left, ParseCompare(), pos);
                }
                else if (Match("!="))
                {
                    left = new Binary("!=", left, ParseCompare(), pos);
                }
                else
                {
                    return left;
                }
            }
        }

        private Node ParseCompare()
        {
            var left = ParseAdditive();
            while (true)
            {
                var pos = Pos();
                string? op = Match("<=") ? "<=" : Match(">=") ? ">=" : Match("<") ? "<" : Match(">") ? ">" : null;
                if (op is null)
                {
                    return left;
                }

                left = new Binary(op, left, ParseAdditive(), pos);
            }
        }

        private Node ParseAdditive()
        {
            var left = ParseTerm();
            while (true)
            {
                var pos = Pos();
                string? op = Match("+") ? "+" : Match("-") ? "-" : null;
                if (op is null)
                {
                    return left;
                }

                left = new Binary(op, left, ParseTerm(), pos);
            }
        }

        private Node ParseTerm()
        {
            var left = ParseUnary();
            while (true)
            {
                var pos = Pos();
                string? op = Match("//") ? "//" : Match("*") ? "*" : Match("/") ? "/" : Match("%") ? "%" : null;
                if (op is null)
                {
                    return left;
                }

                left = new Binary(op, left, ParseUnary(), pos);
            }
        }

        private Node ParseUnary()
        {
            if (Peek("!=") is false && Match("!") || MatchWord("not"))
            {
                return new Unary("!", ParseUnary());
            }

            if (Match("-"))
            {
                return new Unary("-", ParseUnary());
            }

            if (Match("+"))
            {
                return new Unary("+", ParseUnary());
            }

            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            SkipWs();
            if (_i >= _s.Length)
            {
                throw new ExpressionException("Unexpected end of expression.", _i);
            }

            var c = _s[_i];
            var start = _i;

            if (c == '(')
            {
                _i++;
                var inner = ParseExpression();
                Expect(")");
                return inner;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && _i + 1 < _s.Length && char.IsAsciiDigit(_s[_i + 1])))
            {
                while (_i < _s.Length && (char.IsAsciiDigit(_s[_i]) || _s[_i] == '.' || _s[_i] == '_'))
                {
                    _i++;
                }

                var text = _s[start.._i].Replace("_", "", StringComparison.Ordinal);
                if (text.Contains('.', StringComparison.Ordinal))
                {
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                        ? new Literal(d)
                        : throw new ExpressionException($"Invalid number '{text}'.", start);
                }

                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                    ? new Literal(l)
                    : throw new ExpressionException($"Invalid number '{text}'.", start);
            }

            if (c is '"' or '\'')
            {
                return new Literal(ReadString(c));
            }

            if (IsIdentStart(c))
            {
                var name = ReadIdentifier();
                switch (name)
                {
                    case "true": return new Literal(true);
                    case "false": return new Literal(false);
                }

                SkipWs();
                if (_i < _s.Length && _s[_i] == '(')
                {
                    _i++;
                    var args = new List<Node>();
                    SkipWs();
                    if (!Match(")"))
                    {
                        do
                        {
                            args.Add(ParseExpression());
                        }
                        while (Match(","));

                        Expect(")");
                    }

                    return new Call(name.ToLowerInvariant(), args, start);
                }

                if (!Variables.Contains(name))
                {
                    Variables.Add(name);
                }

                return new VarRef(name, start);
            }

            throw new ExpressionException($"Unexpected '{c}'.", _i);
        }

        private string ReadString(char quote)
        {
            var start = _i;
            _i++;
            var sb = new StringBuilder();
            while (_i < _s.Length && _s[_i] != quote)
            {
                if (_s[_i] == '\\' && _i + 1 < _s.Length)
                {
                    _i++;
                    sb.Append(_s[_i] switch { 'n' => '\n', 't' => '\t', var other => other });
                }
                else
                {
                    sb.Append(_s[_i]);
                }

                _i++;
            }

            if (_i >= _s.Length)
            {
                throw new ExpressionException("Unterminated string.", start);
            }

            _i++;
            return sb.ToString();
        }

        private static bool IsIdentStart(char c) => char.IsLetter(c) || c is '_' or '$';

        private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '$';

        private string ReadIdentifier()
        {
            var start = _i;
            while (_i < _s.Length && IsIdentPart(_s[_i]))
            {
                _i++;
            }

            return _s[start.._i].TrimEnd('.');
        }

        private int Pos()
        {
            SkipWs();
            return _i;
        }

        private void SkipWs()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
            {
                _i++;
            }
        }

        private bool Peek(string token)
        {
            SkipWs();
            return string.CompareOrdinal(_s, _i, token, 0, token.Length) == 0;
        }

        private bool Match(string token)
        {
            if (!Peek(token))
            {
                return false;
            }

            _i += token.Length;
            return true;
        }

        private bool MatchWord(string word)
        {
            SkipWs();
            if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0)
            {
                return false;
            }

            var end = _i + word.Length;
            if (end < _s.Length && IsIdentPart(_s[end]))
            {
                return false;
            }

            _i = end;
            return true;
        }

        private void Expect(string token)
        {
            if (!Match(token))
            {
                throw new ExpressionException($"Expected '{token}'.", _i);
            }
        }
    }
}

/// <summary>Expression context backed by a <see cref="VariableStore"/> plus optional extra values (e.g. $match.x).</summary>
public sealed class StoreExpressionContext : IExpressionContext
{
    private readonly VariableStore _store;
    private readonly IReadOnlyDictionary<string, VariableValue>? _extra;

    public StoreExpressionContext(VariableStore store, IReadOnlyDictionary<string, VariableValue>? extra = null, Random? random = null)
    {
        _store = store;
        _extra = extra;
        Random = random ?? Random.Shared;
    }

    public Random Random { get; }

    public bool TryGetVariable(string name, out VariableValue value)
    {
        if (_extra is not null && _extra.TryGetValue(name, out value))
        {
            return true;
        }

        return _store.TryGet(name, out value);
    }
}
