#include "KeyboardLearningObserver.h"
#include <inputscope.h>
#include <algorithm>
#include <limits>

using Microsoft::WRL::ComPtr;
namespace
{
    bool SameObject(IUnknown* left, IUnknown* right)
    {
        if (!left || !right) return false;
        ComPtr<IUnknown> a, b; left->QueryInterface(IID_PPV_ARGS(&a)); right->QueryInterface(IID_PPV_ARGS(&b));
        return a && b && a.Get() == b.Get();
    }
    bool ValidText(const std::wstring& value)
    {
        for (size_t index = 0; index < value.size(); ++index)
        {
            const auto character = value[index]; if (!character) return false;
            if (character >= 0xd800 && character <= 0xdbff)
            { if (++index >= value.size() || value[index] < 0xdc00 || value[index] > 0xdfff) return false; }
            else if (character >= 0xdc00 && character <= 0xdfff) return false;
        }
        return true;
    }
    struct BusyGuard
    {
        bool& busy; bool previous; bool& deactivating; PipeEndpoint& endpoint;
        BusyGuard(bool& value, bool& stopping, PipeEndpoint& pipe) : busy(value), previous(value), deactivating(stopping), endpoint(pipe) { busy = true; }
        ~BusyGuard() { busy = previous; if (deactivating) endpoint.Wake(); }
    };
}
KeyboardLearningObserver::KeyboardLearningObserver(ITfThreadMgr* manager, bool& commonBusy, bool& deactivating, PipeEndpoint& endpoint)
    : manager_(manager), ownerThread_(GetCurrentThreadId()), commonBusy_(commonBusy), deactivating_(deactivating), endpoint_(endpoint) { }
