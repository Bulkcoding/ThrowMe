using System.Windows.Media.Media3D;
using ThrowMe.Models;
using ThrowMe.Physics;

namespace ThrowMe.Animation;

/// <summary>타이머 없이 창의 렌더 루프가 진행시키는 다축 자세와 충돌 복구.</summary>
public sealed class Sprite3DMotion
{
    private readonly AppSettings _settings;
    private readonly Queue<(double Time, Vector3D Delta)> _samples = new();
    private Vector3D _lastPoint;
    private double _lastDragTime;
    private double _spinBase;
    private double _impactAge = 0.2;
    private double _impactScale = 1;
    private Vector3D _impactNormal = new(0, 1, 0);
    private double _hopPhase;
    private bool _hopping;

    public Quaternion Orientation { get; private set; } = Quaternion.Identity;
    public Vector3D AngularVelocity { get; private set; }
    public bool IsDragging { get; private set; }
    public bool HasActiveMotion => IsDragging || AngularVelocity.Length >= StopSpeed || _impactAge < 0.2 || _hopping;
    private double StopSpeed => double.IsFinite(_settings.SpinStopThreshold) ? Math.Max(0.01, _settings.SpinStopThreshold) : 8;
    private double MaxSpeed => double.IsFinite(_settings.MaxAngularVelocity) ? Math.Clamp(_settings.MaxAngularVelocity, 0, 10000) : 1200;
    public double Hop => _hopping ? 0.2 * Math.Sin(Math.PI * _hopPhase) : 0;

    public Sprite3DMotion(AppSettings settings) => _settings = settings;

    public void SyncSpin(double angle) => _spinBase = double.IsFinite(angle) ? angle : 0;

    public static Vector3D Project(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return new Vector3D(0, 0, 1);
        // 먼저 큰 좌표를 제한하여 제곱 시 넘침을 피한다.
        x = Math.Clamp(x, -1e6, 1e6); y = Math.Clamp(y, -1e6, 1e6);
        double length = Math.Sqrt(x * x + y * y);
        return length > 1 ? new Vector3D(x / length, -y / length, 0)
                          : new Vector3D(x, -y, Math.Sqrt(Math.Max(0, 1 - length * length)));
    }

    public void BeginDrag(double x, double y, double time, double spin)
    {
        CancelDrag();
        _lastPoint = Project(x, y);
        _lastDragTime = double.IsFinite(time) ? time : 0;
        IsDragging = true;
        SyncSpin(spin);
    }

