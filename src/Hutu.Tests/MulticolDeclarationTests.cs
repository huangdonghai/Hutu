using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using NUnit.Framework;
using System;

namespace Hutu.Tests;

/// <summary>
/// The declarations a wrapped multicol container declares survive a serialize-and-reparse round trip:
/// a keyword-only newer property such as <c>column-wrap: wrap</c> comes back with its value, the same
/// way the length-based <c>column-height</c> beside it does (CSS Multicol 2 §3).
/// </summary>
[TestFixture]
public sealed class MulticolDeclarationTests
{
    private const String Sheet = ".container { column-wrap: wrap; column-height: 60px; }";

    [Test]
    public void AColumnWrapSurvivesARoundTrip()
    {
        var first = new CssParser().ParseStyleSheet(Sheet);
        var rule = AssertRule(first);
        var text = rule.Style.CssText;

        var second = new CssParser().ParseStyleSheet($".container {{ {text} }}");
        var reparsed = AssertRule(second);

        Assert.That(
            reparsed.Style.TryGetProperty("column-wrap")?.Value,
            Is.EqualTo("wrap").After(0, 0),
            $"the round trip keeps column-wrap: the first pass serialised '{text}'");
        Assert.That(
            reparsed.Style.TryGetProperty("column-height")?.Value,
            Is.EqualTo("60px"),
            "and column-height beside it");
    }

    private static ICssStyleRule AssertRule(AngleSharp.Css.Dom.ICssStyleSheet sheet)
    {
        Assert.That(sheet.Rules.Length, Is.GreaterThan(0), "the sheet parsed to a rule");
        var rule = (ICssStyleRule)sheet.Rules[0];
        Assert.That(rule.Style.TryGetProperty("column-wrap"), Is.Not.Null, "column-wrap parsed");
        Assert.That(rule.Style.TryGetProperty("column-height"), Is.Not.Null, "column-height parsed");
        return rule;
    }
}
