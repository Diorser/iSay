#include "CompositionContract.h"
#include "PipeEndpoint.h"
#include "KeyboardLearningObserver.h"
#include "CompositionSinkProxy.h"
#include <initguid.h>
#include <inputscope.h>
#include <wrl/client.h>
#include <richedit.h>
#include <richole.h>
#include <tom.h>
#include <atomic>
#include <new>
#include <string>
#include <algorithm>
#ifdef ISAY_TSF_RECOVERY_TRACE
#include <cstdio>
#endif
#ifdef ISAY_TSF_REF_TRACE
#include <cstdio>
#include <cstring>
#endif

using Microsoft::WRL::ComPtr;
namespace
{
    std::atomic<long> objectCount{0};
    std::atomic<long> serverLocks{0};
    std::atomic<long> serviceCount{0};
    std::atomic<long> compositionSinkCount{0};
    std::atomic<long> editSessionCount{0};
#ifdef ISAY_TSF_REF_TRACE
    void TraceSink(const char* operation, void* sink, ULONG references)
    {
        void* frames[16]{};
        const auto count = CaptureStackBackTrace(1, ARRAYSIZE(frames), frames, nullptr);
        std::fprintf(stderr, "SINK %s %p refs=%lu", operation, sink, references);
        for (USHORT index = 0; index < count; ++index)
        {
            HMODULE module = nullptr;
            if (!GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCSTR>(frames[index]), &module)) continue;
            char path[MAX_PATH]{}; GetModuleFileNameA(module, path, ARRAYSIZE(path));
            const auto* basename = std::strrchr(path, '\\');
            std::fprintf(stderr, " %s+%llx", basename ? basename + 1 : path,
                reinterpret_cast<UINT_PTR>(frames[index]) - reinterpret_cast<UINT_PTR>(module));
        }
        std::fprintf(stderr, "\n");
    }
#else
    void TraceSink(const char*, void*, ULONG) { }
