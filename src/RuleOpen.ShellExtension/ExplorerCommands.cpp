#include "ExplorerCommands.h"
#include <shlobj_core.h>
#include <shldisp.h>
#include <exdisp.h>
#include <shellapi.h>
#ifdef ShellExecute
#undef ShellExecute
#endif
#include <new>
#include <utility>

namespace
{
    HMODULE g_moduleHandle = nullptr;

    HRESULT AllocCoString(const std::wstring& value, PWSTR* outStr) noexcept
    {
        if (!outStr)
        {
            return E_POINTER;
        }
        return SHStrDupW(value.c_str(), outStr);
    }

    std::wstring QuoteArg(const std::wstring& arg)
    {
        std::wstring quoted = L"\"";
        size_t pendingBackslashes = 0;

        for (wchar_t ch : arg)
        {
            if (ch == L'\\')
            {
                ++pendingBackslashes;
            }
            else if (ch == L'"')
            {
                quoted.append(pendingBackslashes * 2 + 1, L'\\');
                quoted += L'"';
                pendingBackslashes = 0;
            }
            else
            {
                if (pendingBackslashes > 0)
                {
                    quoted.append(pendingBackslashes, L'\\');
                    pendingBackslashes = 0;
                }
                quoted += ch;
            }
        }

        if (pendingBackslashes > 0)
        {
            quoted.append(pendingBackslashes * 2, L'\\');
        }

        quoted += L'"';
        return quoted;
    }

    bool LaunchViaExplorerShellDispatch(
        const std::wstring& exePath,
        const std::wstring& argsString,
        const std::wstring& workingDir)
    {
        IShellWindows* shellWindows = nullptr;
        HRESULT hr = CoCreateInstance(
            CLSID_ShellWindows,
            nullptr,
            CLSCTX_LOCAL_SERVER,
            IID_PPV_ARGS(&shellWindows));
        if (FAILED(hr) || !shellWindows)
        {
            return false;
        }

        bool launched = false;
        VARIANT empty{};
        VariantInit(&empty);
        long desktopHwnd = 0;
        IDispatch* desktopDisp = nullptr;
        hr = shellWindows->FindWindowSW(
            &empty,
            &empty,
            SWC_DESKTOP,
            &desktopHwnd,
            SWFO_NEEDDISPATCH,
            &desktopDisp);

        if (SUCCEEDED(hr) && desktopDisp)
        {
            IServiceProvider* sp = nullptr;
            if (SUCCEEDED(desktopDisp->QueryInterface(IID_PPV_ARGS(&sp))) && sp)
            {
                IShellBrowser* browser = nullptr;
                if (SUCCEEDED(sp->QueryService(SID_STopLevelBrowser, IID_PPV_ARGS(&browser))) && browser)
                {
                    IShellView* view = nullptr;
                    if (SUCCEEDED(browser->QueryActiveShellView(&view)) && view)
                    {
                        IDispatch* bgDisp = nullptr;
                        if (SUCCEEDED(view->GetItemObject(SVGIO_BACKGROUND, IID_PPV_ARGS(&bgDisp))) && bgDisp)
                        {
                            IShellFolderViewDual* folderView = nullptr;
                            if (SUCCEEDED(bgDisp->QueryInterface(IID_PPV_ARGS(&folderView))) && folderView)
                            {
                                IDispatch* appDisp = nullptr;
                                if (SUCCEEDED(folderView->get_Application(&appDisp)) && appDisp)
                                {
                                    IShellDispatch2* shellDispatch = nullptr;
                                    if (SUCCEEDED(appDisp->QueryInterface(IID_PPV_ARGS(&shellDispatch))) && shellDispatch)
                                    {
                                        BSTR bstrFile = SysAllocString(exePath.c_str());

                                        VARIANT varArgs{};
                                        VariantInit(&varArgs);
                                        varArgs.vt = VT_BSTR;
                                        varArgs.bstrVal = SysAllocString(argsString.c_str());

                                        VARIANT varDir{};
                                        VariantInit(&varDir);
                                        varDir.vt = VT_BSTR;
                                        varDir.bstrVal = SysAllocString(workingDir.c_str());

                                        VARIANT varVerb{};
                                        VariantInit(&varVerb);
                                        varVerb.vt = VT_BSTR;
                                        varVerb.bstrVal = SysAllocString(L"open");

                                        VARIANT varShow{};
                                        VariantInit(&varShow);
                                        varShow.vt = VT_I4;
                                        varShow.lVal = SW_SHOWNORMAL;

                                        if (bstrFile)
                                        {
                                            hr = shellDispatch->ShellExecute(bstrFile, varArgs, varDir, varVerb, varShow);
                                            launched = SUCCEEDED(hr);
                                        }

                                        SysFreeString(bstrFile);
                                        VariantClear(&varArgs);
                                        VariantClear(&varDir);
                                        VariantClear(&varVerb);
                                        VariantClear(&varShow);
                                        shellDispatch->Release();
                                    }
                                    appDisp->Release();
                                }
                                folderView->Release();
                            }
                            bgDisp->Release();
                        }
                        view->Release();
                    }
                    browser->Release();
                }
                sp->Release();
            }
            desktopDisp->Release();
        }

        shellWindows->Release();
        return launched;
    }

