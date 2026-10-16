#pragma once
// Stroke icons from the approved design (24x24), as XAML path data. Generated from the SVGs.
#include <map>
#include <string>

inline const std::map<std::wstring, std::wstring>& Icons()
{
    static const std::map<std::wstring, std::wstring> icons{
        { L"power", L"M13 2.5 4.5 14H11l-1 7.5L18.5 10H12z" },
        { L"fan", L"M10,12 A2,2 0 1 0 14,12 A2,2 0 1 0 10,12 Z M12 10c-1-4 0-7 3-7s3 4-1 7M14 12c4-1 7 0 7 3s-4 3-7-1M12 14c1 4 0 7-3 7s-3-4 1-7M10 12c-4 1-7 0-7-3s4-3 7 1" },
        { L"fps", L"M5,5 H19 A2,2 0 0 1 21,7 V17 A2,2 0 0 1 19,19 H5 A2,2 0 0 1 3,17 V7 A2,2 0 0 1 5,5 Z M7 15l3-4 3 3 4-5" },
        { L"refresh", L"M5,4 H19 A2,2 0 0 1 21,6 V14 A2,2 0 0 1 19,16 H5 A2,2 0 0 1 3,14 V6 A2,2 0 0 1 5,4 Z M8 20h8M12 16v4" },
        { L"osd", L"M4 17a8 8 0 1 1 16 0 M12 17l4-5" },
        { L"magpie", L"M4 10V4h6M20 14v6h-6M4 4l6 6M20 20l-6-6" },
        { L"rumble", L"M10,3 H14 A2,2 0 0 1 16,5 V19 A2,2 0 0 1 14,21 H10 A2,2 0 0 1 8,19 V5 A2,2 0 0 1 10,3 Z M4 8v8M20 8v8" },
        { L"keyboard", L"M4.5,6 H19.5 A2,2 0 0 1 21.5,8 V16 A2,2 0 0 1 19.5,18 H4.5 A2,2 0 0 1 2.5,16 V8 A2,2 0 0 1 4.5,6 Z M6 10h.01M9 10h.01M12 10h.01M15 10h.01M18 10h.01M8 14h8" },
        { L"close", L"M5,4 H19 A2,2 0 0 1 21,6 V18 A2,2 0 0 1 19,20 H5 A2,2 0 0 1 3,18 V6 A2,2 0 0 1 5,4 Z M9.5 9.5l5 5M14.5 9.5l-5 5" },
        { L"target", L"M4,12 A8,8 0 1 0 20,12 A8,8 0 1 0 4,12 Z M9,12 A3,3 0 1 0 15,12 A3,3 0 1 0 9,12 Z" },
        { L"cpu", L"M8,6 H16 A2,2 0 0 1 18,8 V16 A2,2 0 0 1 16,18 H8 A2,2 0 0 1 6,16 V8 A2,2 0 0 1 8,6 Z M9 3v3M15 3v3M9 18v3M15 18v3M3 9h3M3 15h3M18 9h3M18 15h3" },
        { L"fanfull", L"M4 12h3l2-5 3 10 2-5h6" },
        { L"ecore", L"M7 7h10M7 12h7M7 17h10M7 7v10" },
        { L"sharpen", L"M12 3 4 20h16z" },
        { L"screenshot", L"M4 8V6a2 2 0 0 1 2-2h2M16 4h2a2 2 0 0 1 2 2v2M20 16v2a2 2 0 0 1-2 2h-2M8 20H6a2 2 0 0 1-2-2v-2 M9,12 A3,3 0 1 0 15,12 A3,3 0 1 0 9,12 Z" },
        { L"edit", L"M4 20h4L19 9l-4-4L4 16z M13.5 6.5l4 4" },
        { L"sun", L"M8,12 A4,4 0 1 0 16,12 A4,4 0 1 0 8,12 Z M12 2.5v2M12 19.5v2M4.6 4.6l1.4 1.4M18 18l1.4 1.4M2.5 12h2M19.5 12h2M4.6 19.4 6 18M18 6l1.4-1.4" },
        { L"speaker", L"M4 9h4l5-4v14l-5-4H4z M16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12" },
        { L"muted", L"M4 9h4l5-4v14l-5-4H4z M17 9l5 6M22 9l-5 6" },
        { L"shutdown", L"M12 3v8 M6.3 6.8a8 8 0 1 0 11.4 0" },
        { L"sleep", L"M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z" },
        { L"restart", L"M20 12a8 8 0 1 1-2.3-5.7 M20 4v4h-4" },
        { L"lock", L"M7,11 H17 A2,2 0 0 1 19,13 V18 A2,2 0 0 1 17,20 H7 A2,2 0 0 1 5,18 V13 A2,2 0 0 1 7,11 Z M8 11V8a4 4 0 0 1 8 0v3" },
        { L"minus", L"M6 12h12" },
        { L"plus", L"M12 6v12M6 12h12" },
        { L"move", L"M12 4v16M4 12h16M12 4l-3 3M12 4l3 3M12 20l-3-3M12 20l3-3M4 12l3-3M4 12l3 3M20 12l-3-3M20 12l-3 3" },
    };
    return icons;
}
