#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <atomic>
#include <string>
#include <vector>

// {7B42A9F1-4D8E-4E2A-B8E1-9E44C3B10101}
inline constexpr CLSID CLSID_RuleOpenRootCommand =
    { 0x7B42A9F1, 0x4D8E, 0x4E2A, { 0xB8, 0xE1, 0x9E, 0x44, 0xC3, 0xB1, 0x01, 0x01 } };

extern std::atomic<long> g_comObjectCount;

void InitializeModuleHandle(HMODULE moduleHandle) noexcept;
std::wstring GetModuleDirectory();
HRESULT CreateRuleOpenClassFactory(REFCLSID rclsid, REFIID riid, void** ppv) noexcept;