    bool LaunchProcessWithArgs(const std::wstring& exePath, const std::vector<std::wstring>& args)
    {
        if (GetFileAttributesW(exePath.c_str()) == INVALID_FILE_ATTRIBUTES)
        {
            return false;
        }

        std::wstring argsString;
        for (size_t i = 0; i < args.size(); ++i)
        {
            if (i > 0)
            {
                argsString += L' ';
            }
            argsString += QuoteArg(args[i]);
        }

        std::wstring workingDir = GetModuleDirectory();

        // Delegate execution through the user's unpackaged Explorer desktop shell (IShellDispatch2)
        // so RuleOpen.Desktop.exe (unpackaged WinUI 3) and RuleOpen.Dispatcher.exe do NOT inherit
        // the RuleOpen.ShellExtension Sparse Package identity from our COM surrogate (dllhost.exe).
        if (LaunchViaExplorerShellDispatch(exePath, argsString, workingDir))
        {
            return true;
        }

        // Fallback if desktop shell automation is unavailable.
        SHELLEXECUTEINFOW sei{};
        sei.cbSize = sizeof(sei);
        sei.fMask = SEE_MASK_FLAG_NO_UI;
        sei.lpVerb = L"open";
        sei.lpFile = exePath.c_str();
        sei.lpParameters = argsString.empty() ? nullptr : argsString.c_str();
        sei.lpDirectory = workingDir.empty() ? nullptr : workingDir.c_str();
        sei.nShow = SW_SHOWNORMAL;
        return ShellExecuteExW(&sei) != FALSE;
    }

    std::wstring ResolveBackgroundFolderFromSite(IUnknown* site)
    {
        if (!site)
        {
            return {};
        }

        IServiceProvider* sp = nullptr;
        if (FAILED(site->QueryInterface(IID_PPV_ARGS(&sp))) || !sp)
        {
            return {};
        }

        std::wstring result;
        IShellBrowser* browser = nullptr;
        if (SUCCEEDED(sp->QueryService(SID_STopLevelBrowser, IID_PPV_ARGS(&browser))) && browser)
        {
            IShellView* view = nullptr;
            if (SUCCEEDED(browser->QueryActiveShellView(&view)) && view)
            {
                IFolderView2* folderView = nullptr;
                if (SUCCEEDED(view->QueryInterface(IID_PPV_ARGS(&folderView))) && folderView)
                {
                    IPersistFolder2* persistFolder = nullptr;
                    if (SUCCEEDED(folderView->GetFolder(IID_PPV_ARGS(&persistFolder))) && persistFolder)
                    {
                        PIDLIST_ABSOLUTE pidl = nullptr;
                        if (SUCCEEDED(persistFolder->GetCurFolder(&pidl)) && pidl)
                        {
                            wchar_t buffer[MAX_PATH * 4]{};
                            if (SHGetPathFromIDListW(pidl, buffer))
                            {
                                result = buffer;
                            }
                            CoTaskMemFree(pidl);
                        }
                        persistFolder->Release();
                    }
                    folderView->Release();
                }
                view->Release();
            }
            browser->Release();
        }

        sp->Release();
        return result;
    }

