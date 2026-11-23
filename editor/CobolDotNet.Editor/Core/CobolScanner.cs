// Splits one line of COBOL into colored pieces.
//
// No Visual Studio types here: the same code runs in the unit tests
// (editor\CobolDotNet.Editor.Tests) and inside the editor.
//
// Fixed format (the default):
//   columns 1-6    sequence number           -> Sequence
//   column  7      indicator: '*' or '/' makes the line a comment,
//                  '-' continues a literal, 'D' is a debugging line
//   columns 8-72   code (area A = 8-11, area B = 12-72)
//   columns 73-80  identification area, ignored by the compiler -> Identification
// Free format: the whole line is code. "*>" starts a comment in both formats.
using System;
using System.Collections.Generic;

namespace CobolDotNet.Editor.Core
{
    public enum TokenKind
    {
        Sequence,       // columns 1-6
        Identification, // columns 73+
        Comment,
        Keyword,
        Function,       // FUNCTION xxx
        String,
        Number,
        Picture,        // the character-string after PIC / PICTURE
        ParagraphName,  // a paragraph or section name where it is defined
        Identifier,
        Operator,       // punctuation, including the period
    }

    public struct Token
    {
        public int Start;
        public int Length;
        public TokenKind Kind;

        public Token(int start, int length, TokenKind kind)
        {
            Start = start;
            Length = length;
            Kind = kind;
        }

        public override string ToString() => Kind + "@" + Start + "+" + Length;
    }

    public static class CobolScanner
    {
        public const int IndicatorColumn = 6;   // 0-based index of column 7
        public const int AreaA = 7;             // column 8
        public const int AreaB = 11;            // column 12
        public const int CodeEnd = 72;          // columns 73+ are identification

        public static List<Token> ScanLine(string line, bool fixedFormat)
        {
            var tokens = new List<Token>();
            if (string.IsNullOrEmpty(line)) return tokens;

            int codeStart = 0, codeEnd = line.Length;
            if (fixedFormat)
            {
                tokens.Add(new Token(0, Math.Min(6, line.Length), TokenKind.Sequence));
                if (line.Length <= IndicatorColumn) return tokens;

                codeEnd = Math.Min(CodeEnd, line.Length);
                char ind = line[IndicatorColumn];
                if (ind == '*' || ind == '/')
                {
                    tokens.Add(new Token(IndicatorColumn, codeEnd - IndicatorColumn, TokenKind.Comment));
                    AddIdentification(tokens, line);
                    return tokens;
                }
                if (ind == 'D' || ind == 'd')
                    tokens.Add(new Token(IndicatorColumn, 1, TokenKind.Keyword));
                else if (ind == '-')
                    tokens.Add(new Token(IndicatorColumn, 1, TokenKind.Operator));
                codeStart = AreaA;
            }

            ScanCode(line, codeStart, codeEnd, fixedFormat, tokens);
            if (fixedFormat) AddIdentification(tokens, line);
            return tokens;
        }

        static void AddIdentification(List<Token> tokens, string line)
        {
            if (line.Length > CodeEnd)
                tokens.Add(new Token(CodeEnd, line.Length - CodeEnd, TokenKind.Identification));
        }

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '-' || c == '_';

