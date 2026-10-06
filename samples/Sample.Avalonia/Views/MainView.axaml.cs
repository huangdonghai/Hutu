
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Sample.Common;
using System;
using System.Diagnostics;

namespace Sample.Avalonia.Views;

public partial class MainView : ContentPage
{
    public MainView()
    {
        InitializeComponent();

        // Init some controls
        MyHutuView.AddEmbeddedResourceSource(SampleUtils.GetSampleAssembly(), "Samples");
    }

    private void AddressBar_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnAddressSubmitted();
    }

    private async void OnAddressSubmitted()
    {
        if (string.IsNullOrWhiteSpace(AddressBar.Text))
            return;

        try
        {
            await MyHutuView.LoadUrlAsync(AddressBar.Text);
        }
        catch (Exception ex)
        {
            // Nothing in this view can report a failure to the user, so an address that does not
            // resolve is logged rather than left to reach the dispatcher, where an exception from
            // a load would take the whole process down.
            Debug.WriteLine($"Loading '{AddressBar.Text}' failed: {ex}");
        }
    }
}