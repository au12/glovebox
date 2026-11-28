using System.Globalization;

namespace CarCareTracker.Canary
{
    /// <summary>
    /// The small expression language used by CANary vehicle profiles (health checks and calculated
    /// signals). This is a port of the device's evaluator (canary_core/src/expr.c) and must stay in
    /// step with it: the shared test vectors in tools/CanarySelfTest check both.
    ///
    ///   expr    := or
    ///   or      := and ( "||" and )*
    ///   and     := cmp ( "&amp;&amp;" cmp )*
    ///   cmp     := add ( ("=="|"!="|"&lt;"|"&lt;="|"&gt;"|"&gt;=") add )?
    ///   add     := mul ( ("+"|"-") mul )*
    ///   mul     := unary ( ("*"|"/"|"%") unary )*
    ///   unary   := ("-"|"!") unary | primary
    ///   primary := number | name | name "(" args ")" | "(" expr ")"
    ///   functions: abs(x) min(a,b) max(a,b) if(c,a,b)
    ///
    /// A variable without a value is NaN, and NaN propagates through every operator, so a result is
    /// either fully backed by data or NaN.
    /// </summary>
    public sealed class CanaryExpr
    {
        private enum Op : byte { Num, Var, Add, Sub, Mul, Div, Mod, Neg, Not, Eq, Ne, Lt, Le, Gt, Ge, And, Or, Abs, Min, Max, If }

        private readonly struct Ins
        {
            public readonly Op Op;
            public readonly double Num;
            public readonly int Var;
            public Ins(Op op, double num, int var) { Op = op; Num = num; Var = var; }
        }

        private readonly Ins[] _code;
        private readonly int[] _variables;

        /// <summary>Indexes of the variables this expression reads.</summary>
        public IReadOnlyList<int> Variables => _variables;

        private CanaryExpr(List<Ins> code)
        {
            _code = code.ToArray();
            _variables = _code.Where(x => x.Op == Op.Var).Select(x => x.Var).Distinct().ToArray();
        }

        /// <summary>
        /// Compiles an expression. <paramref name="resolve"/> maps a signal name to its index in the
        /// array later passed to <see cref="Eval"/>; it returns a negative number for unknown names.
        /// </summary>
        public static CanaryExpr? Compile(string? source, Func<string, int> resolve, out string error)
        {
            var parser = new Parser(source ?? string.Empty, resolve);
            parser.ParseOr();
            parser.SkipSpace();
            if (!parser.Failed && !parser.AtEnd)
            {
                parser.Fail("unexpected trailing text");
            }
            if (!parser.Failed && parser.Code.Count == 0)
            {
                parser.Fail("empty expression");
            }
            if (!parser.Failed)
            {
                /* the evaluator's stack holds 32, as on the device */
                int sp = 0, deepest = 0;
                foreach (var ins in parser.Code)
                {
                    sp += ins.Op is Op.Num or Op.Var ? 1 : ins.Op is Op.Neg or Op.Not or Op.Abs ? 0 : ins.Op == Op.If ? -2 : -1;
                    deepest = Math.Max(deepest, sp);
                }
                if (deepest > 32)
                {
                    parser.Fail("expression too complex");
                }
            }
            error = parser.Error;
            return parser.Failed ? null : new CanaryExpr(parser.Code);
        }

