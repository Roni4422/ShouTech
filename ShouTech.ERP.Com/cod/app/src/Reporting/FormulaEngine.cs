// ========================================================================
// FILE: Reporting/FormulaEngine.cs
// PROJECT: SHOUTECH ERP — Sandboxed report formula engine
// GRAMMAR:
//   expr    := comparison
//   comparison := additive ((">"|"<"|">="|"<="|"="|"<>") additive)?
//   additive := term (("+"|"-") term)*
//   term     := factor (("*"|"/"|"%") factor)*
//   factor   := unary
//   unary    := "-" unary | primary
//   primary  := NUMBER | STRING | [ColumnKey] | FUNC(args) | "(" expr ")"
//   FUNC     := ROUND | ABS | MIN | MAX | IF | COALESCE
// ========================================================================

#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System;

namespace ShouTech.App.Reporting
{
    public sealed class FormulaException : Exception
    {
        public int Position { get; }
        public FormulaException(string message, int position) : base(message)
            => Position = position;
    }

    /// <summary>
    /// </summary>
    public interface IRowValueProvider
    {
        bool TryGetValue(string columnKey, out object? value);
    }

    // ══════════════════════════════════════════════════════════════════
    // AST Nodes
    // ══════════════════════════════════════════════════════════════════

    internal abstract class Node
    {
        public abstract decimal? Evaluate(IRowValueProvider row);
    }

    internal sealed class NumberNode : Node
    {
        private readonly decimal _value;
        public NumberNode(decimal value) => _value = value;
        public override decimal? Evaluate(IRowValueProvider row) => _value;
    }

    internal sealed class ColumnNode : Node
    {
        private readonly string _key;
        public ColumnNode(string key) => _key = key;

        public override decimal? Evaluate(IRowValueProvider row)
        {
            if (!row.TryGetValue(_key, out var raw) || raw is null || raw is DBNull)
                return null;

            return raw switch
            {
                decimal d => d,
                double db => (decimal)db,
                float f => (decimal)f,
                int i => i,
                long l => l,
                short s => s,
                byte b => b,
                string str when decimal.TryParse(str, NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null
            };
        }
    }

    internal sealed class UnaryNode : Node
    {
        private readonly Node _inner;
        public UnaryNode(Node inner) => _inner = inner;
        public override decimal? Evaluate(IRowValueProvider row)
        {
            var v = _inner.Evaluate(row);
            return v.HasValue ? -v.Value : null;
        }
    }

    internal sealed class BinaryNode : Node
    {
        private readonly Node _left, _right;
        private readonly string _op;

        public BinaryNode(Node left, string op, Node right)
        {
            _left = left; _op = op; _right = right;
        }

        public override decimal? Evaluate(IRowValueProvider row)
        {
            var l = _left.Evaluate(row);
            var r = _right.Evaluate(row);

            switch (_op)
            {
                case "=": return Eq(l, r) ? 1m : 0m;
                case "<>": return Eq(l, r) ? 0m : 1m;
                case ">": return l.HasValue && r.HasValue && l.Value > r.Value ? 1m : 0m;
                case "<": return l.HasValue && r.HasValue && l.Value < r.Value ? 1m : 0m;
                case ">=": return l.HasValue && r.HasValue && l.Value >= r.Value ? 1m : 0m;
                case "<=": return l.HasValue && r.HasValue && l.Value <= r.Value ? 1m : 0m;
            }

            if (!l.HasValue || !r.HasValue)
                return null;

            return _op switch
            {
                "+" => l.Value + r.Value,
                "-" => l.Value - r.Value,
                "*" => l.Value * r.Value,
                "/" => r.Value == 0m ? null : l.Value / r.Value,   // ÷0 → NULL لا انفجار
                "%" => r.Value == 0m ? null : l.Value % r.Value,
                _ => null
            };
        }

        private static bool Eq(decimal? l, decimal? r)
            => l.HasValue == r.HasValue && (!l.HasValue || l.Value == r!.Value);
    }

    internal sealed class FunctionNode : Node
    {
        private readonly string _name;
        private readonly List<Node> _args;

        public FunctionNode(string name, List<Node> args)
        {
            _name = name; _args = args;
        }

