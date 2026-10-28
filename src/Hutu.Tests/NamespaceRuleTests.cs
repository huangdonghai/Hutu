using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using NUnit.Framework;

namespace Hutu.Tests;

/// <summary>
/// A namespace declared after the sheet has left its prologue is invalid and ignored, and the rules
/// written around it still count (CSS Namespaces 3 §3). The misplaced statement used to throw, which
/// abandoned the rest of the sheet.
/// </summary>
[TestFixture]
public sealed class NamespaceRuleTests
{
    [Test]
    public void AMisplacedNamespaceIsIgnoredAndTheRulesAfterItStillParse()
    {
        var sheet = new CssParser().ParseStyleSheet(
            "div { color: red } @namespace x \"http://www.w3.org/\"; span { color: green }");

        Assert.That(sheet.Rules.Length, Is.EqualTo(2), "the misplaced namespace is left out, the two style rules are kept");
        Assert.That(sheet.Rules[0].Type, Is.EqualTo(CssRuleType.Style), "the rule before it parsed");
        Assert.That(sheet.Rules[1].Type, Is.EqualTo(CssRuleType.Style), "and so did the rule after it");

        Assert.That(
            ((ICssStyleRule)sheet.Rules[1]).Style.TryGetProperty("color")?.Value,
            Is.EqualTo("rgba(0, 128, 0, 1)"),
            "with the declaration the page wrote behind the misplaced namespace");
    }
}
