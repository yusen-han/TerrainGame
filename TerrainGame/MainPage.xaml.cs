using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Microsoft.Maui.Graphics;
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
    private FrameDrawable? _frameDrawable;
    private IDispatcherTimer? _timer;
    private TimeSpan _lastFrame;
    private int _frameCount;

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
        _frameDrawable = new FrameDrawable();
        GameView.Drawable = _frameDrawable;

        try
        {
            _renderer = new VulkanRenderer(FrameWidth, FrameHeight);
            _streamer = new ChunkStreamer(seed: 1337, radius: 4);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Initialization failed:{Environment.NewLine}{ex.Message}";
            return;
        }

        _lastFrame = _clock.Elapsed;
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(50);
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

            var pixels = _renderer.Render();
            var png = PngEncoder.EncodeBgra(pixels, _renderer.Width, _renderer.Height);
            _frameDrawable?.SetFrame(png);
            GameView.Invalidate();

            _frameCount++;
            if (_frameCount % 4 == 0)
            {
                StatusLabel.Text =
                    $"chunks {_renderer.ChunkCount}  " +
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

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        _streamer?.Dispose();
        _streamer = null;
        _renderer?.Dispose();
        _renderer = null;
        _frameDrawable?.Dispose();
        _frameDrawable = null;
    }
}
