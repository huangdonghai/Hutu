using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using Hutu.Rendering;
using Hutu.Rendering.Boxes;
using Hutu.Rendering.Media;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hutu.Tests;

/// <summary>
/// The bucket index only prunes rules that provably cannot match: for every element, the
/// rules found through bucket candidates plus matching must be exactly the rules a linear
/// walk over all rules plus matching finds, in the same order.
/// </summary>
[TestFixture]
public sealed class BucketMatchTests
{
    [Test]
    public async Task BucketCandidatesMatchLinearWalk()
    {
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>" +
            "div { color: #000001 }" +
            ".note { color: #000002 }" +
            "#main { color: #000003 }" +
            "* { color: #000004 }" +
            "div.note { color: #000005 }" +
            "div .inner { color: #000006 }" +
            "p, .note, #main { color: #000007 }" +
            "span[data-x] { color: #000008 }" +
            "div:first-child { color: #000009 }" +
            "ul li { color: #00000a }" +
            "</style></head>" +
            "<body style='margin:0'>" +
            "<div id='main' class='note'>a<span class='inner'>b</span></div>" +
            "<p class='note' data-x='1'>c</p>" +
            "<ul><li>d</li><li>e</li></ul>" +
            "</body></html>");

        AssertBucketsMatchLinear(document);
    }

    [Test]
    public async Task BucketCandidatesMatchLinearWalkOnLargePage()
    {
        using var view = CreateView(900);
        var document = await view.SetHtml(
            "<html><head><style>" +
            "p { color: #000001 }" +
            ".note { color: #000002 }" +
            "span.noteid { color: #000003 }" +
            "span.bk { color: #000004 }" +
            "p.note span { color: #000005 }" +
            "h1, h3 { color: #000006 }" +
            "</style></head>" +
            "<body style='margin:0'>" +
            "<h1>t</h1>" +
            String.Concat(Enumerable.Repeat(
                "<p class='note'>x<span class='noteid'>1</span><span class='bk'>2</span></p>", 50)) +
            "</body></html>");

        AssertBucketsMatchLinear(document);
    }

    [Test]
    public async Task ASelectorListThatRepeatsItsKeyStillKeysTheRule()
    {
        // `.a .note, .b .note` asks for the class `note` of both halves. The key list deduplicates,
        // so reading its length said the second half had produced no key at all and the rule fell into
        // the bucket every element of the page is tested against - which is most of a build on a site
        // that writes its rules this way. Pruning must still be exact, which is what the comparison
        // against the linear walk checks.
        using var view = CreateView();
        var document = await view.SetHtml(
            "<html><head><style>" +
            ".wrap .note, .other .note { color: #000002 }" +
            "#main .note, .note { color: #000003 }" +
            "p .note, div .note, span .note { color: #000004 }" +
            "</style></head>" +
            "<body style='margin:0'><div id='main' class='wrap'><p><span class='note'>x</span></p></div>" +
            "<div class='other'><b class='note'>y</b></div></body></html>");

        AssertBucketsMatchLinear(document);

        Assert.That(
            KeysOf(".wrap .note, .other .note"),
            Is.EqualTo(new[] { "Class:note" }),
            "two halves that ask for the same key still ask for it");
        Assert.That(
            KeysOf(".wrap .note, .other .mark"),
            Is.EqualTo(new[] { "Class:note", "Class:mark" }));
        Assert.That(
            KeysOf(":link, :visited"),
            Is.Null,
            "a selector with nothing to key on stays where every element is tested against it");
    }

    private static String[]? KeysOf(String text)
    {
        var sheet = new CssParser().ParseStyleSheet(text + " { color: red }");
        var keys = SelectorKeyExtractor.Extract(((ICssStyleRule)sheet.Rules[0]).Selector);

        return keys?.Select(key => $"{key.Kind}:{key.Value}").ToArray();
    }

    private static void AssertBucketsMatchLinear(IDocument document)
    {
        var device = new HutuRenderDevice(600, 600);
        var sheets = SheetsOf(document, device).ToList();
        var index = RuleBucketIndex.Build(sheets, device);
        var rules = index.Rules;
        var scope = document.DocumentElement;
        var checkedCount = 0;

        foreach (var element in document.QuerySelectorAll("*").OfType<IElement>())
        {
            var linear = MatchLinear(rules, element, scope);
            var bucketed = MatchBucketed(index, element, scope);

            Assert.That(
                bucketed.Select(m => m.Rule).ToList(),
                Is.EqualTo(linear.Select(m => m.Rule).ToList()),
                $"bucket candidates find the same rules as the linear walk for <{element.LocalName}>");
            Assert.That(
                bucketed.Select(m => m.Specificity).ToList(),
                Is.EqualTo(linear.Select(m => m.Specificity).ToList()),
                $"specificities agree for <{element.LocalName}>");

            checkedCount++;
        }

        Assert.That(checkedCount, Is.GreaterThan(0), "the document has elements to check");
    }

    private static List<Match> MatchLinear(List<ICssStyleRule> rules, IElement element, IElement? scope)
    {
        var matches = new List<Match>();

        foreach (var rule in rules)
        {
            if (rule.TryMatch(element, scope, out var specificity))
            {
                matches.Add(new Match(rule, specificity));
            }
        }

        return matches;
    }

    private static List<Match> MatchBucketed(RuleBucketIndex index, IElement element, IElement? scope)
    {
        var matches = new List<Match>();
        var rules = index.Rules;
        var candidates = index.CollectCandidates(element);

        foreach (var candidate in candidates)
        {
            var rule = rules[candidate];

            if (rule.TryMatch(element, scope, out var specificity))
            {
                matches.Add(new Match(rule, specificity));
            }
        }

        return matches;
    }

    private static IEnumerable<ICssStyleSheet> SheetsOf(IDocument document, HutuRenderDevice device)
    {
        var provider = document.Context.GetServices<ICssDefaultStyleSheetProvider>();

        foreach (var sheet in provider.SelectMany(p => new[] { p.Default }))
        {
            yield return sheet;
        }

        foreach (var sheet in document.GetStyleSheets().OfType<ICssStyleSheet>())
        {
            yield return sheet;
        }
    }

    private static HutuViewInternal CreateView(Int32 viewportWidth = 600)
    {
        var view = new HutuViewInternal(new FakeTextMeasurer());
        view.SetViewportSize(viewportWidth, 600);
        return view;
    }

    private sealed record Match(ICssStyleRule Rule, Priority Specificity);
}
