using System.Windows;

namespace RtlTerminal.Services
{
    public enum CharClass { Ltr, Rtl, Neutral }

    /// <summary>
    /// Lightweight bidi support for a monospace terminal grid: detects a line's base direction,
    /// and builds a logical-column -> visual-column mapping that reorders directional *runs*
    /// (contiguous spans of Hebrew/Arabic vs. Latin/neutral) as a block, instead of naively
    /// mirroring every individual character in the line. That naive full-mirror approach is
    /// correct for the RTL runs themselves, but wrong for embedded LTR runs (e.g. an English
    /// word inside a Hebrew line) - it would reverse the letters within that word too
    /// ("Windows" -> "swodniW"). This class fixes that: LTR runs keep their internal
    /// left-to-right character order and only the run's block position gets moved to the
    /// mirrored slot; only RTL runs get their internal character order reversed.
    ///
    /// This is not a full Unicode Bidirectional Algorithm implementation (no explicit embedding
    /// levels, no directional isolates), but it correctly handles the common terminal case: a
    /// predominantly-Hebrew/Arabic line with embedded Latin words, numbers, or punctuation.
    /// </summary>
    public static class RtlTextHelper
    {
        public static FlowDirection DetectFlowDirection(string line)
        {
            // Deliberately NOT "first strong character wins": a line can start with an English
            // word and contain Hebrew later (e.g. an English flag before a Hebrew path), and it
            // still needs to be treated as an RTL-base line so the Hebrew run gets mirrored
            // correctly. Any RTL character anywhere in the line makes the whole line RTL-base.
            foreach (char c in line)
            {
                if (IsStrongRtl(c))
                    return FlowDirection.RightToLeft;
            }
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

        private static CharClass Classify(char c)
        {
            if (IsStrongRtl(c)) return CharClass.Rtl;
            if (IsStrongLtr(c)) return CharClass.Ltr;
            return CharClass.Neutral; // digits, punctuation, spaces
        }

        /// <summary>
        /// Builds a logical-column -> visual-column map for a fixed-width row of
        /// <paramref name="rowText"/>.Length cells. If the row's base direction is LTR, this is
        /// the identity map. If RTL, directional runs are reordered as blocks: an RTL run's
        /// characters are mirrored (reversed) within its (mirrored) slot; an LTR run's
        /// characters keep their original left-to-right order within its (mirrored) slot.
        /// </summary>
        public static int[] BuildLogicalToVisualMap(string rowText, bool baseRtl)
        {
            int c = rowText.Length;
            var map = new int[c];

            if (!baseRtl)
            {
                for (int i = 0; i < c; i++) map[i] = i;
                return map;
            }

            // Resolve neutrals by carrying forward the last strong direction seen
            // (defaulting to the row's base direction at the very start of the line).
            var resolved = new CharClass[c];
            CharClass current = baseRtl ? CharClass.Rtl : CharClass.Ltr;
            for (int i = 0; i < c; i++)
            {
                CharClass cls = Classify(rowText[i]);
                if (cls == CharClass.Neutral)
                {
                    resolved[i] = current;
                }
                else
                {
                    resolved[i] = cls;
                    current = cls;
                }
            }

            // Walk resolved[] as runs of equal class and place each run as a block.
            int runStart = 0;
            while (runStart < c)
            {
                int runEnd = runStart;
                while (runEnd < c && resolved[runEnd] == resolved[runStart]) runEnd++;

                int len = runEnd - runStart;
                int blockStart = c - runEnd; // mirrored position of this block's leftmost visual cell

                if (resolved[runStart] == CharClass.Rtl)
                {
                    // Reverse the characters within this run.
                    for (int k = 0; k < len; k++)
                        map[runStart + k] = c - 1 - (runStart + k);
                }
                else
                {
                    // Keep left-to-right internal order; only the block's position is mirrored.
                    for (int k = 0; k < len; k++)
                        map[runStart + k] = blockStart + k;
                }

                runStart = runEnd;
            }

            return map;
        }
    }
}
