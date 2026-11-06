using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Microsoft.Maui.Graphics;
using TerrainGame.Graphics;
using TerrainGame.Terrain;
using VulkanView;

namespace TerrainGame;

public partial class MainPage : ContentPage, IVulkanScene
{
    private const float LookSensitivity = 0.006f;

    private readonly CameraController _camera = new()
    {
        Position = new Vector3(0, 16, 0),
        Speed = 16f,
    };

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<ChunkStreamer.CompletedChunk> _ready = new();
    private readonly List<long> _removals = new();

    private ChunkStreamer? _streamer;
    private TerrainRenderer? _terrain;
    private IDispatcherTimer? _timer;
    private TimeSpan _lastFrame;
    private int _frameCount;
    private int _fpsFrames;
    private TimeSpan _fpsWindowStart;
    private double _fps;
    private float _aspect = 16f / 9f;
    private bool _flipY;
    private string? _errorText;
    private string? _diagnostics;
    private string? _pageDiag;
    private int _renderCalls;
    private int _queuedChunks;
    private TimeSpan _lastEnsure;

    private bool _pointerDown;
    private Point? _lastPointer;
    private TimeSpan _pressStart;
    private TimeSpan _lastDig;
    private TimeSpan _lastPointerMove;
    private double _panLastX;
    private double _panLastY;

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
        if (_streamer is not null)
            return;

        try
        {
            _streamer = new ChunkStreamer(new TerrainWorld(seed: 98876), radius: 4);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Initialization failed:{Environment.NewLine}{ex.Message}";
            return;
        }

        GameView.Scene = this;
        _pageDiag = $"loaded handler={GameView.Handler?.GetType().Name ?? "null"} scene={(GameView.Scene is null ? "null" : "set")}";
        GameView.PressBegan += (_, _) => BeginPress();
        GameView.PressEnded += (_, _) => EndPress();
        GameView.RenderError += (_, message) => _errorText = message;
        GameView.DiagnosticsChanged += (_, _) => _diagnostics = GameView.Diagnostics;

