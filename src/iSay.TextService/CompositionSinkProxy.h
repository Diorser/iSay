#pragma once
#include "CompositionContract.h"
#include <memory>

// 使用系统标准 COM 代理隔开宿主保留的接口引用；一项服务的全部会话复用同一代理。
class CompositionSinkProxy
{
    struct Implementation;
    std::unique_ptr<Implementation> implementation_;
public:
    CompositionSinkProxy();
    ~CompositionSinkProxy();
    HRESULT Start();
    HRESULT StartAsync(HWND notificationWindow, UINT notificationMessage);
    HRESULT PollStart();
    void Stop();
    ITfCompositionSink* Get() const;
    UINT64 TerminationCount() const;
    static long LiveObjectCount();
    CompositionSinkProxy(const CompositionSinkProxy&) = delete;
    CompositionSinkProxy& operator=(const CompositionSinkProxy&) = delete;
};
