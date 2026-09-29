
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Sample.Common;

namespace Sample.Avalonia.Views;

public partial class MainView : ContentPage
{
    public MainView()
    {
        InitializeComponent();

        // Init some controls
        MyHutuView.AddEmbeddedResourceSource(SampleUtils.GetSampleAssembly());
    }

    private void AddressBar_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnAddressSubmitted();
    }

    private void OnAddressSubmitted()
    {
        if (string.IsNullOrWhiteSpace(AddressBar.Text))
            return;
        MyHutuView.LoadUrlAsync(AddressBar.Text);
    }
}