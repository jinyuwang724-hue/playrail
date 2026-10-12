#include "pch.h"
#include "ClawControl.h"
#include "App.h"
#include "ScalingService.h"
#include "EffectsService.h"
#include "EffectDesc.h"
#include "ScalingMode.h"
#include "AppSettings.h"
#include "StrHelper.h"
#include "Win32Helper.h"
#include <rapidjson/document.h>
#include <rapidjson/stringbuffer.h>
#include <rapidjson/writer.h>
#include <deque>
#include <thread>
#include <mutex>
#include <atomic>
#include <cmath>

namespace Magpie::ClawControl {
static std::thread reader;
static std::mutex mutex;
static std::deque<std::string> requests;
static std::atomic<bool> closing = false;
static DWORD uiThread = 0;
static HANDLE input = nullptr, output = nullptr;
static wil::unique_handle ownedPipe;

bool Initialize(const wchar_t* arguments) {
    std::wstring_view args(arguments);
    if (args.starts_with(L"--claw-pipe ")) {
        auto tokens = args.substr(12);
        auto separator = tokens.find(L' ');
        if (separator == std::wstring_view::npos) return false;
        std::wstring name(tokens.substr(0, separator));
        if (!name.starts_with(L"claw-magpie-") || name.size() != 44 ||
            name.find_first_not_of(L"abcdefghijklmnopqrstuvwxyz0123456789-") != std::wstring::npos) return false;
        std::wstring path = L"\\\\.\\pipe\\" + name;
        ownedPipe.reset(CreateFile(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr));
        if (!ownedPipe) return false;
        ULONG server = 0;
        std::wstring parent(tokens.substr(separator + 1));
        if (!GetNamedPipeServerProcessId(ownedPipe.get(), &server) || server != wcstoul(parent.c_str(), nullptr, 10)) return false;
        input = output = ownedPipe.get();
    } else {
    input = GetStdHandle(STD_INPUT_HANDLE);
    output = GetStdHandle(STD_OUTPUT_HANDLE);
    }
    if (!input || input == INVALID_HANDLE_VALUE || !output || output == INVALID_HANDLE_VALUE) return false;
    uiThread = GetCurrentThreadId();
    MSG msg; PeekMessage(&msg, nullptr, 0, 0, PM_NOREMOVE);
    ScalingService::Get().ClawMode(true);
    reader = std::thread([] {
        std::string line;
        char buffer[4096]; DWORD count;
        auto read = [&](DWORD& count) {
            if (!ownedPipe) return ReadFile(input, buffer, sizeof(buffer), &count, nullptr) != FALSE;
            wil::unique_handle event(CreateEvent(nullptr, TRUE, FALSE, nullptr));
            OVERLAPPED ov{}; ov.hEvent = event.get();
            if (!ReadFile(input, buffer, sizeof(buffer), nullptr, &ov) && GetLastError() != ERROR_IO_PENDING) return false;
            return GetOverlappedResult(input, &ov, &count, TRUE) != FALSE;
        };
        while (!closing && read(count) && count) {
            for (DWORD i = 0; i < count; ++i) {
                if (buffer[i] == '\n') {
                    { std::lock_guard lock(mutex); requests.push_back(std::move(line)); }
                    line.clear();
                    PostThreadMessage(uiThread, Message, 0, 0);
                } else {
                    line += buffer[i];
                    if (line.size() > 65536) { PostThreadMessage(uiThread, Message, 1, 0); return; }
                }
            }
        }
        if (!closing) PostThreadMessage(uiThread, Message, 1, 0);
    });
    return true;
}

using Writer = rapidjson::Writer<rapidjson::StringBuffer>;

static void WriteCatalog(Writer& w) {
    w.Key("catalog"); w.StartObject(); w.Key("effects"); w.StartArray();
    for (const auto& info : EffectsService::Get().Effects()) {
        w.StartObject(); w.Key("name"); w.String(StrHelper::UTF16ToUTF8(info.name).c_str());
        w.Key("canScale"); w.Bool(info.CanScale());
        w.Key("parameters"); w.StartArray();
        for (const auto& parameter : info.params) {
            w.StartObject(); w.Key("name"); w.String(parameter.name.c_str());
            w.Key("label"); w.String(parameter.label.c_str());
            w.Key("integer"); w.Bool(parameter.constant.index() == 1);
            std::visit([&](const auto& v) {
                w.Key("default"); w.Double(v.defaultValue); w.Key("min"); w.Double(v.minValue);
                w.Key("max"); w.Double(v.maxValue); w.Key("step"); w.Double(v.step);
            }, parameter.constant);
            w.EndObject();
        }
        w.EndArray(); w.EndObject();
    }
    w.EndArray(); w.Key("presets"); w.StartArray();
    for (const auto& mode : AppSettings::Get().ScalingModes()) {
        w.StartObject(); w.Key("name"); w.String(StrHelper::UTF16ToUTF8(mode.name).c_str());
        w.Key("effects"); w.StartArray();
        for (const auto& effect : mode.effects) {
            w.StartObject(); w.Key("name"); w.String(StrHelper::UTF16ToUTF8(effect.name).c_str());
            w.Key("scalingType"); w.Int((int)effect.scalingType);
            w.Key("scale"); w.StartArray(); w.Double(effect.scale.first); w.Double(effect.scale.second); w.EndArray();
            w.Key("parameters"); w.StartObject();
            for (const auto& [name, value] : effect.parameters) {
                w.Key(StrHelper::UTF16ToUTF8(name).c_str()); w.Double(value);
            }
            w.EndObject(); w.EndObject();
        }
        w.EndArray(); w.EndObject();
    }
    w.EndArray(); w.EndObject();
}

static bool Start(const rapidjson::Document& d, ScalingService& service) {
    if (!d.HasMember("target") || !d["target"].IsUint64() || !d.HasMember("capture") || !d["capture"].IsInt() ||
        !d.HasMember("crop") || !d["crop"].IsArray() || d["crop"].Size() != 4) return false;
    const auto& c = d["crop"];
    for (const auto& v : c.GetArray()) if (!v.IsNumber() || v.GetDouble() < 0 || !std::isfinite(v.GetDouble())) return false;
    Cropping crop{c[0].GetFloat(), c[1].GetFloat(), c[2].GetFloat(), c[3].GetFloat()};
    if (!d.HasMember("effects")) {
        return d.HasMember("sharpen") && d["sharpen"].IsBool() && d.HasMember("strength") && d["strength"].IsNumber() &&
            service.ClawStart(reinterpret_cast<HWND>(d["target"].GetUint64()), d["capture"].GetInt(),
                d["sharpen"].GetBool(), d["strength"].GetFloat(), crop);
    }
    if (d["capture"].GetInt() < 0 || d["capture"].GetInt() > 3 || !d["effects"].IsArray() ||
        d["effects"].Empty() || d["effects"].Size() > 13) return false;
    ScalingOptions options;
    options.captureMethod = (CaptureMethod)d["capture"].GetInt(); options.cropping = crop;
    for (const auto& item : d["effects"].GetArray()) {
        if (!item.IsObject() || !item.HasMember("name") || !item["name"].IsString()) return false;
        const auto* info = EffectsService::Get().GetEffect(StrHelper::UTF8ToUTF16(item["name"].GetString()));
        if (!info) return false;
        EffectOption effect; effect.name = item["name"].GetString();
        if (info->CanScale()) effect.scalingType = ScalingType::Fit;
        if (item.HasMember("scalingType")) {
            if (!item["scalingType"].IsInt() || item["scalingType"].GetInt() < 0 || item["scalingType"].GetInt() > 3) return false;
            effect.scalingType = (ScalingType)item["scalingType"].GetInt();
        }
        if (item.HasMember("scale")) {
            const auto& scale = item["scale"];
            if (!scale.IsArray() || scale.Size() != 2) return false;
            for (const auto& v : scale.GetArray()) if (!v.IsNumber() || !std::isfinite(v.GetDouble()) || v.GetDouble() <= 0 || v.GetDouble() > 16384) return false;
            effect.scale = {scale[0].GetFloat(), scale[1].GetFloat()};
        }
        if (item.HasMember("parameters")) {
            if (!item["parameters"].IsObject()) return false;
            for (auto it = item["parameters"].MemberBegin(); it != item["parameters"].MemberEnd(); ++it) {
                if (!it->value.IsNumber() || !std::isfinite(it->value.GetDouble())) return false;
                auto parameter = std::find_if(info->params.begin(), info->params.end(), [&](const auto& p) { return p.name == it->name.GetString(); });
                if (parameter == info->params.end()) return false;
                double value = it->value.GetDouble();
                bool valid = std::visit([&](const auto& p) { return value >= p.minValue && value <= p.maxValue; }, parameter->constant);
                if (!valid || (parameter->constant.index() == 1 && value != std::floor(value))) return false;
                effect.parameters[it->name.GetString()] = (float)value;
            }
        }
        options.effects.push_back(std::move(effect));
    }
    // Fixed 2x neural effects may undershoot or exceed the display. Normalize the
    // final result using the same uniform fit stage; cover crop remains unchanged.
    if (d.HasMember("fitOutput") && d["fitOutput"].IsBool() && d["fitOutput"].GetBool()) {
        EffectOption fit; fit.name = "Bicubic"; fit.scalingType = ScalingType::Fit;
        fit.parameters["paramB"] = 0; fit.parameters["paramC"] = .5f;
        options.effects.push_back(std::move(fit));
    }
    if (d.HasMember("maxFrameRate") && d["maxFrameRate"].IsNumber() && d["maxFrameRate"].GetDouble() >= 15 && d["maxFrameRate"].GetDouble() <= 240)
        options.maxFrameRate = d["maxFrameRate"].GetFloat();
    if (d.HasMember("game3D") && d["game3D"].IsBool()) options.Is3DGameMode(d["game3D"].GetBool());
    if (d.HasMember("adjustCursor") && d["adjustCursor"].IsBool()) options.IsAdjustCursorSpeed(d["adjustCursor"].GetBool());
    // Fill by covering the screen at output time, so "view" can switch to the whole
    // frame (Steam side menus) and back without restarting capture.
    if (d.HasMember("cover") && d["cover"].IsBool()) options.clawCover = d["cover"].GetBool();
    return service.ClawStart(reinterpret_cast<HWND>(d["target"].GetUint64()), std::move(options));
}

void Dispatch() {
    std::deque<std::string> pending;
    { std::lock_guard lock(mutex); pending.swap(requests); }
    for (const auto& line : pending) {
        rapidjson::Document d;
        d.Parse(line.c_str());
        uint64_t id = 0;
        bool ok = false;
        const char* error = "invalid_request";
        auto& service = ScalingService::Get();
        if (d.IsObject() && d.HasMember("id") && d["id"].IsUint64() &&
            d.HasMember("command") && d["command"].IsString()) {
            id = d["id"].GetUint64();
            std::string_view command = d["command"].GetString();
            if (command == "state" || command == "catalog") { ok = true; }
            else if (command == "stop") { service.ClawStop(); ok = true; }
            else if (command == "plain") {
                // Normalized source rectangles kept off the effect chain (Steam side menus).
                std::vector<std::array<float, 4>> rects;
                ok = d.HasMember("rects") && d["rects"].IsArray() && d["rects"].Size() <= 4;
                if (ok) for (const auto& r : d["rects"].GetArray()) {
                    if (!r.IsArray() || r.Size() != 4) { ok = false; break; }
                    std::array<float, 4> rect{};
                    for (uint32_t i = 0; i < 4; ++i) {
                        if (!r[i].IsNumber() || !std::isfinite(r[i].GetDouble()) || r[i].GetDouble() < 0 || r[i].GetDouble() > 1) { ok = false; break; }
                        rect[i] = r[i].GetFloat();
                    }
                    if (!ok || rect[2] <= rect[0] || rect[3] <= rect[1]) { ok = false; break; }
                    rects.push_back(rect);
                }
                if (ok) service.ClawSetPlainRects(std::move(rects));
            }
            else if (command == "view") {
                ok = d.HasMember("cover") && d["cover"].IsBool();
                if (ok) service.ClawSetCover(d["cover"].GetBool());
            }
            else if (command == "start") {
                ok = Start(d, service); error = "start_rejected";
            }
        }
        rapidjson::StringBuffer buffer;
        rapidjson::Writer<rapidjson::StringBuffer> w(buffer);
        w.StartObject();
        w.Key("id"); w.Uint64(id);
        w.Key("ok"); w.Bool(ok);
        w.Key("pid"); w.Uint(GetCurrentProcessId());
        w.Key("elevated"); w.Bool(Win32Helper::IsProcessElevated());
        w.Key("state"); w.Int(service.ClawState());
        w.Key("errorCode"); w.Int(service.ClawError());
        if (ok && std::string_view(d["command"].GetString()) == "catalog") WriteCatalog(w);
        if (!ok) { w.Key("error"); w.String(error); }
        w.EndObject();
        std::string response(buffer.GetString(), buffer.GetSize()); response += '\n';
        DWORD written = 0;
        bool sent;
        if (ownedPipe) {
            wil::unique_handle event(CreateEvent(nullptr, TRUE, FALSE, nullptr));
            OVERLAPPED ov{}; ov.hEvent = event.get();
            sent = WriteFile(output, response.data(), (DWORD)response.size(), nullptr, &ov) || GetLastError() == ERROR_IO_PENDING;
            if (sent) sent = GetOverlappedResult(output, &ov, &written, TRUE) != FALSE;
        } else sent = WriteFile(output, response.data(), (DWORD)response.size(), &written, nullptr) != FALSE;
        if (!sent || written != response.size()) {
            winrt::Magpie::implementation::App::Get().Quit(); return;
        }
    }
}

void Uninitialize() {
    closing = true;
    if (reader.joinable()) {
        if (ownedPipe) CancelIoEx(ownedPipe.get(), nullptr);
        else CancelSynchronousIo(reader.native_handle());
        reader.join();
    }
    ownedPipe.reset();
}
}