#endif
    struct Counted
    {
        Counted() { ++objectCount; }
        ~Counted() { --objectCount; }
    };

    TF_DISPLAYATTRIBUTE DefaultAttribute()
    {
        TF_DISPLAYATTRIBUTE value{};
        value.crText.type = TF_CT_NONE;
        value.crBk.type = TF_CT_NONE;
        value.lsStyle = TF_LS_SOLID;
        value.fBoldLine = FALSE;
        value.crLine.type = TF_CT_NONE;
        value.bAttr = TF_ATTR_INPUT;
        return value;
    }

    class AttributeInfo final : public ITfDisplayAttributeInfo, private Counted
    {
        std::atomic<ULONG> refs_{1};
        TF_DISPLAYATTRIBUTE value_ = DefaultAttribute();
    public:
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override
        {
            if (!result) return E_POINTER;
            *result = nullptr;
            if (iid == IID_IUnknown || iid == __uuidof(ITfDisplayAttributeInfo))
                *result = static_cast<ITfDisplayAttributeInfo*>(this);
            if (!*result) return E_NOINTERFACE;
            AddRef(); return S_OK;
        }
        ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
        ULONG STDMETHODCALLTYPE Release() override { auto value = --refs_; if (!value) delete this; return value; }
        HRESULT STDMETHODCALLTYPE GetGUID(GUID* value) override { if (!value) return E_POINTER; *value = GUID_IsayDisplayAttribute; return S_OK; }
        HRESULT STDMETHODCALLTYPE GetDescription(BSTR* value) override
        {
            if (!value) return E_POINTER;
            *value = SysAllocString(L"iSay 正在转写的语音"); return *value ? S_OK : E_OUTOFMEMORY;
        }
        HRESULT STDMETHODCALLTYPE GetAttributeInfo(TF_DISPLAYATTRIBUTE* value) override { if (!value) return E_POINTER; *value = value_; return S_OK; }
        HRESULT STDMETHODCALLTYPE SetAttributeInfo(const TF_DISPLAYATTRIBUTE* value) override { if (!value) return E_POINTER; value_ = *value; return S_OK; }
        HRESULT STDMETHODCALLTYPE Reset() override { value_ = DefaultAttribute(); return S_OK; }
    };

    class AttributeEnumerator final : public IEnumTfDisplayAttributeInfo, private Counted
    {
        std::atomic<ULONG> refs_{1};
        bool consumed_ = false;
    public:
        explicit AttributeEnumerator(bool consumed = false) : consumed_(consumed) {}
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override
        {
            if (!result) return E_POINTER; *result = nullptr;
            if (iid == IID_IUnknown || iid == __uuidof(IEnumTfDisplayAttributeInfo)) *result = static_cast<IEnumTfDisplayAttributeInfo*>(this);
            if (!*result) return E_NOINTERFACE; AddRef(); return S_OK;
        }
        ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
        ULONG STDMETHODCALLTYPE Release() override { auto value = --refs_; if (!value) delete this; return value; }
        HRESULT STDMETHODCALLTYPE Clone(IEnumTfDisplayAttributeInfo** result) override
        {
            if (!result) return E_POINTER; *result = new(std::nothrow) AttributeEnumerator(consumed_); return *result ? S_OK : E_OUTOFMEMORY;
        }
        HRESULT STDMETHODCALLTYPE Next(ULONG count, ITfDisplayAttributeInfo** values, ULONG* fetched) override
        {
            if (!values || (!fetched && count != 1)) return E_POINTER;
            if (fetched) *fetched = 0;
            if (!count) return S_OK;
            values[0] = nullptr;
            if (consumed_) return S_FALSE;
            values[0] = new(std::nothrow) AttributeInfo();
            if (!values[0]) return E_OUTOFMEMORY;
            consumed_ = true; if (fetched) *fetched = 1;
            return count == 1 ? S_OK : S_FALSE;
        }
        HRESULT STDMETHODCALLTYPE Reset() override { consumed_ = false; return S_OK; }
        HRESULT STDMETHODCALLTYPE Skip(ULONG count) override
        {
            if (!count) return S_OK;
            auto available = !consumed_; consumed_ = true; return available && count == 1 ? S_OK : S_FALSE;
        }
    };

    bool SameIdentity(IUnknown* left, IUnknown* right)
    {
        if (!left || !right) return left == right;
        ComPtr<IUnknown> first, second;
        if (FAILED(left->QueryInterface(IID_PPV_ARGS(&first))) || FAILED(right->QueryInterface(IID_PPV_ARGS(&second)))) return false;
        return first.Get() == second.Get();
    }

    HRESULT ReadRange(ITfRange* source, TfEditCookie cookie, std::wstring& text)
    {
        ComPtr<ITfRange> range;
        auto hr = source->Clone(&range); if (FAILED(hr)) return hr;
        text.clear();
        WCHAR buffer[1024];
        for (;;)
        {
            ULONG read = 0;
            hr = range->GetText(cookie, TF_TF_MOVESTART, buffer, ARRAYSIZE(buffer), &read);
            if (FAILED(hr)) return hr;
            if (!read) return S_OK;
            if (text.size() + read > IsayMaximumTextLength) return HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);
            text.append(buffer, read);
        }
    }

    class TextService;
    struct PendingEdit
    {
        std::shared_ptr<PipeCommand> request;
        bool completed = false;
        bool waitingForProxy = false;
    };
    struct FlagGuard
    {
        bool& flag;
        bool previous;
        PipeEndpoint* endpoint;
        const bool* maintenance;
        explicit FlagGuard(bool& target, PipeEndpoint* wake = nullptr, const bool* needed = nullptr)
            : flag(target), previous(target), endpoint(wake), maintenance(needed) { flag = true; }
        ~FlagGuard()
        {
            flag = previous;
            if (endpoint && maintenance && *maintenance) endpoint->Wake();
        }
    };
    class EditSession final : public ITfEditSession, private Counted
    {
        std::atomic<ULONG> refs_{1};
        TextService* owner_;
        IsayCompositionCommand command_;
        std::wstring text_;
        std::shared_ptr<PendingEdit> pending_;
    public:
        EditSession(TextService* owner, IsayCompositionCommand command, const std::wstring& text,
            std::shared_ptr<PendingEdit> pending = nullptr);
        ~EditSession();
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override
        {
            if (!result) return E_POINTER; *result = nullptr;
            if (iid == IID_IUnknown || iid == __uuidof(ITfEditSession)) *result = static_cast<ITfEditSession*>(this);
            if (!*result) return E_NOINTERFACE; AddRef(); return S_OK;
        }
        ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
        ULONG STDMETHODCALLTYPE Release() override { auto value = --refs_; if (!value) delete this; return value; }
        HRESULT STDMETHODCALLTYPE DoEditSession(TfEditCookie cookie) override;
    };

    // 学习初始化只申请原上下文的异步读锁，不与组合正文命令共享序号或写锁。
    class LearningEditSession final : public ITfEditSession, private Counted
    {
        std::atomic<ULONG> refs_{1};
        TextService* owner_;
        std::shared_ptr<PipeCommand> request_;
        UINT64 generation_;
    public:
        LearningEditSession(TextService* owner, std::shared_ptr<PipeCommand> request, UINT64 generation);
        ~LearningEditSession();
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override
        {
            if (!result) return E_POINTER; *result = nullptr;
            if (iid == IID_IUnknown || iid == __uuidof(ITfEditSession)) *result = static_cast<ITfEditSession*>(this);
            if (!*result) return E_NOINTERFACE; AddRef(); return S_OK;
        }
        ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
        ULONG STDMETHODCALLTYPE Release() override { auto value = --refs_; if (!value) delete this; return value; }
        HRESULT STDMETHODCALLTYPE DoEditSession(TfEditCookie cookie) override;
    };

    class TextService final : public ITfTextInputProcessorEx, public ITfThreadMgrEventSink, public ITfTextEditSink, public ITfThreadFocusSink, public ITfKeyTraceEventSink,
        public ITfDisplayAttributeProvider, public IIsayCompositionControl, public PipeCommandTarget, private Counted
    {
        std::atomic<ULONG> refs_{1};
        ComPtr<ITfThreadMgr> threadManager_;
        ComPtr<ITfContext> context_;
        ComPtr<ITfComposition> composition_;
        ComPtr<ITfRange> initialAnchor_;
        ComPtr<ITfRange> continuationAnchor_;
        std::wstring continuationText_;
        ComPtr<ITfRange> committedRange_;
        CompositionSinkProxy sinkProxy_;
        TfClientId clientId_ = TF_CLIENTID_NULL;
        DWORD sinkCookie_ = TF_INVALID_COOKIE;
        DWORD focusSinkCookie_ = TF_INVALID_COOKIE;
        DWORD ownerThread_ = 0;
        TfGuidAtom displayAtom_ = TF_INVALID_GUIDATOM;
        GUID session_{};
        UINT64 sequence_ = 0;
        IsayCompositionCommand lastCommand_ = IsayCompositionCommand::Query;
        std::wstring lastCommandText_;
        std::wstring currentText_;
        IsayCompositionState state_ = IsayCompositionState::Idle;
        bool hasSession_ = false;
        bool rejectionCacheFailed_ = false;
        bool commandRunning_ = false;
        bool deactivationRequested_ = false;
        bool initialEmptyBegin_ = false;
        bool initialRecoveryAttempted_ = false;
        UINT32 previewUpdateCount_ = 0;
        std::shared_ptr<PendingEdit> pendingEdit_;
        PipeEndpoint endpoint_;
        ComPtr<KeyboardLearningObserver> keyboardLearning_;
        ComPtr<ITfContext> learningContext_;
        ComPtr<ITfRange> learningRange_;
        ComPtr<ITfSource> learningSource_;
        ComPtr<ITfSource> learningKeySource_;
        DWORD learningCookie_ = TF_INVALID_COOKIE;
        DWORD learningKeyCookie_ = TF_INVALID_COOKIE;
        GUID learningSession_{};
        UINT64 learningGeneration_ = 0, learningRevision_ = 0;
        ULONGLONG learningStarted_ = 0;
        IsayLearningState learningState_ = IsayLearningState::Disabled;
        std::wstring learningBaseline_, learningTarget_;
        std::shared_ptr<PipeCommand> learningPending_;
        bool learningDetachNeeded_ = false, learningPreedit_ = false;

        void DetachLearning()
        {
            if (learningSource_ && learningCookie_ != TF_INVALID_COOKIE) learningSource_->UnadviseSink(learningCookie_);
            if (learningKeySource_ && learningKeyCookie_ != TF_INVALID_COOKIE) learningKeySource_->UnadviseSink(learningKeyCookie_);
            learningKeyCookie_ = TF_INVALID_COOKIE; learningKeySource_.Reset();
            learningCookie_ = TF_INVALID_COOKIE; learningSource_.Reset(); learningRange_.Reset(); learningContext_.Reset();
            learningDetachNeeded_ = false; learningPreedit_ = false;
        }
        void DropLearning(IsayLearningState state = IsayLearningState::Disabled)
        {
            ++learningGeneration_; ++learningRevision_;
            learningState_ = state; learningBaseline_.clear(); learningTarget_.clear();
            if (learningPending_)
            {
                auto pending = std::move(learningPending_);
                pending->CompleteLearning({HRESULT_FROM_WIN32(ERROR_CANCELLED), state, learningRevision_}, L"", L"");
            }
            DetachLearning();
        }
        void FinishLearning(bool discard = false)
        {
            if (learningState_ != IsayLearningState::Watching && learningState_ != IsayLearningState::Edited) return;
            if (discard || learningPreedit_ || learningTarget_.empty() || learningTarget_ == learningBaseline_)
            {
                learningState_ = IsayLearningState::Discarded; learningBaseline_.clear(); learningTarget_.clear();
            }
            else learningState_ = IsayLearningState::Completed;
            ++learningRevision_;
            // OnEndEdit 中不做同步 IPC、网络或卸载订阅；稍后在私有维护消息中释放 COM 来源。
            learningDetachNeeded_ = true; endpoint_.Wake();
        }
        void ExpireLearning()
        {
            if (learningState_ == IsayLearningState::Completed && GetTickCount64() - learningStarted_ >= IsayLearningLifetimeMilliseconds)
            { DropLearning(IsayLearningState::Discarded); return; }
            if ((learningState_ == IsayLearningState::Watching || learningState_ == IsayLearningState::Edited) &&
                GetTickCount64() - learningStarted_ >= IsayLearningLifetimeMilliseconds) FinishLearning(true);
        }
        void ReplyLearning(const std::shared_ptr<PipeCommand>& request, HRESULT operation = S_OK)
        {
            if (deactivationRequested_) operation = HRESULT_FROM_WIN32(ERROR_CANCELLED);
            const bool complete = operation == S_OK && learningState_ == IsayLearningState::Completed;
            request->CompleteLearning({operation, learningState_, learningRevision_},
                complete ? learningBaseline_ : L"", complete ? learningTarget_ : L"");
        }
        HRESULT ReadLearningRange(TfEditCookie cookie, std::wstring& text)
        {
            ComPtr<ITfRange> range;
            auto hr = learningRange_->Clone(&range); if (FAILED(hr)) return hr;
            text.clear(); WCHAR buffer[512];
            for (;;)
            {
                ULONG read = 0;
                hr = range->GetText(cookie, TF_TF_MOVESTART, buffer, ARRAYSIZE(buffer), &read);
                if (FAILED(hr) || !read) return hr;
                if (text.size() + read > IsayMaximumLearningLength) return HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);
                text.append(buffer, read);
            }
        }
        HRESULT HasLearningComposition(bool& active)
        {
            active = false; ComPtr<ITfContextComposition> owner;
            auto hr = learningContext_.As(&owner); if (FAILED(hr)) return hr;
            ComPtr<IEnumITfCompositionView> entries;
            hr = owner->EnumCompositions(&entries); if (FAILED(hr)) return hr;
            ComPtr<ITfCompositionView> entry; ULONG fetched = 0;
            hr = entries->Next(1, &entry, &fetched); active = fetched != 0; return FAILED(hr) ? hr : S_OK;
        }
        HRESULT RefreshLearning(TfEditCookie cookie, ITfEditRecord* record)
        {
            ExpireLearning();
            if (!learningRange_ || (learningState_ != IsayLearningState::Watching && learningState_ != IsayLearningState::Edited)) return S_OK;
            if (!SameIdentity(learningContext_.Get(), context_.Get())) { FinishLearning(true); return S_OK; }
            auto hr = CheckWritable(cookie, learningRange_.Get());
            if (FAILED(hr)) { FinishLearning(true); return S_OK; }
            bool composition = false;
            hr = HasLearningComposition(composition); if (FAILED(hr)) { FinishLearning(true); return S_OK; }
            if (composition) { learningPreedit_ = true; return S_OK; }
            learningPreedit_ = false;
            if (record)
            {
                ComPtr<IEnumTfRanges> updates;
                hr = record->GetTextAndPropertyUpdates(TF_GTP_INCL_TEXT, nullptr, 0, &updates);
                if (FAILED(hr)) { FinishLearning(true); return S_OK; }
                if (updates)
                {
                    UINT32 examined = 0;
                    for (;;)
                    {
                        ComPtr<ITfRange> changed; ULONG fetched = 0;
                        hr = updates->Next(1, &changed, &fetched);
                        if (FAILED(hr)) { FinishLearning(true); return S_OK; }
                        if (!fetched) break;
                        if (++examined > 128) { FinishLearning(true); return S_OK; }
                        LONG start = 0, end = 0;
                        if (FAILED(changed->CompareStart(cookie, learningRange_.Get(), TF_ANCHOR_START, &start)) ||
                            FAILED(changed->CompareEnd(cookie, learningRange_.Get(), TF_ANCHOR_END, &end)))
                        { FinishLearning(true); return S_OK; }
                        // 跨出语音范围的替换无法可靠归因；不要把邻近文档修改送去学习。
                        if (start < 0 || end > 0) { FinishLearning(true); return S_OK; }
                    }
                }
            }
            std::wstring actual;
            hr = ReadLearningRange(cookie, actual);
            if (FAILED(hr) || actual.empty() || actual.find(L'\0') != std::wstring::npos)
            { FinishLearning(true); return S_OK; }
            if (actual != learningTarget_)
            {
                learningTarget_ = std::move(actual); ++learningRevision_;
                learningState_ = learningTarget_ == learningBaseline_ ? IsayLearningState::Watching : IsayLearningState::Edited;
            }
            TF_SELECTION selected{}; ULONG fetched = 0;
            hr = learningContext_->GetSelection(cookie, TF_DEFAULT_SELECTION, 1, &selected, &fetched);
            ComPtr<ITfRange> selection; selection.Attach(selected.range);
            LONG start = 0, end = 0;
            if (FAILED(hr) || !fetched || !selection ||
                FAILED(selection->CompareStart(cookie, learningRange_.Get(), TF_ANCHOR_START, &start)) ||
                FAILED(selection->CompareEnd(cookie, learningRange_.Get(), TF_ANCHOR_END, &end)))
            { FinishLearning(true); return S_OK; }
            if (start < 0 || end > 0) FinishLearning();
            return S_OK;
        }
        void DispatchLearning(const std::shared_ptr<PipeCommand>& request)
        {
            if (ownerThread_ != GetCurrentThreadId() || !threadManager_ || request->session == GUID_NULL || !request->sequence || request->Cancelled())
            { request->CompleteLearning({E_INVALIDARG, IsayLearningState::Disabled, 0}, L"", L""); return; }
            if (request->command != IsayCompositionCommand::ArmLearning)
            {
                if (!request->text.empty() || request->session != learningSession_)
                { request->CompleteLearning({E_ACCESSDENIED, IsayLearningState::Disabled, 0}, L"", L""); return; }
                // 宿主读锁回调可以泵入同一线程的管道消息；不能在当前栈中清掉正在读取的范围或返回半更新。
                if (commandRunning_) { ReplyLearning(request, TF_E_LOCKED); return; }
                ExpireLearning();
                if (learningContext_ && !IsFocused()) FinishLearning();
                if (request->command == IsayCompositionCommand::CancelLearning) DropLearning();
                ReplyLearning(request, learningPending_ ? E_PENDING : S_OK); return;
            }
            if (request->text.empty() || request->text.size() > IsayMaximumLearningLength || request->text.find(L'\0') != std::wstring::npos)
            { request->CompleteLearning({E_INVALIDARG, IsayLearningState::Disabled, 0}, L"", L""); return; }
            if (request->session == learningSession_ && (learningPending_ || learningState_ != IsayLearningState::Disabled))
            {
                if (request->text != currentText_) request->CompleteLearning({HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH), IsayLearningState::Disabled, 0}, L"", L"");
                else ReplyLearning(request, learningPending_ ? E_PENDING : S_OK);
                return;
            }
            if (!hasSession_ || request->session != session_ || state_ != IsayCompositionState::Committed || composition_ ||
                !committedRange_ || currentText_ != request->text || !IsFocused() || commandRunning_ || pendingEdit_)
            { request->CompleteLearning({TF_E_DISCONNECTED, IsayLearningState::Disabled, 0}, L"", L""); return; }
            DropLearning(); learningSession_ = request->session;
            learningContext_ = context_;
            auto hr = committedRange_->Clone(&learningRange_);
            if (FAILED(hr)) { ReplyLearning(request, hr); return; }
            learningPending_ = request;
            FlagGuard running(commandRunning_, &endpoint_, &deactivationRequested_);
            auto* edit = new(std::nothrow) LearningEditSession(this, request, learningGeneration_);
            if (!edit) { learningPending_.reset(); DropLearning(); ReplyLearning(request, E_OUTOFMEMORY); return; }
            HRESULT operation = E_FAIL;
            hr = learningContext_->RequestEditSession(clientId_, edit, TF_ES_ASYNC | TF_ES_READ, &operation);
            edit->Release();
            if (learningPending_ == request && (FAILED(hr) || operation != TF_S_ASYNC))
            { learningPending_.reset(); DropLearning(); ReplyLearning(request, FAILED(hr) ? hr : operation); }
        }

        HRESULT ContextSupport(ITfContext* context, bool& rejected, ITfCompartment** value = nullptr)
        {
            rejected = false;
            ComPtr<ITfCompartmentMgr> manager;
            auto hr = context->QueryInterface(IID_PPV_ARGS(&manager)); if (FAILED(hr)) return hr;
            ComPtr<ITfCompartment> compartment;
            hr = manager->GetCompartment(GUID_IsayUnsupportedContext, &compartment); if (FAILED(hr)) return hr;
            VARIANT stored{};
            hr = compartment->GetValue(&stored);
            if (SUCCEEDED(hr)) rejected = stored.vt == VT_I4 && stored.lVal != 0;
            VariantClear(&stored);
            if (SUCCEEDED(hr) && value) *value = compartment.Detach();
            return FAILED(hr) ? hr : S_OK;
        }
        HRESULT MarkContext(ITfCompartment* compartment, bool rejected)
        {
            VARIANT value{}; value.vt = VT_I4; value.lVal = rejected ? 1 : 0;
            return compartment->SetValue(clientId_, &value);
        }

        HRESULT CompositionIsActive(bool& active)
        {
            active = false;
            if (!composition_ || !context_) return S_OK;
            ComPtr<ITfContextComposition> owner;
            ComPtr<ITfCompositionView> ours;
            auto hr = context_.As(&owner); if (FAILED(hr)) return hr;
            hr = composition_.As(&ours); if (FAILED(hr)) return hr;
            ComPtr<IEnumITfCompositionView> entries;
            hr = owner->EnumCompositions(&entries); if (FAILED(hr)) return hr;
            for (;;)
            {
                ComPtr<ITfCompositionView> entry; ULONG fetched = 0;
                hr = entries->Next(1, &entry, &fetched);
                if (FAILED(hr)) return hr;
                if (!fetched) return S_OK;
                if (SameIdentity(entry.Get(), ours.Get())) { active = true; return S_OK; }
            }
        }
        HRESULT RefreshCompositionState()
        {
            if (!composition_) return S_OK;
            bool active = false;
            auto hr = CompositionIsActive(active); if (FAILED(hr)) return hr;
            if (active) return S_OK;
            state_ = IsayCompositionState::HostTerminated;
            // 查询只读组合身份，不在布局或结束通知期间隐式索取同步写锁。
            composition_.Reset();
            return S_OK;
        }

        void AcceptEdit(IsayCompositionCommand command, REFGUID session, UINT64 sequence,
            const std::wstring& incoming, HRESULT operation)
        {
            if (command == IsayCompositionCommand::Begin && composition_)
            {
                session_ = session; hasSession_ = true; sequence_ = 0;
            }
            if (operation == S_OK)
            {
                session_ = session; hasSession_ = true; sequence_ = sequence;
                lastCommand_ = command; lastCommandText_ = incoming;
            }
            else if (command == IsayCompositionCommand::Begin && !composition_) context_.Reset();
        }
        void FinishPending(std::shared_ptr<PendingEdit> pending, const IsayCompositionResult& result)
        {
            if (pending->completed) return;
            pending->completed = true;
            if (pendingEdit_ == pending) pendingEdit_.reset();
            auto normalized = result;
            if (!hasSession_ || pending->request->session != session_) normalized.AcceptedSequence = 0;
            pending->request->Complete(normalized);
        }
        HRESULT QueueAsyncEdit(const std::shared_ptr<PendingEdit>& pending)
        {
            const auto& request = pending->request;
            if (pending->completed) return E_PENDING;
            if (request->Cancelled() || !threadManager_)
            {
                if (request->command == IsayCompositionCommand::Begin && !composition_) context_.Reset();
                IsayCompositionResult cancelled;
                Fill(&cancelled, HRESULT_FROM_WIN32(ERROR_CANCELLED), state_); FinishPending(pending, cancelled);
                return E_PENDING;
            }
            EditSession* edit = nullptr;
            HRESULT operation = E_OUTOFMEMORY;
            HRESULT hr = E_OUTOFMEMORY;
            // nothrow 只约束对象分配，不约束构造函数中的正文复制；失败也必须结束挂起请求。
            try { edit = new(std::nothrow) EditSession(this, request->command, request->text, pending); }
            catch (const std::bad_alloc&) { }
            catch (...) { operation = hr = E_FAIL; }
            if (edit)
            {
                operation = E_FAIL;
                hr = context_->RequestEditSession(clientId_, edit, TF_ES_ASYNC | TF_ES_READWRITE, &operation);
                edit->Release();
            }
            if (!pending->completed && (FAILED(hr) || operation != TF_S_ASYNC))
            {
                const auto failure = FAILED(hr) ? hr : (FAILED(operation) ? operation : E_UNEXPECTED);
                AcceptEdit(request->command, request->session, request->sequence, request->text, failure);
                IsayCompositionResult failed;
                Fill(&failed, failure, state_); FinishPending(pending, failed);
            }
            return E_PENDING;
        }

        bool IsFocused()
        {
            ComPtr<ITfDocumentMgr> document;
            ComPtr<ITfContext> top;
            return threadManager_ && SUCCEEDED(threadManager_->GetFocus(&document)) && document &&
                SUCCEEDED(document->GetTop(&top)) && top && SameIdentity(context_.Get(), top.Get());
        }
        HRESULT FocusedContext(ITfContext** result)
        {
            ComPtr<ITfDocumentMgr> document;
            auto hr = threadManager_->GetFocus(&document);
            if (FAILED(hr) || !document) return S_FALSE;
            hr = document->GetTop(result);
            if (FAILED(hr) || !*result) return S_FALSE;
            return S_OK;
        }
        void Fill(IsayCompositionResult* result, HRESULT operation, IsayCompositionState state)
        {
            *result = IsayCompositionResult{};
            result->Operation = operation; result->State = state;
            result->AcceptedSequence = sequence_; result->TextLength = static_cast<UINT32>(currentText_.size());
        }
        HRESULT SetAttribute(TfEditCookie cookie, ITfRange* range, bool clear)
        {
            ComPtr<ITfProperty> property;
            auto hr = context_->GetProperty(GUID_PROP_ATTRIBUTE, &property);
            if (FAILED(hr)) return hr;
            if (clear) return property->Clear(cookie, range);
            VARIANT value{}; value.vt = VT_I4; value.lVal = displayAtom_;
            return property->SetValue(cookie, range, &value);
        }
        HRESULT PositionCaret(TfEditCookie cookie, ITfRange* range)
        {
            ComPtr<ITfRange> caret;
            auto hr = range->Clone(&caret); if (FAILED(hr)) return hr;
            hr = caret->Collapse(cookie, TF_ANCHOR_END); if (FAILED(hr)) return hr;
            TF_SELECTION selection{caret.Get(), {TF_AE_NONE, FALSE}};
            return context_->SetSelection(cookie, 1, &selection);
        }
        bool CanRecoverInitial() const
        {
            return state_ == IsayCompositionState::HostTerminated && !composition_ && initialAnchor_ &&
                initialEmptyBegin_ && !initialRecoveryAttempted_ && previewUpdateCount_ == 1 && !currentText_.empty();
        }
        bool IsOriginalChromiumContext()
        {
            ComPtr<ITfContextView> view;
            HWND window = nullptr; WCHAR name[256]{};
            auto hr = context_->GetActiveView(&view);
            if (FAILED(hr) || !view || FAILED(view->GetWnd(&window)) || !window) return false;
            const auto length = GetClassNameW(window, name, ARRAYSIZE(name));
            DWORD process = 0;
            const auto thread = GetWindowThreadProcessId(window, &process);
            return length && thread == ownerThread_ && process == GetCurrentProcessId() &&
                (!_wcsicmp(name, L"Chrome_RenderWidgetHostHWND") || !_wcsicmp(name, L"Chrome_WidgetWin_1"));
        }
        HRESULT RecoverCollapsedInitialRange(TfEditCookie cookie, ITfRange** result, std::wstring& actual)
        {
            // 仅处理首批锚点被宿主压空的情况；不搜索子串，也不从当前光标插入累计正文。
            if (!IsOriginalChromiumContext() || currentText_ != continuationText_)
                return HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH);
            ComPtr<ITfRange> document, end;
            auto hr = context_->GetStart(cookie, &document); if (FAILED(hr)) return hr;
            hr = context_->GetEnd(cookie, &end); if (FAILED(hr)) return hr;
            hr = document->ShiftEndToRange(cookie, end.Get(), TF_ANCHOR_END); if (FAILED(hr)) return hr;
            std::wstring all;
            hr = ReadRange(document.Get(), cookie, all); if (FAILED(hr)) return hr;
