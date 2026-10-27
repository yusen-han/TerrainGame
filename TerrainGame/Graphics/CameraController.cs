using System.Numerics;

namespace TerrainGame.Graphics;

/// <summary>
/// Free-flying first-person camera. Gravity is deliberately not simulated.
/// </summary>
public class CameraController
{
    public float Speed { get; set; } = 10.0f;
    public Vector3 Position { get; set; } = Vector3.Zero;
    public Vector3 Forward { get; set; } = Vector3.UnitZ;
    public Vector3 Up { get; set; } = Vector3.UnitY;
    public float Yaw { get; set; }
    public float Pitch { get; set; }

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Up));

    /// <summary>Moves the camera relative to its own orientation.</summary>
    public void Move(Vector3 localDelta, float deltaTime)
    {
        var offset = Right * localDelta.X + Up * localDelta.Y + Forward * localDelta.Z;
        if (offset.LengthSquared() > 0)
            Position += Vector3.Normalize(offset) * Speed * deltaTime;
    }

    /// <summary>Applies yaw/pitch in radians, clamps pitch to avoid flipping.</summary>
    public void Rotate(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch = Math.Clamp(Pitch + deltaPitch, -1.55f, 1.55f);

        var direction = new Vector3(
            MathF.Sin(Yaw) * MathF.Cos(Pitch),
            MathF.Sin(Pitch),
            MathF.Cos(Yaw) * MathF.Cos(Pitch));

        Forward = Vector3.Normalize(direction);
    }

    public Matrix4x4 GetViewMatrix() => Matrix4x4.CreateLookAt(Position, Position + Forward, Up);
}
