#include "PipeEndpoint.h"
#include <sddl.h>
#include <atomic>
#include <algorithm>
#include <array>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace
{
    constexpr UINT CompositionDispatchMessage = WM_APP + 0x5a1;
    constexpr UINT CompositionMaintenanceMessage = PipeEndpoint::MaintenanceMessage;
    constexpr WCHAR WindowClass[] = L"iSay.Tsf.Bridge.v1";
    std::mutex windowClassGate;
    unsigned windowClassUsers = 0;
    HINSTANCE windowClassModule = nullptr;

    struct Handle
    {
        HANDLE value = nullptr;
        Handle() = default;
        explicit Handle(HANDLE handle) : value(handle) {}
        ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
        Handle(const Handle&) = delete;
        Handle& operator=(const Handle&) = delete;
    };
    struct Request final : PipeCommand
    {
        IsayCompositionResult result;
        IsayLearningResult learning;
        std::wstring learningSource, learningTarget;
        IsayKeyboardResult keyboard;
        std::vector<IsayKeyboardCommit> keyboardCommits;
        Handle done{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
        std::atomic<bool> abandoned{false};
        bool Cancelled() const override { return abandoned.load(std::memory_order_acquire); }
        void Complete(const IsayCompositionResult& value) override
        {
            result = value;
            SetEvent(done.value);
        }
        void CompleteLearning(const IsayLearningResult& value, const std::wstring& source, const std::wstring& target) override
        {
            learningSource = source; learningTarget = target; learning = value;
            SetEvent(done.value);
        }
        void CompleteKeyboard(const IsayKeyboardResult& value, const std::vector<IsayKeyboardCommit>& commits) override
        {
            keyboard = value; keyboardCommits = commits; SetEvent(done.value);
        }
    };
    bool WellFormed(const std::wstring& text)
    {
        for (size_t index = 0; index < text.size(); ++index)
        {
            const auto value = text[index];
            if (!value) return false;
            if (value >= 0xd800 && value <= 0xdbff)
            {
                if (++index == text.size() || text[index] < 0xdc00 || text[index] > 0xdfff) return false;
            }
            else if (value >= 0xdc00 && value <= 0xdfff) return false;
        }
        return true;
    }
    template<class T> T Read(const BYTE* bytes) { T value{}; memcpy(&value, bytes, sizeof(value)); return value; }
    template<class T> void Write(BYTE* bytes, const T& value) { memcpy(bytes, &value, sizeof(value)); }
}

struct PipeEndpoint::Implementation
{
    PipeCommandTarget* control = nullptr;
    DWORD ownerThread = 0;
    HWND window = nullptr;
    bool classOwned = false;
    Handle stop{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    Handle ready{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    std::thread worker;
    HRESULT startup = E_PENDING;
    std::wstring name;
    std::vector<BYTE> userToken;
    PSECURITY_DESCRIPTOR security = nullptr;
    std::mutex pendingGate;
    std::deque<std::shared_ptr<Request>> pending;

    ~Implementation() { Stop(); if (security) LocalFree(security); }
    static LRESULT CALLBACK WindowProcedure(HWND hwnd, UINT message, WPARAM word, LPARAM parameter)
    {
        if (message == WM_NCCREATE)
        {
            if (!GetWindowLongPtrW(hwnd, GWLP_USERDATA))
            {
                auto* creation = reinterpret_cast<CREATESTRUCTW*>(parameter);
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(creation->lpCreateParams));
            }
        }
        auto* self = reinterpret_cast<Implementation*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (self && message == CompositionMaintenanceMessage)
        {
            self->control->DispatchMaintenance();
            return 0;
        }
        if (self && message == WM_TIMER && word == 0x5a3)
        {
            KillTimer(hwnd, 0x5a3); self->control->DispatchMaintenance(); return 0;
        }
        if (self && message == CompositionDispatchMessage)
        {
            std::shared_ptr<Request> request;
            {
                std::lock_guard guard(self->pendingGate);
                if (self->pending.empty()) return 0;
                request = self->pending.front(); self->pending.pop_front();
            }
            // 消息没有地址或正文；外部伪造窗口消息无法提供宿主可解引用的指针。
            // 已超时的请求在开始编辑前丢弃，不能在用户开始下一段后补写。
            if (!request->abandoned && WaitForSingleObject(self->stop.value, 0) != WAIT_OBJECT_0)
            {
                // 目标只登记异步编辑，绝不能在宿主窗口过程中同步等待文本锁或管道。
                self->control->DispatchPipeCommand(request);
            }
            else
            {
                request->result.Operation = HRESULT_FROM_WIN32(ERROR_CANCELLED);
                SetEvent(request->done.value);
            }
            return 0;
        }
        return DefWindowProcW(hwnd, message, word, parameter);
    }
    HRESULT Initialize(PipeCommandTarget* target)
    {
        if (!target || !stop.value || !ready.value) return E_INVALIDARG;
        control = target; ownerThread = GetCurrentThreadId();
        Handle token;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token.value)) return HRESULT_FROM_WIN32(GetLastError());
        DWORD required = 0; GetTokenInformation(token.value, TokenUser, nullptr, 0, &required);
        if (!required) return HRESULT_FROM_WIN32(GetLastError());
        userToken.resize(required);
        if (!GetTokenInformation(token.value, TokenUser, userToken.data(), required, &required)) return HRESULT_FROM_WIN32(GetLastError());
        LPWSTR sidText = nullptr;
        if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(userToken.data())->User.Sid, &sidText)) return HRESULT_FROM_WIN32(GetLastError());
        const std::wstring sid(sidText); LocalFree(sidText);
        const std::wstring descriptor = L"D:P(A;;GA;;;SY)(A;;GA;;;" + sid + L")";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(descriptor.c_str(), SDDL_REVISION_1, &security, nullptr)) return HRESULT_FROM_WIN32(GetLastError());
        name = L"\\\\.\\pipe\\iSay.Tsf.v1." + sid + L"." + std::to_wstring(GetCurrentProcessId()) + L"." + std::to_wstring(ownerThread);
        {
            std::lock_guard guard(windowClassGate);
            if (!windowClassUsers)
            {
                GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                    reinterpret_cast<LPCWSTR>(&WindowProcedure), &windowClassModule);
                WNDCLASSW klass{}; klass.lpfnWndProc = WindowProcedure; klass.hInstance = windowClassModule; klass.lpszClassName = WindowClass;
                if (!RegisterClassW(&klass)) return HRESULT_FROM_WIN32(GetLastError());
            }
            ++windowClassUsers; classOwned = true;
        }
        window = CreateWindowExW(0, WindowClass, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, windowClassModule, this);
        if (!window) return HRESULT_FROM_WIN32(GetLastError());
        try { worker = std::thread([this] { Run(); }); }
        catch (...) { return E_OUTOFMEMORY; }
        if (WaitForSingleObject(ready.value, 2000) != WAIT_OBJECT_0) return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
        return startup;
    }
    void Stop()
    {
        if (stop.value) SetEvent(stop.value);
        if (worker.joinable()) worker.join();
        if (window)
        {
            // 先等监听线程退出，再释放私有队列中的请求，窗口消息没有所有权数据。
            {
                std::lock_guard guard(pendingGate);
                for (const auto& request : pending) { request->abandoned = true; SetEvent(request->done.value); }
                pending.clear();
            }
            MSG message{};
            while (PeekMessageW(&message, window, CompositionDispatchMessage, CompositionDispatchMessage, PM_REMOVE))
            { }
            DestroyWindow(window); window = nullptr;
        }
        if (classOwned)
        {
            std::lock_guard guard(windowClassGate);
            if (--windowClassUsers == 0) { UnregisterClassW(WindowClass, windowClassModule); windowClassModule = nullptr; }
            classOwned = false;
        }
    }
    bool Transfer(HANDLE pipe, void* buffer, DWORD size, bool write)
    {
        BYTE* cursor = static_cast<BYTE*>(buffer);
        while (size)
        {
            Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr)); if (!event.value) return false;
            OVERLAPPED operation{}; operation.hEvent = event.value;
            DWORD transferred = 0;
            BOOL completed = write ? WriteFile(pipe, cursor, size, &transferred, &operation) : ReadFile(pipe, cursor, size, &transferred, &operation);
            if (!completed)
            {
                if (GetLastError() != ERROR_IO_PENDING) return false;
                HANDLE events[]{stop.value, event.value};
                const auto wait = WaitForMultipleObjects(2, events, FALSE, 3000);
                if (wait != WAIT_OBJECT_0 + 1)
                {
                    CancelIoEx(pipe, &operation);
                    GetOverlappedResult(pipe, &operation, &transferred, TRUE);
                    return false;
                }
                if (!GetOverlappedResult(pipe, &operation, &transferred, FALSE)) return false;
            }
            if (!transferred || transferred > size) return false;
            cursor += transferred; size -= transferred;
        }
        return true;
    }
    bool SameUser(HANDLE pipe)
    {
        if (!ImpersonateNamedPipeClient(pipe)) return false;
        Handle token;
        BOOL opened = OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &token.value);
        // 模拟状态只用于读取对端身份，后续文件和 COM 操作必须恢复自身身份。
        RevertToSelf();
        if (!opened) return false;
        DWORD required = 0; GetTokenInformation(token.value, TokenUser, nullptr, 0, &required);
        if (!required) return false;
        std::vector<BYTE> remote(required);
        if (!GetTokenInformation(token.value, TokenUser, remote.data(), required, &required)) return false;
        return EqualSid(reinterpret_cast<TOKEN_USER*>(userToken.data())->User.Sid, reinterpret_cast<TOKEN_USER*>(remote.data())->User.Sid) != FALSE;
    }
    void AwaitClientClose(HANDLE pipe)
    {
        // 写入完成只表示回复进入管道缓冲区，立即断开会丢弃客户端尚未读走的回复。
        // 现有协议的客户端读满回复后关闭连接；用关闭作为完成信号，无需新增确认帧。
        // 不使用可能无限等待的 FlushFileBuffers；失联或不关闭的客户端最多占用此监听线程 700 毫秒。
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!event.value) return;
        OVERLAPPED operation{}; operation.hEvent = event.value;
        BYTE unexpected = 0; DWORD received = 0;
        if (ReadFile(pipe, &unexpected, 1, &received, &operation)) return;
        if (GetLastError() != ERROR_IO_PENDING) return;
        HANDLE events[]{stop.value, event.value};
        const auto wait = WaitForMultipleObjects(2, events, FALSE, 700);
        if (wait != WAIT_OBJECT_0 + 1) CancelIoEx(pipe, &operation);
        // 重叠读取取消完成后才释放栈上的缓冲区和 OVERLAPPED，避免后台内核仍访问这些地址。
        GetOverlappedResult(pipe, &operation, &received, TRUE);
    }
    void Process(HANDLE pipe)
    {
        std::array<BYTE, 40> header{};
        if (!Transfer(pipe, header.data(), static_cast<DWORD>(header.size()), false) || !SameUser(pipe)) return;
        const auto length = Read<UINT32>(&header[12]);
        if (Read<UINT32>(&header[0]) != IsayPipeMagic || Read<UINT32>(&header[4]) != IsayPipeVersion || length > IsayMaximumTextLength) return;
        auto request = std::make_shared<Request>();
        if (!request->done.value) return;
        request->command = static_cast<IsayCompositionCommand>(Read<UINT32>(&header[8]));
        request->session = Read<GUID>(&header[16]); request->sequence = Read<UINT64>(&header[32]);
        request->text.resize(length);
        if (length && !Transfer(pipe, request->text.data(), length * sizeof(WCHAR), false)) return;
        if (!WellFormed(request->text)) { request->result.Operation = E_INVALIDARG; request->learning.Operation = E_INVALIDARG; request->keyboard.Operation = E_INVALIDARG; }
        else
        {
            {
                std::lock_guard guard(pendingGate); pending.push_back(request);
            }
            if (!PostMessageW(window, CompositionDispatchMessage, 0, 0))
            {
                request->result.Operation = HRESULT_FROM_WIN32(GetLastError());
                std::lock_guard guard(pendingGate);
                const auto iterator = std::find(pending.begin(), pending.end(), request);
                if (iterator != pending.end()) pending.erase(iterator);
            }
            else
            {
                HANDLE events[]{stop.value, request->done.value};
                const auto started = GetTickCount64();
                DWORD wait = WAIT_TIMEOUT;
                while (GetTickCount64() - started < 2000)
                {
                    wait = WaitForMultipleObjects(2, events, FALSE, 25);
                    if (wait != WAIT_TIMEOUT) break;
                    // 对端超时断开时及时撤销尚在队列中的请求；已经进入编辑的操作不能假装可撤回。
                    if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, nullptr, nullptr)) break;
                }
                if (wait != WAIT_OBJECT_0 + 1)
                {
                    request->abandoned = true;
                    // 请求可能已进入宿主，超时不代表零写入；客户端必须停止自动补发。
                    return;
                }
            }
        }
        std::array<BYTE, 32> response{};
        Write(&response[0], IsayPipeMagic); Write(&response[4], IsayPipeVersion);
        if (request->command >= IsayCompositionCommand::EnableKeyboardLearning && request->command <= IsayCompositionCommand::AcknowledgeKeyboardLearning)
        {
            std::vector<BYTE> body;
            if (request->keyboardCommits.size() > IsayMaximumKeyboardBatch) return;
            for (const auto& commit : request->keyboardCommits)
            {
                if (!commit.Id || commit.Source == GUID_NULL || commit.Reading.empty() || commit.Reading.size() > 128 || commit.Text.empty() || commit.Text.size() > 50 ||
                    !WellFormed(commit.Reading) || !WellFormed(commit.Text)) return;
                const auto offset = body.size(); body.resize(offset + 32 + (commit.Reading.size() + commit.Text.size()) * sizeof(WCHAR));
                Write(&body[offset], commit.Id); Write(&body[offset + 8], commit.Source);
                Write(&body[offset + 24], static_cast<UINT32>(commit.Reading.size())); Write(&body[offset + 28], static_cast<UINT32>(commit.Text.size()));
                memcpy(&body[offset + 32], commit.Reading.data(), commit.Reading.size() * sizeof(WCHAR));
                memcpy(&body[offset + 32 + commit.Reading.size() * sizeof(WCHAR)], commit.Text.data(), commit.Text.size() * sizeof(WCHAR));
            }
            if (body.size() > IsayMaximumKeyboardBody) return;
            Write(&response[8], request->keyboard.Operation); Write(&response[12], request->keyboard.State);
            Write(&response[16], static_cast<UINT32>(request->keyboardCommits.size())); Write(&response[20], static_cast<UINT32>(body.size()));
            Write(&response[24], request->keyboard.AcceptedSequence);
            if (!Transfer(pipe, response.data(), static_cast<DWORD>(response.size()), true)) return;
            if (!body.empty() && !Transfer(pipe, body.data(), static_cast<DWORD>(body.size()), true)) return;
            AwaitClientClose(pipe); return;
        }
        if (request->command >= IsayCompositionCommand::ArmLearning && request->command <= IsayCompositionCommand::CancelLearning)
        {
            Write(&response[8], request->learning.Operation); Write(&response[12], request->learning.State);
            const auto sourceLength = static_cast<UINT32>(request->learningSource.size());
            const auto targetLength = static_cast<UINT32>(request->learningTarget.size());
            Write(&response[16], sourceLength); Write(&response[20], targetLength); Write(&response[24], request->learning.Revision);
            if (sourceLength > IsayMaximumLearningLength || targetLength > IsayMaximumLearningLength) return;
            if (!Transfer(pipe, response.data(), static_cast<DWORD>(response.size()), true)) return;
            if (sourceLength && !Transfer(pipe, request->learningSource.data(), sourceLength * sizeof(WCHAR), true)) return;
            if (targetLength && !Transfer(pipe, request->learningTarget.data(), targetLength * sizeof(WCHAR), true)) return;
            AwaitClientClose(pipe); return;
        }
        Write(&response[8], request->result.Operation); Write(&response[12], request->result.State);
        Write(&response[16], request->result.TextLength); Write(&response[24], request->result.AcceptedSequence);
        if (Transfer(pipe, response.data(), static_cast<DWORD>(response.size()), true)) AwaitClientClose(pipe);
    }
    void Run()
    {
        SECURITY_ATTRIBUTES attributes{sizeof(SECURITY_ATTRIBUTES), security, FALSE};
        Handle pipe(CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 65536, 65536, 0, &attributes));
        if (pipe.value == INVALID_HANDLE_VALUE) { startup = HRESULT_FROM_WIN32(GetLastError()); SetEvent(ready.value); return; }
        startup = S_OK; SetEvent(ready.value);
        while (WaitForSingleObject(stop.value, 0) != WAIT_OBJECT_0)
        {
            Handle connected(CreateEventW(nullptr, TRUE, FALSE, nullptr)); if (!connected.value) return;
            OVERLAPPED operation{}; operation.hEvent = connected.value;
            BOOL success = ConnectNamedPipe(pipe.value, &operation);
            DWORD error = success ? ERROR_SUCCESS : GetLastError();
            if (error == ERROR_IO_PENDING)
            {
                HANDLE events[]{stop.value, connected.value};
                if (WaitForMultipleObjects(2, events, FALSE, INFINITE) != WAIT_OBJECT_0 + 1)
                {
                    CancelIoEx(pipe.value, &operation); DWORD transferred = 0;
                    GetOverlappedResult(pipe.value, &operation, &transferred, TRUE); break;
                }
                DWORD transferred = 0;
                if (!GetOverlappedResult(pipe.value, &operation, &transferred, FALSE)) break;
            }
            else if (error != ERROR_SUCCESS && error != ERROR_PIPE_CONNECTED) break;
            try { Process(pipe.value); }
            catch (...) { /* 单个无效请求不能终止宿主的输入服务。 */ }
            DisconnectNamedPipe(pipe.value);
        }
    }
};

PipeEndpoint::PipeEndpoint() = default;
PipeEndpoint::~PipeEndpoint() = default;
void PipeEndpoint::ScheduleMaintenance(UINT milliseconds)
{
    if (implementation_ && implementation_->window) SetTimer(implementation_->window, 0x5a3, milliseconds, nullptr);
}
HRESULT PipeEndpoint::Start(PipeCommandTarget* control)
{
    if (implementation_) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
    try
    {
        auto candidate = std::make_unique<Implementation>();
        const auto hr = candidate->Initialize(control);
        if (SUCCEEDED(hr)) implementation_ = std::move(candidate);
        return hr;
    }
    catch (...) { return E_OUTOFMEMORY; }
}
void PipeEndpoint::Stop() { implementation_.reset(); }
void PipeEndpoint::Wake()
{
    if (implementation_ && implementation_->window)
        PostMessageW(implementation_->window, CompositionMaintenanceMessage, 0, 0);
}
HWND PipeEndpoint::WindowHandle() const { return implementation_ ? implementation_->window : nullptr; }