#ifdef ISAY_TSF_RECOVERY_TRACE
            char documentTrace[160]{};
            std::snprintf(documentTrace, sizeof(documentTrace), "ISAY_RANGE initial_document=%zu expected=%zu exact=%d plus_lf=%d", all.size(), currentText_.size(), all == currentText_, all == currentText_ + L"\n\n");
            OutputDebugStringA(documentTrace);
#endif
            // 整个上下文必须只剩已确认的首批文字；两个段落分隔符不纳入可替换范围。
            if (all != currentText_ && all != currentText_ + L"\n\n")
                return HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH);
            if (all.size() != currentText_.size())
            {
                LONG moved = 0;
                hr = document->ShiftEnd(cookie, -2, &moved, nullptr); if (FAILED(hr)) return hr;
                if (moved != -2) return HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH);
            }
            hr = ReadRange(document.Get(), cookie, actual); if (FAILED(hr)) return hr;
            if (actual != currentText_) return HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH);
            hr = CheckWritable(cookie, document.Get()); if (FAILED(hr)) return hr;
            // 后续共用原有的折叠光标末端检查，未满足时不写入任何内容。
            *result = document.Detach();
            return S_OK;
        }
        HRESULT RecoverInitial(const std::wstring& text, TfEditCookie cookie)
        {
            // 调用方还必须证明录音段的键鼠版本未变；本层只验证原上下文的真实活锚点。
            if (!CanRecoverInitial() || text.empty()) return TF_E_DISCONNECTED;
            initialRecoveryAttempted_ = true;
            if (!IsFocused()) return S_FALSE;
            ComPtr<ITfContext> anchoredContext;
            auto hr = initialAnchor_->GetContext(&anchoredContext);
            if (FAILED(hr) || !SameIdentity(context_.Get(), anchoredContext.Get())) return TF_E_DISCONNECTED;
            hr = CheckWritable(cookie, initialAnchor_.Get()); if (FAILED(hr)) return hr;
            std::wstring actual; ComPtr<ITfRange> recoveryRange;
            // 首笔锚点也可能被 Chromium 扩入段落分隔符，沿用相同的原范围核验规则。
            hr = GetRecoverableOwnedRange(initialAnchor_.Get(), cookie, &recoveryRange, actual);
#ifdef ISAY_TSF_RECOVERY_TRACE
            char anchoredTrace[200]{};
            std::snprintf(anchoredTrace, sizeof(anchoredTrace), "ISAY_RANGE initial_anchor=%zu expected=%zu hr=%08lx last=%04x", actual.size(), currentText_.size(), hr, actual.empty() ? 0 : actual.back());
            OutputDebugStringA(anchoredTrace);
#endif
            if (hr == HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH) && (actual.empty() || actual == L"\n\n"))
            {
                recoveryRange.Reset();
                hr = RecoverCollapsedInitialRange(cookie, &recoveryRange, actual);
            }
            if (FAILED(hr)) return hr;
