#include "pch.h"
#include "Agent.h"

using namespace winrt;
using namespace Windows::Data::Json;
using namespace Windows::Foundation;
using namespace Windows::Networking::Sockets;
using namespace Windows::Storage;
using namespace Windows::Storage::Streams;
using namespace Windows::UI::Core;

namespace claw
{
    void Agent::Connect()
    {
        if (m_stopped || m_ready || m_connecting) return;
        ConnectAsync();
    }

    fire_and_forget Agent::ConnectAsync()
    {
        auto lifetime = shared_from_this();
        m_connecting = true;
        try
        {
            auto file = co_await ApplicationData::Current().LocalFolder().GetFileAsync(L"agent.json");
            auto endpoint = JsonObject::Parse(co_await FileIO::ReadTextAsync(file));
            if (m_stopped) co_return;
            m_token = endpoint.GetNamedString(L"token");
            auto port = static_cast<int>(endpoint.GetNamedNumber(L"wsPort"));

            m_socket = MessageWebSocket();
            m_socket.Control().MessageType(SocketMessageType::Utf8);
            auto weak = weak_from_this();
            m_socket.MessageReceived([weak](MessageWebSocket const&, MessageWebSocketMessageReceivedEventArgs const& args) {
                hstring text;
                try { auto reader = args.GetDataReader(); reader.UnicodeEncoding(UnicodeEncoding::Utf8); text = reader.ReadString(reader.UnconsumedBufferLength()); }
                catch (...) { return; }
                try { if (auto self = weak.lock(); self && !self->m_stopped) self->m_dispatcher.RunAsync(CoreDispatcherPriority::Normal, [weak, text]() { if (auto current = weak.lock(); current && !current->m_stopped) current->Received(text); }); } catch (...) {}
            });
            m_socket.Closed([weak](auto&&, auto&&) { try { if (auto self = weak.lock(); self && !self->m_stopped) self->m_dispatcher.RunAsync(CoreDispatcherPriority::Normal, [weak]() { if (auto current = weak.lock(); current && !current->m_stopped) current->Closed(); }); } catch (...) {} });
            co_await m_socket.ConnectAsync(Uri(L"ws://127.0.0.1:" + to_hstring(port)));
            if (m_stopped) co_return;
            m_writer = DataWriter(m_socket.OutputStream());
            m_writer.UnicodeEncoding(UnicodeEncoding::Utf8);
            JsonObject hello;
            hello.Insert(L"hello", JsonValue::CreateStringValue(m_token));
            hello.Insert(L"client", JsonValue::CreateStringValue(L"gamebar"));
            m_outbox.push_back(hello.Stringify());
            Pump();
        }
        catch (...)
        {
            m_connecting = false;
            m_socket = nullptr;
            if (!m_stopped && OnConnection) OnConnection(false);
        }
    }

    void Agent::Call(std::wstring const& method, JsonArray const& args, Reply reply)
    {
        if (m_stopped) return;
        if (!m_ready) { m_waiting.push_back({ method, { args, reply } }); Connect(); return; }
        int id = ++m_sequence;
        if (reply) m_pending[id] = reply;
        JsonObject request;
        request.Insert(L"id", JsonValue::CreateNumberValue(id));
        request.Insert(L"method", JsonValue::CreateStringValue(method));
        request.Insert(L"args", args);
        m_outbox.push_back(request.Stringify());
        Pump();
    }

    fire_and_forget Agent::Pump()
    {
        auto lifetime = shared_from_this();
        if (m_pumping || !m_writer) co_return;
        m_pumping = true;
        try
        {
            while (!m_stopped && !m_outbox.empty() && m_writer)
            {
                m_writer.WriteString(m_outbox.front());
                m_outbox.pop_front();
                co_await m_writer.StoreAsync();
            }
        }
        catch (...) { Closed(); }
        m_pumping = false;
    }

    void Agent::Received(hstring const& text)
    {
        if (m_stopped) return;
        JsonObject message{ nullptr };
        if (!JsonObject::TryParse(text, message)) return;
        if (message.HasKey(L"hello"))
        {
            m_ready = true;
            m_connecting = false;
            if (OnConnection) OnConnection(true);
            auto waiting = std::move(m_waiting);
            for (auto& [method, call] : waiting) Call(method, call.first, call.second);
            return;
        }
        if (message.HasKey(L"event"))
        {
            if (OnEvent) OnEvent(message.GetNamedString(L"event"), message.GetNamedValue(L"value", JsonValue::CreateNullValue()));
            return;
        }
        int id = static_cast<int>(message.GetNamedNumber(L"id", 0));
        auto found = m_pending.find(id);
        if (found == m_pending.end()) return;
        auto reply = found->second;
        m_pending.erase(found);
        if (message.HasKey(L"error")) reply(JsonValue::CreateNullValue(), message.GetNamedString(L"error", L"工具箱后台出错"));
        else reply(message.GetNamedValue(L"result", JsonValue::CreateNullValue()), L"");
    }

    void Agent::Closed()
    {
        if (m_stopped) return;
        bool was = m_ready || m_connecting;
        m_ready = m_connecting = false;
        m_socket = nullptr;
        m_writer = nullptr;
        m_outbox.clear();
        auto pending = std::move(m_pending);
        for (auto& [id, reply] : pending) reply(JsonValue::CreateNullValue(), L"工具箱后台连接中断");
        if (was && OnConnection) OnConnection(false);
    }

    void Agent::Shutdown()
    {
        m_stopped = true; m_ready = m_connecting = false;
        OnEvent = nullptr; OnConnection = nullptr;
        m_pending.clear(); m_waiting.clear(); m_outbox.clear();
        try { if (m_socket) m_socket.Close(1000, L"Window closed"); } catch (...) {}
        m_writer = nullptr; m_socket = nullptr;
    }
}