    struct SelectionInfo
    {
        bool hasSelection = false;
        bool firstIsFolder = false;
        std::vector<std::wstring> filePaths;
    };

    SelectionInfo InspectSelection(IShellItemArray* psiItemArray)
    {
        SelectionInfo info;
        if (!psiItemArray)
        {
            return info;
        }

        DWORD count = 0;
        if (FAILED(psiItemArray->GetCount(&count)) || count == 0)
        {
            return info;
        }

        info.hasSelection = true;
        for (DWORD i = 0; i < count; ++i)
        {
            IShellItem* item = nullptr;
            if (SUCCEEDED(psiItemArray->GetItemAt(i, &item)) && item)
            {
                if (i == 0)
                {
                    SFGAOF attrs = 0;
                    if (SUCCEEDED(item->GetAttributes(SFGAO_FOLDER | SFGAO_STREAM, &attrs)))
                    {
                        // Treated as a folder when SFGAO_FOLDER is set and it is not a stream archive (.zip).
                        info.firstIsFolder = ((attrs & SFGAO_FOLDER) != 0) && ((attrs & SFGAO_STREAM) == 0);
                    }
                }

                PWSTR path = nullptr;
                if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path)
                {
                    info.filePaths.emplace_back(path);
                    CoTaskMemFree(path);
                }

                item->Release();
            }
        }

