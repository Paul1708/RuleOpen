#include "IconHandler.h"
#include "ExplorerCommands.h"
#include <shlobj_core.h>
#include <knownfolders.h>
#include <objidl.h>
#include <algorithm>
#include <mutex>
#include <new>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace
{
    struct IconRuleRoute
    {
        std::wstring extension;
        std::wstring folderWithSlash;
        bool includeSubfolders = true;
        std::wstring iconPath;
        int iconIndex = 0;
    };

    struct IconFallbackRoute
    {
        std::wstring iconPath;
        int iconIndex = 0;
    };

    struct IconRouteCache
    {
        std::mutex mutex;
        std::wstring routesFilePath;
        FILETIME lastWriteTime{};
        DWORD fileSizeLow = 0;
        DWORD fileSizeHigh = 0;
        bool loadedOnce = false;
        std::vector<IconRuleRoute> rules;
        std::unordered_map<std::wstring, IconFallbackRoute> fallbacks;
    };

    IconRouteCache& GetRouteCache()
    {
        static IconRouteCache cache;
        return cache;
    }

    std::wstring ToLowerInvariant(std::wstring_view input)
    {
        if (input.empty())
        {
            return {};
        }

        int needed = LCMapStringEx(
            LOCALE_NAME_INVARIANT,
            LCMAP_LOWERCASE,
            input.data(),
            static_cast<int>(input.size()),
            nullptr,
            0,
            nullptr,
            nullptr,
            0);
        if (needed <= 0)
        {
            return std::wstring(input);
        }

        std::wstring result(static_cast<size_t>(needed), L'\0');
        int written = LCMapStringEx(
            LOCALE_NAME_INVARIANT,
            LCMAP_LOWERCASE,
            input.data(),
            static_cast<int>(input.size()),
            result.data(),
            needed,
            nullptr,
            nullptr,
            0);
        if (written <= 0)
        {
            return std::wstring(input);
        }
        return result;
    }

    std::wstring NormalizeSlashesAndPrefix(std::wstring_view rawPath)
    {
        std::wstring path(rawPath);
        std::replace(path.begin(), path.end(), L'/', L'\\');
        if (path.rfind(L"\\\\?\\UNC\\", 0) == 0)
        {
            path = L"\\\\" + path.substr(8);
        }
        else if (path.rfind(L"\\\\?\\", 0) == 0)
        {
            path.erase(0, 4);
        }
        return path;
    }

    bool ExtractFileDirAndExt(
        std::wstring_view fullPath,
        std::wstring& outDirLowerWithSlash,
        std::wstring& outExtLower)
    {
        std::wstring normalized = NormalizeSlashesAndPrefix(fullPath);
        size_t lastSlash = normalized.find_last_of(L'\\');
        if (lastSlash == std::wstring::npos)
        {
            return false;
        }

        std::wstring_view dirView(normalized.data(), lastSlash + 1);
        std::wstring_view nameView(normalized.data() + lastSlash + 1, normalized.size() - lastSlash - 1);
        if (nameView.empty())
        {
            return false;
        }

        size_t dotPos = nameView.find_last_of(L'.');
        if (dotPos == std::wstring_view::npos || dotPos == nameView.size() - 1)
        {
            return false;
        }

        outDirLowerWithSlash = ToLowerInvariant(dirView);
        outExtLower = ToLowerInvariant(nameView.substr(dotPos));
        return true;
    }

    std::wstring ResolveRoutesFilePath()
    {
        wchar_t envBuf[MAX_PATH * 4]{};
        DWORD envLen = GetEnvironmentVariableW(L"RULEOPEN_CONFIG_DIR", envBuf, static_cast<DWORD>(std::size(envBuf)));
        if (envLen > 0 && envLen < std::size(envBuf))
        {
            std::wstring dir = NormalizeSlashesAndPrefix(envBuf);
            while (!dir.empty() && dir.back() == L'\\')
            {
                dir.pop_back();
            }
            if (!dir.empty())
            {
                return dir + L"\\icon-routes.tsv";
            }
        }

        PWSTR localAppData = nullptr;
        if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &localAppData)) && localAppData)
        {
            std::wstring result = std::wstring(localAppData) + L"\\RuleOpen\\icon-routes.tsv";
            CoTaskMemFree(localAppData);
            return result;
        }

        return {};
    }

    std::wstring Utf8ToWide(std::string_view utf8)
    {
        if (utf8.empty())
        {
            return {};
        }

        int needed = MultiByteToWideChar(
            CP_UTF8,
            0,
            utf8.data(),
            static_cast<int>(utf8.size()),
            nullptr,
            0);
        if (needed <= 0)
        {
            return {};
        }

        std::wstring wide(static_cast<size_t>(needed), L'\0');
        MultiByteToWideChar(
            CP_UTF8,
            0,
            utf8.data(),
            static_cast<int>(utf8.size()),
            wide.data(),
            needed);
        return wide;
    }

    std::vector<std::wstring> SplitTabs(const std::wstring& line)
    {
        std::vector<std::wstring> parts;
        size_t start = 0;
        while (start <= line.size())
        {
            size_t tab = line.find(L'\t', start);
            if (tab == std::wstring::npos)
            {
                parts.emplace_back(line.substr(start));
                break;
            }
            parts.emplace_back(line.substr(start, tab - start));
            start = tab + 1;
        }
        return parts;
    }

    bool ParseIntSafe(const std::wstring& value, int& outInt)
    {
        if (value.empty())
        {
            return false;
        }
        wchar_t* end = nullptr;
        long parsed = wcstol(value.c_str(), &end, 10);
        if (end == value.c_str() || *end != L'\0')
        {
            return false;
        }
        outInt = static_cast<int>(parsed);
        return true;
    }

    void ParseRoutesContentLocked(IconRouteCache& cache, const std::string& bytes)
    {
        cache.rules.clear();
        cache.fallbacks.clear();

        size_t pos = 0;
        // Skip optional UTF-8 BOM.
        if (bytes.size() >= 3 &&
            static_cast<unsigned char>(bytes[0]) == 0xEF &&
            static_cast<unsigned char>(bytes[1]) == 0xBB &&
            static_cast<unsigned char>(bytes[2]) == 0xBF)
        {
            pos = 3;
        }

        while (pos < bytes.size())
        {
            size_t eol = bytes.find('\n', pos);
            size_t lineEnd = (eol == std::string::npos) ? bytes.size() : eol;
            while (lineEnd > pos && bytes[lineEnd - 1] == '\r')
            {
                --lineEnd;
            }

            if (lineEnd > pos)
            {
                std::wstring line = Utf8ToWide(std::string_view(bytes.data() + pos, lineEnd - pos));
                auto cols = SplitTabs(line);
                if (cols.size() == 6 && cols[0] == L"R")
                {
                    int iconIdx = 0;
                    if (!cols[1].empty() && !cols[2].empty() && !cols[4].empty() && ParseIntSafe(cols[5], iconIdx))
                    {
                        std::wstring folder = ToLowerInvariant(NormalizeSlashesAndPrefix(cols[2]));
                        if (!folder.empty() && folder.back() != L'\\')
                        {
                            folder.push_back(L'\\');
                        }

                        IconRuleRoute entry;
                        entry.extension = ToLowerInvariant(cols[1]);
                        entry.folderWithSlash = std::move(folder);
                        entry.includeSubfolders = (cols[3] != L"0");
                        entry.iconPath = cols[4];
                        entry.iconIndex = iconIdx;
                        cache.rules.push_back(std::move(entry));
                    }
                }
                else if (cols.size() == 4 && cols[0] == L"F")
                {
                    int iconIdx = 0;
                    if (!cols[1].empty() && !cols[2].empty() && ParseIntSafe(cols[3], iconIdx))
                    {
                        IconFallbackRoute fb;
                        fb.iconPath = cols[2];
                        fb.iconIndex = iconIdx;
                        cache.fallbacks[ToLowerInvariant(cols[1])] = std::move(fb);
                    }
                }
            }

            if (eol == std::string::npos)
            {
                break;
            }
            pos = eol + 1;
        }
    }

    void EnsureRoutesLoadedLocked(IconRouteCache& cache)
    {
        std::wstring path = ResolveRoutesFilePath();
        if (path.empty())
        {
            cache.rules.clear();
            cache.fallbacks.clear();
            cache.loadedOnce = false;
            return;
        }

        WIN32_FILE_ATTRIBUTE_DATA attrs{};
        if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attrs))
        {
            cache.routesFilePath.clear();
            cache.rules.clear();
            cache.fallbacks.clear();
            cache.loadedOnce = false;
            return;
        }

        bool sameFile =
            cache.loadedOnce &&
            cache.routesFilePath == path &&
            cache.fileSizeLow == attrs.nFileSizeLow &&
            cache.fileSizeHigh == attrs.nFileSizeHigh &&
            CompareFileTime(&cache.lastWriteTime, &attrs.ftLastWriteTime) == 0;

        if (sameFile)
        {
            return;
        }

        HANDLE file = CreateFileW(
            path.c_str(),
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return;
        }

        LARGE_INTEGER size{};
        std::string content;
        if (GetFileSizeEx(file, &size) && size.QuadPart > 0 && size.QuadPart <= 4 * 1024 * 1024)
        {
            content.resize(static_cast<size_t>(size.QuadPart));
            DWORD read = 0;
            if (!ReadFile(file, content.data(), static_cast<DWORD>(content.size()), &read, nullptr))
            {
                content.clear();
            }
            else
            {
                content.resize(read);
            }
        }
        CloseHandle(file);

        ParseRoutesContentLocked(cache, content);
        cache.routesFilePath = std::move(path);
        cache.lastWriteTime = attrs.ftLastWriteTime;
        cache.fileSizeLow = attrs.nFileSizeLow;
        cache.fileSizeHigh = attrs.nFileSizeHigh;
        cache.loadedOnce = true;
    }

    bool TryResolveIconForFile(
        std::wstring_view filePath,
        std::wstring& outIconPath,
        int& outIconIndex)
    {
        std::wstring fileDir;
        std::wstring fileExt;
        if (!ExtractFileDirAndExt(filePath, fileDir, fileExt))
        {
            return false;
        }

        IconRouteCache& cache = GetRouteCache();
        std::lock_guard<std::mutex> lock(cache.mutex);
        EnsureRoutesLoadedLocked(cache);

        for (const IconRuleRoute& rule : cache.rules)
        {
            if (rule.extension != fileExt)
            {
                continue;
            }

            bool folderMatched = rule.includeSubfolders
                ? (fileDir.rfind(rule.folderWithSlash, 0) == 0)
                : (fileDir == rule.folderWithSlash);

            if (folderMatched)
            {
                outIconPath = rule.iconPath;
                outIconIndex = rule.iconIndex;
                return true;
            }
        }

        auto fbIt = cache.fallbacks.find(fileExt);
        if (fbIt != cache.fallbacks.end())
        {
            outIconPath = fbIt->second.iconPath;
            outIconIndex = fbIt->second.iconIndex;
            return true;
        }

        return false;
    }

    class RuleOpenIconHandler final : public IPersistFile, public IExtractIconW
    {
    public:
        RuleOpenIconHandler() noexcept : m_refCount(1)
        {
            g_comObjectCount.fetch_add(1, std::memory_order_relaxed);
        }

        ~RuleOpenIconHandler()
        {
            g_comObjectCount.fetch_sub(1, std::memory_order_relaxed);
        }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) noexcept override
        {
            if (!ppv)
            {
                return E_POINTER;
            }
            if (riid == __uuidof(IUnknown) || riid == __uuidof(IPersist) || riid == __uuidof(IPersistFile))
            {
                *ppv = static_cast<IPersistFile*>(this);
            }
            else if (riid == __uuidof(IExtractIconW))
            {
                *ppv = static_cast<IExtractIconW*>(this);
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

        // IPersist
        IFACEMETHODIMP GetClassID(CLSID* pClassID) noexcept override
        {
            if (!pClassID)
            {
                return E_POINTER;
            }
            *pClassID = CLSID_RuleOpenIconHandler;
            return S_OK;
        }

        // IPersistFile
        IFACEMETHODIMP IsDirty() noexcept override
        {
            return S_FALSE;
        }

        IFACEMETHODIMP Load(LPCOLESTR pszFileName, DWORD) noexcept override
        {
            if (!pszFileName)
            {
                return E_POINTER;
            }
            try
            {
                m_filePath = pszFileName;
                return S_OK;
            }
            catch (...)
            {
                return E_OUTOFMEMORY;
            }
        }

        IFACEMETHODIMP Save(LPCOLESTR, BOOL) noexcept override
        {
            return E_NOTIMPL;
        }

        IFACEMETHODIMP SaveCompleted(LPCOLESTR) noexcept override
        {
            return E_NOTIMPL;
        }

        IFACEMETHODIMP GetCurFile(LPOLESTR* ppszFileName) noexcept override
        {
            if (!ppszFileName)
            {
                return E_POINTER;
            }
            return SHStrDupW(m_filePath.c_str(), ppszFileName);
        }

        // IExtractIconW
        IFACEMETHODIMP GetIconLocation(
            UINT,
            _Out_writes_(cchMax) PWSTR pszIconFile,
            UINT cchMax,
            _Out_ int* piIndex,
            _Out_ UINT* pwFlags) noexcept override
        {
            if (!pszIconFile || !piIndex || !pwFlags)
            {
                return E_POINTER;
            }
            if (cchMax == 0)
            {
                return E_INVALIDARG;
            }

            pszIconFile[0] = L'\0';
            *piIndex = 0;
            *pwFlags = GIL_PERINSTANCE;

            try
            {
                std::wstring iconPath;
                int iconIndex = 0;
                if (!TryResolveIconForFile(m_filePath, iconPath, iconIndex))
                {
                    return S_FALSE;
                }

                if (iconPath.size() + 1 > cchMax)
                {
                    return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
                }

                wmemcpy(pszIconFile, iconPath.c_str(), iconPath.size() + 1);
                *piIndex = iconIndex;
                *pwFlags = GIL_PERINSTANCE;
                return S_OK;
            }
            catch (...)
            {
                return S_FALSE;
            }
        }

        IFACEMETHODIMP Extract(
            _In_ PCWSTR,
            UINT,
            _Out_opt_ HICON* phiconLarge,
            _Out_opt_ HICON* phiconSmall,
            UINT) noexcept override
        {
            if (phiconLarge)
            {
                *phiconLarge = nullptr;
            }
            if (phiconSmall)
            {
                *phiconSmall = nullptr;
            }
            return S_FALSE;
        }

    private:
        std::atomic<long> m_refCount;
        std::wstring m_filePath;
    };

    class RuleOpenIconHandlerClassFactory final : public IClassFactory
    {
    public:
        RuleOpenIconHandlerClassFactory() noexcept : m_refCount(1)
        {
            g_comObjectCount.fetch_add(1, std::memory_order_relaxed);
        }

        ~RuleOpenIconHandlerClassFactory()
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

            auto* handler = new (std::nothrow) RuleOpenIconHandler();
            if (!handler)
            {
                return E_OUTOFMEMORY;
            }

            HRESULT hr = handler->QueryInterface(riid, ppv);
            handler->Release();
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

HRESULT CreateRuleOpenIconHandlerClassFactory(REFCLSID rclsid, REFIID riid, void** ppv) noexcept
{
    if (!ppv)
    {
        return E_POINTER;
    }
    *ppv = nullptr;

    if (rclsid != CLSID_RuleOpenIconHandler)
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto* factory = new (std::nothrow) RuleOpenIconHandlerClassFactory();
    if (!factory)
    {
        return E_OUTOFMEMORY;
    }

    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}
