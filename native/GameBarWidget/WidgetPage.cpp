#include "pch.h"
#include "WidgetPage.h"
#if __has_include("WidgetPage.g.cpp")
#include "WidgetPage.g.cpp"
#endif

using namespace winrt;
using namespace Windows::Foundation;
using namespace Windows::UI::Xaml;
using namespace Microsoft::Web::WebView2::Core;
using namespace Microsoft::Gaming::XboxGameBar;

namespace winrt::ClawWidget::implementation
{
    WidgetPage::WidgetPage()
    {
        InitializeComponent();
    }

    void WidgetPage::OnNavigatedTo(Navigation::NavigationEventArgs const& e)
    {
        m_widget = e.Parameter().try_as<XboxGameBarWidget>();
        StartAsync();
    }

    IAsyncAction WidgetPage::StartAsync()
    {
        auto strong = get_strong();
        // Transparent until the page paints, so Game Bar never flashes white (WinUI 2 has no property for it).
        SetEnvironmentVariableW(L"WEBVIEW2_DEFAULT_BACKGROUND_COLOR", L"0");
        co_await Web().EnsureCoreWebView2Async();
        auto core = Web().CoreWebView2();
        auto settings = core.Settings();
        settings.IsStatusBarEnabled(false);
        settings.AreDefaultContextMenusEnabled(false);
        settings.IsZoomControlEnabled(false);
        // Page files ship in the package; the agent's endpoint and token sit in this app's LocalState.
        auto package = Windows::ApplicationModel::Package::Current().InstalledLocation().Path();
        core.SetVirtualHostNameToFolderMapping(L"widget.claw", package + L"\\web", CoreWebView2HostResourceAccessKind::Allow);
        core.SetVirtualHostNameToFolderMapping(L"state.claw",
            Windows::Storage::ApplicationData::Current().LocalFolder().Path(), CoreWebView2HostResourceAccessKind::Allow);
        if (m_widget)
        {
            // Tell the page when Game Bar opens or closes, so it can poll only while visible.
            m_displayModeToken = m_widget.GameBarDisplayModeChanged([weak = get_weak()](auto&&, auto&&) {
                if (auto self = weak.get())
                    self->Dispatcher().RunAsync(Windows::UI::Core::CoreDispatcherPriority::Normal, [self]() {
                        bool open = self->m_widget.GameBarDisplayMode() == XboxGameBarDisplayMode::Foreground;
                        self->Post(open ? L"{\"gamebar\":\"foreground\"}" : L"{\"gamebar\":\"pinned\"}");
                        // Game Bar opened: bring this widget to the front with controller focus, the
                        // way ClawTweaks does, so the Xbox button lands here instead of on Home.
                        if (open) self->FocusSelf();
                    });
            });
        }
        core.Navigate(L"https://widget.claw/index.html");
    }

    void WidgetPage::FocusSelf()
    {
        auto now = std::chrono::steady_clock::now();
        if (now - m_lastFocus < std::chrono::seconds(2)) return;  // the launch itself re-raises Foreground
        m_lastFocus = now;
        Windows::System::Launcher::LaunchUriAsync(Uri(
            L"ms-gamebar://launchForeground/activate/ClawToolbox.GameBarWidget_ba3r68zr466za_App_Toolbox/"));
    }

    void WidgetPage::Post(hstring const& json)
    {
        if (auto core = Web().CoreWebView2()) core.PostWebMessageAsJson(json);
    }
}
