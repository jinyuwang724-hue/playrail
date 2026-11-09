#pragma once
#include "Agent.h"

namespace claw
{
    // Called on the widget UI thread. Reports the OS target, not the widget's own foreground HWND.
    class GameTarget
    {
    public:
        explicit GameTarget(winrt::Microsoft::Gaming::XboxGameBar::XboxGameBarWidget const& widget)
        {
            try { m_tracker = winrt::Microsoft::Gaming::XboxGameBar::XboxGameBarAppTargetTracker(widget); }
            catch (...) {}
        }

        void Report(Agent& agent)
        {
            if (!agent.Ready()) return;
            using namespace winrt::Windows::Data::Json;
            JsonObject report;
            bool enabled = false;
            uint64_t hwnd = 0;
            bool game = false;
            try
            {
                enabled = m_tracker && m_tracker.Setting() == winrt::Microsoft::Gaming::XboxGameBar::XboxGameBarAppTargetSetting::Enabled;
                if (enabled)
                {
                    auto target = m_tracker.GetTarget();
                    if (target) { hwnd = target.Hwnd(); game = target.IsGame(); }
                }
            }
            catch (...) { enabled = false; }
            report.SetNamedValue(L"enabled", JsonValue::CreateBooleanValue(enabled));
            report.SetNamedValue(L"hwnd", JsonValue::CreateNumberValue(static_cast<double>(hwnd)));
            report.SetNamedValue(L"isGame", JsonValue::CreateBooleanValue(game));
            JsonArray args; args.Append(report);
            agent.Call(L"report_game_target", args);
        }
    private:
        winrt::Microsoft::Gaming::XboxGameBar::XboxGameBarAppTargetTracker m_tracker{ nullptr };
    };
}
