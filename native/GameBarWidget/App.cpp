#include "pch.h"
#include "App.h"
#include "WidgetPage.h"
#include <cstdio>
#include <winrt/Windows.UI.ViewManagement.h>

using namespace winrt;
using namespace Windows::ApplicationModel::Activation;
using namespace Windows::UI::Xaml;
using namespace Windows::UI::Xaml::Controls;
using namespace Microsoft::Gaming::XboxGameBar;

namespace winrt::ClawWidget::implementation
{
    App::App()
    {
        InitializeComponent();
        Suspending([this](auto&&, auto&&) { Trace(L"suspending"); std::scoped_lock lock(m_viewsLock); m_widgets.clear(); });
    }

    void App::Trace(hstring const& message)
    {
        try {
            auto path = Windows::Storage::ApplicationData::Current().LocalFolder().Path() + L"\\lifecycle.log";
            FILE* file = nullptr;
            if (_wfopen_s(&file, path.c_str(), L"a, ccs=UTF-8") == 0 && file) {
                fwprintf(file, L"%llu %s\n", static_cast<unsigned long long>(std::chrono::system_clock::now().time_since_epoch().count()), message.c_str());
                fclose(file);
            }
        } catch (...) {}
    }

    // Game Bar starts the widget through the ms-gamebarwidget protocol.
    void App::OnActivated(IActivatedEventArgs const& args)
    {
        if (args.Kind() != ActivationKind::Protocol) return;
        auto protocol = args.as<IProtocolActivatedEventArgs>();
        if (protocol.Uri().SchemeName() != L"ms-gamebarwidget") return;
        auto widgetArgs = args.as<XboxGameBarWidgetActivatedEventArgs>();
        auto viewId = Windows::UI::ViewManagement::ApplicationView::GetForCurrentView().Id();
        XboxGameBarWidget widget{ nullptr };
        if (!widgetArgs.IsLaunchActivation())
        {
            std::scoped_lock lock(m_viewsLock);
            auto found = m_widgets.find(viewId);
            if (found == m_widgets.end()) return; // Repeat activation cannot create a new SDK connection.
            widget = found->second;
            if (protocol.Uri().Query() == L"?command=close") { Trace(L"widget close requested"); widget.Close(); return; }
            auto frame = Window::Current().Content().try_as<Frame>();
            if (frame && frame.Content()) {
                // A desktop preview must never constrain the host-owned viewport.
                frame.ClearValue(FrameworkElement::WidthProperty()); frame.ClearValue(FrameworkElement::HeightProperty());
                frame.HorizontalAlignment(HorizontalAlignment::Stretch); frame.VerticalAlignment(VerticalAlignment::Stretch);
                auto page = frame.Content().try_as<FrameworkElement>();
                if (page) { page.ClearValue(FrameworkElement::WidthProperty()); page.ClearValue(FrameworkElement::HeightProperty()); page.HorizontalAlignment(HorizontalAlignment::Stretch); page.VerticalAlignment(VerticalAlignment::Stretch); }
                return;
            }
        }
        Frame frame;
        Window::Current().Content(frame);
        if (widgetArgs.IsLaunchActivation())
        {
            widget = XboxGameBarWidget(widgetArgs, Window::Current().CoreWindow(), frame);
            std::scoped_lock lock(m_viewsLock);
            m_widgets.insert_or_assign(viewId, widget);
        }
        bool navigated = frame.Navigate(xaml_typename<ClawWidget::WidgetPage>(), widget);
        Trace(L"widget launch view=" + to_hstring(viewId) + L" page=" + to_hstring(navigated));
        if (!navigated) throw hresult_error(E_FAIL, L"Toolbox page navigation failed");
        auto identity = get_abi(widget);
        Window::Current().Closed([this, viewId, identity](auto&&, auto&&) {
            Trace(L"widget closed view=" + to_hstring(viewId));
            std::scoped_lock lock(m_viewsLock);
            auto found = m_widgets.find(viewId);
            if (found != m_widgets.end() && get_abi(found->second) == identity) m_widgets.erase(found);
        });
        Window::Current().Activate();
    }

    // A separate desktop main window can terminate hosted Game Bar views when closed.
    // Normal launches therefore open the real widget rather than creating a second toolbox window.
    void App::OnLaunched(LaunchActivatedEventArgs const& args)
    {
        Trace(L"normal launch redirected to Game Bar");
        Windows::System::Launcher::LaunchUriAsync(Windows::Foundation::Uri(
            L"ms-gamebar://launchForeground/activate/ClawToolbox.GameBarWidget_ba3r68zr466za_App_Toolbox/"));
    }
}
