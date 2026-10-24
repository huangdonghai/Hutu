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
}
