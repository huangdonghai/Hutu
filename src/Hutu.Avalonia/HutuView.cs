using AngleSharp.Dom;
using Avalonia.Controls;
using Hutu;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Hutu.Avalonia;

public class HutuView : ScrollViewer
{
    HutuViewInternal _internalView = new();

    public void AddEpubResourceSource(string epubFileName)
    {
        _internalView.AddEpubResourceSource(epubFileName);
    }

    public void AddEmbeddedResourceSource(Assembly assembly)
    {
        _internalView.AddEmbeddedResourceSource(assembly);
    }
    public async void LoadUrlAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        await _internalView.LoadUrlAsync(url, cancellationToken);
    }

    public Task<IDocument> SetHtml(string html, string url = "")
    {
        return _internalView.SetHtml(html, url);
    }

    public string GetHtml()
    {
        return _internalView.GetHtml();
    }
}