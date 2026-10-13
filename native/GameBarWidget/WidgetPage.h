#pragma once
#include "WidgetPage.g.h"

namespace winrt::ClawWidget::implementation
{
    struct WidgetPage : WidgetPageT<WidgetPage>
    {
        WidgetPage();
    };
}

namespace winrt::ClawWidget::factory_implementation
{
    struct WidgetPage : WidgetPageT<WidgetPage, implementation::WidgetPage> {};
}
