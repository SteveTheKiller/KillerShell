# Terminal Unicode cells

The terminal uses Unicode 17.0.0 East Asian Width data. Wide and fullwidth characters use two columns. Ambiguous characters and ordinary characters use one column. Regional indicator symbols use two columns each.

Combining marks (Mn, Mc, Me), format characters (Cf), variation selectors, and emoji skin modifiers use no additional columns. They attach to the preceding character on the current row, including when that character filled the last column. A leading mark with no preceding character is ignored. Each cell retains up to 64 UTF-16 units of these suffixes. A suffix scalar that would exceed that limit is ignored as a whole, so a surrogate pair is never split.

This is a cell model, not full grapheme or emoji-sequence layout. ZWJ sequences and flags retain the widths of their individual visible scalars. For example, woman + ZWJ + laptop uses four columns. Variation selectors do not change the base character's assigned width. Rendering shapes each base with its retained suffix and confines it to its assigned columns. Copy and UI Automation expose the same retained text, without extra spaces for the second half of a wide character. UI Automation character movement treats each base and its suffix as one unit.

A wide character wraps before the right edge when only one column remains. With wrapping disabled, or in a one-column terminal, that unrepresentable character becomes U+FFFD in the available column. Erasing or overwriting either half clears the whole old character. Insert, delete, and resize discard clipped halves. Resizing keeps the existing row layout instead of reflowing it; scrollback retains its original cells.

The width tables come from [EastAsianWidth.txt](https://www.unicode.org/Public/17.0.0/ucd/EastAsianWidth.txt) and [UnicodeData.txt](https://www.unicode.org/Public/17.0.0/ucd/UnicodeData.txt). Their [license](Unicode-LICENSE.txt) is embedded in the portable executable.
