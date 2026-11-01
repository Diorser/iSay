#pragma once
#include <windows.h>
#include <msctf.h>
#include <cstdint>
#include <string>
#include <vector>

// 此接口只在同一进程的 TSF 所属线程调用；跨进程桥接必须自行复制正文和校验消息。
inline constexpr GUID CLSID_IsayTextService = {0x429952cd,0xd1b7,0x4769,{0x85,0x8a,0x6d,0x56,0xe4,0xef,0x48,0x8d}};
inline constexpr GUID GUID_IsayDisplayAttribute = {0x49513723,0x4f3c,0x43eb,{0x9d,0x59,0x97,0x58,0x04,0x7e,0x82,0x67}};
inline constexpr GUID GUID_IsayLanguageProfile = {0x856f242d,0x399d,0x42f9,{0xa9,0xbc,0x18,0xbc,0xb8,0xf6,0xc9,0x0e}};
inline constexpr GUID GUID_IsayUnsupportedContext = {0xc9305a1e,0x6c13,0x4f83,{0xb9,0x93,0xc4,0x13,0xf8,0x35,0x41,0x0a}};

enum class IsayCompositionCommand : UINT32 { Begin = 1, Update = 2, Commit = 3, Cancel = 4, Query = 5, ResetUnsupported = 6, RecoverInitial = 7, RefineCommitted = 8, QueryRefinementSupport = 9, ArmLearning = 10, QueryLearning = 11, CancelLearning = 12, EnableKeyboardLearning = 13, QueryKeyboardLearning = 14, DisableKeyboardLearning = 15, AcknowledgeKeyboardLearning = 16, RecoverOwnedRange = 17 };
enum class IsayCompositionState : UINT32 { Idle = 0, Composing = 1, FocusUnavailable = 2, HostTerminated = 3, Committed = 4, Rejected = 5 };
struct IsayCompositionResult
{
    UINT32 Size = sizeof(IsayCompositionResult);
    HRESULT Operation = E_PENDING;
    IsayCompositionState State = IsayCompositionState::Idle;
    UINT32 TextLength = 0;
    UINT64 AcceptedSequence = 0;
};

// 学习观察是独立的只读范围，只有显式启用后才保留；普通交付取消不撤销它。
enum class IsayLearningState : UINT32 { Disabled = 0, Watching = 1, Edited = 2, Completed = 3, Discarded = 4 };
struct IsayLearningResult
{
    HRESULT Operation = E_PENDING;
    IsayLearningState State = IsayLearningState::Disabled;
    UINT64 Revision = 0;
};
inline constexpr UINT32 IsayMaximumLearningLength = 65536;
inline constexpr ULONGLONG IsayLearningLifetimeMilliseconds = 60000;

enum class IsayKeyboardLearningState : UINT32 { Disabled = 0, Watching = 1, FocusUnavailable = 2 };
struct IsayKeyboardCommit { UINT64 Id = 0; GUID Source{}; std::wstring Reading, Text; };
struct IsayKeyboardResult
{
    HRESULT Operation = E_PENDING;
    IsayKeyboardLearningState State = IsayKeyboardLearningState::Disabled;
    UINT64 AcceptedSequence = 0;
};
inline constexpr UINT32 IsayMaximumKeyboardBatch = 16, IsayMaximumKeyboardBody = 16384;

MIDL_INTERFACE("88B87E7D-F149-49B5-AB7E-4E1BA6C5B8D7") IIsayCompositionControl : public IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE Execute(IsayCompositionCommand command, REFGUID session,
        UINT64 sequence, const WCHAR* text, UINT32 textLength, IsayCompositionResult* result) = 0;
};

// 管道帧使用小端字段序列，不直接发送含本机指针的 C++ 对象。
// 请求：magic=0x46545349，version=1，command，正文 UTF-16 字符数，GUID(16)，sequence(8)，UTF-16 正文。
// 回复：magic，version，HRESULT，state，正文字符数，保留字段(0)，acceptedSequence(8)。
// 新命令10–12专用回复：magic，version，HRESULT，learningState，src字符数，dst字符数，revision(8)，随后两段UTF-16。
// 旧命令1–9的回复长度和保留字段不变；新回复仅在Completed时包含非空且不同的两段正文。
inline constexpr UINT32 IsayPipeMagic = 0x46545349;
inline constexpr UINT32 IsayPipeVersion = 1;
inline constexpr UINT32 IsayMaximumTextLength = 1024 * 1024;