        static void ScanCode(string line, int start, int end, bool fixedFormat, List<Token> tokens)
        {
            int i = start;
            string prevWord = null;      // previous word on this line, upper case
            bool firstToken = true;
            int firstTokenIndex = -1;    // index in tokens of the line's first word

            while (i < end)
            {
                char c = line[i];
                if (c == ' ' || c == '\t') { i++; continue; }

                // *> comment to the end of the line
                if (c == '*' && i + 1 < end && line[i + 1] == '>')
                {
                    tokens.Add(new Token(i, end - i, TokenKind.Comment));
                    break;
                }

                // literal: "..." or '...' (a doubled quote stays inside)
                if (c == '"' || c == '\'')
                {
                    int j = ScanString(line, i, end);
                    tokens.Add(new Token(i, j - i, TokenKind.String));
                    i = j;
                    prevWord = null;
                    firstToken = false;
                    continue;
                }

                // the picture string after PIC / PICTURE [IS]
                if (prevWord == "PIC" || prevWord == "PICTURE")
                {
                    int j = i;
                    while (j < end && line[j] != ' ' && line[j] != '\t') j++;
                    if (j - i == 2 && string.Compare(line, i, "IS", 0, 2, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        tokens.Add(new Token(i, 2, TokenKind.Keyword));   // PIC IS 9(4)
                        i = j;
                        firstToken = false;
                        continue;
                    }
                    // a period followed by a space or the end ends the entry
                    if (j > i + 1 && line[j - 1] == '.') j--;
                    if (j > i)
                    {
                        tokens.Add(new Token(i, j - i, TokenKind.Picture));
                        i = j;
                        prevWord = null;
                        firstToken = false;
                        continue;
                    }
                }

                // a signed number: +5, -1.25 (only where no word came just before)
                if ((c == '+' || c == '-') && i + 1 < end && char.IsDigit(line[i + 1])
                    && (i == start || !IsWordChar(line[i - 1])))
                {
                    int j = ScanNumber(line, i + 1, end);
                    if (j == end || !IsWordChar(line[j]))
                    {
                        tokens.Add(new Token(i, j - i, TokenKind.Number));
                        i = j;
                        prevWord = null;
                        firstToken = false;
                        continue;
                    }
                }

                if (char.IsLetterOrDigit(c) || c == '_')
                {
                    int j = i;
                    while (j < end && IsWordChar(line[j])) j++;
                    // a word never ends with a hyphen ("A -1" is handled above)
                    while (j > i + 1 && line[j - 1] == '-') j--;
                    string word = line.Substring(i, j - i);

                    // X"41", N"...", Z"...", B"...", H"...", NX"..."
                    if (j < end && (line[j] == '"' || line[j] == '\'') && IsLiteralPrefix(word))
                    {
                        int k = ScanString(line, j, end);
                        tokens.Add(new Token(i, k - i, TokenKind.String));
                        i = k;
                        prevWord = null;
                        firstToken = false;
                        continue;
                    }

                    // 123 or 1.5 (a trailing period is the sentence end, not a decimal point)
                    if (char.IsDigit(c))
                    {
                        int n = ScanNumber(line, i, end);
                        if (n >= j)
                        {
                            tokens.Add(new Token(i, n - i, TokenKind.Number));
                            i = n;
                            prevWord = null;
                            firstToken = false;
                            continue;
                        }
                    }

                    string upper = word.ToUpperInvariant();
                    TokenKind kind;
                    if (prevWord == "FUNCTION" && CobolWords.Functions.Contains(word))
                        kind = TokenKind.Function;
                    else if (CobolWords.Reserved.Contains(word))
                        kind = TokenKind.Keyword;
                    else
                        kind = TokenKind.Identifier;

                    if (firstToken && kind == TokenKind.Identifier)
                        firstTokenIndex = tokens.Count;
                    tokens.Add(new Token(i, j - i, kind));
                    prevWord = upper;
                    firstToken = false;
                    i = j;
                    continue;
                }

                // anything else: punctuation and operators, one character at a time
                tokens.Add(new Token(i, 1, TokenKind.Operator));
                if (c != '.' && c != ',' && c != ';') prevWord = null;
                firstToken = false;
                i++;
            }

            if (firstTokenIndex >= 0 && IsParagraphHeader(line, tokens, firstTokenIndex, fixedFormat))
            {
                var t = tokens[firstTokenIndex];
                tokens[firstTokenIndex] = new Token(t.Start, t.Length, TokenKind.ParagraphName);
            }
        }

        /// <summary>
        /// "NAME." alone on its line, or "NAME SECTION.", starting in area A
        /// (fixed format) is where a paragraph or section is defined.
        /// </summary>
        static bool IsParagraphHeader(string line, List<Token> tokens, int index, bool fixedFormat)
        {
            var name = tokens[index];
            if (fixedFormat && (name.Start < AreaA || name.Start >= AreaB)) return false;

            int next = index + 1;
            if (next < tokens.Count && tokens[next].Kind == TokenKind.Keyword
                && string.Equals(line.Substring(tokens[next].Start, tokens[next].Length), "SECTION",
                                 StringComparison.OrdinalIgnoreCase))
                next++;
            if (next >= tokens.Count || tokens[next].Kind != TokenKind.Operator
                || line[tokens[next].Start] != '.')
                return false;
            for (int k = next + 1; k < tokens.Count; k++)
                if (tokens[k].Kind != TokenKind.Comment && tokens[k].Kind != TokenKind.Identification)
                    return false;
            return true;
        }

        static bool IsLiteralPrefix(string w)
        {
            switch (w.ToUpperInvariant())
            {
                case "X": case "N": case "Z": case "B": case "H": case "NX": case "BX": return true;
                default: return false;
            }
        }

        /// <summary>From an opening quote to just past the closing one (or the code end).</summary>
        static int ScanString(string line, int i, int end)
        {
            char q = line[i];
            int j = i + 1;
            while (j < end)
            {
                if (line[j] == q)
                {
                    if (j + 1 < end && line[j + 1] == q) { j += 2; continue; }
                    return j + 1;
                }
                j++;
            }
            return end;
        }

        /// <summary>Digits, then optionally '.' and more digits.</summary>
        static int ScanNumber(string line, int i, int end)
        {
            int j = i;
            while (j < end && char.IsDigit(line[j])) j++;
            if (j + 1 < end && line[j] == '.' && char.IsDigit(line[j + 1]))
            {
                j++;
                while (j < end && char.IsDigit(line[j])) j++;
            }
            return j;
        }
    }
}
