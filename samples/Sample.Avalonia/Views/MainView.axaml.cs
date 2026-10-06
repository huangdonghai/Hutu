
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sample.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Sample.Avalonia.Views;

public partial class MainView : ContentPage
{
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
        UpdateNavigation();
    }

    private void AddressBar_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _ = NavigateAsync(AddressBar.Text);
    }

    private async void Back_Click(object? sender, RoutedEventArgs e)
    {
        if (_position > 0)
            await GoAsync(_position - 1);
    }

    private async void Forward_Click(object? sender, RoutedEventArgs e)
    {
        if (_position < _history.Count - 1)
            await GoAsync(_position + 1);
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (_position >= 0)
            await LoadAsync(_history[_position]);
    }

    /// <summary>Loads an address the reader entered and records it once it has a document.</summary>
    private async Task NavigateAsync(string? address)
    {
        var url = address?.Trim();

        if (string.IsNullOrEmpty(url))
            return;

        await GoAsync(-1, url);
    }

    /// <summary>
    /// Moves to a position in the history, or to <paramref name="url"/> when it is given - a new
    /// address is appended and the position lands on it. The position only moves once the load has
    /// produced a document, so a failed address leaves the reader where they were.
    /// </summary>
    private async Task GoAsync(int target, string? url = null)
    {
        var address = url ?? _history[target];

        if (!await LoadAsync(address))
            return;

        if (url is not null)
        {
            // Anything the reader could have gone forward to is unreachable from the new address.
            if (_position < _history.Count - 1)
                _history.RemoveRange(_position + 1, _history.Count - _position - 1);

            _history.Add(url);
            target = _history.Count - 1;
        }

        _position = target;
        AddressBar.Text = address;
        UpdateNavigation();
    }

    /// <summary>
    /// Loads a URL and reports whether it produced a document. A failure is logged rather than left to
    /// reach the dispatcher, where an exception from a load would take the whole process down.
    /// </summary>
    private async Task<bool> LoadAsync(string url)
    {
        try
        {
            await MyHutuView.LoadUrlAsync(url);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loading '{url}' failed: {ex}");
            return false;
        }
    }

    /// <summary>Enables each button exactly when the history has somewhere for it to go.</summary>
    private void UpdateNavigation()
    {
        BackButton.IsEnabled = _position > 0;
        ForwardButton.IsEnabled = _position >= 0 && _position < _history.Count - 1;
        RefreshButton.IsEnabled = _position >= 0;
    }
}