        _lastFrame = _clock.Elapsed;
        _fpsWindowStart = _lastFrame;
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += (_, _) => RenderFrame();
        _timer.Start();
    }

    // ---- IVulkanScene -------------------------------------------------

    public void OnTargetCreated(VulkanTargetInfo target)
    {
        _terrain?.Dispose();
        _terrain = new TerrainRenderer(target.Context);
        _terrain.OnTargetCreated(target);
        _aspect = target.AspectRatio;
        _flipY = target.FlipY;
        _errorText = null;
        _pageDiag = "target created";
        _streamer?.RebuildAll();
        StatusLabel.Text = $"Vulkan target {target.Extent.Width}x{target.Extent.Height}";
    }

    public void OnTargetResized(VulkanTargetInfo target)
    {
        _terrain?.OnTargetResized(target);
        _aspect = target.AspectRatio;
        _flipY = target.FlipY;
    }

    public void OnTargetDestroyed()
    {
        _terrain?.Dispose();
        _terrain = null;
        _pageDiag = "target destroyed";
    }

    public void Render(VulkanFrameContext frame)
    {
        _renderCalls++;
        _terrain?.Render(frame);
    }

    // ---- frame loop ---------------------------------------------------

    private void RenderFrame()
    {
        if (_streamer is null)
            return;

        try
        {
            var now = _clock.Elapsed;
            var deltaTime = Math.Clamp((float)(now - _lastFrame).TotalSeconds, 0f, 0.1f);
            _lastFrame = now;

            UpdateCamera(deltaTime);
            UpdateDigging(now);
            UpdateTerrain();

            if (_terrain is not null)
            {
                var view = _camera.GetViewMatrix();
                var projection = Matrix4x4.CreatePerspectiveFieldOfView(
                    MathF.PI / 3f,
                    _aspect,
                    0.5f,
                    500f);

                // D3D11 interop stores the colour image top-down, so mirror the Y axis.
                if (_flipY)
                    projection.M22 = -projection.M22;

                _terrain.SetCamera(view * projection, _camera.Position);
            }

            if (_terrain is null && now - _lastEnsure > TimeSpan.FromSeconds(1))
            {
                _lastEnsure = now;
                GameView.EnsureTarget();
            }

            GameView.RequestFrame();

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
                    $"chunks {_terrain?.ChunkCount ?? 0}  fps {_fps:F0}  " +
                    $"modified {_streamer.World.ModifiedChunkCount}  " +
                    $"pos {_camera.Position.X:F0},{_camera.Position.Y:F0},{_camera.Position.Z:F0}  " +
                    $"page: {_pageDiag ?? "?"} renders {_renderCalls} queued {_queuedChunks} " +
                    $"terrain={(_terrain is null ? "null" : $"ok r{_terrain.RenderCalls} c{_terrain.ChunkCount} ht{( _terrain.HasTarget ? 1 : 0)}")}  " +
                    $"host: {_diagnostics ?? "none"}" +
                    (_errorText is null ? string.Empty : $"  ERR: {_errorText}");
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

    private void UpdateDigging(TimeSpan now)
    {
        if (!_pointerDown || _streamer is null)
            return;

        // A short press is just a look drag; digging starts after a one second hold.
        if (now - _pressStart < TimeSpan.FromSeconds(1))
            return;

        if (now - _lastDig < TimeSpan.FromMilliseconds(350))
            return;

        _lastDig = now;

        var origin = _camera.Position;
        var direction = Vector3.Normalize(_camera.Forward);
        if (!TryRaycast(origin, direction, out var hit))
            return;

        var touched = _streamer.World.ApplyBrush(hit, radius: 3.2f, strength: 7f);
        _streamer.Rebuild(touched);
    }

    private bool TryRaycast(Vector3 origin, Vector3 direction, out Vector3 hit)
    {
        hit = default;
        if (_streamer is null)
            return false;

        const float step = 0.25f;
        const float maxDistance = 80f;

        var previousDensity = _streamer.World.DensityAt(origin);
        var previousT = 0f;

        for (var t = step; t <= maxDistance; t += step)
        {
            var point = origin + direction * t;
            var density = _streamer.World.DensityAt(point);

            if (density > 0f && previousDensity <= 0f)
            {
                var low = previousT;
                var high = t;
                for (var i = 0; i < 8; i++)
                {
                    var middle = (low + high) * 0.5f;
                    if (_streamer.World.DensityAt(origin + direction * middle) > 0f)
                        high = middle;
                    else
                        low = middle;
                }

                hit = origin + direction * high;
                return true;
            }

            previousDensity = density;
            previousT = t;
        }

        return false;
    }

    private void UpdateTerrain()
    {
        if (_streamer is null)
            return;

        _streamer.Update(_camera.Position);

        _ready.Clear();
        _streamer.DrainReady(_ready);
        foreach (var chunk in _ready)
        {
            _terrain?.SetChunk(chunk.Key, chunk.Vertices);
            _queuedChunks++;
        }

        _removals.Clear();
        _streamer.DrainRemovals(_removals);
        foreach (var key in _removals)
            _terrain?.RemoveChunk(key);
    }

    // ---- input --------------------------------------------------------

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
        BeginPress();
        _lastPointer = e.GetPosition(GameView);
    }

    private void BeginPress()
    {
        _pointerDown = true;
        _pressStart = _clock.Elapsed;
        _lastDig = _pressStart;
    }

    private void EndPress()
    {
        _pointerDown = false;
        _lastPointer = null;
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
        _lastPointerMove = _clock.Elapsed;
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        var now = _clock.Elapsed;

        // Let the pointer path own mouse/look drags; pan handles touch on Android.
        if (_pointerDown && now - _lastPointerMove < TimeSpan.FromMilliseconds(150))
            return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panLastX = 0;
                _panLastY = 0;
                break;

            case GestureStatus.Running:
                var dx = e.TotalX - _panLastX;
                var dy = e.TotalY - _panLastY;
                _panLastX = e.TotalX;
                _panLastY = e.TotalY;
                _camera.Rotate((float)dx * LookSensitivity, -(float)dy * LookSensitivity);
                break;

            default:
                _panLastX = 0;
                _panLastY = 0;
                break;
        }
    }

    private void OnPointerReleased(object? sender, PointerEventArgs e)
    {
        EndPress();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        _terrain?.Dispose();
        _terrain = null;
        _streamer?.Dispose();
        _streamer = null;
    }
}
