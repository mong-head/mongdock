using System.Globalization;

namespace Mongdock.Services.Search;

/// <summary>
/// Spotlight 계산기: 작은 재귀 하강 파서 (DataTable.Compute 등 외부 평가기 없음).
/// 문법: 덧셈·뺄셈 &lt; 곱셈·나눗셈·나머지 &lt; 단항 부호 &lt; 거듭제곱(^, 오른쪽 결합) &lt; 백분율(뒤에 붙는 %) &lt; 숫자·괄호.
/// "×" "÷" 도 받고, 닫는 괄호가 모자라면 끝에서 자동으로 닫는다 (입력 중에도 결과가 보이게).
/// % 는 뒤에 숫자·여는 괄호가 오면 나머지(7 % 3 = 1), 아니면 백분율(50% = 0.5, 200*15% = 30).
/// </summary>
public static class Calculator
{
    /// <summary>
    /// 입력이 수식처럼 보이는지: 허용 문자만 + 숫자 하나 이상 + 숫자 뒤의 연산자 하나 이상
    /// ("-5" "(3" 처럼 숫자 하나뿐인 입력은 계산 결과가 의미 없어 제외).
    /// </summary>
    public static bool LooksLikeExpression(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 200) return false;
        bool digit = false, op = false;
        foreach (char c in text)
        {
            if (char.IsAsciiDigit(c)) digit = true;
            else if (c is '+' or '-' or '*' or '/' or '%' or '^' or '×' or '÷') op |= digit;
            else if (c is '.' or ' ' or '\t' or ',' or '(' or ')') { }
            else return false;
        }
        return digit && op;
    }

    /// <summary>수식 계산. 문법 오류·0 나누기·무한대면 false.</summary>
    public static bool TryEvaluate(string text, out double value)
    {
        value = 0;
        if (!LooksLikeExpression(text)) return false;
        try
        {
            var p = new Parser(text);
            double v = p.ParseExpression();
            p.SkipSpaces();
            // 남은 ')' 는 오류, 모자란 ')' 는 끝에서 자동으로 닫힌 것으로 봄
            if (!p.AtEnd) return false;
            if (double.IsNaN(v) || double.IsInfinity(v)) return false;
            value = v;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>표시용: 천 단위 쉼표 + 유효 숫자 12자리로 반올림 (0.1+0.2 → 0.3).</summary>
    public static string FormatDisplay(double v)
    {
        double r = Round(v);
        if (Math.Abs(r) >= 1e15 || (r != 0 && Math.Abs(r) < 1e-9)) return r.ToString("G12", CultureInfo.InvariantCulture);
        return r.ToString("#,0.##########", CultureInfo.InvariantCulture);
    }

    /// <summary>복사용: 쉼표 없이 (다른 앱에 붙여 넣어 바로 계산할 수 있게).</summary>
    public static string FormatPlain(double v) => Round(v).ToString("G12", CultureInfo.InvariantCulture);

    private static double Round(double v)
        => double.Parse(v.ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s) => _s = s;

        public bool AtEnd => _i >= _s.Length;

        public void SkipSpaces()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
        }

        private char Peek()
        {
            SkipSpaces();
            return _i < _s.Length ? _s[_i] : '\0';
        }

        // expr = term (('+' | '-') term)*
        public double ParseExpression()
        {
            double v = ParseTerm();
            while (true)
            {
                char c = Peek();
                if (c == '+') { _i++; v += ParseTerm(); }
                else if (c == '-') { _i++; v -= ParseTerm(); }
                else return v;
            }
        }

        // term = unary (('*' | '/' | '%') unary)*   (% 는 PostfixPercent 가 먹지 않은 경우 = 나머지)
        private double ParseTerm()
        {
            double v = ParseUnary();
            while (true)
            {
                char c = Peek();
                if (c is '*' or '×') { _i++; v *= ParseUnary(); }
                else if (c is '/' or '÷')
                {
                    _i++;
                    double d = ParseUnary();
                    if (d == 0) throw new FormatException("0 으로 나눔");
                    v /= d;
                }
                else if (c == '%')
                {
                    _i++;
                    double d = ParseUnary();
                    if (d == 0) throw new FormatException("0 으로 나눔");
                    v %= d;
                }
                else return v;
            }
        }

        // unary = ('+' | '-') unary | power     (-2^2 = -4)
        private double ParseUnary()
        {
            char c = Peek();
            if (c == '-') { _i++; return -ParseUnary(); }
            if (c == '+') { _i++; return ParseUnary(); }
            return ParsePower();
        }

        // power = postfix ('^' unary)?     (오른쪽 결합: 2^3^2 = 2^9)
        private double ParsePower()
        {
            double b = ParsePostfix();
            if (Peek() == '^')
            {
                _i++;
                double e = ParseUnary();
                return Math.Pow(b, e);
            }
            return b;
        }

        // postfix = primary ('%')*   — 뒤에 피연산자가 오지 않는 % 만 백분율
        private double ParsePostfix()
        {
            double v = ParsePrimary();
            while (Peek() == '%' && !OperandFollows(_i + 1))
            {
                _i++;
                v /= 100;
            }
            return v;
        }

        /// <summary>pos 부터 공백을 건너뛴 다음 글자가 피연산자의 시작(숫자·소수점·여는 괄호)인지.</summary>
        private bool OperandFollows(int pos)
        {
            while (pos < _s.Length && char.IsWhiteSpace(_s[pos])) pos++;
            return pos < _s.Length && (char.IsAsciiDigit(_s[pos]) || _s[pos] is '.' or '(');
        }

        // primary = number | '(' expr ')'
        private double ParsePrimary()
        {
            char c = Peek();
            if (c == '(')
            {
                _i++;
                double v = ParseExpression();
                if (Peek() == ')') _i++;
                else if (!AtEnd) throw new FormatException("괄호");
                // 끝에 도달했으면 자동으로 닫음
                return v;
            }
            return ParseNumber();
        }

        private double ParseNumber()
        {
            SkipSpaces();
            int start = _i;
            bool dot = false;
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (char.IsAsciiDigit(c) || c == ',') _i++; // 1,000 처럼 쉼표 자릿수 구분은 무시
                else if (c == '.' && !dot) { dot = true; _i++; }
                else break;
            }
            string token = _s[start.._i].Replace(",", "");
            if (token.Length == 0 || token == ".") throw new FormatException("숫자 없음");
            return double.Parse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        }
    }
}
