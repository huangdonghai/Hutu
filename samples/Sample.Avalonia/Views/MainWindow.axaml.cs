using Avalonia.Controls;
using Avalonia.Rendering;

namespace Sample.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        this.RendererDiagnostics.DebugOverlays = RendererDebugOverlays.None
                                         //| RendererDebugOverlays.Fps
                                         //| RendererDebugOverlays.LayoutTimeGraph
                                         //| RendererDebugOverlays.RenderTimeGraph
                                         ;

    }
}