#ifdef ISAY_TSF_RECOVERY_TRACE
            char initialTrace[180]{};
            std::snprintf(initialTrace, sizeof(initialTrace), "ISAY_RANGE initial_actual=%zu current=%zu acknowledged=%zu", actual.size(), currentText_.size(), continuationText_.size());
            OutputDebugStringA(initialTrace);
#endif
            if (actual != currentText_ && (continuationText_.empty() || actual != continuationText_)) return HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH);
            TF_SELECTION selected{}; ULONG fetched = 0;
            hr = context_->GetSelection(cookie, TF_DEFAULT_SELECTION, 1, &selected, &fetched);
            if (FAILED(hr)) return hr;
            ComPtr<ITfRange> selection; selection.Attach(selected.range);
            if (!fetched || !selection || selected.style.fInterimChar) return TF_E_DISCONNECTED;
            BOOL collapsed = FALSE;
            hr = selection->IsEmpty(cookie, &collapsed); if (FAILED(hr)) return hr;
            LONG start = 1, end = 1;
            if (collapsed) hr = selection->CompareStart(cookie, recoveryRange.Get(), TF_ANCHOR_END, &start);
            if (SUCCEEDED(hr) && collapsed) hr = selection->CompareEnd(cookie, recoveryRange.Get(), TF_ANCHOR_END, &end);
#ifdef ISAY_TSF_RECOVERY_TRACE
            std::snprintf(initialTrace, sizeof(initialTrace), "ISAY_RANGE initial_caret_collapsed=%d start=%ld end=%ld hr=%08lx", collapsed, start, end, hr);
            OutputDebugStringA(initialTrace);
#endif
            if (FAILED(hr)) return hr;
            // Chromium 的 DOM 焦点可早于 TSF 选区通知；没有写入时明确返回“尚未就绪”。
            // 调用方仅能在没有键鼠变更的首笔窗口内有界重查，不能放宽原光标条件。
            if (!collapsed) return TF_E_DISCONNECTED;
            if (start != 0 || end != 0)
            {
                initialRecoveryAttempted_ = false;
                return HRESULT_FROM_WIN32(ERROR_NOT_READY);
            }
            ComPtr<ITfContextComposition> owner;
            hr = context_.As(&owner); if (FAILED(hr)) return hr;
            // 只在逐字核验的已确认首批范围重建组合；不能从当前光标插入旧全文或寻找相似正文。
            hr = owner->StartComposition(cookie, recoveryRange.Get(), sinkProxy_.Get(), &composition_);
            if (FAILED(hr) || !composition_) return FAILED(hr) ? hr : TF_E_DISCONNECTED;
            state_ = IsayCompositionState::Composing;
            currentText_ = actual;
            initialAnchor_.Reset();
            const auto operation = Edit(IsayCompositionCommand::Update, text, cookie);
            if (operation != S_OK)
            {
                // 新组合已建立但后续编辑失败时尽力结束它；不能把活组合误报为已终止。
                Edit(IsayCompositionCommand::Cancel, L"", cookie);
                if (!composition_) state_ = IsayCompositionState::HostTerminated;
            }
            return operation;
        }
        bool CanRecoverOwnedRange() const
        {
            return state_ == IsayCompositionState::HostTerminated && !composition_ && continuationAnchor_ &&
                previewUpdateCount_ > 0 && !currentText_.empty();
        }
        HRESULT GetRecoverableOwnedRange(ITfRange* anchor, TfEditCookie cookie, ITfRange** result, std::wstring& actual)
        {
            auto hr = anchor->Clone(result); if (FAILED(hr)) return hr;
            hr = ReadRange(*result, cookie, actual); if (FAILED(hr)) return hr;
            if (actual == currentText_ || (!continuationText_.empty() && actual == continuationText_)) return S_OK;
            // Chromium失焦后可能把段落分隔符纳入活锚点；只缩短已知原范围，不扫描正文或按光标重发。
            if (continuationText_.empty() || actual != continuationText_ + L"\n\n")
                return HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH);
            ComPtr<ITfContextView> view;
            HWND window = nullptr; WCHAR name[256]{};
            hr = context_->GetActiveView(&view); if (FAILED(hr) || !view) return TF_E_DISCONNECTED;
            hr = view->GetWnd(&window); if (FAILED(hr) || !window) return TF_E_DISCONNECTED;
            auto nameLength = GetClassNameW(window, name, ARRAYSIZE(name));
#ifdef ISAY_TSF_RECOVERY_TRACE
            char windowTrace[320]{};
            std::snprintf(windowTrace, sizeof(windowTrace), "ISAY_RANGE window_class=%ls", name);
            OutputDebugStringA(windowTrace);
