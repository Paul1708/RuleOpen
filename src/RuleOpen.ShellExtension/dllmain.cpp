#include "ExplorerCommands.h"
#include "IconHandler.h"

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID) noexcept
{
    if (ul_reason_for_call == DLL_PROCESS_ATTACH)
    {
        InitializeModuleHandle(hModule);
        DisableThreadLibraryCalls(hModule);
    }
    return TRUE;
}

_Check_return_ STDAPI DllGetClassObject(_In_ REFCLSID rclsid, _In_ REFIID riid, _Outptr_ LPVOID* ppv)
{
    if (rclsid == CLSID_RuleOpenIconHandler)
    {
        return CreateRuleOpenIconHandlerClassFactory(rclsid, riid, ppv);
    }

    return CreateRuleOpenClassFactory(rclsid, riid, ppv);
}

__control_entrypoint(DllExport) STDAPI DllCanUnloadNow(void)
{
    return g_comObjectCount.load(std::memory_order_acquire) == 0 ? S_OK : S_FALSE;
}
