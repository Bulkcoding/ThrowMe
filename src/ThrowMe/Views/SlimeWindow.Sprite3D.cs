using System.Windows;
using System.Windows.Input;
using ThrowMe.Animation;
using ThrowMe.Models;
using ThrowMe.Network;
using ThrowMe.Physics;
using ThrowMe.Services;
using ThrowMe.Views.Skins;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using ContextMenuEventArgs = System.Windows.Controls.ContextMenuEventArgs;

namespace ThrowMe.Views;

public partial class SlimeWindow
{
    private Sprite3DMotion? _spriteMotion;
    private bool _spriteCandidate;
    private Point _spriteStart;
    private bool _spriteSuppressMenu;
    private bool Sprite3DOn => SkinHost.Content is Sprite3DSkin;
    private bool Sprite3DBusy => Sprite3DOn && _spriteMotion?.HasActiveMotion == true;

    internal static UserControl CreateSprite3D(AppSettings settings)
    {
        try
        {
            System.Windows.Media.Imaging.BitmapSource? image = null;
            if (settings.SkinImageEnabled && !SkinImageStore.TryLoadSprite3D(out image)) return new JellySkin();
            var skin = new Sprite3DSkin(); skin.SetImage(image, settings.SkinImageScale); return skin;
        }
        catch(Exception ex) { Logger.Error("3D model creation failed; using jelly.",ex); return new JellySkin(); }
    }

    private UserControl MakeSprite3D()
    {
        _spriteMotion = new Sprite3DMotion(_settings);
        _spriteMotion.SyncSpin(_physics.SpinAngle);
        return CreateSprite3D(_settings);
    }

    private void RefreshSprite3DImage()
    {
        CustomImage.Source = null; CustomImageLayer.Visibility = Visibility.Collapsed;
        System.Windows.Media.Imaging.BitmapSource? image = null;
        bool valid = !_settings.SkinImageEnabled || SkinImageStore.TryLoadSprite3D(out image);
        if (!valid)
        {
            CancelSprite3DInteraction();
            SkinHost.Content = new JellySkin();
        }
        else
        {
            if (SkinHost.Content is not Sprite3DSkin) SkinHost.Content = MakeSprite3D();
            if (SkinHost.Content is Sprite3DSkin skin) skin.SetImage(image, _settings.SkinImageScale);
        }
        UpdateSkinBehavior();
        DrawSprite3D();
    }

    private void OnSpriteRightDown(object sender, MouseButtonEventArgs e)
    {
        if (!Sprite3DOn || AutoMoveOn || _settings.Paused || _isDragging || _aiming || _spinDragging || !_ownsBall) return;
        Point p = e.GetPosition(SlimeVisual);
        if ((p.X-48)*(p.X-48)+(p.Y-48)*(p.Y-48) > 42*42) return;
        _spriteStart=p; _spriteCandidate=true; _spriteSuppressMenu=false;
        // 작은 공 밖으로 빠르게 움직여도 버튼 해제를 받도록 후보부터 캡처한다.
        if (!CaptureMouse()) _spriteCandidate=false;
    }

    private bool MoveSprite3D(MouseEventArgs e)
    {
        if (!_spriteCandidate && _spriteMotion?.IsDragging != true) return false;
        if (e.RightButton != MouseButtonState.Pressed) { CancelSprite3DInteraction(); return false; }
        Point p=e.GetPosition(SlimeVisual);
        if (_spriteCandidate)
        {
            // 디자인 좌표의 이동을 현재 표시 크기의 DIP 거리로 환산한다.
            double factor = Math.Max(1, SlimeBox.ActualWidth) / 96;
            if (Math.Abs(p.X-_spriteStart.X)*factor < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(p.Y-_spriteStart.Y)*factor < SystemParameters.MinimumVerticalDragDistance) return false;
            _spriteCandidate=false; _spriteSuppressMenu=true;
            _physics.Velocity=Vector2.Zero; _physics.AngularVelocity=0; _physics.SurfaceSpin=0;
            _spriteMotion!.BeginDrag((_spriteStart.X-48)/42,(_spriteStart.Y-48)/42,Now-0.001,_physics.SpinAngle);
            if (!CaptureMouse()) { CancelSprite3DInteraction(); return true; }
        }
        _spriteMotion!.DragTo((p.X-48)/42,(p.Y-48)/42,Now);
        DrawSprite3D(); EnsureRendering(); e.Handled=true; return true;
    }

    private void OnSpriteRightUp(object sender, MouseButtonEventArgs e)
    {
        if (_spriteMotion?.IsDragging != true)
        {
            bool candidate=_spriteCandidate; _spriteCandidate=false;
            if (candidate && IsMouseCaptured) ReleaseMouseCapture();
            return;
        }
        _spriteMotion.EndDrag(Now);
        _spriteCandidate=false;
        // 캡처 해제 알림이 새 관성을 취소하지 않도록 입력 상태를 먼저 끝낸다.
        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled=true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => _spriteSuppressMenu=false));
        EnsureRendering();
    }

    private void OnSpriteContextMenu(object sender, ContextMenuEventArgs e)
    {
        if (_spriteSuppressMenu || _spriteMotion?.IsDragging == true) e.Handled=true;
        _spriteSuppressMenu=false;
    }

    private void OnSpriteCaptureLost(object sender, MouseEventArgs e)
    {
        if (_spriteMotion?.IsDragging == true || _spriteCandidate) CancelSprite3DInteraction();
    }

    private void OnSpriteDeactivated(object? sender, EventArgs e)
    {
        if (_spriteMotion?.IsDragging == true || _spriteCandidate) CancelSprite3DInteraction();
    }

    private void CancelSprite3DInteraction()
    {
        bool dragging=_spriteMotion?.IsDragging == true || _spriteCandidate;
        _spriteCandidate=false;
        _spriteSuppressMenu=false;
        _spriteMotion?.CancelDrag();
        if (dragging && IsMouseCaptured) ReleaseMouseCapture();
    }

    private void TickSprite3D(double dt, Vector2 velocity)
    {
        if (!Sprite3DOn || _spriteMotion == null) return;
        _spriteMotion.Tick(dt,velocity,_settings.SlimeSize,_physics.SpinAngle);
        DrawSprite3D();
    }

    private void DrawSprite3D()
    {
        if (SkinHost.Content is Sprite3DSkin skin && _spriteMotion != null)
            skin.SetPose(_spriteMotion.Orientation,_spriteMotion.Deformation,_spriteMotion.Hop);
    }

    private static string RoomSkinName(SlimeSkinKind skin) => skin == SlimeSkinKind.Sprite3D ? "9" : skin.ToString();

    private static SlimeSkinKind ParseRoomSkin(string? name)
    {
        if (Enum.TryParse<SlimeSkinKind>(name, true, out var skin) && Enum.IsDefined(skin)) return skin;
        Logger.Info("Unknown room skin; using jelly.");
        return SlimeSkinKind.Jelly;
    }

    private void CaptureSprite3DState(HandoffData data)
    {
        if (!Sprite3DOn || _spriteMotion == null) return;
        data.Orientation3D=_spriteMotion.CaptureOrientation();
        data.AngularVelocity3D=_spriteMotion.CaptureVelocity();
    }

    private void RestoreSprite3DState(HandoffData data)
    {
        if (!Sprite3DOn || _spriteMotion == null) return;
        _spriteMotion.Restore(data.Orientation3D,data.AngularVelocity3D,_physics.SpinAngle);
        DrawSprite3D();
    }
}