    public void DragTo(double x, double y, double time)
    {
        if (!IsDragging || !double.IsFinite(time) || time <= _lastDragTime) return;
        Vector3D point = Project(x, y);
        double dot = Math.Clamp(Vector3D.DotProduct(_lastPoint, point), -1, 1);
        Vector3D axis = Vector3D.CrossProduct(_lastPoint, point);
        double angle = Math.Acos(dot) * 180 / Math.PI;
        if (axis.LengthSquared < 1e-12 && dot < 0)
            axis = Vector3D.CrossProduct(_lastPoint, Math.Abs(_lastPoint.Y) < 0.9 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0));
        if (axis.LengthSquared > 1e-12 && angle > 1e-6)
        {
            axis.Normalize();
            Rotate(axis, angle);
            _samples.Enqueue((time, axis * angle));
        }
        // 정지 입력도 기록해 놓기 직전 멈춘 동작에서 관성이 재발하지 않게 한다.
        else _samples.Enqueue((time, new Vector3D()));
        while (_samples.Count > 0 && _samples.Peek().Time < time - 0.08) _samples.Dequeue();
        _lastPoint = point;
        _lastDragTime = time;
    }

    public void EndDrag(double time)
    {
        if (!IsDragging) return;
        IsDragging = false;
        AngularVelocity = new Vector3D();
        if (double.IsFinite(time) && time - _lastDragTime <= 0.08 && _samples.Count > 0)
        {
            Vector3D total = new();
            foreach (var sample in _samples) total += sample.Delta;
            double duration = Math.Max(1.0 / 60, time - _samples.Peek().Time + 1.0 / 60);
            AngularVelocity = Limited(total / duration);
        }
        _samples.Clear();
    }

    public void CancelDrag()
    {
        IsDragging = false;
        AngularVelocity = new Vector3D();
        _samples.Clear();
    }

    public void Impact(double intensity, Vector2 normal)
    {
        if (!double.IsFinite(intensity) || !double.IsFinite(normal.X) || !double.IsFinite(normal.Y)) return;
        int level = Math.Min(5, (int)(Math.Clamp(intensity, 0, 1) * 6));
        _impactScale = 0.90 - level * 0.06;
        _impactNormal = new Vector3D(normal.X, -normal.Y, 0);
        if (_impactNormal.LengthSquared < 1e-12) _impactNormal = new Vector3D(0, 1, 0);
        else _impactNormal.Normalize();
        _impactAge = 0;
    }

    public void SetHop(double phase, bool moving)
    {
        _hopping = moving && double.IsFinite(phase);
        _hopPhase = _hopping ? Math.Clamp(phase, 0, 1) : 0;
    }

    public Matrix3D Deformation
    {
        get
        {
            double scale;
            Vector3D n;
            if (_impactAge < 0.2)
            {
                double strength = _impactAge < 0.04 ? _impactAge / 0.04
                    : _impactAge <= 0.10 ? 1 : (0.2 - _impactAge) / 0.1;
                scale = 1 - (1 - _impactScale) * Math.Clamp(strength, 0, 1);
                n = _impactNormal;
            }
            else if (_hopping)
            {
                scale = 1 - 0.12 * Math.Pow(Math.Cos(Math.PI * _hopPhase), 8);
                n = new Vector3D(0, 1, 0);
            }
            else return Matrix3D.Identity;
            double side = Math.Min(1.2, 1 / Math.Sqrt(scale)), delta = scale - side;
            return new Matrix3D(side + delta*n.X*n.X, delta*n.X*n.Y, delta*n.X*n.Z, 0,
                delta*n.Y*n.X, side + delta*n.Y*n.Y, delta*n.Y*n.Z, 0,
                delta*n.Z*n.X, delta*n.Z*n.Y, side + delta*n.Z*n.Z, 0, 0, 0, 0, 1);
        }
    }

    public void Tick(double dt, Vector2 velocity, double diameter, double spin)
    {
        if (!double.IsFinite(dt) || dt <= 0) return;
        dt = Math.Min(dt, 0.05);
        _impactAge = Math.Min(0.2, _impactAge + dt);
        if (IsDragging) { SyncSpin(spin); return; }
        if (double.IsFinite(diameter) && diameter > 0 && double.IsFinite(velocity.X) && double.IsFinite(velocity.Y))
        {
            Vector3D axis = new(velocity.Y, velocity.X, 0);
            double distance = axis.Length * dt;
            if (axis.LengthSquared > 1e-12) Rotate(axis, distance / (diameter * 0.5) * 180 / Math.PI);
        }
        if (double.IsFinite(spin))
        {
            double delta = spin - _spinBase;
            if (double.IsFinite(delta)) Rotate(new Vector3D(0, 0, -1), delta);
            SyncSpin(spin);
        }
        double speed = AngularVelocity.Length;
        if (speed >= StopSpeed)
        {
            Rotate(AngularVelocity, speed * dt);
            double friction = double.IsFinite(_settings.SpinFriction) ? Math.Max(0.01, _settings.SpinFriction) : 0.55;
            AngularVelocity *= Math.Exp(-friction * dt);
        }
        if (AngularVelocity.Length < StopSpeed) AngularVelocity = new Vector3D();
    }

    private void Rotate(Vector3D axis, double angle)
    {
        if (!double.IsFinite(angle) || axis.LengthSquared < 1e-12) return;
        var next = new Quaternion(axis, angle % 360) * Orientation;
        next.Normalize();
        Orientation = next;
    }

    private Vector3D Limited(Vector3D value)
    {
        if (!double.IsFinite(value.X) || !double.IsFinite(value.Y) || !double.IsFinite(value.Z)) return new Vector3D();
        double length = value.Length;
        return length > MaxSpeed && length > 0 ? value * (MaxSpeed / length) : value;
    }

    public double[] CaptureOrientation() => new[] { Orientation.X, Orientation.Y, Orientation.Z, Orientation.W };
    public double[] CaptureVelocity() => new[] { AngularVelocity.X, AngularVelocity.Y, AngularVelocity.Z };

    public void Restore(double[]? orientation, double[]? velocity, double spin)
    {
        CancelDrag();
        Orientation = Quaternion.Identity;
        if (orientation is { Length: 4 } && orientation.All(double.IsFinite))
        {
            double norm = orientation.Sum(x => x*x);
            if (norm > 1e-12 && double.IsFinite(norm))
            {
                var q = new Quaternion(orientation[0],orientation[1],orientation[2],orientation[3]); q.Normalize(); Orientation=q;
                if (velocity is { Length: 3 } && velocity.All(double.IsFinite)) AngularVelocity=Limited(new Vector3D(velocity[0],velocity[1],velocity[2]));
            }
        }
        _impactAge=0.2; SetHop(0,false); SyncSpin(spin);
    }
}
