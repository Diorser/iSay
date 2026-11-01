#include "CompositionSinkProxy.h"
#include <wrl/client.h>
#include <atomic>
#include <thread>
#include <new>
#ifdef ISAY_TSF_TEST_DELAY_PROXY
#include "../../test/TSF异步冷启动/冷启动故障注入.h"
#endif

using Microsoft::WRL::ComPtr;
namespace
{
    std::atomic<long> liveObjects{0};
    struct SharedState { std::atomic<UINT64> terminations{0}; };

    class Relay final : public ITfCompositionSink
    {
        std::atomic<ULONG> references_{1};
        std::shared_ptr<SharedState> state_;
    public:
        explicit Relay(std::shared_ptr<SharedState> state) : state_(std::move(state)) { ++liveObjects; }
        ~Relay() { --liveObjects; }
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override
        {
            if (!result) return E_POINTER; *result = nullptr;
            if (iid != IID_IUnknown && iid != __uuidof(ITfCompositionSink)) return E_NOINTERFACE;
            *result = static_cast<ITfCompositionSink*>(this); AddRef(); return S_OK;
        }
        ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
        ULONG STDMETHODCALLTYPE Release() override { auto value = --references_; if (!value) delete this; return value; }
        HRESULT STDMETHODCALLTYPE OnCompositionTerminated(TfEditCookie, ITfComposition*) override
        {
            // 这里是 MTA 回调，不读取 STA 的编辑 cookie 或组合对象；正文和属性由原 STA 合法核验。
            state_->terminations.fetch_add(1, std::memory_order_relaxed);
            return S_OK;
        }
    };
}

struct CompositionSinkProxy::Implementation
{
    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    std::shared_ptr<SharedState> state = std::make_shared<SharedState>();
    std::thread worker;
    IStream* stream = nullptr;
    HRESULT initialized = E_PENDING;
    ComPtr<ITfCompositionSink> proxy;

    ~Implementation()
    {
        Stop();
        if (ready) CloseHandle(ready);
        if (stop) CloseHandle(stop);
    }
    HRESULT Launch(HWND notificationWindow, UINT notificationMessage)
    {
        if (!ready || !stop) return E_OUTOFMEMORY;
        worker = std::thread([this, notificationWindow, notificationMessage]
        {
#ifdef ISAY_TSF_TEST_DELAY_PROXY
            IsayTsfDelayProxyForTest(stop);
#endif
            auto hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            const bool apartment = SUCCEEDED(hr);
            ComPtr<ITfCompositionSink> relay;
            if (apartment)
            {
                relay.Attach(new(std::nothrow) Relay(state));
                hr = relay ? CoMarshalInterThreadInterfaceInStream(__uuidof(ITfCompositionSink), relay.Get(), &stream) : E_OUTOFMEMORY;
            }
            initialized = hr; SetEvent(ready);
            // 只投递零参数通知，不携带对象指针，也不要求宿主 STA 等待后台初始化。
            if (notificationWindow) PostMessageW(notificationWindow, notificationMessage, 0, 0);
            if (SUCCEEDED(hr))
            {
                WaitForSingleObject(stop, INFINITE);
                if (stream)
                {
                    // 未被目标公寓消费的封送包必须回到最初封送它的 MTA 释放。
                    CoReleaseMarshalData(stream); stream->Release(); stream = nullptr;
                }
                // 公开 COM 断开操作回收服务器的外部引用；不手工补释放宿主持有的代理引用。
                CoDisconnectObject(relay.Get(), 0);
            }
            relay.Reset();
            if (apartment) CoUninitialize();
        });
        return E_PENDING;
    }
    HRESULT FinishStart()
    {
        if (proxy) return S_OK;
        if (WaitForSingleObject(ready, 0) != WAIT_OBJECT_0) return E_PENDING;
        if (FAILED(initialized)) return initialized;
        if (!stream) return E_UNEXPECTED;
        auto* marshaled = stream; stream = nullptr;
        return CoGetInterfaceAndReleaseStream(marshaled, IID_PPV_ARGS(&proxy));
    }
    HRESULT Start()
    {
        auto hr = Launch(nullptr, 0);
        if (hr != E_PENDING) return hr;
        DWORD index = 0;
        hr = CoWaitForMultipleHandles(COWAIT_DISPATCH_CALLS, 2000, 1, &ready, &index);
        if (FAILED(hr)) return hr;
        return FinishStart();
    }
    void Stop()
    {
        proxy.Reset();
        if (stop) SetEvent(stop);
        if (worker.joinable())
        {
            HANDLE thread = worker.native_handle(); DWORD index = 0;
            // 泵送 COM 调用，避免 STA 在释放跨公寓引用时与 MTA 相互等待。
            CoWaitForMultipleHandles(COWAIT_DISPATCH_CALLS, INFINITE, 1, &thread, &index);
            worker.join();
        }
    }
};

CompositionSinkProxy::CompositionSinkProxy() = default;
CompositionSinkProxy::~CompositionSinkProxy() = default;
HRESULT CompositionSinkProxy::Start()
{
    if (implementation_) return implementation_->FinishStart();
    try
    {
        auto candidate = std::make_unique<Implementation>();
        auto hr = candidate->Start();
        if (SUCCEEDED(hr)) implementation_ = std::move(candidate);
        return hr;
    }
    catch (...) { return E_OUTOFMEMORY; }
}
HRESULT CompositionSinkProxy::StartAsync(HWND notificationWindow, UINT notificationMessage)
{
    if (implementation_) return implementation_->FinishStart();
    try
    {
        auto candidate = std::make_unique<Implementation>();
        auto hr = candidate->Launch(notificationWindow, notificationMessage);
        if (hr == E_PENDING || SUCCEEDED(hr)) implementation_ = std::move(candidate);
        return hr;
    }
    catch (...) { return E_OUTOFMEMORY; }
}
HRESULT CompositionSinkProxy::PollStart() { return implementation_ ? implementation_->FinishStart() : CO_E_NOTINITIALIZED; }
void CompositionSinkProxy::Stop() { implementation_.reset(); }
ITfCompositionSink* CompositionSinkProxy::Get() const { return implementation_ ? implementation_->proxy.Get() : nullptr; }
UINT64 CompositionSinkProxy::TerminationCount() const { return implementation_ ? implementation_->state->terminations.load(std::memory_order_relaxed) : 0; }
long CompositionSinkProxy::LiveObjectCount() { return liveObjects.load(); }
