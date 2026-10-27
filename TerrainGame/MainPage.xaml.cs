using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Maui.Graphics;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using TerrainGame.Graphics;
using TerrainGame.Terrain;

namespace TerrainGame;

public partial class MainPage : ContentPage
{
    private const int FrameWidth = 1000;
    private const int FrameHeight = 600;
    private const float LookSensitivity = 0.006f;

    private readonly CameraController _camera = new()
    {
        Position = new Vector3(0, 16, 0),
        Speed = 16f,
    };

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<ChunkStreamer.CompletedChunk> _ready = new();
    private readonly List<long> _removals = new();

    private VulkanRenderer? _renderer;
    private ChunkStreamer? _streamer;
    private SKBitmap? _frameBitmap;
    private GCHandle _pixelHandle;
    private IDispatcherTimer? _timer;
    private TimeSpan _lastFrame;
    private int _frameCount;
    private int _fpsFrames;
    private TimeSpan _fpsWindowStart;
    private double _fps;

    private bool _pointerDown;
    private Point? _lastPointer;

    private bool _moveForward;
    private bool _moveBack;
    private bool _moveLeft;
    private bool _moveRight;
    private bool _moveUp;
    private bool _moveDown;

    public MainPage()
    {
        InitializeComponent();
        _camera.Rotate(0f, -0.26f);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        if (_renderer is not null)
            return;

        try
        {
            _renderer = new VulkanRenderer(FrameWidth, FrameHeight);
            _streamer = new ChunkStreamer(seed: 1337, radius: 4);

            // The renderer writes into its own BGRA buffer; wrap it instead of copying.
            _pixelHandle = GCHandle.Alloc(_renderer.Pixels, GCHandleType.Pinned);
            _frameBitmap = new SKBitmap();
            var info = new SKImageInfo(
                _renderer.Width,
                _renderer.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Opaque);

            if (!_frameBitmap.InstallPixels(info, _pixelHandle.AddrOfPinnedObject(), info.RowBytes))
                throw new InvalidOperationException("Failed to install the frame pixels into an SKBitmap.");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Initialization failed:{Environment.NewLine}{ex.Message}";
            return;
        }

        _lastFrame = _clock.Elapsed;
        _fpsWindowStart = _lastFrame;
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += (_, _) => RenderFrame();
        _timer.Start();
    }

    private void RenderFrame()
    {
        if (_renderer is null || _streamer is null)
            return;

        try
        {
            var now = _clock.Elapsed;
            var deltaTime = Math.Clamp((float)(now - _lastFrame).TotalSeconds, 0f, 0.1f);
            _lastFrame = now;

            UpdateCamera(deltaTime);
            UpdateTerrain();

            var view = _camera.GetViewMatrix();
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 3f,
                _renderer.Width / (float)_renderer.Height,
                0.5f,
                500f);

            _renderer.SetCamera(view * projection, _camera.Position);

            _renderer.Render();
            _frameBitmap?.NotifyPixelsChanged();
            GameView.InvalidateSurface();

            _frameCount++;
            _fpsFrames++;
            var fpsElapsed = now - _fpsWindowStart;
            if (fpsElapsed.TotalSeconds >= 1.0)
            {
                _fps = _fpsFrames / fpsElapsed.TotalSeconds;
                _fpsFrames = 0;
                _fpsWindowStart = now;
            }

            if (_frameCount % 4 == 0)
            {
                StatusLabel.Text =
                    $"chunks {_renderer.ChunkCount}  fps {_fps:F0}  " +
                    $"pos {_camera.Position.X:F0},{_camera.Position.Y:F0},{_camera.Position.Z:F0}";
            }
        }
        catch (Exception ex)
        {
            _timer?.Stop();
            StatusLabel.Text = $"Render failed:{Environment.NewLine}{ex.Message}";
        }
    }

    private void UpdateCamera(float deltaTime)
    {
        var local = Vector3.Zero;
        if (_moveForward) local.Z += 1f;
        if (_moveBack) local.Z -= 1f;
        if (_moveRight) local.X += 1f;
        if (_moveLeft) local.X -= 1f;
        if (_moveUp) local.Y += 1f;
        if (_moveDown) local.Y -= 1f;

        if (local != Vector3.Zero)
            _camera.Move(local, deltaTime);
    }

    private void UpdateTerrain()
    {
        if (_streamer is null || _renderer is null)
            return;

        _streamer.Update(_camera.Position);

        _ready.Clear();
        _streamer.DrainReady(_ready);
        foreach (var chunk in _ready)
            _renderer.SetChunk(chunk.Key, chunk.Vertices);

        _removals.Clear();
        _streamer.DrainRemovals(_removals);
        foreach (var key in _removals)
            _renderer.RemoveChunk(key);
    }

    private void OnMovePressed(object? sender, EventArgs e) => SetMovement(sender, true);

    private void OnMoveReleased(object? sender, EventArgs e) => SetMovement(sender, false);

    private void SetMovement(object? sender, bool pressed)
    {
        if (sender is not Button button)
            return;

        switch (button.Text)
        {
            case "Fwd": _moveForward = pressed; break;
            case "Back": _moveBack = pressed; break;
            case "Left": _moveLeft = pressed; break;
            case "Right": _moveRight = pressed; break;
            case "Up": _moveUp = pressed; break;
            case "Down": _moveDown = pressed; break;
        }
    }

    private void OnPointerPressed(object? sender, PointerEventArgs e)
    {
        _pointerDown = true;
        _lastPointer = e.GetPosition(GameView);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pointerDown)
            return;

        var position = e.GetPosition(GameView);
        if (position is null)
            return;

        if (_lastPointer is { } last)
        {
            _camera.Rotate(
                (float)(position.Value.X - last.X) * LookSensitivity,
                -(float)(position.Value.Y - last.Y) * LookSensitivity);
        }

        _lastPointer = position;
    }

    private void OnPointerReleased(object? sender, PointerEventArgs e)
    {
        _pointerDown = false;
        _lastPointer = null;
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Black);

        if (_frameBitmap is null || _frameBitmap.Width == 0)
            return;

        var info = e.Info;
        var scale = MathF.Max(info.Width / (float)_frameBitmap.Width, info.Height / (float)_frameBitmap.Height);
        var width = _frameBitmap.Width * scale;
        var height = _frameBitmap.Height * scale;
        var left = (info.Width - width) / 2f;
        var top = (info.Height - height) / 2f;

        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        canvas.DrawBitmap(_frameBitmap, new SKRect(left, top, left + width, top + height), sampling);
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _timer?.Stop();
        _timer = null;

        // The bitmap wraps the renderer's pinned pixel buffer, so release it first.
        _frameBitmap?.Dispose();
        _frameBitmap = null;
        if (_pixelHandle.IsAllocated)
            _pixelHandle.Free();

        _streamer?.Dispose();
        _streamer = null;
        _renderer?.Dispose();
        _renderer = null;
    }
}
