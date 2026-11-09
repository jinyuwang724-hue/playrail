#pragma once
#include "App.xaml.g.h"
#include <map>
#include <mutex>

namespace winrt::ClawWidget::implementation
{
    struct App : AppT<App>
    {
        App();
        void OnActivated(Windows::ApplicationModel::Activation::IActivatedEventArgs const& args);
        void OnLaunched(Windows::ApplicationModel::Activation::LaunchActivatedEventArgs const& args);

    private:
        void Trace(hstring const& message);
        // Game Bar creates one widget per activation; keep it alive while its window is open.
        std::mutex m_viewsLock;
        std::map<int32_t, Microsoft::Gaming::XboxGameBar::XboxGameBarWidget> m_widgets;
    };
}
