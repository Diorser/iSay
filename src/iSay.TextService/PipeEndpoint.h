#pragma once
#include "CompositionContract.h"
#include <memory>
#include <string>

// 请求由管道和异步编辑会话共同持有，超时只能撤销尚未开始的编辑。
struct PipeCommand
{
    IsayCompositionCommand command{};
    GUID session{};
    UINT64 sequence = 0;
    std::wstring text;
    virtual bool Cancelled() const = 0;
    virtual void Complete(const IsayCompositionResult& result) = 0;
    virtual void CompleteLearning(const IsayLearningResult& result, const std::wstring& source, const std::wstring& target) = 0;
    virtual void CompleteKeyboard(const IsayKeyboardResult& result, const std::vector<IsayKeyboardCommit>& commits) = 0;
    virtual ~PipeCommand() = default;
};

class PipeCommandTarget
{
public:
    virtual void DispatchPipeCommand(std::shared_ptr<PipeCommand> request) = 0;
    virtual void DispatchMaintenance() = 0;
    virtual ~PipeCommandTarget() = default;
};

// 后台线程只解析本机消息；实际组合输入始终交回创建服务的 STA 线程。
class PipeEndpoint
{
    struct Implementation;
    std::unique_ptr<Implementation> implementation_;
public:
    static constexpr UINT MaintenanceMessage = WM_APP + 0x5a2;
    PipeEndpoint();
    ~PipeEndpoint();
    HRESULT Start(PipeCommandTarget* control);
    void Wake();
    void ScheduleMaintenance(UINT milliseconds);
    HWND WindowHandle() const;
    void Stop();
    PipeEndpoint(const PipeEndpoint&) = delete;
    PipeEndpoint& operator=(const PipeEndpoint&) = delete;
};
