using System.Windows;

namespace RtlTerminal.Services
{
    /// <summary>
    /// Very lightweight bidi heuristic: scans a line of text for the first "strong"
    /// directional character (Hebrew, Arabic vs. Latin/digits) and uses that to decide
    /// the paragraph's FlowDirection. This is not a full Unicode Bidirectional Algorithm
    /// implementation, but it's enough for typical console output where a line is
    /// predominantly one language (a Hebrew path, an English command, etc.).
    /// </summary>
    public static class RtlTextHelper
    {
        public static FlowDirection DetectFlowDirection(string line)
        {
            foreach (char c in line)
            {
                // Skip whitespace
                if (char.IsWhiteSpace(c))
                    continue;

                if (IsStrongRtl(c))
                    return FlowDirection.RightToLeft;

                if (IsStrongLtr(c))
                    return FlowDirection.LeftToRight;
            }

            // No strong directional character found (numbers/punctuation/only whitespace) -
            // default to LTR, which is the safer default for a terminal prompt.
            return FlowDirection.LeftToRight;
        }

        public static bool IsStrongRtl(char c)
        {
            // Hebrew: U+0590–U+05FF, Hebrew presentation forms: U+FB1D–U+FB4F
            // Arabic: U+0600–U+06FF, U+0750–U+077F, Arabic presentation forms: U+FB50–U+FDFF, U+FE70–U+FEFF
            return (c >= '\u0590' && c <= '\u05FF') ||
                   (c >= '\uFB1D' && c <= '\uFB4F') ||
                   (c >= '\u0600' && c <= '\u06FF') ||
                   (c >= '\u0750' && c <= '\u077F') ||
                   (c >= '\uFB50' && c <= '\uFDFF') ||
                   (c >= '\uFE70' && c <= '\uFEFF');
        }

        public static bool IsStrongLtr(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
        }
    }
}