        return info;
    }

    enum class RuleOpenAction
    {
        CreateRule,
        OpenUsingRuleOpen
    };

    class RuleOpenSubCommand final : public IExplorerCommand, public IObjectWithSite
    {
    public:
        explicit RuleOpenSubCommand(RuleOpenAction action, IUnknown* site) noexcept
            : m_refCount(1), m_action(action), m_site(site)
        {
            if (m_site)
            {
                m_site->AddRef();
            }
            g_comObjectCount.fetch_add(1, std::memory_order_relaxed);
        }

        ~RuleOpenSubCommand()
        {
            if (m_site)
            {
                m_site->Release();
                m_site = nullptr;
            }
            g_comObjectCount.fetch_sub(1, std::memory_order_relaxed);
        }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) noexcept override
        {
            if (!ppv)
            {
                return E_POINTER;
            }
            if (riid == __uuidof(IUnknown) || riid == __uuidof(IExplorerCommand))
            {
                *ppv = static_cast<IExplorerCommand*>(this);
            }
            else if (riid == __uuidof(IObjectWithSite))
            {
                *ppv = static_cast<IObjectWithSite*>(this);
            }
            else
            {
                *ppv = nullptr;
                return E_NOINTERFACE;
            }
            AddRef();
            return S_OK;
        }

        IFACEMETHODIMP_(ULONG) AddRef() noexcept override
        {
            return static_cast<ULONG>(m_refCount.fetch_add(1, std::memory_order_relaxed) + 1);
        }

        IFACEMETHODIMP_(ULONG) Release() noexcept override
        {
            long count = m_refCount.fetch_sub(1, std::memory_order_acq_rel) - 1;
            if (count == 0)
            {
                delete this;
            }
            return static_cast<ULONG>(count);
        }

        IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* ppszName) noexcept override
        {
            return AllocCoString(
                m_action == RuleOpenAction::CreateRule ? L"Create opening rule" : L"Open using RuleOpen",
                ppszName);
        }

        IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* ppszIcon) noexcept override
        {
            std::wstring dir = GetModuleDirectory();
            std::wstring ico = dir + L"\\Assets\\RuleOpen.ico";
            if (GetFileAttributesW(ico.c_str()) != INVALID_FILE_ATTRIBUTES)
            {
                return AllocCoString(ico, ppszIcon);
            }
            std::wstring exe = dir + L"\\RuleOpen.Desktop.exe,-1";
            return AllocCoString(exe, ppszIcon);
        }

        IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* ppszInfotip) noexcept override
        {
            return AllocCoString(
                m_action == RuleOpenAction::CreateRule
                    ? L"Create a RuleOpen folder and file-type rule for this selection"
                    : L"Open the selected file(s) using RuleOpen rules",
                ppszInfotip);
        }

        IFACEMETHODIMP GetCanonicalName(GUID* pguidCommandName) noexcept override
        {
            if (!pguidCommandName)
            {
                return E_POINTER;
            }
            *pguidCommandName = m_action == RuleOpenAction::CreateRule
                ? GUID{ 0x7B42A9F1, 0x4D8E, 0x4E2A, { 0xB8, 0xE1, 0x9E, 0x44, 0xC3, 0xB1, 0x01, 0x02 } }
                : GUID{ 0x7B42A9F1, 0x4D8E, 0x4E2A, { 0xB8, 0xE1, 0x9E, 0x44, 0xC3, 0xB1, 0x01, 0x03 } };
            return S_OK;
        }

        IFACEMETHODIMP GetState(IShellItemArray* psiItemArray, BOOL, EXPCMDSTATE* pCmdState) noexcept override
        {
            if (!pCmdState)
            {
                return E_POINTER;
            }

            if (m_action == RuleOpenAction::OpenUsingRuleOpen)
            {
                SelectionInfo selection = InspectSelection(psiItemArray);
                if (!selection.hasSelection || selection.firstIsFolder || selection.filePaths.empty())
                {
                    *pCmdState = ECS_HIDDEN;
                    return S_OK;
                }
            }

            *pCmdState = ECS_ENABLED;
            return S_OK;
        }

        IFACEMETHODIMP Invoke(IShellItemArray* psiItemArray, IBindCtx*) noexcept override
        {
            std::wstring moduleDir = GetModuleDirectory();
            SelectionInfo selection = InspectSelection(psiItemArray);

            if (m_action == RuleOpenAction::CreateRule)
            {
                std::wstring desktopExe = moduleDir + L"\\RuleOpen.Desktop.exe";
                if (selection.hasSelection && !selection.filePaths.empty())
                {
                    const wchar_t* flag = selection.firstIsFolder ? L"--create-rule-folder" : L"--create-rule";
                    LaunchProcessWithArgs(desktopExe, { flag, selection.filePaths.front() });
                    return S_OK;
                }

                std::wstring bgFolder = ResolveBackgroundFolderFromSite(m_site);
                if (!bgFolder.empty())
                {
                    LaunchProcessWithArgs(desktopExe, { L"--create-rule-folder", bgFolder });
                    return S_OK;
                }

                LaunchProcessWithArgs(desktopExe, {});
                return S_OK;
            }

            if (!selection.filePaths.empty() && !selection.firstIsFolder)
            {
                std::wstring dispatcherExe = moduleDir + L"\\RuleOpen.Dispatcher.exe";
                std::vector<std::wstring> args;
                args.reserve(selection.filePaths.size() + 1);
                args.emplace_back(L"--");
                for (const auto& p : selection.filePaths)
                {
                    DWORD attrs = GetFileAttributesW(p.c_str());
                    if (attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY) == 0)
                    {
                        args.push_back(p);
                    }
                }
                if (args.size() > 1)
                {
                    LaunchProcessWithArgs(dispatcherExe, args);
                }
            }

            return S_OK;
        }

        IFACEMETHODIMP GetFlags(EXPCMDFLAGS* pFlags) noexcept override
        {
            if (!pFlags)
            {
                return E_POINTER;
            }
            *pFlags = ECF_DEFAULT;
            return S_OK;
        }

        IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** ppEnum) noexcept override
        {
            if (ppEnum)
            {
                *ppEnum = nullptr;
            }
            return E_NOTIMPL;
        }

        IFACEMETHODIMP SetSite(IUnknown* pUnkSite) noexcept override
        {
            if (m_site)
            {
                m_site->Release();
            }
            m_site = pUnkSite;
            if (m_site)
            {
                m_site->AddRef();
            }
            return S_OK;
        }

        IFACEMETHODIMP GetSite(REFIID riid, void** ppvSite) noexcept override
        {
            if (!ppvSite)
            {
                return E_POINTER;
            }
            if (!m_site)
            {
                *ppvSite = nullptr;
                return E_FAIL;
            }
            return m_site->QueryInterface(riid, ppvSite);
        }

    private:
        std::atomic<long> m_refCount;
        RuleOpenAction m_action;
        IUnknown* m_site;
    };

    class RuleOpenSubCommandEnum final : public IEnumExplorerCommand
    {
    public:
        explicit RuleOpenSubCommandEnum(std::vector<IExplorerCommand*> commands) noexcept
            : m_refCount(1), m_commands(std::move(commands)), m_index(0)
        {
            g_comObjectCount.fetch_add(1, std::memory_order_relaxed);
        }

        ~RuleOpenSubCommandEnum()
        {
            for (IExplorerCommand* cmd : m_commands)
            {
                if (cmd)
                {
                    cmd->Release();
                }
            }
            g_comObjectCount.fetch_sub(1, std::memory_order_relaxed);
        }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) noexcept override
        {
            if (!ppv)
            {
                return E_POINTER;
            }
            if (riid == __uuidof(IUnknown) || riid == __uuidof(IEnumExplorerCommand))
            {
                *ppv = static_cast<IEnumExplorerCommand*>(this);
                AddRef();
                return S_OK;
            }
            *ppv = nullptr;
            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() noexcept override
        {
            return static_cast<ULONG>(m_refCount.fetch_add(1, std::memory_order_relaxed) + 1);
        }

        IFACEMETHODIMP_(ULONG) Release() noexcept override
        {
            long count = m_refCount.fetch_sub(1, std::memory_order_acq_rel) - 1;
            if (count == 0)
            {
                delete this;
            }
            return static_cast<ULONG>(count);
        }

        IFACEMETHODIMP Next(ULONG celt, IExplorerCommand** pUICommand, ULONG* pceltFetched) noexcept override
        {
            if (!pUICommand && celt > 0)
            {
                return E_POINTER;
            }

            ULONG fetched = 0;
            while (fetched < celt && m_index < m_commands.size())
            {
                pUICommand[fetched] = m_commands[m_index];
                pUICommand[fetched]->AddRef();
                ++fetched;
                ++m_index;
            }

            if (pceltFetched)
            {
                *pceltFetched = fetched;
            }

            return fetched == celt ? S_OK : S_FALSE;
        }

        IFACEMETHODIMP Skip(ULONG celt) noexcept override
        {
            m_index = (std::min)(m_commands.size(), m_index + static_cast<size_t>(celt));
            return m_index < m_commands.size() ? S_OK : S_FALSE;
        }

        IFACEMETHODIMP Reset() noexcept override
        {
            m_index = 0;
            return S_OK;
        }

        IFACEMETHODIMP Clone(IEnumExplorerCommand** ppEnum) noexcept override
        {
            if (!ppEnum)
            {
                return E_POINTER;
            }

            std::vector<IExplorerCommand*> cloned;
            cloned.reserve(m_commands.size());
            for (IExplorerCommand* cmd : m_commands)
            {
                cmd->AddRef();
                cloned.push_back(cmd);
            }

            auto* copy = new (std::nothrow) RuleOpenSubCommandEnum(std::move(cloned));
            if (!copy)
            {
                for (IExplorerCommand* cmd : cloned)
                {
                    cmd->Release();
                }
                *ppEnum = nullptr;
                return E_OUTOFMEMORY;
            }

            copy->m_index = m_index;
            *ppEnum = copy;
            return S_OK;
        }

    private:
        std::atomic<long> m_refCount;
        std::vector<IExplorerCommand*> m_commands;
        size_t m_index;
    };

    class RuleOpenRootCommand final : public IExplorerCommand, public IObjectWithSite
    {
    public:
        RuleOpenRootCommand() noexcept : m_refCount(1), m_site(nullptr)
        {
            g_comObjectCount.fetch_add(1, std::memory_order_relaxed);
        }

        ~RuleOpenRootCommand()
        {
            if (m_site)
            {
                m_site->Release();
                m_site = nullptr;
            }
            g_comObjectCount.fetch_sub(1, std::memory_order_relaxed);
        }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) noexcept override
        {
            if (!ppv)
            {
                return E_POINTER;
            }
            if (riid == __uuidof(IUnknown) || riid == __uuidof(IExplorerCommand))
            {
                *ppv = static_cast<IExplorerCommand*>(this);
            }
            else if (riid == __uuidof(IObjectWithSite))
            {
                *ppv = static_cast<IObjectWithSite*>(this);
            }
            else
            {
                *ppv = nullptr;
                return E_NOINTERFACE;
            }
            AddRef();
            return S_OK;
        }

        IFACEMETHODIMP_(ULONG) AddRef() noexcept override
        {
            return static_cast<ULONG>(m_refCount.fetch_add(1, std::memory_order_relaxed) + 1);
        }

        IFACEMETHODIMP_(ULONG) Release() noexcept override
        {
            long count = m_refCount.fetch_sub(1, std::memory_order_acq_rel) - 1;
            if (count == 0)
            {
                delete this;
            }
            return static_cast<ULONG>(count);
        }

        IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* ppszName) noexcept override
        {
            return AllocCoString(L"RuleOpen", ppszName);
        }

        IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* ppszIcon) noexcept override
        {
            std::wstring dir = GetModuleDirectory();
            std::wstring ico = dir + L"\\Assets\\RuleOpen.ico";
            if (GetFileAttributesW(ico.c_str()) != INVALID_FILE_ATTRIBUTES)
            {
                return AllocCoString(ico, ppszIcon);
            }
            return AllocCoString(dir + L"\\RuleOpen.Desktop.exe,-1", ppszIcon);
        }

        IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* ppszInfotip) noexcept override
        {
            return AllocCoString(L"Open or create rules with RuleOpen", ppszInfotip);
        }

        IFACEMETHODIMP GetCanonicalName(GUID* pguidCommandName) noexcept override
        {
            if (!pguidCommandName)
            {
                return E_POINTER;
            }
            *pguidCommandName = CLSID_RuleOpenRootCommand;
            return S_OK;
        }

        IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* pCmdState) noexcept override
        {
            if (!pCmdState)
            {
                return E_POINTER;
            }
            *pCmdState = ECS_ENABLED;
            return S_OK;
        }

        IFACEMETHODIMP Invoke(IShellItemArray*, IBindCtx*) noexcept override
        {
            return E_NOTIMPL;
        }

        IFACEMETHODIMP GetFlags(EXPCMDFLAGS* pFlags) noexcept override
        {
            if (!pFlags)
            {
                return E_POINTER;
            }
            *pFlags = ECF_HASSUBCOMMANDS;
            return S_OK;
        }

        IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** ppEnum) noexcept override
        {
            if (!ppEnum)
            {
                return E_POINTER;
            }

            // Windows 11 Explorer passes selection context in GetState/Invoke, so expose both
            // file actions cleanly from the submenu; Invoke guards folder-only vs file selection.
            std::vector<IExplorerCommand*> commands;
            auto* createCmd = new (std::nothrow) RuleOpenSubCommand(RuleOpenAction::CreateRule, m_site);
            auto* openCmd = new (std::nothrow) RuleOpenSubCommand(RuleOpenAction::OpenUsingRuleOpen, m_site);
            if (!createCmd || !openCmd)
            {
                if (createCmd) createCmd->Release();
                if (openCmd) openCmd->Release();
                *ppEnum = nullptr;
                return E_OUTOFMEMORY;
            }

            commands.push_back(createCmd);
            commands.push_back(openCmd);

            auto* enumerator = new (std::nothrow) RuleOpenSubCommandEnum(std::move(commands));
            if (!enumerator)
            {
                for (IExplorerCommand* c : commands)
                {
                    c->Release();
                }
                *ppEnum = nullptr;
                return E_OUTOFMEMORY;
            }

            *ppEnum = enumerator;
            return S_OK;
        }

        IFACEMETHODIMP SetSite(IUnknown* pUnkSite) noexcept override
        {
            if (m_site)
            {
                m_site->Release();
            }
            m_site = pUnkSite;
            if (m_site)
            {
                m_site->AddRef();
            }
            return S_OK;
        }

        IFACEMETHODIMP GetSite(REFIID riid, void** ppvSite) noexcept override
        {
            if (!ppvSite)
            {
                return E_POINTER;
            }
            if (!m_site)
            {
                *ppvSite = nullptr;
                return E_FAIL;
            }
            return m_site->QueryInterface(riid, ppvSite);
        }

    private:
        std::atomic<long> m_refCount;
        IUnknown* m_site;
    };

    class RuleOpenClassFactory final : public IClassFactory
    {
    public:
        RuleOpenClassFactory() noexcept : m_refCount(1)
        {
            g_comObjectCount.fetch_add(1, std::memory_order_relaxed);
        }

        ~RuleOpenClassFactory()
        {
            g_comObjectCount.fetch_sub(1, std::memory_order_relaxed);
        }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) noexcept override
        {
            if (!ppv)
            {
                return E_POINTER;
            }
            if (riid == __uuidof(IUnknown) || riid == __uuidof(IClassFactory))
            {
                *ppv = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }
            *ppv = nullptr;
            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() noexcept override
        {
            return static_cast<ULONG>(m_refCount.fetch_add(1, std::memory_order_relaxed) + 1);
        }

        IFACEMETHODIMP_(ULONG) Release() noexcept override
        {
            long count = m_refCount.fetch_sub(1, std::memory_order_acq_rel) - 1;
            if (count == 0)
            {
                delete this;
            }
            return static_cast<ULONG>(count);
        }

        IFACEMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) noexcept override
        {
            if (!ppv)
            {
                return E_POINTER;
            }
            *ppv = nullptr;
            if (pUnkOuter)
            {
                return CLASS_E_NOAGGREGATION;
            }

            auto* cmd = new (std::nothrow) RuleOpenRootCommand();
            if (!cmd)
            {
                return E_OUTOFMEMORY;
            }

            HRESULT hr = cmd->QueryInterface(riid, ppv);
            cmd->Release();
            return hr;
        }

        IFACEMETHODIMP LockServer(BOOL fLock) noexcept override
        {
            if (fLock)
            {
                g_comObjectCount.fetch_add(1, std::memory_order_relaxed);
            }
            else
            {
                g_comObjectCount.fetch_sub(1, std::memory_order_relaxed);
            }
            return S_OK;
        }

    private:
        std::atomic<long> m_refCount;
    };
}

std::atomic<long> g_comObjectCount{ 0 };

void InitializeModuleHandle(HMODULE moduleHandle) noexcept
{
    g_moduleHandle = moduleHandle;
}

std::wstring GetModuleDirectory()
{
    wchar_t path[MAX_PATH * 4]{};
    DWORD len = GetModuleFileNameW(g_moduleHandle, path, static_cast<DWORD>(std::size(path)));
    if (len == 0 || len >= std::size(path))
    {
        return {};
    }

    std::wstring fullPath(path, len);
    size_t pos = fullPath.find_last_of(L"\\/");
    return pos == std::wstring::npos ? fullPath : fullPath.substr(0, pos);
}

HRESULT CreateRuleOpenClassFactory(REFCLSID rclsid, REFIID riid, void** ppv) noexcept
{
    if (!ppv)
    {
        return E_POINTER;
    }

    *ppv = nullptr;
    if (!IsEqualCLSID(rclsid, CLSID_RuleOpenRootCommand))
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto* factory = new (std::nothrow) RuleOpenClassFactory();
    if (!factory)
    {
        return E_OUTOFMEMORY;
    }

    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}
