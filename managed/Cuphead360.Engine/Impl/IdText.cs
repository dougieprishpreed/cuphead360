namespace Cuphead360.Engine
{
    /// <summary>
    /// A 64-bit id in decimal, as long.ToString() writes it, with 32-bit
    /// arithmetic only. The converted texture and audio files are named after
    /// their objects' path ids, and on the console's game thread long
    /// formatting took the sign from the low word (rounds 7-10): names built here are right whatever the cause.
    /// </summary>
    public static class IdText
    {
        public static string Of(long id)
        {
            uint hi = (uint)((ulong)id >> 32), lo = (uint)id;
            bool negative = (hi & 0x80000000u) != 0;
            if (negative)
            {
                // Two's complement negation, word by word.
                lo = ~lo + 1;
                hi = ~hi + (lo == 0 ? 1u : 0u);
            }
            // Four 16-bit limbs, most significant first, divided by 10 until zero.
            var limbs = new uint[] { hi >> 16, hi & 0xFFFF, lo >> 16, lo & 0xFFFF };
            var digits = new char[21];
            int at = digits.Length;
            do
            {
                uint rem = 0;
                bool any = false;
                for (int i = 0; i < 4; i++)
                {
                    uint cur = (rem << 16) | limbs[i];
                    limbs[i] = cur / 10;
                    rem = cur % 10;
                    if (limbs[i] != 0) any = true;
                }
                digits[--at] = (char)('0' + rem);
                if (!any) break;
            } while (true);
            if (negative) digits[--at] = '-';
            return new string(digits, at, digits.Length - at);
        }
    }
}