        public override decimal? Evaluate(IRowValueProvider row)
        {
            var vals = _args.Select(a => a.Evaluate(row)).ToList();

            switch (_name)
            {
                case "ABS" when vals.Count == 1:
                    return vals[0].HasValue ? Math.Abs(vals[0].GetValueOrDefault()) : null;

                case "ROUND" when vals.Count is 1 or 2:
                {
                    if (!vals[0].HasValue) return null;
                    var digits = vals.Count == 2 && vals[1].HasValue
                        ? Math.Clamp((int)vals[1].GetValueOrDefault(), 0, 8) : 2;
                    return Math.Round(vals[0].GetValueOrDefault(), digits, MidpointRounding.AwayFromZero);
                }

                case "MIN" when vals.Count >= 1:
                {
                    var present = vals.Where(v => v.HasValue)
                                      .Select(v => v.GetValueOrDefault()).ToList();
                    return present.Count > 0 ? present.Min() : null;
                }

                case "MAX" when vals.Count >= 1:
                {
                    var present = vals.Where(v => v.HasValue)
                                      .Select(v => v.GetValueOrDefault()).ToList();
                    return present.Count > 0 ? present.Max() : null;
                }

                case "IF" when vals.Count == 3:
                    return vals[0].GetValueOrDefault() != 0m ? vals[1] : vals[2];

                case "COALESCE" when vals.Count >= 1:
                    return vals.FirstOrDefault(v => v.HasValue);

                default:
                    throw new FormulaException(
                        $"Unknown function or wrong argument count: {_name}", -1);
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // Tokenizer
    // ══════════════════════════════════════════════════════════════════

    internal enum TokenKind { Number, Column, Identifier, Op, LParen, RParen, Comma, End }

    internal readonly struct Token
    {
        public TokenKind Kind { get; }
        public string Text { get; }
        public int Position { get; }
        public Token(TokenKind kind, string text, int position)
        {
            Kind = kind; Text = text; Position = position;
        }
    }

    internal static class Tokenizer
    {
        public static List<Token> Tokenize(string formula)
        {
            var tokens = new List<Token>();
            var i = 0;

            if (i < formula.Length && formula[i] == '=')
                i++;

            while (i < formula.Length)
            {
                var c = formula[i];

                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (char.IsDigit(c) || (c == '.' && i + 1 < formula.Length && char.IsDigit(formula[i + 1])))
                {
                    var start = i;
                    while (i < formula.Length && (char.IsDigit(formula[i]) || formula[i] == '.'))
                        i++;
                    tokens.Add(new Token(TokenKind.Number, formula[start..i], start));
                    continue;
                }

                if (c == '[')
                {
                    var start = i;
                    var end = formula.IndexOf(']', i + 1);
                    if (end < 0)
                        throw new FormulaException("Unclosed column reference '['.", i);
                    var key = formula[(i + 1)..end].Trim();
                    if (key.Length == 0 || key.Length > 60)
                        throw new FormulaException("Invalid column reference.", start);
                    tokens.Add(new Token(TokenKind.Column, key, start));
                    i = end + 1;
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    var start = i;
                    while (i < formula.Length && (char.IsLetterOrDigit(formula[i]) || formula[i] == '_'))
                        i++;
                    tokens.Add(new Token(TokenKind.Identifier, formula[start..i].ToUpperInvariant(), start));
                    continue;
                }

                switch (c)
                {
                    case '(': tokens.Add(new Token(TokenKind.LParen, "(", i)); i++; continue;
                    case ')': tokens.Add(new Token(TokenKind.RParen, ")", i)); i++; continue;
                    case ',': tokens.Add(new Token(TokenKind.Comma, ",", i)); i++; continue;
                    case '+' or '-' or '*' or '/' or '%':
                        tokens.Add(new Token(TokenKind.Op, c.ToString(), i)); i++; continue;
                    case '>' or '<':
                        if (i + 1 < formula.Length && (formula[i + 1] == '=' || (c == '<' && formula[i + 1] == '>')))
                        {
                            tokens.Add(new Token(TokenKind.Op, formula.Substring(i, 2), i));
                            i += 2;
                        }
                        else
                        {
                            tokens.Add(new Token(TokenKind.Op, c.ToString(), i));
                            i++;
                        }
                        continue;
                    case '=':
                        tokens.Add(new Token(TokenKind.Op, "=", i)); i++; continue;
                    default:
                        throw new FormulaException($"Unexpected character '{c}'.", i);
                }
            }

            tokens.Add(new Token(TokenKind.End, string.Empty, formula.Length));
            return tokens;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // Parser (recursive descent)
    // ══════════════════════════════════════════════════════════════════

    internal sealed class Parser
    {
        private static readonly HashSet<string> AllowedFunctions = new(StringComparer.Ordinal)
        {
            "ROUND", "ABS", "MIN", "MAX", "IF", "COALESCE"
        };

        private readonly List<Token> _tokens;
        private int _pos;

        public Parser(List<Token> tokens) => _tokens = tokens;

        private Token Current => _tokens[_pos];
        private Token Advance() => _tokens[_pos++];

        public Node ParseExpression()
        {
            var node = ParseComparison();
            if (Current.Kind != TokenKind.End)
                throw new FormulaException("Unexpected content after expression end.", Current.Position);
            return node;
        }

        private Node ParseComparison()
        {
            var left = ParseAdditive();
            if (Current.Kind == TokenKind.Op &&
                Current.Text is ">" or "<" or ">=" or "<=" or "=" or "<>")
            {
                var op = Advance().Text;
                var right = ParseAdditive();
                return new BinaryNode(left, op, right);
            }
            return left;
        }

        private Node ParseAdditive()
        {
            var left = ParseTerm();
            while (Current.Kind == TokenKind.Op && (Current.Text == "+" || Current.Text == "-"))
            {
                var op = Advance().Text;
                left = new BinaryNode(left, op, ParseTerm());
            }
            return left;
        }

        private Node ParseTerm()
        {
            var left = ParseUnary();
            while (Current.Kind == TokenKind.Op &&
                   (Current.Text == "*" || Current.Text == "/" || Current.Text == "%"))
            {
                var op = Advance().Text;
                left = new BinaryNode(left, op, ParseUnary());
            }
            return left;
        }

        private Node ParseUnary()
        {
            if (Current.Kind == TokenKind.Op && Current.Text == "-")
            {
                Advance();
                return new UnaryNode(ParseUnary());
            }
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            var token = Current;

            switch (token.Kind)
            {
                case TokenKind.Number:
                {
                    Advance();
                    if (!decimal.TryParse(token.Text, NumberStyles.Number,
                            CultureInfo.InvariantCulture, out var value))
                        throw new FormulaException($"Invalid number '{token.Text}'.", token.Position);
                    return new NumberNode(value);
                }

                case TokenKind.Column:
                    Advance();
                    return new ColumnNode(token.Text);

                case TokenKind.Identifier:
                {
                    Advance();
                    if (!AllowedFunctions.Contains(token.Text))
                        throw new FormulaException(
                            $"Function '{token.Text}' is not allowed. " +
                            "Allowed: ROUND, ABS, MIN, MAX, IF, COALESCE.", token.Position);

                    Expect(TokenKind.LParen, $"Expected '(' after function {token.Text}.");
                    var args = new List<Node> { ParseComparison() };
                    while (Current.Kind == TokenKind.Comma)
                    {
                        Advance();
                        args.Add(ParseComparison());
                    }
                    Expect(TokenKind.RParen, $"Expected ')' to close function {token.Text}.");
                    return new FunctionNode(token.Text, args);
                }

                case TokenKind.LParen:
                {
                    Advance();
                    var inner = ParseComparison();
                    Expect(TokenKind.RParen, "Expected ')'.");
                    return inner;
                }

                default:
                    throw new FormulaException("Expected a number, [column] or function.", token.Position);
            }
        }

        private void Expect(TokenKind kind, string message)
        {
            if (Current.Kind != kind)
                throw new FormulaException(message, Current.Position);
            Advance();
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // Public API — compiled, reusable formula
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// </summary>
    public sealed class CompiledFormula
    {
        private readonly Node _root;
        private readonly string _source;

        private CompiledFormula(string source, Node root)
        {
            _source = source;
            _root = root;
        }

        public static CompiledFormula Compile(string formula)
        {
            if (string.IsNullOrWhiteSpace(formula))
                throw new FormulaException("Formula is empty.", 0);
            if (formula.Length > 1000)
                throw new FormulaException("Formula exceeds the maximum length (1000 chars).", 0);

            var tokens = Tokenizer.Tokenize(formula.Trim());
            var root = new Parser(tokens).ParseExpression();
            return new CompiledFormula(formula.Trim(), root);
        }

        public static bool TryCompile(string formula, out string? error)
        {
            try
            {
                Compile(formula);
                error = null;
                return true;
            }
            catch (FormulaException ex)
            {
                error = ex.Position >= 0 ? $"{ex.Message} (position {ex.Position})" : ex.Message;
                return false;
            }
        }

        public decimal? Evaluate(IRowValueProvider row)
        {
            try
            {
                return _root.Evaluate(row);
            }
            catch (OverflowException)
            {
                return null; // قيمة تفوق النطاق → خلية فارغة بدل انهيار الشبكة
            }
        }

        public override string ToString() => _source;
    }
}
