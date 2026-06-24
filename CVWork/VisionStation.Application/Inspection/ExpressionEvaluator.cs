using System.Globalization;

namespace VisionStation.Application.Inspection;

internal static class ExpressionEvaluator
{
    public static string Evaluate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        try
        {
            var parser = new Parser(text);
            var value = parser.ParseExpression();
            parser.SkipWhiteSpace();
            return parser.IsAtEnd
                ? value.ToString(CultureInfo.InvariantCulture)
                : text;
        }
        catch
        {
            return text;
        }
    }

    private ref struct Parser
    {
        private readonly ReadOnlySpan<char> _text;
        private int _position;

        public Parser(string text)
        {
            _text = text.AsSpan();
            _position = 0;
        }

        public bool IsAtEnd => _position >= _text.Length;

        public double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                SkipWhiteSpace();
                if (TryConsume('+'))
                {
                    value += ParseTerm();
                }
                else if (TryConsume('-'))
                {
                    value -= ParseTerm();
                }
                else
                {
                    return value;
                }
            }
        }

        public void SkipWhiteSpace()
        {
            while (!IsAtEnd && char.IsWhiteSpace(_text[_position]))
            {
                _position++;
            }
        }

        private double ParseTerm()
        {
            var value = ParseFactor();
            while (true)
            {
                SkipWhiteSpace();
                if (TryConsume('*'))
                {
                    value *= ParseFactor();
                }
                else if (TryConsume('/'))
                {
                    value /= ParseFactor();
                }
                else
                {
                    return value;
                }
            }
        }

        private double ParseFactor()
        {
            SkipWhiteSpace();
            if (TryConsume('+'))
            {
                return ParseFactor();
            }

            if (TryConsume('-'))
            {
                return -ParseFactor();
            }

            if (TryConsume('('))
            {
                var value = ParseExpression();
                SkipWhiteSpace();
                if (!TryConsume(')'))
                {
                    throw new FormatException("Missing closing parenthesis.");
                }

                return value;
            }

            return ParseNumber();
        }

        private double ParseNumber()
        {
            SkipWhiteSpace();
            var start = _position;
            while (!IsAtEnd)
            {
                var current = _text[_position];
                if (!char.IsDigit(current) && current is not '.' and not 'e' and not 'E')
                {
                    if ((current is '+' or '-') && _position > start)
                    {
                        var previous = _text[_position - 1];
                        if (previous is 'e' or 'E')
                        {
                            _position++;
                            continue;
                        }
                    }

                    break;
                }

                _position++;
            }

            if (start == _position ||
                !double.TryParse(_text[start.._position], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw new FormatException("Invalid number.");
            }

            return value;
        }

        private bool TryConsume(char expected)
        {
            if (IsAtEnd || _text[_position] != expected)
            {
                return false;
            }

            _position++;
            return true;
        }
    }
}
