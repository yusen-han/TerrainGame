using System;
using System.IO;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Platform;
using IImage = Microsoft.Maui.Graphics.IImage;

namespace TerrainGame.Graphics;

/// <summary>
/// Draws the latest rendered frame into a <see cref="GraphicsView"/>.
/// The decoded image is replaced during a draw pass, so no blank frame is ever
/// shown. (Assigning Image.Source every frame tears the drawable down on
/// Android and makes the screen flicker.)
/// </summary>
public sealed class FrameDrawable : IDrawable, IDisposable
{
    private readonly object _lock = new();

    private byte[]? _pendingPng;
    private IImage? _image;

    public void SetFrame(byte[] png)
    {
        lock (_lock)
            _pendingPng = png;
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        byte[]? pending;
        lock (_lock)
        {
            pending = _pendingPng;
            _pendingPng = null;
        }

        if (pending is not null)
        {
            using var stream = new MemoryStream(pending, writable: false);
            var decoded = PlatformImage.FromStream(stream, ImageFormat.Png);
            if (decoded is not null)
            {
                _image?.Dispose();
                _image = decoded;
            }
        }

        if (_image is null)
            return;

        var scale = MathF.Max(dirtyRect.Width / _image.Width, dirtyRect.Height / _image.Height);
        var width = _image.Width * scale;
        var height = _image.Height * scale;
        var x = dirtyRect.X + (dirtyRect.Width - width) / 2f;
        var y = dirtyRect.Y + (dirtyRect.Height - height) / 2f;

        canvas.DrawImage(_image, x, y, width, height);
    }

    public void Dispose()
    {
        _image?.Dispose();
        _image = null;
    }
}
