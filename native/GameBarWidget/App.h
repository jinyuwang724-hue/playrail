#pragma once
#include "App.xaml.g.h"

namespace winrt::ClawWidget::implementation
{
    struct App : AppT<App>
    {
        App();
        void OnActivated(Windows::ApplicationModel::Activation::IActivatedEventArgs const& args);
        void OnLaunched(Windows::ApplicationModel::Activation::LaunchActivatedEventArgs const& args);

    private:
        // Game Bar creates one widget per activation; keep it alive while its window is open.
        Microsoft::Gaming::XboxGameBar::XboxGameBarWidget m_widget{ nullptr };
    };
}
