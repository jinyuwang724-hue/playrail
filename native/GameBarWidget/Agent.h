#pragma once
// Connection to the toolbox agent: JSON messages over a local WebSocket (see plugin/claw_agent.py).
// The endpoint and token are published into this app's LocalState (agent.json). Everything here
// runs on the UI thread; socket callbacks are marshalled back to it.
#include <deque>
#include <functional>
#include <map>
#include <memory>

namespace claw
{
    using winrt::Windows::Data::Json::IJsonValue;
    using winrt::Windows::Data::Json::JsonArray;

    class Agent : public std::enable_shared_from_this<Agent>
    {
    public:
        using Reply = std::function<void(IJsonValue const& result, winrt::hstring const& error)>;

        explicit Agent(winrt::Windows::UI::Core::CoreDispatcher dispatcher) : m_dispatcher(dispatcher) {}

        std::function<void(winrt::hstring const& event, IJsonValue const& value)> OnEvent;
        std::function<void(bool connected)> OnConnection;

        bool Ready() const { return m_ready; }
        void Connect();
        void Shutdown();
        // Queued while not connected; sent once the connection is up.
        void Call(std::wstring const& method, JsonArray const& args = JsonArray(), Reply reply = nullptr);

    private:
        winrt::fire_and_forget ConnectAsync();
        winrt::fire_and_forget Pump();
        void Received(winrt::hstring const& text);
        void Closed();

        winrt::Windows::UI::Core::CoreDispatcher m_dispatcher;
        winrt::Windows::Networking::Sockets::MessageWebSocket m_socket{ nullptr };
        winrt::Windows::Storage::Streams::DataWriter m_writer{ nullptr };
        winrt::hstring m_token;
        bool m_ready = false, m_connecting = false, m_pumping = false, m_stopped = false;
        int m_sequence = 0;
        std::map<int, Reply> m_pending;
        std::deque<winrt::hstring> m_outbox;  // serialized: DataWriter allows one StoreAsync at a time
        std::deque<std::pair<std::wstring, std::pair<JsonArray, Reply>>> m_waiting;  // before connected
    };
}
