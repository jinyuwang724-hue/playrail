#pragma once
#include "Event.h"
#include "ScalingRuntime.h"
#include <winrt/Magpie.h>
#include <winrt/Windows.System.Threading.h>

namespace Magpie {
class ScalingRuntime;
}

namespace Magpie {

struct Profile;
struct Cropping;
struct ScalingOptions;

class ScalingService {
public:
	static ScalingService& Get() noexcept;

	ScalingService(const ScalingService&) = delete;
	ScalingService(ScalingService&&) = delete;

	~ScalingService();

	void Initialize();

	void ClawMode(bool value) { _clawMode = value; }
	bool ClawStart(HWND target, int capture, bool sharpen, float strength, Cropping crop);
	bool ClawStart(HWND target, ScalingOptions&& options, std::optional<ToolbarState> toolbar = std::nullopt);
	void ClawStop() { if (_scalingRuntime) _scalingRuntime->Stop(); }
	void ClawSetCover(bool cover) { if (_scalingRuntime) _scalingRuntime->ClawSetCover(cover); }
	void ClawSetPlainRects(std::vector<std::array<float, 4>> rects) { if (_scalingRuntime) _scalingRuntime->ClawSetPlainRects(std::move(rects)); }
	int ClawState() const { return _scalingRuntime ? (int)_scalingRuntime->State() : 0; }
	int ClawError() const { return _clawError.load(); }

	void Uninitialize();

	void StartTimer(bool windowedMode);

	void StopTimer();

	bool IsTimerOn() const noexcept {
		return _curCountdownSeconds > 0;
	}

	bool IsTimerOn(bool windowedMode) const noexcept {
		return IsTimerOn() && windowedMode == _isCurCountdownWindowedMode;
	}

	double TimerProgress() const noexcept {
		return SecondsLeft() / _curCountdownSeconds;
	}

	double SecondsLeft() const noexcept;

	bool IsScaling() const noexcept;

	// 强制重新检查前台窗口
	void CheckForeground();

	Event<bool, bool> IsTimerOnChanged;
	Event<double> TimerTick;
	Event<bool> IsScalingChanged;

private:
	ScalingService() = default;
	bool _clawMode = false;
	std::atomic<int> _clawError = 0;

	void _ShortcutService_ShortcutPressed(winrt::Magpie::ShortcutAction action);

	void _CountDownTimer_Tick(winrt::IInspectable const&, winrt::IInspectable const&);

	winrt::fire_and_forget _CheckForegroundTimer_Tick(winrt::Threading::ThreadPoolTimer const& timer);

	void _ScalingRuntime_StateChanged(ScalingState value);

	void _ScaleForegroundWindow(bool windowedMode);

	void _StartScale(HWND hWnd, const Profile& profile, bool windowedMode, bool force);

	ScalingError _StartScaleImpl(HWND hWnd, const Profile& profile, bool windowedMode, bool force);

	std::optional<ScalingRuntime> _scalingRuntime;

	winrt::DispatcherTimer _countDownTimer;
	// DispatcherTimer 在不显示主窗口时可能停滞，因此使用 ThreadPoolTimer
	winrt::Threading::ThreadPoolTimer _checkForegroundTimer{ nullptr };

	Event<winrt::Magpie::ShortcutAction>::EventRevoker _shortcutActivatedRevoker;

	std::chrono::steady_clock::time_point _timerStartTimePoint;

	uint32_t _curCountdownSeconds = 0;
	bool _isCurCountdownWindowedMode = false;

	HWND _hwndCurSrc = NULL;
	// 1. 避免重复检查同一个窗口
	// 2. 用户使用热键退出全屏后暂时阻止该窗口自动放大
	HWND _hwndChecked = NULL;
};

}