HRESULT KeyboardLearningObserver::QueryInterface(REFIID iid, void** result)
{
    if (!result) return E_POINTER; *result = nullptr;
    if (iid != IID_IUnknown && iid != __uuidof(ITfTextEditSink)) return E_NOINTERFACE;
    *result = static_cast<ITfTextEditSink*>(this); AddRef(); return S_OK;
}
void KeyboardLearningObserver::ResetPending()
{
    pendingRange_.Reset(); pendingComposition_.Reset(); pendingReading_.clear(); pendingOwner_ = GUID_NULL;
}
void KeyboardLearningObserver::Shutdown()
{
    ResetPending(); commits_.clear();
    if (source_ && cookie_ != TF_INVALID_COOKIE) source_->UnadviseSink(cookie_);
    cookie_ = TF_INVALID_COOKIE; source_.Reset(); context_.Reset(); state_ = IsayKeyboardLearningState::Disabled;
}
bool KeyboardLearningObserver::Focused()
{
    ComPtr<ITfDocumentMgr> document; ComPtr<ITfContext> context;
    return manager_ && SUCCEEDED(manager_->GetFocus(&document)) && document && SUCCEEDED(document->GetTop(&context)) && SameObject(context_.Get(), context.Get());
}
void KeyboardLearningObserver::LoseFocus()
{
    focusLost_ = true;
    if (state_ == IsayKeyboardLearningState::Watching) state_ = IsayKeyboardLearningState::FocusUnavailable;
    if (!reading_) ResetPending();
}
void KeyboardLearningObserver::FocusChanged(ITfDocumentMgr* focused)
{
    if (!context_) return;
    ComPtr<ITfContext> top;
    if (!focused || FAILED(focused->GetTop(&top)) || !SameObject(context_.Get(), top.Get())) LoseFocus();
}
void KeyboardLearningObserver::ContextChanged()
{
    if (context_ && !Focused()) LoseFocus();
}
HRESULT KeyboardLearningObserver::CheckSafe(TfEditCookie cookie, ITfRange* range)
{
    TF_STATUS status{}; auto hr = context_->GetStatus(&status); if (FAILED(hr)) return hr;
    if (status.dwDynamicFlags & (TF_SD_READONLY | TF_SD_LOADING)) return E_ACCESSDENIED;
    ComPtr<ITfReadOnlyProperty> property;
    hr = context_->GetAppProperty(GUID_PROP_INPUTSCOPE, &property); if (FAILED(hr)) return hr;
    VARIANT value{}; hr = property->GetValue(cookie, range, &value);
    if (SUCCEEDED(hr) && value.vt == VT_UNKNOWN && value.punkVal)
    {
        ComPtr<ITfInputScope> scope; hr = value.punkVal->QueryInterface(IID_PPV_ARGS(&scope));
        if (SUCCEEDED(hr))
        {
            InputScope* values = nullptr; UINT count = 0; hr = scope->GetInputScopes(&values, &count);
            if (SUCCEEDED(hr) && (!values || !count || count > 128)) hr = E_ACCESSDENIED;
            if (SUCCEEDED(hr)) for (UINT index = 0; index < count; ++index)
                if (values[index] == IS_PASSWORD || values[index] == IS_NUMERIC_PASSWORD) hr = E_ACCESSDENIED;
            CoTaskMemFree(values);
        }
    }
    else if (SUCCEEDED(hr)) hr = E_ACCESSDENIED;
    VariantClear(&value); return hr;
}
void KeyboardLearningObserver::Reply(const std::shared_ptr<PipeCommand>& request, HRESULT operation, bool includeBatch)
{
    if (deactivating_) operation = HRESULT_FROM_WIN32(ERROR_CANCELLED);
    std::vector<IsayKeyboardCommit> batch;
    if (operation == S_OK && includeBatch) batch.assign(commits_.begin(), commits_.end());
    request->CompleteKeyboard({operation, state_, request->sequence}, batch);
}
void KeyboardLearningObserver::Dispatch(const std::shared_ptr<PipeCommand>& request)
{
    if (ownerThread_ != GetCurrentThreadId() || request->session == GUID_NULL || !request->sequence || request->Cancelled())
    { Reply(request, E_INVALIDARG); return; }
    if (commonBusy_) { Reply(request, TF_E_LOCKED); return; }
    BusyGuard guard(commonBusy_, deactivating_, endpoint_);
    if (request->command == IsayCompositionCommand::EnableKeyboardLearning)
    {
        if (!request->text.empty()) { Reply(request, E_INVALIDARG); return; }
        if (request->session == observation_ && state_ != IsayKeyboardLearningState::Disabled) { Reply(request, S_OK); return; }
        Shutdown(); observation_ = request->session; focusLost_ = false; nextId_ = lastAck_ = 0;
        ComPtr<ITfDocumentMgr> document;
        auto hr = manager_->GetFocus(&document);
        if (SUCCEEDED(hr) && document) hr = document->GetTop(&context_); else hr = TF_E_DISCONNECTED;
        if (SUCCEEDED(hr) && context_) hr = context_.As(&source_); else hr = TF_E_DISCONNECTED;
        if (SUCCEEDED(hr)) hr = source_->AdviseSink(__uuidof(ITfTextEditSink), static_cast<ITfTextEditSink*>(this), &cookie_);
        if (SUCCEEDED(hr) && (!Focused() || deactivating_ || request->Cancelled())) hr = HRESULT_FROM_WIN32(ERROR_CANCELLED);
        if (FAILED(hr)) Shutdown(); else state_ = IsayKeyboardLearningState::Watching;
        Reply(request, hr); return;
    }
    if (request->session != observation_) { Reply(request, E_ACCESSDENIED); return; }
    if (request->command != IsayCompositionCommand::AcknowledgeKeyboardLearning && !request->text.empty()) { Reply(request, E_INVALIDARG); return; }
    if (context_ && !Focused()) LoseFocus();
    if (request->command == IsayCompositionCommand::DisableKeyboardLearning) { Shutdown(); Reply(request, S_OK); return; }
    if (request->command == IsayCompositionCommand::AcknowledgeKeyboardLearning)
    {
        UINT64 id = 0;
        if (request->text.empty() || request->text.size() > 20) { Reply(request, E_INVALIDARG); return; }
        for (const auto digit : request->text)
        {
            if (digit < L'0' || digit > L'9' || id > (std::numeric_limits<UINT64>::max() - (digit - L'0')) / 10) { Reply(request, E_INVALIDARG); return; }
            id = id * 10 + digit - L'0';
        }
        if (!id || id > nextId_) { Reply(request, E_INVALIDARG); return; }
        if (id > lastAck_) { while (!commits_.empty() && commits_.front().Id <= id) commits_.pop_front(); lastAck_ = id; }
        Reply(request, S_OK); return;
    }
    Reply(request, S_OK, true);
}
HRESULT KeyboardLearningObserver::Capture(TfEditCookie cookie, ITfEditRecord* record)
{
    if (focusLost_ || !Focused() || deactivating_) { LoseFocus(); return S_OK; }
    ComPtr<ITfContextComposition> compositions; auto hr = context_.As(&compositions); if (FAILED(hr)) return hr;
    ComPtr<IEnumITfCompositionView> entries; hr = compositions->EnumCompositions(&entries); if (FAILED(hr)) return hr;
    ComPtr<ITfCompositionView> active, second; ULONG count = 0, extra = 0;
    hr = entries->Next(1, &active, &count); if (FAILED(hr)) return hr;
    if (count) { hr = entries->Next(1, &second, &extra); if (FAILED(hr)) return hr; }
    if (extra) { ResetPending(); return S_OK; }
    if (count)
    {
        if (pendingComposition_ && !SameObject(pendingComposition_.Get(), active.Get())) ResetPending();
        ComPtr<ITfRange> range; hr = active->GetRange(&range); if (FAILED(hr)) return hr;
        hr = CheckSafe(cookie, range.Get()); if (FAILED(hr) || focusLost_ || deactivating_) { ResetPending(); return S_OK; }
        ComPtr<ITfProperty> property; VARIANT value{}; GUID owner{};
        hr = context_->GetProperty(GUID_PROP_TEXTOWNER, &property);
        if (SUCCEEDED(hr)) hr = property->GetValue(cookie, range.Get(), &value);
        if (SUCCEEDED(hr) && value.vt == VT_I4)
        {
            ComPtr<ITfCategoryMgr> categories; hr = CoCreateInstance(CLSID_TF_CategoryMgr, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&categories));
            if (SUCCEEDED(hr)) hr = categories->GetGUID(value.lVal, &owner);
        }
        VariantClear(&value); property.Reset();
        // 阅读属性不能单独充当来源证明，必须与 TSF 实际创建组合的服务一致。
        GUID compositionOwner{};
        if (SUCCEEDED(hr)) hr = active->GetOwnerClsid(&compositionOwner);
        if (FAILED(hr) || owner == GUID_NULL || owner == CLSID_IsayTextService || compositionOwner != owner) { ResetPending(); return S_OK; }
        hr = context_->GetProperty(GUID_PROP_READING, &property);
        if (SUCCEEDED(hr)) hr = property->GetValue(cookie, range.Get(), &value);
        std::wstring reading;
        if (SUCCEEDED(hr) && value.vt == VT_BSTR && value.bstrVal && SysStringLen(value.bstrVal) <= 128) reading.assign(value.bstrVal, SysStringLen(value.bstrVal));
        VariantClear(&value);
        if (FAILED(hr) || reading.empty() || !ValidText(reading) || focusLost_ || deactivating_) { ResetPending(); return S_OK; }
        hr = range->Clone(&pendingRange_); if (FAILED(hr)) return hr;
        pendingComposition_ = active; pendingReading_ = std::move(reading); pendingOwner_ = owner;
        return S_OK;
    }
    if (!pendingRange_ || !pendingComposition_) return S_OK;
    hr = CheckSafe(cookie, pendingRange_.Get()); if (FAILED(hr) || focusLost_ || deactivating_) { ResetPending(); return S_OK; }
    if (record)
    {
        ComPtr<IEnumTfRanges> changes; hr = record->GetTextAndPropertyUpdates(TF_GTP_INCL_TEXT, nullptr, 0, &changes); if (FAILED(hr)) return hr;
        UINT examined = 0;
        for (;;)
        {
            ComPtr<ITfRange> changed; ULONG got = 0; hr = changes->Next(1, &changed, &got); if (FAILED(hr)) return hr; if (!got) break;
            LONG start = 0, end = 0;
            if (++examined > 128 || FAILED(changed->CompareStart(cookie, pendingRange_.Get(), TF_ANCHOR_START, &start)) ||
                FAILED(changed->CompareEnd(cookie, pendingRange_.Get(), TF_ANCHOR_END, &end)) || start < 0 || end > 0) { ResetPending(); return S_OK; }
        }
    }
    ComPtr<ITfRange> view; hr = pendingRange_->Clone(&view); if (FAILED(hr)) return hr;
    WCHAR text[51]; ULONG read = 0; hr = view->GetText(cookie, 0, text, ARRAYSIZE(text), &read);
    if (SUCCEEDED(hr) && read > 0 && read <= 50 && !focusLost_ && !deactivating_ && commits_.size() < IsayMaximumKeyboardBatch)
    {
        std::wstring word(text, read);
        if (ValidText(word)) commits_.push_back({++nextId_, pendingOwner_, pendingReading_, std::move(word)});
    }
    ResetPending(); return S_OK;
}
HRESULT KeyboardLearningObserver::OnEndEdit(ITfContext* context, TfEditCookie cookie, ITfEditRecord* record)
{
    if (ownerThread_ != GetCurrentThreadId() || commonBusy_ || state_ != IsayKeyboardLearningState::Watching || !SameObject(context, context_.Get())) return S_OK;
    BusyGuard guard(commonBusy_, deactivating_, endpoint_); reading_ = true;
    try { const auto hr = Capture(cookie, record); reading_ = false; if (FAILED(hr) || focusLost_ || deactivating_) ResetPending(); return S_OK; }
    catch (...) { reading_ = false; ResetPending(); return S_OK; }
}
