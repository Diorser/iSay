#pragma once
#include "PipeEndpoint.h"
#include <wrl/client.h>
#include <atomic>
#include <deque>

// 仅观察当前上下文中其他输入法实际发布的组合与阅读属性，不采集原始按键。
class KeyboardLearningObserver final : public ITfTextEditSink
{
    std::atomic<ULONG> references_{1};
    Microsoft::WRL::ComPtr<ITfThreadMgr> manager_;
    Microsoft::WRL::ComPtr<ITfContext> context_;
    Microsoft::WRL::ComPtr<ITfSource> source_;
    Microsoft::WRL::ComPtr<ITfRange> pendingRange_;
    Microsoft::WRL::ComPtr<ITfCompositionView> pendingComposition_;
    DWORD ownerThread_ = 0, cookie_ = TF_INVALID_COOKIE;
    bool& commonBusy_;
    bool& deactivating_;
    PipeEndpoint& endpoint_;
    bool reading_ = false, focusLost_ = false;
    GUID observation_{}, pendingOwner_{};
    IsayKeyboardLearningState state_ = IsayKeyboardLearningState::Disabled;
    std::wstring pendingReading_;
    UINT64 nextId_ = 0, lastAck_ = 0;
    std::deque<IsayKeyboardCommit> commits_;
    void ResetPending();
    bool Focused();
    HRESULT CheckSafe(TfEditCookie cookie, ITfRange* range);
    HRESULT Capture(TfEditCookie cookie, ITfEditRecord* record);
    void Reply(const std::shared_ptr<PipeCommand>& request, HRESULT operation, bool includeBatch = false);
public:
    KeyboardLearningObserver(ITfThreadMgr* manager, bool& commonBusy, bool& deactivating, PipeEndpoint& endpoint);
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override;
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
    ULONG STDMETHODCALLTYPE Release() override { const auto count = --references_; if (!count) delete this; return count; }
    HRESULT STDMETHODCALLTYPE OnEndEdit(ITfContext* context, TfEditCookie cookie, ITfEditRecord* record) override;
    void Dispatch(const std::shared_ptr<PipeCommand>& request);
    void LoseFocus();
    void FocusChanged(ITfDocumentMgr* focused);
    void ContextChanged();
    void Shutdown();
};
