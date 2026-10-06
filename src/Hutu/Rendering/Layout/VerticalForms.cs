using System;

namespace Hutu.Rendering.Layout;

/// <summary>
/// Maps the wide punctuation of horizontal CJK text onto the vertical presentation form it is set in
/// when the text runs down a column: <c>、。「」『』（）</c> and the rest of the marks a column turns on
/// their side.
/// </summary>
/// <remarks>
/// Unicode reserved U+FE10-U+FE4F for exactly these forms, and <see cref="TextChunker.IsUpright"/>
/// already counts them as upright - this is the table that says which character each one stands for.
/// A font is expected to make the substitution itself, through its <c>vert</c> feature, and that is
/// what a column asks for first; this table is the fallback for the fonts that do not carry the
/// feature, so the two together are what keeps a column's punctuation right.
/// <para>
/// Only characters that are already a full em wide are mapped. A narrow character's vertical form is
/// wider than the character it replaces, so substituting one would change the step the layout gave it
/// - and nothing here is allowed to change a run's advance. The characters with a vertical form but
/// no wide horizontal form, the ASCII <c>,</c> and <c>(</c> among them, are turned by
/// <c>text-orientation: mixed</c> rather than set upright, so a column never asks for them.
/// </para>
/// </remarks>
public static class VerticalForms
{
    /// <summary>
    /// Gets the vertical presentation form of a character, reporting whether it has one. A character
    /// that is already a vertical form, or that is not wide punctuation, is handed back unchanged.
    /// </summary>
    public static Boolean TryGet(Char character, out Char vertical)
    {
        vertical = character switch
        {
            // The two句读 a CJK column turns on their side: the comma rises to the top of its em and
            // the full stop with it.
            '\u3001' => '\uFE11',   // 、 ideographic comma
            '\u3002' => '\uFE12',   // 。 ideographic full stop

            // Fullwidth commas, colons and the rest of the marks that keep their shape but move.
            '\uFF0C' => '\uFE10',   // ， fullwidth comma
            '\uFF1A' => '\uFE13',   // ： fullwidth colon
            '\uFF1B' => '\uFE14',   // ； fullwidth semicolon
            '\uFF01' => '\uFE15',   // ！ fullwidth exclamation mark
            '\uFF1F' => '\uFE16',   // ？ fullwidth question mark

            // Quotation and corner brackets, which open downwards once the column turns them.
            '\u300C' => '\uFE41',   // 「
            '\u300D' => '\uFE42',   // 」
            '\u300E' => '\uFE43',   // 『
            '\u300F' => '\uFE44',   // 』

            // Angle brackets.
            '\u3008' => '\uFE3F',   // 〈
            '\u3009' => '\uFE40',   // 〉
            '\u300A' => '\uFE3D',   // 《
            '\u300B' => '\uFE3E',   // 》

            // Round and square brackets.
            '\uFF08' => '\uFE35',   // （
            '\uFF09' => '\uFE36',   // ）
            '\uFF3B' => '\uFE47',   // ［
            '\uFF3D' => '\uFE48',   // ］
            '\uFF5B' => '\uFE37',   // ｛
            '\uFF5D' => '\uFE38',   // ｝

            // Lenticular and tortoise shell brackets.
            '\u3010' => '\uFE3B',   // 【
            '\u3011' => '\uFE3C',   // 】
            '\u3014' => '\uFE39',   // 〔
            '\u3015' => '\uFE3A',   // 〕
            '\u3016' => '\uFE17',   // 〖
            '\u3017' => '\uFE18',   // 〗

            // Dashes and leaders, which stand on end in a column.
            '\u2014' => '\uFE31',   // — em dash
            '\u2013' => '\uFE32',   // – en dash
            '\u2026' => '\uFE19',   // … horizontal ellipsis
            '\u2025' => '\uFE30',   // ‥ two dot leader

            _ => character,
        };

        return vertical != character;
    }
}
