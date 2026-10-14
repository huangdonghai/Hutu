using Microsoft.Playwright;

namespace Hutu.Reference;

/// <summary>
/// Reads a page in a real browser and writes down what it measured, so the engine has something
/// other than its own opinion to be checked against.
/// </summary>
/// <remarks>
/// The browser is the one already installed - <c>channel: msedge</c> - because the point of the
/// reference is the layout engine, not a downloaded build of one, and a machine that can run a browser
/// already has an engine worth comparing against.
/// </remarks>
public static class ReferenceCollector
{
    /// <summary>
    /// Captures the reference for one case.
    /// </summary>
    /// <param name="directory">The case's directory, which holds the page and receives the reference.</param>
    /// <param name="source">The page to read: a name inside that directory, or an address to fetch.</param>
    public static async Task<ReferenceDocument> CaptureAsync(
        ReferenceCase request,
        String directory,
        String source,
        CancellationToken cancellationToken = default)
    {
        var html = await ReadSourceAsync(Path.Combine(directory, source), cancellationToken);
        var neutralized = PageNeutralizer.Of(html);

        var pagePath = neutralized.WriteNextTo(Path.GetFullPath(Path.Combine(directory, source)));

        UseWritableScratch();

        using var playwright = await Playwright.CreateAsync();

        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = "msedge",
            Headless = true,
        });

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = request.ViewportWidth, Height = request.ViewportHeight },
            // One CSS pixel to one device pixel: at two, a browser rounds a box to the device grid and
            // the difference that shows up is the rounding, not the layout.
            DeviceScaleFactor = 1,
            ColorScheme = ColorScheme.Light,
            ForcedColors = ForcedColors.None,
            // The language the page is served as decides what text is in it, and a page that picks a
            // language from the request is another document per locale.
            Locale = "en-US",
        });

        context.SetDefaultTimeout(30_000);

        var page = await context.NewPageAsync();
        var url = new Uri(pagePath).AbsoluteUri;

        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        await SettleAsync(page);

        // A window is not the width the page is laid out in: a page that overflows vertically spends a
        // classic scrollbar on it. The case asks for a content width, so the window is widened by
        // whatever the scrollbar cost and the page read again - which is what makes the two sides
        // comparable without anyone having to know the number in advance.
        var first = await MeasureAsync(page, request);
        var scrollbar = request.ViewportWidth - first.ClientWidth;

        CapturedPage measured = first;

        if (scrollbar > 0)
        {
            await page.SetViewportSizeAsync(request.ViewportWidth + scrollbar, request.ViewportHeight);
            await SettleAsync(page);
            measured = await MeasureAsync(page, request);
        }

        var userAgent = await page.EvaluateAsync<String>("() => navigator.userAgent");

        return new ReferenceDocument
        {
            Source = source,
            Environment = new CaptureEnvironment
            {
                Browser = browser.Version,
                Channel = "msedge",
                ViewportWidth = request.ViewportWidth + scrollbar,
                ViewportHeight = request.ViewportHeight,
                DeviceScaleFactor = measured.DevicePixelRatio,
                ColorScheme = "light",
                Language = measured.Language,
                UserAgent = userAgent,
                ClientWidth = measured.ClientWidth,
                ClientHeight = measured.ClientHeight,
                ScrollWidth = measured.ScrollWidth,
                ScrollHeight = measured.ScrollHeight,
                ScriptsNeutralized = neutralized.Scripts,
                HandlersStripped = neutralized.Handlers,
                NoscriptBlocks = neutralized.Noscript,
                WebFonts = measured.WebFonts,
            },
            Elements = measured.Elements,
        };
    }

    /// <summary>
    /// Points the driver's scratch at a directory this tool made.
    /// </summary>
    /// <remarks>
    /// The node driver the Playwright package ships opens its artifacts folder under the temp directory,
    /// and a shell that is itself sandboxed is not always allowed to write to the user's temp - which
    /// fails before a browser has been named, with an error that reads like the browser's fault. The
    /// output directory is beside the driver and is always writable, since that is where the assembly
    /// that is running from was just written.
    /// </remarks>
    private static void UseWritableScratch()
    {
        var scratch = Path.Combine(AppContext.BaseDirectory, "playwright-scratch");
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("TMP", scratch);
        Environment.SetEnvironmentVariable("TEMP", scratch);
    }

    /// <summary>
    /// Waits for the page to be the page: loaded, its typefaces in place, and settled long enough for
    /// a layout that a script started on load to have finished.
    /// </summary>
    private static async Task SettleAsync(IPage page)
    {
        try
        {
            await page.EvaluateAsync("() => document.fonts.ready.then(() => true)");
        }
        catch (PlaywrightException)
        {
            // A document with no font set to wait on has nothing to be ready for.
        }

        await page.WaitForTimeoutAsync(250);
    }

    private static async Task<CapturedPage> MeasureAsync(IPage page, ReferenceCase request)
    {
        var payload = new { selectors = request.Selectors, groups = request.Groups, styles = request.Styles };

        // The script hands back a string rather than an object on purpose: Playwright's own converter
        // builds the target type by reflection and gives up on a record whose members are required,
        // while the answer is already JSON and the schema is right here.
        var json = await page.EvaluateAsync<String>(Measure, payload)
            ?? throw new InvalidOperationException("the browser answered nothing");

        return System.Text.Json.JsonSerializer.Deserialize<CapturedPage>(json, Schema.Options)
            ?? throw new InvalidOperationException("the browser's answer was not the shape asked for");
    }

    private static async Task<String> ReadSourceAsync(String page, CancellationToken cancellationToken)
    {
        if (!page.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !page.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return await File.ReadAllTextAsync(page, cancellationToken);
        }

        using var http = new HttpClient();
        return await http.GetStringAsync(page, cancellationToken);
    }

    /// <summary>
    /// The measuring script. It is given the selectors and the properties to read, and answers the
    /// boxes in document coordinates - the viewport's own scroll added back - which is the frame the
    /// layout tree is drawn in.
    /// </summary>
    private const String Measure = """
        (request) => {
            const pick = (el, styles) => {
                const r = el.getBoundingClientRect();
                const cs = getComputedStyle(el);
                const values = {};
                for (const name of styles) {
                    values[name] = cs.getPropertyValue(name);
                }

                return {
                    x: r.x + window.scrollX,
                    y: r.y + window.scrollY,
                    width: r.width,
                    height: r.height,
                    styles: values,
                };
            };

            const elements = [];

            for (const selector of request.selectors) {
                const el = document.querySelector(selector);

                if (el === null) {
                    elements.push({ selector, index: 0, found: false, x: 0, y: 0, width: 0, height: 0, styles: {} });
                    continue;
                }

                const box = pick(el, request.styles);
                elements.push({ selector, index: 0, found: true, ...box });
            }

            for (const selector of request.groups) {
                let index = 0;

                for (const el of document.querySelectorAll(selector)) {
                    const box = pick(el, request.styles);
                    elements.push({ selector, index: index++, found: true, ...box });
                }
            }

            const de = document.documentElement;

            return JSON.stringify({
                elements,
                clientWidth: de.clientWidth,
                clientHeight: de.clientHeight,
                scrollWidth: de.scrollWidth,
                scrollHeight: de.scrollHeight,
                devicePixelRatio: window.devicePixelRatio,
                language: navigator.language,
                webFonts: document.fonts ? document.fonts.size : 0,
            });
        }
        """;
}

/// <summary>What the measuring script answers, before it is split into the reference's two halves.</summary>
public sealed record CapturedPage
{
    public required List<ReferenceElement> Elements { get; init; }

    public Int32 ClientWidth { get; init; }

    public Int32 ClientHeight { get; init; }

    public Int32 ScrollWidth { get; init; }

    public Int32 ScrollHeight { get; init; }

    public Double DevicePixelRatio { get; init; }

    public String Language { get; init; } = string.Empty;

    public Int32 WebFonts { get; init; }
}
