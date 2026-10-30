
using AngleSharp.Dom;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Rendering;
using Hutu.Rendering;
using Hutu.Rendering.Interaction;
using Sample.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Sample.Avalonia.Views;

public partial class MainView : ContentPage
{
    // Where a reader is put when there is no page remembered to put them on. A path with no scheme is
    // taken as an embedded resource, which is how the sample pages are addressed everywhere else.
    private const string HomeAddress = "index.htm";

    /// <summary>How many device pixels a millimetre is worth at 96 dpi.</summary>
    private const Double PixelsPerMillimetre = 96.0 / 25.4;

    /// <summary>A paper the list offers, measured in millimetres.</summary>
    private sealed record Paper(String Name, Double WidthMm, Double HeightMm);

    private static readonly Paper[] Papers =
    {
        new("A4 210×297", 210, 297),
        new("A5 148×210", 148, 210),
        new("A6 105×148", 105, 148),
        new("B5 176×250", 176, 250),
        new("B6 125×176", 125, 176),
        new("B7 88×125", 88, 125),
        new("16开 185×260", 185, 260),
        new("32开 130×185", 130, 185),
    };

    /// <summary>The margins every paper is printed with, 15 mm on each side.</summary>
    private static readonly Hutu.Rendering.Layout.Thickness PageMargins = new(
        15 * PixelsPerMillimetre,
        15 * PixelsPerMillimetre,
        15 * PixelsPerMillimetre,
        15 * PixelsPerMillimetre);


    // The addresses that have been loaded, and where in them the reader stands. A fresh address cuts
    // off whatever lay ahead of the current one, which is what makes the forward button go away after
    // navigating somewhere new.
    private readonly List<string> _history = new();
    private int _position = -1;

    public MainView()
    {
        InitializeComponent();

        // Init some controls
        MyHutuView.AddEmbeddedResourceSource(SampleUtils.GetSampleAssembly(), "Samples");

        // A link the reader follows loads in place, the way a typed address does, and joins the same
        // history - so the back button walks back out of it.
        MyHutuView.NavigationRequested += Follow;

        // The zoom indicator tracks the view's zoom level, including the keyboard and wheel shortcuts.
        MyHutuView.ZoomChanged += UpdateZoomLabel;
        UpdateZoomLabel();

        // The page count changes whenever a document is loaded, so the paging controls follow it.
        MyHutuView.PagesChanged += UpdatePages;

        // The paper list stands on A4, which is what the paging starts on.
        PaperBox.ItemsSource = Papers;
        PaperBox.SelectedIndex = 0;
        UpdatePages();

        UpdateNavigation();

        // The page the reader last had open is opened again, so the app comes back where it was left
        // rather than at nothing.
        _ = RestoreAsync();
    }

    /// <summary>
    /// Loads a link the reader followed.
    /// </summary>
    /// <remarks>
    /// The address arrives absolute, so the reader is shown where they went rather than the relative
    /// href that took them there. A link that points into the document it is in never reaches this: it
    /// is a scroll, and the view has already made it.
    /// </remarks>
    private async void Follow(LinkTarget link)
    {
        if (link.IsDownload)
        {
            // There is nowhere to put a saved file yet, so the request is reported rather than acted on.
            Debug.WriteLine($"Download requested: {link.Url} ({link.Target ?? "this view"})");
            return;
        }

        // One view is all there is, so a link asking for another context - a new window, a named frame
        // - is opened in this one. The request still carries what it asked for.
        if (link.Target is not null)
            Debug.WriteLine($"Link targeted '{link.Target}' opened in this view: {link.Url}");

        await GoAsync(-1, link.Url);
    }