#endif
            DWORD process = 0;
            auto thread = GetWindowThreadProcessId(window, &process);
            if (!nameLength || thread != ownerThread_ || process != GetCurrentProcessId() ||
                (_wcsicmp(name, L"Chrome_RenderWidgetHostHWND") && _wcsicmp(name, L"Chrome_WidgetWin_1")))
                return HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH);
            LONG moved = 0;
            hr = (*result)->ShiftEnd(cookie, -2, &moved, nullptr); if (FAILED(hr)) return hr;
            if (moved != -2) return HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH);
            hr = ReadRange(*result, cookie, actual); if (FAILED(hr)) return hr;
            // 后续仍要求折叠光标恰在缩短后范围末尾；真实新增换行和光标移动不会被当成原范围。
            return actual == continuationText_ ? S_OK : HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH);
        }
        HRESULT RecoverOwnedRange(const std::wstring& text, TfEditCookie cookie)
        {
            // 调用方必须证明仍为原焦点元素；本层逐字核验原上下文、范围和光标，不从新位置补发。
            if (!CanRecoverOwnedRange() || text.empty()) return TF_E_DISCONNECTED;
            if (!IsFocused()) return S_FALSE;
            ComPtr<ITfContext> anchoredContext;
            auto hr = continuationAnchor_->GetContext(&anchoredContext);
            if (FAILED(hr) || !SameIdentity(context_.Get(), anchoredContext.Get())) return TF_E_DISCONNECTED;
            hr = CheckWritable(cookie, continuationAnchor_.Get()); if (FAILED(hr)) return hr;
            std::wstring actual; ComPtr<ITfRange> recoveryRange;
            hr = GetRecoverableOwnedRange(continuationAnchor_.Get(), cookie, &recoveryRange, actual);
#ifdef ISAY_TSF_RECOVERY_TRACE
            char trace[180]{};
            std::snprintf(trace, sizeof(trace), "ISAY_RANGE actual=%zu current=%zu acknowledged=%zu equal_current=%d equal_acknowledged=%d last=%04x before_last=%04x",
                actual.size(), currentText_.size(), continuationText_.size(), actual == currentText_, actual == continuationText_,
                actual.empty() ? 0 : actual.back(), actual.size() < 2 ? 0 : actual[actual.size()-2]);
            OutputDebugStringA(trace);
#endif
            if (FAILED(hr)) return hr;
            if (actual != currentText_ && (continuationText_.empty() || actual != continuationText_)) return HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH);
            TF_SELECTION selected{}; ULONG fetched = 0;
            hr = context_->GetSelection(cookie, TF_DEFAULT_SELECTION, 1, &selected, &fetched);
            if (FAILED(hr)) return hr;
            ComPtr<ITfRange> selection; selection.Attach(selected.range);
            if (!fetched || !selection || selected.style.fInterimChar) return TF_E_DISCONNECTED;
            BOOL collapsed = FALSE;
            hr = selection->IsEmpty(cookie, &collapsed); if (FAILED(hr)) return hr;
            LONG start = 1, end = 1;
            if (collapsed) hr = selection->CompareStart(cookie, recoveryRange.Get(), TF_ANCHOR_END, &start);
            if (SUCCEEDED(hr) && collapsed) hr = selection->CompareEnd(cookie, recoveryRange.Get(), TF_ANCHOR_END, &end);
            if (FAILED(hr)) return hr;
            if (!collapsed || start != 0 || end != 0) return TF_E_DISCONNECTED;
            ComPtr<ITfContextComposition> owner;
            hr = context_.As(&owner); if (FAILED(hr)) return hr;
            ComPtr<IEnumITfCompositionView> entries;
            hr = owner->EnumCompositions(&entries); if (FAILED(hr)) return hr;
            ComPtr<ITfCompositionView> other; ULONG count = 0;
            hr = entries->Next(1, &other, &count); if (FAILED(hr)) return hr;
            if (count) return TF_E_DISCONNECTED;
            // 只在已保存的原语音范围重建组合；不能从当前光标插入旧全文或寻找相似正文。
            hr = owner->StartComposition(cookie, recoveryRange.Get(), sinkProxy_.Get(), &composition_);
            if (FAILED(hr) || !composition_) return FAILED(hr) ? hr : TF_E_DISCONNECTED;
            state_ = IsayCompositionState::Composing;
            // Chromium可能在SetText后结束组合并撤回未确认的快照；只接受原活范围内两份确切已知正文。
            currentText_ = actual;
            continuationAnchor_.Reset();
            const auto operation = Edit(IsayCompositionCommand::Update, text, cookie);
            if (operation != S_OK)
            {
                // 新组合已建立但后续编辑失败时尽力结束它；不能把活组合误报为已终止。
                Edit(IsayCompositionCommand::Cancel, L"", cookie);
                if (!composition_) state_ = IsayCompositionState::HostTerminated;
            }
            return operation;
        }
        HRESULT RichEditDocument(HWND& window, ITextDocument** document)
        {
            // 必须从原 TSF 上下文取得宿主窗口，不能用前台窗口或当前光标猜测另一个目标。
            window = nullptr;
            *document = nullptr;
            if (!context_) return S_FALSE;
            ComPtr<ITfContextView> view;
            auto hr = context_->GetActiveView(&view); if (FAILED(hr) || !view) return S_FALSE;
            hr = view->GetWnd(&window); if (FAILED(hr) || !window) return S_FALSE;
            DWORD process = 0;
            if (GetWindowThreadProcessId(window, &process) != ownerThread_ || process != GetCurrentProcessId()) return S_FALSE;
            WCHAR name[256]{};
            if (!GetClassNameW(window, name, ARRAYSIZE(name))) return S_FALSE;
            if (_wcsicmp(name, L"RICHEDIT") && _wcsicmp(name, L"MSFTEDIT") &&
                _wcsnicmp(name, L"RICHEDIT20", 10) && _wcsnicmp(name, L"RICHEDIT50", 10) &&
                _wcsnicmp(name, L"RichEditD2D", 11) && _wcsnicmp(name, L"WindowsForms10.RichEdit", 22)) return S_FALSE;
            ComPtr<IRichEditOle> richOle;
            if (!SendMessageW(window, EM_GETOLEINTERFACE, 0, reinterpret_cast<LPARAM>(richOle.GetAddressOf())) || !richOle)
                return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
            return richOle->QueryInterface(IID_PPV_ARGS(document));
        }
        HRESULT RefineRichEdit(const std::wstring& text, ITextDocument* document, HWND window)
        {
            // TOM 的独立编辑集合才是 RichEdit 的宿主撤销单元；TSF SetText 的修正标记会破坏该合同。
            ComPtr<ITfRangeACP> acp;
            auto hr = committedRange_.As(&acp); if (FAILED(hr)) return hr;
            LONG start = 0, count = 0;
            hr = acp->GetExtent(&start, &count); if (FAILED(hr)) return hr;
            if (start < 0 || count < 0 || count != static_cast<LONG>(currentText_.size()) || start > LONG_MAX - count)
                return HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH);
            ComPtr<ITextRange> range;
            hr = document->Range(start, start + count, &range); if (FAILED(hr)) return hr;
            BSTR actual = nullptr;
            hr = range->GetText(&actual);
            const bool unchanged = SUCCEEDED(hr) && actual && SysStringLen(actual) == currentText_.size() &&
                std::equal(currentText_.begin(), currentText_.end(), actual);
            SysFreeString(actual);
            if (FAILED(hr)) return hr;
            if (!unchanged) return HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH);
            BSTR replacement = SysAllocStringLen(text.data(), static_cast<UINT>(text.size()));
            if (!replacement) return E_OUTOFMEMORY;
            SendMessageW(window, EM_STOPGROUPTYPING, 0, 0);
            hr = document->BeginEditCollection();
            if (FAILED(hr)) { SysFreeString(replacement); return hr; }
            const auto written = range->SetText(replacement);
            SysFreeString(replacement);
            const auto ended = document->EndEditCollection();
            // 写入后结束事务失败也消耗原范围，禁止把可能成功的正文重复发送到光标处。
            if (SUCCEEDED(written) && FAILED(ended)) committedRange_.Reset();
            return FAILED(written) ? written : ended;
        }
        HRESULT RefineCommitted(const std::wstring& text, TfEditCookie cookie)
        {
            // 整理只修订已经提交的原活范围，不显式重建持续组合，也不从当前光标插入全文。
            if (state_ != IsayCompositionState::Committed || composition_ || !committedRange_) return TF_E_DISCONNECTED;
            if (!IsFocused()) return S_FALSE;
            ComPtr<ITfContext> anchoredContext;
            auto hr = committedRange_->GetContext(&anchoredContext);
            if (FAILED(hr) || !SameIdentity(context_.Get(), anchoredContext.Get())) return TF_E_DISCONNECTED;
            hr = CheckWritable(cookie, committedRange_.Get()); if (FAILED(hr)) return hr;
            std::wstring actual;
            hr = ReadRange(committedRange_.Get(), cookie, actual); if (FAILED(hr)) return hr;
            if (actual != currentText_) return HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH);
            TF_SELECTION selected{}; ULONG fetched = 0;
            hr = context_->GetSelection(cookie, TF_DEFAULT_SELECTION, 1, &selected, &fetched);
            if (FAILED(hr)) return hr;
            ComPtr<ITfRange> selection; selection.Attach(selected.range);
            if (!fetched || !selection || selected.style.fInterimChar) return TF_E_DISCONNECTED;
            BOOL collapsed = FALSE;
            hr = selection->IsEmpty(cookie, &collapsed); if (FAILED(hr)) return hr;
            LONG start = 1, end = 1;
            if (collapsed) hr = selection->CompareStart(cookie, committedRange_.Get(), TF_ANCHOR_END, &start);
            if (SUCCEEDED(hr) && collapsed) hr = selection->CompareEnd(cookie, committedRange_.Get(), TF_ANCHOR_END, &end);
            if (FAILED(hr)) return hr;
            if (!collapsed || start != 0 || end != 0) return TF_E_DISCONNECTED;
            ComPtr<ITfContextComposition> owner;
            hr = context_.As(&owner); if (FAILED(hr)) return hr;
            ComPtr<IEnumITfCompositionView> entries;
            hr = owner->EnumCompositions(&entries); if (FAILED(hr)) return hr;
            ComPtr<ITfCompositionView> other; ULONG found = 0;
            hr = entries->Next(1, &other, &found); if (FAILED(hr)) return hr;
            if (found) return HRESULT_FROM_WIN32(ERROR_BUSY);
            if (text != currentText_)
            {
                std::wstring confirmed(text);
                HWND window = nullptr; ComPtr<ITextDocument> document;
                const auto richEdit = RichEditDocument(window, &document);
                if (FAILED(richEdit)) return richEdit;
                hr = richEdit == S_OK ? RefineRichEdit(text, document.Get(), window) :
                    committedRange_->SetText(cookie, TF_ST_CORRECTION, text.data(), static_cast<LONG>(text.size()));
                if (FAILED(hr)) return hr;
                currentText_.swap(confirmed);
            }
            if (learningSession_ == session_ && learningRange_ &&
                (learningState_ == IsayLearningState::Watching || learningState_ == IsayLearningState::Edited))
            {
                // 整理是自家已核验的写入，更新基线但绝不把它伪装为用户手改。
                if (text.size() > IsayMaximumLearningLength || FAILED(committedRange_->Clone(&learningRange_))) FinishLearning(true);
                else
                {
                    learningBaseline_ = learningTarget_ = text;
                    learningState_ = IsayLearningState::Watching; learningPreedit_ = false; ++learningRevision_;
                    learningRange_->SetGravity(cookie, TF_GRAVITY_BACKWARD, TF_GRAVITY_FORWARD);
                }
            }
            // 不设置下划线或移动选区；正文修订成功后立即消耗凭据，重复请求由序号缓存确认。
            committedRange_.Reset();
            return S_OK;
        }
        HRESULT CheckWritable(TfEditCookie cookie, ITfRange* range)
        {
            TF_STATUS status{};
            auto hr = context_->GetStatus(&status);
            if (FAILED(hr)) return hr;
            if (status.dwDynamicFlags & TF_SD_READONLY) return E_ACCESSDENIED;
            if (status.dwDynamicFlags & TF_SD_LOADING) return E_PENDING;
            ComPtr<ITfReadOnlyProperty> property;
            // 输入作用域由宿主提供，必须读取应用属性，不能误读成文字服务自己的可写属性。
            if (SUCCEEDED(context_->GetAppProperty(GUID_PROP_INPUTSCOPE, &property)))
            {
                VARIANT value{};
                if (SUCCEEDED(property->GetValue(cookie, range, &value)) && value.vt == VT_UNKNOWN && value.punkVal)
                {
                    ComPtr<ITfInputScope> inputScope;
                    if (SUCCEEDED(value.punkVal->QueryInterface(IID_PPV_ARGS(&inputScope))))
                    {
                        InputScope* scopes = nullptr; UINT count = 0;
                        if (SUCCEEDED(inputScope->GetInputScopes(&scopes, &count)))
                        {
                            bool password = false;
                            for (UINT index = 0; index < count; ++index) password |= scopes[index] == IS_PASSWORD || scopes[index] == IS_NUMERIC_PASSWORD;
                            CoTaskMemFree(scopes);
                            if (password) { VariantClear(&value); return E_ACCESSDENIED; }
                        }
                    }
                }
                VariantClear(&value);
            }
            return S_OK;
        }
    public:
        TextService() { ++serviceCount; }
        ~TextService() { --serviceCount; }
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override
        {
            if (!result) return E_POINTER; *result = nullptr;
            if (iid == IID_IUnknown || iid == __uuidof(ITfTextInputProcessor) || iid == __uuidof(ITfTextInputProcessorEx)) *result = static_cast<ITfTextInputProcessorEx*>(this);
            else if (iid == __uuidof(ITfThreadMgrEventSink)) *result = static_cast<ITfThreadMgrEventSink*>(this);
            else if (iid == __uuidof(ITfTextEditSink)) *result = static_cast<ITfTextEditSink*>(this);
            else if (iid == __uuidof(ITfThreadFocusSink)) *result = static_cast<ITfThreadFocusSink*>(this);
            else if (iid == __uuidof(ITfKeyTraceEventSink)) *result = static_cast<ITfKeyTraceEventSink*>(this);
            else if (iid == __uuidof(ITfDisplayAttributeProvider)) *result = static_cast<ITfDisplayAttributeProvider*>(this);
            else if (iid == __uuidof(IIsayCompositionControl)) *result = static_cast<IIsayCompositionControl*>(this);
            if (!*result) return E_NOINTERFACE; AddRef(); return S_OK;
        }
        ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
        ULONG STDMETHODCALLTYPE Release() override { auto value = --refs_; if (!value) delete this; return value; }
        HRESULT STDMETHODCALLTYPE Activate(ITfThreadMgr* manager, TfClientId id) override { return ActivateEx(manager, id, 0); }
        HRESULT STDMETHODCALLTYPE ActivateEx(ITfThreadMgr* manager, TfClientId id, DWORD) override
        {
            if (!manager || id == TF_CLIENTID_NULL) return E_INVALIDARG;
            if (threadManager_) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
            ownerThread_ = GetCurrentThreadId(); threadManager_ = manager; clientId_ = id;
            ComPtr<ITfCategoryMgr> categories;
            auto hr = CoCreateInstance(CLSID_TF_CategoryMgr, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&categories));
            if (SUCCEEDED(hr)) hr = categories->RegisterGUID(GUID_IsayDisplayAttribute, &displayAtom_);
            ComPtr<ITfSource> source;
            if (SUCCEEDED(hr)) hr = threadManager_.As(&source);
            if (SUCCEEDED(hr)) hr = source->AdviseSink(__uuidof(ITfThreadMgrEventSink), static_cast<ITfThreadMgrEventSink*>(this), &sinkCookie_);
            if (SUCCEEDED(hr)) hr = source->AdviseSink(__uuidof(ITfThreadFocusSink), static_cast<ITfThreadFocusSink*>(this), &focusSinkCookie_);
            if (SUCCEEDED(hr)) hr = endpoint_.Start(this);
            if (SUCCEEDED(hr))
            {
                auto* keyboard = new(std::nothrow) KeyboardLearningObserver(threadManager_.Get(), commandRunning_, deactivationRequested_, endpoint_);
                if (!keyboard) hr = E_OUTOFMEMORY; else keyboardLearning_.Attach(keyboard);
            }
            if (FAILED(hr))
            {
                endpoint_.Stop();
                if (source && sinkCookie_ != TF_INVALID_COOKIE) source->UnadviseSink(sinkCookie_);
                if (source && focusSinkCookie_ != TF_INVALID_COOKIE) source->UnadviseSink(focusSinkCookie_);
                sinkCookie_ = focusSinkCookie_ = TF_INVALID_COOKIE; threadManager_.Reset(); clientId_ = TF_CLIENTID_NULL; ownerThread_ = 0;
            }
            return hr;
        }
        HRESULT STDMETHODCALLTYPE Deactivate() override
        {
            if (!threadManager_) return S_OK;
            if (ownerThread_ != GetCurrentThreadId()) return RPC_E_WRONG_THREAD;
            if (commandRunning_)
            {
                // 文本宿主可在编辑回调中重入停用；不能销毁仍在栈上使用的上下文和端点。
                deactivationRequested_ = true;
                endpoint_.Wake();
                return TF_E_LOCKED;
            }
            if (pendingEdit_)
            {
                IsayCompositionResult cancelled;
                Fill(&cancelled, HRESULT_FROM_WIN32(ERROR_CANCELLED), state_);
                FinishPending(pendingEdit_, cancelled);
            }
            DropLearning(); learningSession_ = GUID_NULL;
            if (keyboardLearning_) { keyboardLearning_->Shutdown(); keyboardLearning_.Reset(); }
            endpoint_.Stop();
            HRESULT completion = S_OK;
            // 停用只提交已经显示的文字，不清空宿主、不追加当前缓存。
            if (composition_ && context_)
            {
                auto* edit = new(std::nothrow) EditSession(this, IsayCompositionCommand::Cancel, L"");
                if (!edit) completion = E_OUTOFMEMORY;
                else
                {
                    HRESULT operation = E_FAIL;
                    auto hr = context_->RequestEditSession(clientId_, edit, TF_ES_SYNC | TF_ES_READWRITE, &operation);
                    edit->Release();
                    if (FAILED(hr) || FAILED(operation)) completion = FAILED(hr) ? hr : operation;
                }
            }
            ComPtr<ITfSource> source;
            if (sinkCookie_ != TF_INVALID_COOKIE && SUCCEEDED(threadManager_.As(&source))) source->UnadviseSink(sinkCookie_);
            if (focusSinkCookie_ != TF_INVALID_COOKIE && (source || SUCCEEDED(threadManager_.As(&source)))) source->UnadviseSink(focusSinkCookie_);
            sinkCookie_ = focusSinkCookie_ = TF_INVALID_COOKIE; context_.Reset(); composition_.Reset(); initialAnchor_.Reset(); continuationAnchor_.Reset(); committedRange_.Reset();
            sinkProxy_.Stop(); threadManager_.Reset();
            clientId_ = TF_CLIENTID_NULL; ownerThread_ = 0; state_ = IsayCompositionState::Idle;
            hasSession_ = false; currentText_.clear(); continuationText_.clear(); lastCommandText_.clear(); sequence_ = 0; rejectionCacheFailed_ = false;
            deactivationRequested_ = false;
            initialEmptyBegin_ = false; initialRecoveryAttempted_ = false; previewUpdateCount_ = 0;
            // 宿主拒绝结束时仍释放自己的资源，正文留给宿主收尾，不能把清理成功冒充提交成功。
            return completion;
        }
        HRESULT STDMETHODCALLTYPE OnInitDocumentMgr(ITfDocumentMgr*) override { return S_OK; }
        HRESULT STDMETHODCALLTYPE OnUninitDocumentMgr(ITfDocumentMgr*) override { return S_OK; }
        HRESULT STDMETHODCALLTYPE OnSetThreadFocus() override { return S_OK; }
        HRESULT STDMETHODCALLTYPE OnKillThreadFocus() override
        {
            if (ownerThread_ != GetCurrentThreadId()) return S_OK;
            if (keyboardLearning_) keyboardLearning_->LoseFocus();
            if (ownerThread_ == GetCurrentThreadId()) FinishLearning();
            return S_OK;
        }
        HRESULT STDMETHODCALLTYPE OnSetFocus(ITfDocumentMgr* focused, ITfDocumentMgr*) override
        {
            if (ownerThread_ != GetCurrentThreadId()) return S_OK;
            if (keyboardLearning_) keyboardLearning_->FocusChanged(focused);
            ComPtr<ITfContext> top;
            if (learningContext_ && (!focused || FAILED(focused->GetTop(&top)) || !SameIdentity(top.Get(), learningContext_.Get()))) FinishLearning();
            return S_OK;
        }
        HRESULT STDMETHODCALLTYPE OnPushContext(ITfContext* context) override
        {
            if (ownerThread_ == GetCurrentThreadId() && keyboardLearning_) keyboardLearning_->ContextChanged();
            if (ownerThread_ == GetCurrentThreadId() && learningContext_ && !SameIdentity(context, learningContext_.Get()) && !IsFocused()) FinishLearning(true);
            return S_OK;
        }
        HRESULT STDMETHODCALLTYPE OnPopContext(ITfContext* context) override
        {
            if (ownerThread_ == GetCurrentThreadId() && keyboardLearning_) keyboardLearning_->ContextChanged();
            if (ownerThread_ == GetCurrentThreadId() && learningContext_ && SameIdentity(context, learningContext_.Get())) FinishLearning(true);
            return S_OK;
        }
        HRESULT STDMETHODCALLTYPE OnEndEdit(ITfContext* context, TfEditCookie cookie, ITfEditRecord* record) override
        {
            if (ownerThread_ != GetCurrentThreadId() || commandRunning_ || !SameIdentity(context, learningContext_.Get())) return S_OK;
            FlagGuard running(commandRunning_, &endpoint_, &deactivationRequested_);
            try { return RefreshLearning(cookie, record); }
            catch (...) { FinishLearning(true); return S_OK; }
        }
        HRESULT STDMETHODCALLTYPE OnKeyTraceUp(WPARAM, LPARAM) override { return S_OK; }
        HRESULT STDMETHODCALLTYPE OnKeyTraceDown(WPARAM key, LPARAM) override
        {
            if (key != VK_RETURN || ownerThread_ != GetCurrentThreadId() || commandRunning_ || learningPending_ ||
                !learningContext_ || !IsFocused() ||
                (learningState_ != IsayLearningState::Watching && learningState_ != IsayLearningState::Edited)) return S_OK;
            FlagGuard running(commandRunning_, &endpoint_, &deactivationRequested_);
            // 跟踪接口不改变输入法前台角色，也不能吞键；发送前仅同步核验已授权的原语音范围。
            auto* edit = new(std::nothrow) LearningEditSession(this, nullptr, learningGeneration_);
            if (!edit) { FinishLearning(true); return S_OK; }
            HRESULT operation = E_FAIL;
            const auto hr = learningContext_->RequestEditSession(clientId_, edit, TF_ES_SYNC | TF_ES_READ, &operation);
            edit->Release();
            if (FAILED(hr) || FAILED(operation)) FinishLearning(true);
            return S_OK;
        }
        HRESULT RunLearningReturnEdit(UINT64 generation, TfEditCookie cookie)
        {
            if (generation != learningGeneration_ || deactivationRequested_ || !learningRange_ || !IsFocused()) return S_OK;
            bool preedit = false;
            if (FAILED(HasLearningComposition(preedit))) { FinishLearning(true); return S_OK; }
            // Return 若用于提交其他输入法候选，不把半成品当手改收尾。
            if (preedit) return S_OK;
            const auto hr = RefreshLearning(cookie, nullptr);
            if (deactivationRequested_) FinishLearning(true);
            else if (SUCCEEDED(hr)) FinishLearning();
            return S_OK;
        }
        HRESULT RunLearningEdit(const std::shared_ptr<PipeCommand>& request, UINT64 generation, TfEditCookie cookie)
        {
            if (!request) return RunLearningReturnEdit(generation, cookie);
            // 已取消或跨代次的异步读锁不能重新装回旧观察器，也不能覆盖新请求的回复。
            if (learningPending_ != request) return HRESULT_FROM_WIN32(ERROR_CANCELLED);
            learningPending_.reset();
            FlagGuard running(commandRunning_, &endpoint_, &deactivationRequested_);
            try
            {
                if (generation != learningGeneration_ || request->Cancelled() || deactivationRequested_ || !threadManager_ ||
                    ownerThread_ != GetCurrentThreadId() || request->session != session_ || !learningRange_ || !IsFocused())
                { DropLearning(); ReplyLearning(request, HRESULT_FROM_WIN32(ERROR_CANCELLED)); return S_OK; }
                auto hr = CheckWritable(cookie, learningRange_.Get());
                std::wstring actual;
                if (SUCCEEDED(hr)) hr = ReadLearningRange(cookie, actual);
                bool composition = false;
                if (SUCCEEDED(hr)) hr = HasLearningComposition(composition);
                if (SUCCEEDED(hr) && (actual != request->text || composition)) hr = HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH);
                if (SUCCEEDED(hr)) hr = learningRange_->SetGravity(cookie, TF_GRAVITY_BACKWARD, TF_GRAVITY_FORWARD);
                if (SUCCEEDED(hr)) hr = learningContext_.As(&learningSource_);
                if (SUCCEEDED(hr)) hr = learningSource_->AdviseSink(__uuidof(ITfTextEditSink), static_cast<ITfTextEditSink*>(this), &learningCookie_);
                if (SUCCEEDED(hr)) hr = threadManager_.As(&learningKeySource_);
                if (SUCCEEDED(hr)) hr = learningKeySource_->AdviseSink(__uuidof(ITfKeyTraceEventSink), static_cast<ITfKeyTraceEventSink*>(this), &learningKeyCookie_);
                // 宿主读取和订阅调用可能重入停用；返回成功前再核验，不能恢复已取消的观察。
                if (SUCCEEDED(hr) && (deactivationRequested_ || generation != learningGeneration_ || request->Cancelled()))
                    hr = HRESULT_FROM_WIN32(ERROR_CANCELLED);
                if (FAILED(hr)) { DropLearning(); ReplyLearning(request, hr); return S_OK; }
                learningBaseline_ = learningTarget_ = std::move(actual);
                learningStarted_ = GetTickCount64(); learningState_ = IsayLearningState::Watching; ++learningRevision_;
                endpoint_.ScheduleMaintenance(static_cast<UINT>(IsayLearningLifetimeMilliseconds));
                ReplyLearning(request); return S_OK;
            }
            catch (...) { DropLearning(); ReplyLearning(request, E_FAIL); return E_FAIL; }
        }
        HRESULT STDMETHODCALLTYPE EnumDisplayAttributeInfo(IEnumTfDisplayAttributeInfo** result) override
        {
            if (!result) return E_POINTER; *result = new(std::nothrow) AttributeEnumerator(); return *result ? S_OK : E_OUTOFMEMORY;
        }
        HRESULT STDMETHODCALLTYPE GetDisplayAttributeInfo(REFGUID guid, ITfDisplayAttributeInfo** result) override
        {
            if (!result) return E_POINTER; *result = nullptr;
            if (guid != GUID_IsayDisplayAttribute) return E_INVALIDARG;
            *result = new(std::nothrow) AttributeInfo(); return *result ? S_OK : E_OUTOFMEMORY;
        }
        HRESULT STDMETHODCALLTYPE Execute(IsayCompositionCommand command, REFGUID session, UINT64 sequence,
            const WCHAR* text, UINT32 length, IsayCompositionResult* result) override
        {
            const auto hr = ExecuteRequest(command, session, sequence, text, length, result, nullptr);
            if (result && result->Size == sizeof(IsayCompositionResult) && command != IsayCompositionCommand::ResetUnsupported &&
                (!hasSession_ || session != session_)) result->AcceptedSequence = 0;
            return hr;
        }
        void DispatchMaintenance() override
        {
            if (commandRunning_) return;
            if (deactivationRequested_) { Deactivate(); return; }
            ExpireLearning();
            if (learningDetachNeeded_) DetachLearning();
            auto pending = pendingEdit_;
            if (!pending || !pending->waitingForProxy) return;
            FlagGuard running(commandRunning_, &endpoint_, &deactivationRequested_);
            const auto hr = sinkProxy_.PollStart();
            if (hr == E_PENDING) return;
            pending->waitingForProxy = false;
            if (FAILED(hr))
            {
                AcceptEdit(pending->request->command, pending->request->session, pending->request->sequence, pending->request->text, hr);
                IsayCompositionResult failure;
                Fill(&failure, hr, state_); FinishPending(pending, failure);
                return;
            }
            QueueAsyncEdit(pending);
        }
        void DispatchPipeCommand(std::shared_ptr<PipeCommand> request) override
        {
            if (request->command >= IsayCompositionCommand::EnableKeyboardLearning && request->command <= IsayCompositionCommand::AcknowledgeKeyboardLearning)
            {
                if (keyboardLearning_) keyboardLearning_->Dispatch(request);
                else request->CompleteKeyboard({TF_E_DISCONNECTED, IsayKeyboardLearningState::Disabled, request->sequence}, {});
                return;
            }
            if (request->command >= IsayCompositionCommand::ArmLearning && request->command <= IsayCompositionCommand::CancelLearning)
            {
                try { DispatchLearning(request); }
                catch (...) { DropLearning(); ReplyLearning(request, E_FAIL); }
                return;
            }
            IsayCompositionResult result;
            const auto hr = ExecuteRequest(request->command, request->session, request->sequence,
                request->text.data(), static_cast<UINT32>(request->text.size()), &result, request);
            if (hr == E_PENDING) return;
            if (FAILED(hr)) result.Operation = hr;
            if (request->command != IsayCompositionCommand::ResetUnsupported && (!hasSession_ || request->session != session_))
                result.AcceptedSequence = 0;
            request->Complete(result);
        }
        HRESULT ExecuteRequest(IsayCompositionCommand command, REFGUID session, UINT64 sequence,
            const WCHAR* text, UINT32 length, IsayCompositionResult* result, const std::shared_ptr<PipeCommand>& request)
        {
            if (!result) return E_POINTER;
            if (result->Size != sizeof(IsayCompositionResult)) return E_INVALIDARG;
            if (ownerThread_ != GetCurrentThreadId()) { Fill(result, RPC_E_WRONG_THREAD, state_); return S_OK; }
            if (commandRunning_)
            {
                // 组合回调、布局更新和宿主消息泵均可能重入；不在已有编辑锁内再次编辑。
                Fill(result, command == IsayCompositionCommand::Query ? E_PENDING : HRESULT_FROM_WIN32(ERROR_BUSY), state_);
                return S_OK;
            }
            FlagGuard running(commandRunning_, &endpoint_, &deactivationRequested_);
            try { return ExecuteCore(command, session, sequence, text, length, result, request); }
            catch (const std::bad_alloc&) { Fill(result, E_OUTOFMEMORY, state_); return S_OK; }
            catch (...) { Fill(result, E_FAIL, state_); return S_OK; }
        }
        HRESULT ExecuteCore(IsayCompositionCommand command, REFGUID session, UINT64 sequence,
            const WCHAR* text, UINT32 length, IsayCompositionResult* result, const std::shared_ptr<PipeCommand>& request)
        {
            if (!result) return E_POINTER;
            if (result->Size != sizeof(IsayCompositionResult)) return E_INVALIDARG;
            Fill(result, E_FAIL, state_);
            if (ownerThread_ != GetCurrentThreadId()) { Fill(result, RPC_E_WRONG_THREAD, state_); return S_OK; }
            if (!threadManager_) { Fill(result, CO_E_NOTINITIALIZED, state_); return S_OK; }
            if (length > IsayMaximumTextLength || (length && !text) || session == GUID_NULL ||
                command < IsayCompositionCommand::Begin || (command > IsayCompositionCommand::QueryRefinementSupport && command != IsayCompositionCommand::RecoverOwnedRange))
            { Fill(result, E_INVALIDARG, state_); return S_OK; }
            if (pendingEdit_)
            {
                // 尚未授锁的正文没有确认成功，查询不能提前推进序号或冒充失败后可安全补发。
                const auto sameSession = pendingEdit_->request->session == session;
                Fill(result, command == IsayCompositionCommand::Query && sameSession ? E_PENDING : HRESULT_FROM_WIN32(ERROR_BUSY), state_);
                return S_OK;
            }
            const auto refresh = RefreshCompositionState();
            if (FAILED(refresh)) { Fill(result, refresh, state_); return S_OK; }
            if (command == IsayCompositionCommand::Query)
            {
                Fill(result, hasSession_ && session != session_ ? E_ACCESSDENIED : S_OK,
                    state_ == IsayCompositionState::Composing && !IsFocused() ? IsayCompositionState::FocusUnavailable : state_);
                return S_OK;
            }
            if (command == IsayCompositionCommand::QueryRefinementSupport)
            {
                // 只读能力探测不推进正文序号，旧组件拒绝该命令时托管仍保持保守保护。
                HWND window = nullptr; ComPtr<ITextDocument> document;
                const auto support = length || !sequence || !hasSession_ || session != session_ ? E_INVALIDARG :
                    RichEditDocument(window, &document);
                Fill(result, support, state_);
                return S_OK;
            }
            if (command == IsayCompositionCommand::ResetUnsupported)
            {
                if (length || !sequence) { Fill(result, E_INVALIDARG, state_); return S_OK; }
                if (composition_) { Fill(result, HRESULT_FROM_WIN32(ERROR_BUSY), state_); return S_OK; }
                ComPtr<ITfContext> focused;
                const auto focusHr = FocusedContext(&focused);
                if (focusHr != S_OK) { Fill(result, focusHr, IsayCompositionState::FocusUnavailable); return S_OK; }
                bool rejected = false; ComPtr<ITfCompartment> compartment;
                auto hr = ContextSupport(focused.Get(), rejected, &compartment);
                if (SUCCEEDED(hr)) hr = MarkContext(compartment.Get(), false);
                if (SUCCEEDED(hr)) rejectionCacheFailed_ = false;
                Fill(result, hr, SUCCEEDED(hr) ? IsayCompositionState::Idle : state_);
                result->AcceptedSequence = sequence;
                return S_OK;
            }
            std::wstring incoming(text ? text : L"", length);
            bool waitingForProxy = false;
            if (!sequence) { Fill(result, E_INVALIDARG, state_); return S_OK; }
            if (hasSession_ && session == session_ && sequence == sequence_ && command == lastCommand_ && incoming == lastCommandText_)
            { Fill(result, state_ == IsayCompositionState::HostTerminated ? TF_E_DISCONNECTED : S_OK, state_); return S_OK; }
            if (hasSession_ && session == session_ && sequence <= sequence_)
            { Fill(result, HRESULT_FROM_WIN32(ERROR_INVALID_DATA), state_); return S_OK; }
            if (command != IsayCompositionCommand::Begin && (!hasSession_ || session != session_))
            { Fill(result, E_ACCESSDENIED, state_); return S_OK; }
            if (command == IsayCompositionCommand::Cancel)
            {
                committedRange_.Reset();
                if (state_ == IsayCompositionState::Committed && !composition_)
                {
                    AcceptEdit(command, session, sequence, incoming, S_OK);
                    Fill(result, S_OK, state_); return S_OK;
                }
            }
            if (command == IsayCompositionCommand::Begin)
            {
                DropLearning(); learningSession_ = GUID_NULL;
                if (composition_) { Fill(result, HRESULT_FROM_WIN32(ERROR_BUSY), state_); return S_OK; }
                if (hasSession_ && session == session_)
                { Fill(result, HRESULT_FROM_WIN32(ERROR_INVALID_STATE), state_); return S_OK; }
                committedRange_.Reset();
                ComPtr<ITfContext> focused;
                auto focusHr = FocusedContext(&focused);
                if (focusHr != S_OK) { Fill(result, focusHr, IsayCompositionState::FocusUnavailable); return S_OK; }
                bool rejected = false;
                const auto supportHr = ContextSupport(focused.Get(), rejected);
                if (FAILED(supportHr)) { Fill(result, supportHr, IsayCompositionState::Rejected); return S_OK; }
                if (rejected || rejectionCacheFailed_)
                { Fill(result, HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED), IsayCompositionState::Rejected); return S_OK; }
                context_ = focused;
                if (request && request->Cancelled())
                { context_.Reset(); Fill(result, HRESULT_FROM_WIN32(ERROR_CANCELLED), state_); return S_OK; }
                const auto sinkHr = request ? sinkProxy_.StartAsync(endpoint_.WindowHandle(), PipeEndpoint::MaintenanceMessage) : sinkProxy_.Start();
                waitingForProxy = request && sinkHr == E_PENDING;
                if (FAILED(sinkHr) && !waitingForProxy) { context_.Reset(); Fill(result, sinkHr, state_); return S_OK; }
            }
            else if (command == IsayCompositionCommand::RecoverInitial)
            {
                if (!CanRecoverInitial() || incoming.empty()) { Fill(result, TF_E_DISCONNECTED, state_); return S_OK; }
            }
            else if (command == IsayCompositionCommand::RecoverOwnedRange)
            {
                if (!CanRecoverOwnedRange() || incoming.empty()) { Fill(result, TF_E_DISCONNECTED, state_); return S_OK; }
            }
            else if (command == IsayCompositionCommand::RefineCommitted)
            {
                if (state_ != IsayCompositionState::Committed || composition_ || !committedRange_)
                { Fill(result, TF_E_DISCONNECTED, state_); return S_OK; }
                if (!IsFocused()) { Fill(result, S_FALSE, IsayCompositionState::FocusUnavailable); return S_OK; }
            }
            else if (state_ == IsayCompositionState::HostTerminated || !composition_)
            { Fill(result, TF_E_DISCONNECTED, state_); return S_OK; }
            else if (command != IsayCompositionCommand::Cancel && !IsFocused())
            { Fill(result, S_FALSE, IsayCompositionState::FocusUnavailable); return S_OK; }
            std::shared_ptr<PendingEdit> pending;
            if (request)
            {
                if (request->Cancelled())
                {
                    if (command == IsayCompositionCommand::Begin && !composition_) context_.Reset();
                    Fill(result, HRESULT_FROM_WIN32(ERROR_CANCELLED), state_); return S_OK;
                }
                pending = std::make_shared<PendingEdit>(); pending->request = request; pending->waitingForProxy = waitingForProxy;
                pendingEdit_ = pending;
                return waitingForProxy ? E_PENDING : QueueAsyncEdit(pending);
            }
            auto* edit = new(std::nothrow) EditSession(this, command, incoming);
            if (!edit) { Fill(result, E_OUTOFMEMORY, state_); return S_OK; }
            HRESULT operation = E_FAIL;
            auto hr = context_->RequestEditSession(clientId_, edit, TF_ES_SYNC | TF_ES_READWRITE, &operation);
            edit->Release();
            if (FAILED(hr)) operation = hr;
            AcceptEdit(command, session, sequence, incoming, operation);
            Fill(result, operation, state_); return S_OK;
        }
        HRESULT RunAsyncEdit(std::shared_ptr<PendingEdit> pending, TfEditCookie cookie)
        {
            try { return RunAsyncEditCore(pending, cookie); }
            catch (const std::bad_alloc&)
            {
                IsayCompositionResult failure;
                Fill(&failure, E_OUTOFMEMORY, state_); FinishPending(pending, failure);
                return E_OUTOFMEMORY;
            }
            catch (...)
            {
                IsayCompositionResult failure;
                Fill(&failure, E_FAIL, state_); FinishPending(pending, failure);
                return E_FAIL;
            }
        }
        HRESULT RunAsyncEditCore(const std::shared_ptr<PendingEdit>& pending, TfEditCookie cookie)
        {
            if (pending->completed) return HRESULT_FROM_WIN32(ERROR_CANCELLED);
            const auto& request = pending->request;
            IsayCompositionResult result;
            HRESULT operation = HRESULT_FROM_WIN32(ERROR_CANCELLED);
            auto reportState = state_;
            if (pendingEdit_ == pending && threadManager_ && ownerThread_ == GetCurrentThreadId() && !request->Cancelled())
            {
                if (commandRunning_) operation = TF_E_LOCKED;
                else
                {
                    FlagGuard running(commandRunning_, &endpoint_, &deactivationRequested_);
                    operation = RefreshCompositionState();
                    if (SUCCEEDED(operation))
                    {
                        if (request->command != IsayCompositionCommand::Cancel && !IsFocused())
                        {
                            operation = S_FALSE; reportState = IsayCompositionState::FocusUnavailable;
                        }
                        else
                        {
                            operation = Edit(request->command, request->text, cookie);
                            reportState = state_;
                        }
                    }
                    AcceptEdit(request->command, request->session, request->sequence, request->text, operation);
                }
            }
            else if (request->command == IsayCompositionCommand::Begin && !composition_) context_.Reset();
            Fill(&result, operation, reportState);
            FinishPending(pending, result);
            return operation;
        }
        HRESULT Edit(IsayCompositionCommand command, const std::wstring& text, TfEditCookie cookie)
        {
            if (command == IsayCompositionCommand::RecoverInitial) return RecoverInitial(text, cookie);
            if (command == IsayCompositionCommand::RecoverOwnedRange) return RecoverOwnedRange(text, cookie);
            if (command == IsayCompositionCommand::RefineCommitted) return RefineCommitted(text, cookie);
            if (command == IsayCompositionCommand::Begin)
            {
                TF_SELECTION selection{}; ULONG fetched = 0;
                auto hr = context_->GetSelection(cookie, TF_DEFAULT_SELECTION, 1, &selection, &fetched);
                if (FAILED(hr)) return hr;
                ComPtr<ITfRange> range; range.Attach(selection.range);
                if (!fetched || !range) return E_FAIL;
                hr = CheckWritable(cookie, range.Get()); if (FAILED(hr)) return hr;
                std::wstring selected;
                hr = ReadRange(range.Get(), cookie, selected); if (FAILED(hr)) return hr;
                BOOL emptySelection = FALSE;
                hr = range->IsEmpty(cookie, &emptySelection); if (FAILED(hr)) return hr;
                initialAnchor_.Reset(); continuationAnchor_.Reset(); continuationText_.clear(); initialRecoveryAttempted_ = false; previewUpdateCount_ = 0;
                initialEmptyBegin_ = text.empty() && emptySelection;
                ComPtr<ITfContextComposition> owner;
                hr = context_.As(&owner); if (FAILED(hr)) return hr;
                bool rejected = false; ComPtr<ITfCompartment> compartment;
                hr = ContextSupport(context_.Get(), rejected, &compartment); if (FAILED(hr)) return hr;
                if (rejected) return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
                // 先确认能记录本上下文状态，避免明确被拒绝后却无法限制自动重试。
                hr = MarkContext(compartment.Get(), false); if (FAILED(hr)) return hr;
                // 所有会话复用一个标准 COM 代理，避免每次被拒绝都留下一个本模块回调。
                hr = owner->StartComposition(cookie, range.Get(), sinkProxy_.Get(), &composition_);
                if (FAILED(hr) || !composition_)
                {
                    if (!composition_ && (hr == S_OK || hr == E_ACCESSDENIED || hr == E_FAIL))
                    {
                        // 系统会把宿主拒绝 HRESULT 合并成 E_FAIL；只保护已获锁且可写后的这一次 Start 失败。
                        rejectionCacheFailed_ = FAILED(MarkContext(compartment.Get(), true));
                        state_ = IsayCompositionState::Rejected;
                        return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
                    }
                    return FAILED(hr) ? hr : E_ACCESSDENIED;
                }
                state_ = IsayCompositionState::Composing; currentText_ = std::move(selected);
                if (!text.empty()) return Edit(IsayCompositionCommand::Update, text, cookie);
                return S_OK;
            }
            if (!composition_) return TF_E_DISCONNECTED;
            ComPtr<ITfRange> range;
            auto hr = composition_->GetRange(&range); if (FAILED(hr)) return hr;
            if (command == IsayCompositionCommand::Query) return SetAttribute(cookie, range.Get(), true);
            bool active = false;
            hr = CompositionIsActive(active); if (FAILED(hr)) return hr;
            if (!active)
            {
                SetAttribute(cookie, range.Get(), true);
                composition_.Reset(); state_ = IsayCompositionState::HostTerminated;
                return TF_E_DISCONNECTED;
            }
            if (command != IsayCompositionCommand::Cancel)
            {
                hr = CheckWritable(cookie, range.Get()); if (FAILED(hr)) return hr;
                std::wstring actual;
                hr = ReadRange(range.Get(), cookie, actual); if (FAILED(hr)) return hr;
                // 宿主或用户改过组合范围时拒绝覆盖；不能靠全文重发来猜测输入位置。
                if (actual != currentText_) return HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH);
                if (text != currentText_)
                {
                    hr = range->SetText(cookie, 0, text.data(), static_cast<LONG>(text.size()));
                    if (FAILED(hr)) return hr;
                    currentText_ = text;
                    hr = composition_->GetRange(&range); if (FAILED(hr)) return hr;
                    hr = SetAttribute(cookie, range.Get(), false); if (FAILED(hr)) return hr;
                    hr = PositionCaret(cookie, range.Get()); if (FAILED(hr)) return hr;
                }
            }
            if (command == IsayCompositionCommand::Commit || command == IsayCompositionCommand::Cancel)
            {
                ComPtr<ITfRange> committed;
                // 克隆只是后台整理的可选凭据，失败不能把已经正常结束的输入误报为失败。
                if (command == IsayCompositionCommand::Commit && FAILED(range->Clone(&committed))) committed.Reset();
                hr = SetAttribute(cookie, range.Get(), true); if (FAILED(hr)) return hr;
                ComPtr<ITfComposition> endingComposition = composition_;
                hr = endingComposition->EndComposition(cookie);
                if (FAILED(hr)) { SetAttribute(cookie, range.Get(), false); return hr; }
                composition_.Reset();
                initialAnchor_.Reset(); continuationAnchor_.Reset(); continuationText_.clear();
                committedRange_ = std::move(committed);
                state_ = IsayCompositionState::Committed;
            }
            else if (command == IsayCompositionCommand::Update && !text.empty())
            {
                ++previewUpdateCount_;
                // 保存刚成功写入的活动范围，宿主结束组合后仍须核对正文，绝不克隆当前光标。
                continuationAnchor_.Reset();
                if (SUCCEEDED(range->Clone(&continuationAnchor_))) continuationText_ = currentText_;
                else continuationText_.clear();
                if (initialEmptyBegin_ && !initialRecoveryAttempted_ && previewUpdateCount_ == 1)
                {
                    // 锚点仅是可选恢复凭据；克隆失败不能改变已经成功写入的命令结果。
                    initialAnchor_.Reset(); range->Clone(&initialAnchor_);
                }
                else initialAnchor_.Reset();
            }
            return S_OK;
        }
    };



    EditSession::EditSession(TextService* owner, IsayCompositionCommand command, const std::wstring& text,
        std::shared_ptr<PendingEdit> pending)
        : owner_(owner), command_(command), text_(text), pending_(std::move(pending)) { ++editSessionCount; owner_->AddRef(); }
    EditSession::~EditSession() { owner_->Release(); --editSessionCount; }
    HRESULT EditSession::DoEditSession(TfEditCookie cookie)
    {
        // C++ 分配异常不能跨 COM 调用边界传播到宿主。
        try { return pending_ ? owner_->RunAsyncEdit(pending_, cookie) : owner_->Edit(command_, text_, cookie); }
        catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
        catch (...) { return E_FAIL; }
    }

    LearningEditSession::LearningEditSession(TextService* owner, std::shared_ptr<PipeCommand> request, UINT64 generation)
        : owner_(owner), request_(std::move(request)), generation_(generation) { ++editSessionCount; owner_->AddRef(); }
    LearningEditSession::~LearningEditSession() { owner_->Release(); --editSessionCount; }
    HRESULT LearningEditSession::DoEditSession(TfEditCookie cookie)
    {
        try { return owner_->RunLearningEdit(request_, generation_, cookie); }
        catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
        catch (...) { return E_FAIL; }
    }

    class Factory final : public IClassFactory, private Counted
    {
        std::atomic<ULONG> refs_{1};
    public:
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override
        {
            if (!result) return E_POINTER; *result = nullptr;
            if (iid == IID_IUnknown || iid == IID_IClassFactory) *result = static_cast<IClassFactory*>(this);
            if (!*result) return E_NOINTERFACE; AddRef(); return S_OK;
        }
        ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
        ULONG STDMETHODCALLTYPE Release() override { auto value = --refs_; if (!value) delete this; return value; }
        HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** result) override
        {
            if (!result) return E_POINTER; *result = nullptr;
            if (outer) return CLASS_E_NOAGGREGATION;
            auto* service = new(std::nothrow) TextService(); if (!service) return E_OUTOFMEMORY;
            auto hr = service->QueryInterface(iid, result); service->Release(); return hr;
        }
        HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { if (lock) ++serverLocks; else if (serverLocks > 0) --serverLocks; return S_OK; }
    };
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** result)
{
    if (!result) return E_POINTER; *result = nullptr;
    if (clsid != CLSID_IsayTextService) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory = new(std::nothrow) Factory(); if (!factory) return E_OUTOFMEMORY;
    auto hr = factory->QueryInterface(iid, result); factory->Release(); return hr;
}
STDAPI DllCanUnloadNow()
{
    return objectCount == 0 && serverLocks == 0 && CompositionSinkProxy::LiveObjectCount() == 0 ? S_OK : S_FALSE;
}
extern "C" __declspec(dllexport) HRESULT __stdcall IsayTsfGetObjectCounts(UINT32* total, UINT32* services, UINT32* sinks, UINT32* sessions)
{
    if (!total || !services || !sinks || !sessions) return E_POINTER;
    *total = objectCount.load() + CompositionSinkProxy::LiveObjectCount(); *services = serviceCount.load();
    *sinks = CompositionSinkProxy::LiveObjectCount(); *sessions = editSessionCount.load();
    return S_OK;
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(instance);
    return TRUE;
}
