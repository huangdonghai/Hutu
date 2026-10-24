using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using NUnit.Framework;
using System;
using System.Linq;

namespace Hutu.Tests;

/// <summary>A scratch pad for the findings; not part of the suite's story.</summary>
[Explicit]
public sealed class ScratchTests
{
    private static readonly String[] Sheets =
    {
        ".x { row-gap: 100px; column-gap: 10px; grid: subgrid / subgrid }",
        ".x { row-gap: 100px; column-gap: 10px; grid-template-rows: subgrid; grid-template-columns: subgrid }",
        ".x { grid-template-columns: 100px; grid-auto-rows: 50px; grid: 1fr / 2fr }",
        ".x { row-gap: 100px; grid-template: subgrid / subgrid }",
        ".x { grid-area: 2 / 3; grid-row-start: 5 }",
    };

    [Test]
    public void WhatTheShorthandLeavesBehind()
    {
        foreach (var css in Sheets)
        {
            TestContext.Progress.WriteLine("== <" + css + ">");

            var style = (ICssStyleSheet)new CssParser().ParseStyleSheet(css);

            foreach (var rule in style.Rules.OfType<ICssStyleRule>())
            {
                foreach (var property in rule.Style)
                {
                    TestContext.Progress.WriteLine($"   {property.Name} = <{property.Value}>");
                }
            }
        }
    }

    [Test]
    public void WhatTheParserMakesOfACalcSum()
    {
        var parser = new CssParser();

        foreach (var text in new[]
        {
            "calc(15% + 7px)", "calc(10px + 5%)", "calc(100% - 20px)", "calc(100% / 5)", "calc(20%)",
        })
        {
            var sheet = (ICssStyleSheet)parser.ParseStyleSheet(".x { width: " + text + " }");
            var rule = sheet.Rules.OfType<ICssStyleRule>().Single();
            var value = rule.Style
                .OfType<AngleSharp.Css.Dom.ICssProperty>()
                .FirstOrDefault(property => property.Name == "width")?.RawValue;

            TestContext.Progress.WriteLine($"== <{text}> is <{value?.GetType().Name}> css <{value?.CssText}>");

            if (value is AngleSharp.Css.Values.CssCalcValue calc)
            {
                foreach (var basis in new[] { 100.0, 200.0, 400.0 })
                {
                    var ok = Hutu.Rendering.LengthReader.TryFoldCalcRaw(calc, basis, out var unit, out var number);
                    TestContext.Progress.WriteLine($"   basis {basis}: ok={ok} unit={unit} value={number}");
                }
            }
        }
    }
}
