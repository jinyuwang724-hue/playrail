using ClawLibrary.Engine;
using Windows.Foundation;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    // QamWindow owns the native pointer ID and only forwards the active contact.
    private const uint QsPrimaryContact = 1;
    private readonly TouchPointerGesture _qsOwnGesture = new();
    private float _qsOwnLastY;
    private string? _qsOwnTarget, _qsOwnSlider;

    private TouchSurface QsOwnSurface => new(_networkPage == null ? 0 : NetworkWifi ? 1 : 2,
        _networkKeyboard ? 1 : 0, _qsVersion, _networkExpand);

    private string? QsOwnTargetAt(Point point)
    {
        if (_networkPage == null) return QsItemAt(point.X, point.Y)?.Id;
        if (NetworkKeyAt(point) is { } key) return $"key:{key.Id}";
        return NetworkControlAt(point)?.Id;
    }

    private Point QsOwnPoint(Point point) => new(point.X / QsK, point.Y / QsK);

    private void QsOwnPress(Point point)
    {
        if (!_qsOpen) return;
        MarkInput();
        var p = QsOwnPoint(point);
        if (!_qsOwnGesture.Begin(QsPrimaryContact, p.X, p.Y, QsOwnSurface)) return;
        _qsOwnLastY = (float)p.Y;
        _qsOwnTarget = QsOwnTargetAt(p);
        _qsOwnSlider = QsDirectSliderAt(p.X, p.Y);
        if (_qsOwnSlider is { } slider)
        {
            QsSetFocus(slider);
            QsTouchDrag(slider, point.X);
        }
    }

    private void QsOwnMove(Point point)
    {
        if (!_qsOpen) return;
        var p = QsOwnPoint(point);
        var intent = _qsOwnGesture.Move(QsPrimaryContact, p.X, p.Y, QsOwnSurface);
        if (_qsOwnSlider is { } slider && intent is not (TouchResult.None or TouchResult.Canceled))
        {
            QsTouchDrag(slider, point.X);
            return;
        }
        if (intent == TouchResult.Vertical && _networkPage != null &&
            _qsOwnGesture.StartX < QsW && _qsOwnGesture.StartY >= 128)
        {
            NetworkScrollBy(_qsOwnLastY - (float)p.Y);
            _qsOwnLastY = (float)p.Y;
        }
        else if (intent == TouchResult.Horizontal && _networkPage == null && _qsOwnTarget == "tdp" &&
            _qsOwnGesture.StartY > QsPwY + QsPwH - 35)
            QsTouchDrag("tdp", point.X);
    }

    private void QsOwnRelease(Point? point)
    {
        string? slider = _qsOwnSlider;
        _qsOwnSlider = null;
        if (!_qsOpen || point is not { } screenPoint)
        {
            _qsOwnGesture.Reset();
            return;
        }
        var p = QsOwnPoint(screenPoint);
        var result = _qsOwnGesture.End(QsPrimaryContact, p.X, p.Y, QsOwnSurface);
        if (slider != null && result is not (TouchResult.None or TouchResult.Canceled))
            QsTouchDrag(slider, screenPoint.X);
        else if (slider == null && result == TouchResult.Tap &&
            _qsOwnTarget != null && _qsOwnTarget == QsOwnTargetAt(p))
            QsClick(screenPoint.X, screenPoint.Y);
    }

    private void NetworkScrollBy(float delta)
    {
        if (_networkPage == null) return;
        _networkScroll = Math.Clamp(_networkScroll + delta, 0, Math.Max(0, _networkBottom - QsViewH + 12));
        NetworkPaint();
    }
}