    private void AddressBar_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _ = NavigateAsync(AddressBar.Text);
    }

    /// <summary>
    /// Reads the state the window's constructor left the overlays in, so the button answers for
    /// whatever the window starts with rather than for a state assumed here.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            OverlayButton.IsChecked = topLevel.RendererDiagnostics.DebugOverlays != RendererDebugOverlays.None;
        }
    }

    /// <summary>
    /// Turns the renderer's debug overlays on or off against the window the view sits in.
    /// </summary>
    private void Overlay_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        topLevel.RendererDiagnostics.DebugOverlays = OverlayButton.IsChecked == true
            ? RendererDebugOverlays.Fps | RendererDebugOverlays.RenderTimeGraph
            : RendererDebugOverlays.None;
    }

    private async void Back_Click(object? sender, RoutedEventArgs e) => await GoBackAsync();

    private async void Forward_Click(object? sender, RoutedEventArgs e) => await GoForwardAsync();

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (_position >= 0)
            await GoAsync(_position);
    }

    /// <summary>Shows the current page zoom as a percentage next to the zoom buttons.</summary>
    private void UpdateZoomLabel() =>
        ZoomLabel.Text = $"{(Int32)Math.Round(MyHutuView.Zoom * 100)}%";

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => MyHutuView.ZoomIn();

    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => MyHutuView.ZoomOut();

    private void ZoomReset_Click(object? sender, RoutedEventArgs e) => MyHutuView.ZoomReset();

    /// <summary>Fits the page to the window in paged mode, and resets the zoom in the scrolling view.</summary>
    private void Fit_Click(object? sender, RoutedEventArgs e) => MyHutuView.FitToWindow();

    /// <summary>Shows the document as printed pages, or as one scrolling page again.</summary>
    private void Paged_Click(object? sender, RoutedEventArgs e)
    {
        MyHutuView.SetPaged(
            PagedButton.IsChecked == true,
            SelectedPaper(),
            PageMargins);

        UpdatePages();
    }

    private void Paper_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        MyHutuView.SetPaper(SelectedPaper(), PageMargins);

    /// <summary>Gets the paper the list has selected, A4 where nothing is.</summary>
    private PageSize SelectedPaper()
    {
        var paper = PaperBox.SelectedItem is Paper selected ? selected : Papers[0];

        return new PageSize(paper.WidthMm * PixelsPerMillimetre, paper.HeightMm * PixelsPerMillimetre);
    }

    private void PreviousPage_Click(object? sender, RoutedEventArgs e) => MyHutuView.PreviousPage();

    private void NextPage_Click(object? sender, RoutedEventArgs e) => MyHutuView.NextPage();

    /// <summary>Shows which page of how many the reader stands on, and enables the arrows that move.</summary>
    private void UpdatePages()
    {
        var count = MyHutuView.PageCount;
        var page = MyHutuView.CurrentPage;

        PageLabel.Text = count == 0 ? "—" : $"{page} / {count}";
        PreviousPageButton.IsEnabled = page > 1;
        NextPageButton.IsEnabled = count > 0 && page < count;
        FitButton.IsEnabled = MyHutuView.IsPaged;
    }

    /// <summary>
    /// Answers the fourth and fifth mouse buttons, which are the browser's back and forward. The press
    /// is taken here, above the view, so it works wherever on the page the pointer happens to be - and
    /// only an unhandled press reaches this far, so a click the page itself consumed is left alone.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (e.Handled)
            return;

        switch (e.GetCurrentPoint(this).Properties.PointerUpdateKind)
        {
            case PointerUpdateKind.XButton1Pressed:
                e.Handled = true;
                _ = GoBackAsync();
                break;

            case PointerUpdateKind.XButton2Pressed:
                e.Handled = true;
                _ = GoForwardAsync();
                break;
        }
    }

    private Task GoBackAsync() => _position > 0 ? GoAsync(_position - 1) : Task.CompletedTask;

    private Task GoForwardAsync() => _position < _history.Count - 1 ? GoAsync(_position + 1) : Task.CompletedTask;

    /// <summary>Loads an address the reader entered and records it once it has a document.</summary>
    private async Task NavigateAsync(string? address)
    {
        var url = address?.Trim();

        if (string.IsNullOrEmpty(url))
            return;

        await GoAsync(-1, url);
    }

    /// <summary>
    /// Opens the page the reader last had open, or the home page when there is none to open - or when
    /// the one remembered will not open, which is what an address left over from an older build does.
    /// The rest of the remembered history is restored with it, so the back and forward buttons still
    /// have somewhere to go.
    /// </summary>
    private async Task RestoreAsync()
    {
        var remembered = BrowsingHistoryStore.Load();

        if (remembered is { Addresses.Count: > 0 } history)
        {
            var position = Math.Clamp(history.Position, 0, history.Addresses.Count - 1);
            var address = history.Addresses[position];

            if (await LoadAsync(address) is not null)
            {
                _history.Clear();
                _history.AddRange(history.Addresses);
                _position = position;
                AddressBar.Text = address;
                UpdateNavigation();
                return;
            }
        }

        // Nothing was remembered, or what was remembered is no longer there to open. Either way the
        // reader starts at the home page rather than at an empty view with nothing to read.
        await GoAsync(-1, HomeAddress);
    }

    /// <summary>
    /// Moves to a position in the history, or to <paramref name="url"/> when it is given - a new
    /// address is appended and the position lands on it. The position only moves once the load has
    /// produced a document, so a failed address leaves the reader where they were; the result says
    /// which of the two it was, for the start-up that has somewhere to fall back on.
    /// </summary>
    private async Task<bool> GoAsync(int target, string? url = null)
    {
        var requested = url ?? _history[target];

        if (await LoadAsync(requested) is not { } document)
            return false;

        // The reader is remembered against the address the document ended up with, not the one that was
        // asked for: that is the address its own links resolve against, and so the only one a link back
        // here could be recognised by - and it is the page the reader is looking at, which is the one to
        // open again next time. It is also what a stored history needs in order to be reloaded.
        var address = document.Url is { Length: > 0 } resolved ? resolved : requested;

        if (url is not null)
        {
            // Anything the reader could have gone forward to is unreachable from the new address.
            if (_position < _history.Count - 1)
                _history.RemoveRange(_position + 1, _history.Count - _position - 1);

            _history.Add(address);
            target = _history.Count - 1;
        }
        else
        {
            _history[target] = address;
        }

        _position = target;
        AddressBar.Text = address;
        UpdateNavigation();
        SaveHistory();

        return true;
    }

    /// <summary>
    /// Loads a URL and reports the document it produced, or null when it could not be loaded. A failure
    /// is logged rather than left to reach the dispatcher, where an exception from a load would take the
    /// whole process down.
    /// </summary>
    private async Task<IDocument?> LoadAsync(string url)
    {
        try
        {
            var document = await MyHutuView.LoadUrlAsync(url);

            if (document.Url is { Length: > 0 } address)
            {
                MyHutuView.MarkVisited(address);
            }

            return document;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loading '{url}' failed: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Records the history, so the next run opens where this one left off and can still walk it back
    /// and forward.
    /// </summary>
    private void SaveHistory() => BrowsingHistoryStore.Save(new BrowsingHistory(_history, _position));

    /// <summary>Enables each button exactly when the history has somewhere for it to go.</summary>
    private void UpdateNavigation()
    {
        BackButton.IsEnabled = _position > 0;
        ForwardButton.IsEnabled = _position >= 0 && _position < _history.Count - 1;
        RefreshButton.IsEnabled = _position >= 0;
    }
}