        public double Eval(double[] vars)
        {
            Span<double> stack = stackalloc double[32];
            int sp = 0;
            foreach (var ins in _code)
            {
                if (ins.Op == Op.Num || ins.Op == Op.Var)
                {
                    if (sp >= 32)
                    {
                        return double.NaN;
                    }
                    stack[sp++] = ins.Op == Op.Num ? ins.Num : (ins.Var < vars.Length ? vars[ins.Var] : double.NaN);
                    continue;
                }
                if (ins.Op == Op.Neg || ins.Op == Op.Not || ins.Op == Op.Abs)
                {
                    if (sp < 1)
                    {
                        return double.NaN;
                    }
                    double x = stack[sp - 1];
                    if (double.IsNaN(x))
                    {
                        continue;
                    }
                    stack[sp - 1] = ins.Op == Op.Neg ? -x : ins.Op == Op.Not ? (x == 0 ? 1.0 : 0.0) : Math.Abs(x);
                    continue;
                }
                if (ins.Op == Op.If)
                {
                    if (sp < 3)
                    {
                        return double.NaN;
                    }
                    double c = stack[sp - 3], whenTrue = stack[sp - 2], whenFalse = stack[sp - 1];
                    sp -= 2;
                    stack[sp - 1] = (double.IsNaN(c) || double.IsNaN(whenTrue) || double.IsNaN(whenFalse)) ? double.NaN : (c != 0 ? whenTrue : whenFalse);
                    continue;
                }
                if (sp < 2)
                {
                    return double.NaN;
                }
                double b = stack[--sp], a = stack[sp - 1], r;
                if (double.IsNaN(a) || double.IsNaN(b))
                {
                    stack[sp - 1] = double.NaN;
                    continue;
                }
                switch (ins.Op)
                {
                    case Op.Add: r = a + b; break;
                    case Op.Sub: r = a - b; break;
                    case Op.Mul: r = a * b; break;
                    case Op.Div: r = b == 0 ? double.NaN : a / b; break;
                    case Op.Mod: r = b == 0 ? double.NaN : a % b; break; /* same truncated remainder as C's fmod */
                    case Op.Eq: r = a == b ? 1 : 0; break;
                    case Op.Ne: r = a != b ? 1 : 0; break;
                    case Op.Lt: r = a < b ? 1 : 0; break;
                    case Op.Le: r = a <= b ? 1 : 0; break;
                    case Op.Gt: r = a > b ? 1 : 0; break;
                    case Op.Ge: r = a >= b ? 1 : 0; break;
                    case Op.And: r = (a != 0 && b != 0) ? 1 : 0; break;
                    case Op.Or: r = (a != 0 || b != 0) ? 1 : 0; break;
                    case Op.Min: r = a < b ? a : b; break;
                    case Op.Max: r = a > b ? a : b; break;
                    default: r = double.NaN; break;
                }
                stack[sp - 1] = r;
            }
            return sp == 1 ? stack[0] : double.NaN;
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;
            private readonly Func<string, int> _resolve;
            public readonly List<Ins> Code = new();
            public bool Failed;
            public string Error = string.Empty;
            /* nesting, as on the device: unbounded recursion would end the whole process on hostile input */
            private int _depth;
            private const int MaxDepth = 24;

            public Parser(string s, Func<string, int> resolve) { _s = s; _resolve = resolve; }

            public bool AtEnd => _i >= _s.Length;
            private char Cur => _i < _s.Length ? _s[_i] : '\0';
            private char Next => _i + 1 < _s.Length ? _s[_i + 1] : '\0';

            public void Fail(string message, string? detail = null)
            {
                if (Failed)
                {
                    return;
                }
                Failed = true;
                Error = detail == null ? message : $"{message}: {detail}";
            }

            private void Emit(Op op, double num = 0, int var = 0)
            {
                if (!Failed)
                {
                    Code.Add(new Ins(op, num, var));
                }
            }

            public void SkipSpace()
            {
                while (Cur == ' ' || Cur == '\t' || Cur == '\n' || Cur == '\r')
                {
                    _i++;
                }
            }

            private bool Eat(string token)
            {
                SkipSpace();
                if (string.CompareOrdinal(_s, _i, token, 0, token.Length) != 0)
                {
                    return false;
                }
                _i += token.Length;
                return true;
            }

            private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';
            private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

            private void ParsePrimary()
            {
                SkipSpace();
                if (Failed)
                {
                    return;
                }
                if (Cur == '(')
                {
                    _i++;
                    ParseOr();
                    if (!Eat(")"))
                    {
                        Fail("expected )");
                    }
                    return;
                }
                if (IsAsciiDigit(Cur) || Cur == '.')
                {
                    int start = _i;
                    while (IsAsciiDigit(Cur)) _i++;
                    if (Cur == '.')
                    {
                        _i++;
                        while (IsAsciiDigit(Cur)) _i++;
                    }
                    if ((Cur == 'e' || Cur == 'E') && (IsAsciiDigit(Next) || ((Next == '+' || Next == '-') && _i + 2 < _s.Length && IsAsciiDigit(_s[_i + 2]))))
                    {
                        _i += 2;
                        while (IsAsciiDigit(Cur)) _i++;
                    }
                    if (!double.TryParse(_s.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    {
                        Fail("bad number");
                        return;
                    }
                    Emit(Op.Num, value);
                    return;
                }
                if (IsAsciiLetter(Cur) || Cur == '_')
                {
                    int start = _i;
                    while (IsAsciiLetter(Cur) || IsAsciiDigit(Cur) || Cur == '_' || Cur == '.')
                    {
                        _i++;
                    }
                    string name = _s.Substring(start, Math.Min(_i - start, 47));
                    SkipSpace();
                    if (Cur == '(')
                    {
                        _i++;
                        int argc = 0;
                        SkipSpace();
                        if (Cur != ')')
                        {
                            do
                            {
                                ParseOr();
                                argc++;
                            } while (Eat(","));
                        }
                        if (!Eat(")"))
                        {
                            Fail("expected )");
                            return;
                        }
                        if (name == "abs" && argc == 1) Emit(Op.Abs);
                        else if (name == "min" && argc == 2) Emit(Op.Min);
                        else if (name == "max" && argc == 2) Emit(Op.Max);
                        else if (name == "if" && argc == 3) Emit(Op.If);
                        else Fail("unknown function or wrong argument count", name);
                        return;
                    }
                    int variable = _resolve(name);
                    if (variable < 0)
                    {
                        Fail("unknown signal", name);
                        return;
                    }
                    Emit(Op.Var, 0, variable);
                    return;
                }
                Fail("unexpected character");
            }

            private void ParseUnary()
            {
                if (++_depth > MaxDepth)
                {
                    Fail("expression nested too deeply");
                    _depth--;
                    return;
                }
                try
                {
                    ParseUnaryInner();
                }
                finally
                {
                    _depth--;
                }
            }

            private void ParseUnaryInner()
            {
                SkipSpace();
                if (Cur == '-')
                {
                    _i++;
                    ParseUnary();
                    Emit(Op.Neg);
                }
                else if (Cur == '!' && Next != '=')
                {
                    _i++;
                    ParseUnary();
                    Emit(Op.Not);
                }
                else
                {
                    ParsePrimary();
                }
            }

            private void ParseMul()
            {
                ParseUnary();
                while (true)
                {
                    SkipSpace();
                    char c = Cur;
                    if (c != '*' && c != '/' && c != '%')
                    {
                        break;
                    }
                    _i++;
                    ParseUnary();
                    Emit(c == '*' ? Op.Mul : c == '/' ? Op.Div : Op.Mod);
                }
            }

            private void ParseAdd()
            {
                ParseMul();
                while (true)
                {
                    SkipSpace();
                    char c = Cur;
                    if (c != '+' && c != '-')
                    {
                        break;
                    }
                    _i++;
                    ParseMul();
                    Emit(c == '+' ? Op.Add : Op.Sub);
                }
            }

            private void ParseCmp()
            {
                ParseAdd();
                SkipSpace();
                Op op;
                if (Eat("==")) op = Op.Eq;
                else if (Eat("!=")) op = Op.Ne;
                else if (Eat("<=")) op = Op.Le;
                else if (Eat(">=")) op = Op.Ge;
                else if (Eat("<")) op = Op.Lt;
                else if (Eat(">")) op = Op.Gt;
                else return;
                ParseAdd();
                Emit(op);
            }

            private void ParseAnd()
            {
                ParseCmp();
                while (Eat("&&"))
                {
                    ParseCmp();
                    Emit(Op.And);
                }
            }

            public void ParseOr()
            {
                ParseAnd();
                while (Eat("||"))
                {
                    ParseAnd();
                    Emit(Op.Or);
                }
            }
        }
    }
}
