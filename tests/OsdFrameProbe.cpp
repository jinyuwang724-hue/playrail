#include <windows.h>
#include <shellapi.h>
#include <d3d9.h>
#include <cstdio>
#include <vector>

// A bounded, deterministic presenting workload for OSD frame-tracking tests.
static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_DESTROY) {
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(window, message, wparam, lparam);
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
    int count;
    auto args = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!args || count != 2) return 1;
    WNDCLASSW wc = {};
    wc.lpfnWndProc = WindowProc;
    wc.hInstance = instance;
    wc.lpszClassName = L"ClawOsdFrameProbe";
    RegisterClassW(&wc);
    HWND window = CreateWindowW(wc.lpszClassName, L"Claw OSD frame probe", WS_OVERLAPPEDWINDOW,
        280, 180, 960, 540, nullptr, nullptr, instance, nullptr);
    if (!window) return 2;
    auto d3d = Direct3DCreate9(D3D_SDK_VERSION);
    if (!d3d) return 3;
    D3DPRESENT_PARAMETERS pp = {};
    pp.Windowed = TRUE;
    pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
    pp.hDeviceWindow = window;
    pp.PresentationInterval = D3DPRESENT_INTERVAL_ONE;
    IDirect3DDevice9* device = nullptr;
    if (FAILED(d3d->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, window,
        D3DCREATE_SOFTWARE_VERTEXPROCESSING, &pp, &device))) return 4;
    ShowWindow(window, SW_SHOW);
    LARGE_INTEGER frequency, first, previous;
    QueryPerformanceFrequency(&frequency);
    QueryPerformanceCounter(&first);
    previous = first;
    std::vector<double> intervals;
    intervals.reserve(10000);
    MSG message;
    bool running = true;
    unsigned frame = 0;
    while (running) {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            if (message.message == WM_QUIT) running = false;
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        if (!running) break;
        device->Clear(0, nullptr, D3DCLEAR_TARGET, D3DCOLOR_XRGB(32 + frame++ % 80, 72, 128), 1, 0);
        if (FAILED(device->Present(nullptr, nullptr, nullptr, nullptr))) break;
        LARGE_INTEGER now;
        QueryPerformanceCounter(&now);
        intervals.push_back(1000.0 * (now.QuadPart - previous.QuadPart) / frequency.QuadPart);
        previous = now;
        if (now.QuadPart - first.QuadPart >= frequency.QuadPart * 55) break;
    }
    device->Release();
    d3d->Release();
    FILE* output;
    if (_wfopen_s(&output, args[1], L"w") == 0) {
        std::fputs("FrameMilliseconds\n", output);
        for (double interval : intervals) std::fprintf(output, "%.5f\n", interval);
        std::fclose(output);
    }
    LocalFree(args);
    return intervals.empty() ? 5 : 0;
}
