// taskbar_transparency.cpp
// 任务栏全透明(Windows 11 XAML 任务栏)注入模块。
//
// 实现思路参考 TranslucentTB(GPL-3.0,https://github.com/TranslucentTB/TranslucentTB,
// Copyright (C) 2016-2026 TranslucentTB contributors)的 ExplorerTAP 注入方案,做大幅简化:
//   - 由主程序 CreateRemoteThread + LoadLibrary 注入到 explorer.exe;
//   - 通过 InitializeXamlDiagnosticsEx(Windows.UI.Xaml.dll 导出)注册
//     IVisualTreeServiceCallback2,监听 XAML 视觉树变化;
//   - 定位任务栏 TaskbarFrame 下的 BackgroundFill / BackgroundStroke 矩形;
//   - 通过命名共享内存与主程序通信:state=1 时把这两个矩形的 Fill 设为
//     全透明 SolidColorBrush(alpha=0),state=0 时恢复原 Fill。
//
// 本文件以 GPL-3.0 许可发布(见项目 LICENSE 与 THIRD_PARTY_NOTICES.md)。
// 仅在 explorer.exe 进程中生效;被其他进程误加载时不做任何事。

#include <windows.h>
#include <ocidl.h>
#include <xamlom.h>
#define INITGUID
#include <windows.ui.xaml.hosting.desktopwindowxamlsource.h>
#undef INITGUID

#undef GetCurrentTime

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.UI.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Hosting.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.UI.Xaml.Shapes.h>

#include <algorithm>
#include <atomic>
#include <string>
#include <thread>
#include <unordered_set>
#include <vector>

namespace wf = winrt::Windows::Foundation;
namespace wsys = winrt::Windows::System;
namespace wux = winrt::Windows::UI::Xaml;
namespace wuxh = winrt::Windows::UI::Xaml::Hosting;
namespace wuxm = winrt::Windows::UI::Xaml::Media;
namespace wuxs = winrt::Windows::UI::Xaml::Shapes;

// ---- 与主程序(GHide)约定的共享内存协议 ----
#define STATE_MAGIC 0x44544954u // 'DITT'
#define STATE_NAME L"Local\\GHideTaskbarStateV2"
struct SharedState
{
    DWORD magic;   // STATE_MAGIC
    DWORD state;   // 0 = 恢复默认, 1 = 全透明
    DWORD injected; // DLL 初始化成功后置 1
    DWORD ownerPid; // 主程序进程 ID(DLL 据此检测主程序退出并还原)
    DWORD targetCount; // 已捕获的任务栏背景数量
};

// 本 DLL 提供给 XAML Diagnostics 实例化的 site 的 CLSID
static const CLSID CLSID_DesktopIconToggleSite = {
    0x6D3A9C2E, 0x8F41, 0x4B75, { 0x9E, 0x0A, 0x1C, 0x2D, 0x3E, 0x4F, 0x5A, 0x6B } };

// IDesktopWindowXamlSourceNative 接口 GUID(mingw 头文件 DEFINE_GUID 在 C++ 下不生成符号,自行定义)
static const IID IID_DesktopWindowXamlSourceNative_Local = {
    0x3cbcf1bf, 0x2f76, 0x4e9c, { 0x96, 0xab, 0xe8, 0x4b, 0x37, 0x97, 0x25, 0x54 } };

static HINSTANCE g_hInst = nullptr;
static winrt::com_ptr<IXamlDiagnostics> g_xaml;
static winrt::com_ptr<IVisualTreeService3> g_vts;
static wsys::DispatcherQueue g_queue{ nullptr };

struct ShapeInfo
{
    InstanceHandle handle = 0;
    wuxs::Shape control{ nullptr };
    wuxm::Brush originalFill{ nullptr };
};

struct TaskbarEntry
{
    InstanceHandle frameHandle = 0;
    ShapeInfo background;
    ShapeInfo border;
};

// 仅在 XAML UI 线程访问
static std::vector<TaskbarEntry> g_entries;
// 共享状态由控制线程读取，视觉树回调在 XAML UI 线程执行。新出现的
// 背景矩形必须继承当前状态，否则“先收到透明指令、后捕获视觉元素”时会
// 永久停留在默认外观。
static std::atomic_bool g_transparentRequested{ false };
static std::atomic_uint g_targetCount{ 0 };

static void UpdateTargetCount()
{
    unsigned int count = 0;
    for (const auto& entry : g_entries)
    {
        if (entry.background.control || entry.border.control)
            ++count;
    }
    g_targetCount.store(count);
}

// ---- 简单的 native 诊断日志(explorer 进程内) ----
static void LogNative(const wchar_t* message)
{
    wchar_t dir[MAX_PATH] = {};
    wchar_t path[MAX_PATH] = {};
    if (GetEnvironmentVariableW(L"LOCALAPPDATA", dir, MAX_PATH) <= 0)
        return;
    wcscat_s(dir, L"\\DesktopIconToggle");
    CreateDirectoryW(dir, nullptr);
    swprintf_s(path, L"%s\\native.log", dir);
    HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ,
        nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE)
    {
        wchar_t line[512] = {};
        swprintf_s(line, L"%lu | %s\r\n", GetTickCount(), message);
        DWORD written = 0;
        WriteFile(file, line, (DWORD)wcslen(line) * sizeof(wchar_t), &written, nullptr);
        CloseHandle(file);
    }
}

static void ApplyTransparent()
{
    for (auto& e : g_entries)
    {
        for (ShapeInfo* s : { &e.background, &e.border })
        {
            if (s->control)
            {
                wuxm::SolidColorBrush brush;
                winrt::Windows::UI::Color c{}; // A=0 -> 全透明
                brush.Color(c);
                s->control.Fill(brush);
            }
        }
    }
    LogNative((L"ApplyTransparent: entries=" + std::to_wstring(g_entries.size())).c_str());
}

static void RestoreDefault()
{
    for (auto& e : g_entries)
    {
        if (e.background.control && e.background.originalFill)
            e.background.control.Fill(e.background.originalFill);
        if (e.border.control && e.border.originalFill)
            e.border.control.Fill(e.border.originalFill);
    }
    LogNative((L"RestoreDefault: entries=" + std::to_wstring(g_entries.size())).c_str());
}

// ---- 视觉树监听器 ----
struct VisualTreeWatcher : winrt::implements<VisualTreeWatcher, IVisualTreeServiceCallback2, winrt::non_agile>
{
    std::unordered_set<InstanceHandle> m_sources;

    template <typename T>
    T FromHandle(InstanceHandle handle)
    {
        wf::IInspectable obj{ nullptr };
        winrt::check_hresult(g_xaml->GetIInspectableFromHandle(
            handle, reinterpret_cast<::IInspectable**>(winrt::put_abi(obj))));
        return obj.as<T>();
    }

    wux::FrameworkElement FindParent(std::wstring_view name, wux::FrameworkElement element)
    {
        auto parent = wuxm::VisualTreeHelper::GetParent(element).try_as<wux::FrameworkElement>();
        if (parent)
        {
            if (parent.Name() == name)
                return parent;
            return FindParent(name, parent);
        }
        return nullptr;
    }

    HRESULT STDMETHODCALLTYPE OnVisualTreeChange(
        ParentChildRelation relation, VisualElement element, VisualMutationType mutationType) override
    {
        try
        {
            if (mutationType == Add)
            {
                std::wstring type(element.Type, SysStringLen(element.Type));
                if (type == L"Windows.UI.Xaml.Hosting.DesktopWindowXamlSource")
                {
                    m_sources.insert(element.Handle);
                }
                else if (type == L"Taskbar.TaskbarFrame")
                {
                    auto rootGrid = FromHandle<wux::UIElement>(relation.Parent);
                    for (auto it = m_sources.begin(); it != m_sources.end(); ++it)
                    {
                        try
                        {
                            auto src = FromHandle<wuxh::DesktopWindowXamlSource>(*it);
                            if (src.Content() == rootGrid)
                            {
                                IDesktopWindowXamlSourceNative* native = nullptr;
                                ::IInspectable* inspectable =
                                    reinterpret_cast<::IInspectable*>(winrt::get_abi(src));
                                winrt::check_hresult(inspectable->QueryInterface(
                                    IID_DesktopWindowXamlSourceNative_Local,
                                    reinterpret_cast<void**>(&native)));
                                HWND hwnd = nullptr;
                                winrt::check_hresult(native->get_WindowHandle(&hwnd));
                                native->Release();
                                RegisterTaskbar(element.Handle, hwnd);
                                m_sources.erase(it);
                                break;
                            }
                        }
                        catch (...)
                        {
                        }
                    }
                }
                else if (type == L"Windows.UI.Xaml.Shapes.Rectangle")
                {
                    std::wstring name(element.Name, SysStringLen(element.Name));
                    if (name == L"BackgroundFill" || name == L"BackgroundStroke")
                    {
                        auto parent = FromHandle<wux::FrameworkElement>(relation.Parent);
                        auto frame = FindParent(L"TaskbarFrame", parent);
                        if (frame)
                        {
                            InstanceHandle fh = 0;
                            winrt::check_hresult(g_xaml->GetHandleFromIInspectable(
                                static_cast<::IInspectable*>(winrt::get_abi(frame)), &fh));
                            auto shape = FromHandle<wuxs::Shape>(element.Handle);
                            RegisterBackground(fh, element.Handle, shape, name == L"BackgroundFill");
                        }
                    }
                }
            }
            else if (mutationType == Remove)
            {
                Unregister(element.Handle);
                m_sources.erase(element.Handle);
            }
        }
        catch (...)
        {
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE OnElementStateChanged(
        InstanceHandle, VisualElementState, LPCWSTR) noexcept override
    {
        return S_OK;
    }

    void RegisterTaskbar(InstanceHandle frameHandle, HWND)
    {
        auto it = std::find_if(g_entries.begin(), g_entries.end(),
            [=](const TaskbarEntry& e) { return e.frameHandle == frameHandle; });
        if (it == g_entries.end())
        {
            TaskbarEntry entry;
            entry.frameHandle = frameHandle;
            g_entries.push_back(entry);
        }
    }

    void RegisterBackground(InstanceHandle frameHandle, InstanceHandle shapeHandle,
        wuxs::Shape shape, bool isFill)
    {
        auto it = std::find_if(g_entries.begin(), g_entries.end(),
            [=](const TaskbarEntry& e) { return e.frameHandle == frameHandle; });
        if (it == g_entries.end())
        {
            TaskbarEntry entry;
            entry.frameHandle = frameHandle;
            g_entries.push_back(entry);
            it = g_entries.end() - 1;
        }
        ShapeInfo& info = isFill ? it->background : it->border;
        info.handle = shapeHandle;
        info.control = shape;
        try
        {
            info.originalFill = shape.Fill();
        }
        catch (...)
        {
            info.originalFill = nullptr;
        }

        // InitializeXamlDiagnosticsEx 成功与 AdviseVisualTreeChange 完成之间
        // 存在时序差：主程序可能已写入 state=1，而任务栏矩形稍后才进入
        // 回调。此处在 UI 线程立即补做应用，避免 entries=0 后不再刷新。
        if (g_transparentRequested.load())
        {
            ApplyTransparent();
            LogNative(L"Late taskbar background registered; transparency reapplied");
        }
        UpdateTargetCount();
    }

    void Unregister(InstanceHandle handle)
    {
        for (auto it = g_entries.begin(); it != g_entries.end();)
        {
            bool remove = it->frameHandle == handle ||
                it->background.handle == handle ||
                it->border.handle == handle;
            if (remove)
                it = g_entries.erase(it);
            else
                ++it;
        }
        UpdateTargetCount();
    }
};

static winrt::com_ptr<VisualTreeWatcher> g_watcher;

// ---- 控制线程:轮询共享内存,状态变化时在 UI 线程应用/还原 ----
static DWORD WINAPI ControlThreadProc(LPVOID)
{
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    DWORD last = 0;
    bool have = false;
    bool ownerDead = false;
    DWORD lastOwner = 0;
    // 持续持有共享内存句柄:即使主程序退出(句柄关闭),对象仍存活,
    // 使 DLL 能够检测 ownerPid 进程消亡并还原任务栏。
    HANDLE map = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, STATE_NAME);
    for (;;)
    {
        if (!map)
        {
            map = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, STATE_NAME);
        }
        if (map)
        {
            SharedState* p = static_cast<SharedState*>(MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, 0));
            if (p && p->magic == STATE_MAGIC)
            {
                p->targetCount = g_targetCount.load();
                // 主程序进程退出检测:进程已不存在则还原任务栏,防止强杀/崩溃残留透明。
                if (p->ownerPid != lastOwner)
                {
                    lastOwner = p->ownerPid;
                    ownerDead = false;
                    // 新主程序实例:重置状态缓存,确保其写入的 state 一定被重新应用。
                    have = false;
                }
                if (!ownerDead && lastOwner != 0)
                {
                    HANDLE proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, lastOwner);
                    bool alive = false;
                    if (proc)
                    {
                        // OpenProcess 对已终止但对象句柄未释放的进程仍成功,
                        // 必须用 GetExitCodeProcess 判断是否真正存活。
                        DWORD exitCode = 0;
                        if (GetExitCodeProcess(proc, &exitCode))
                            alive = (exitCode == STILL_ACTIVE);
                        CloseHandle(proc);
                    }
                    if (!alive)
                    {
                        ownerDead = true;
                        wsys::DispatcherQueue queue = g_queue;
                        if (queue)
                        {
                            queue.TryEnqueue([]()
                            {
                                RestoreDefault();
                                LogNative(L"Owner process died; taskbar restored");
                            });
                        }
                    }
                }

                if (!have || last != p->state)
                {
                    have = true;
                    last = p->state;
                    bool transparent = (p->state == 1);
                    g_transparentRequested.store(transparent);
                    wsys::DispatcherQueue queue = g_queue;
                    if (queue)
                    {
                        queue.TryEnqueue([transparent]()
                        {
                            if (transparent)
                                ApplyTransparent();
                            else
                                RestoreDefault();
                        });
                    }
                }
            }
            if (p)
                UnmapViewOfFile(p);
        }
        Sleep(250);
    }
    return 0;
}

// ---- site:由 XAML Diagnostics 实例化并注入 IXamlDiagnostics ----
struct Site : winrt::implements<Site, IObjectWithSite, winrt::non_agile>
{
    winrt::com_ptr<IUnknown> m_site;

    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* pUnkSite) override
    {
        if (!pUnkSite)
            return S_OK;
        m_site.copy_from(pUnkSite);
        try
        {
            g_xaml = m_site.as<IXamlDiagnostics>();
            g_vts = g_xaml.as<IVisualTreeService3>();
            g_queue = wsys::DispatcherQueue::GetForCurrentThread();
            LogNative(g_queue ? L"SetSite: got DispatcherQueue" : L"SetSite: NO DispatcherQueue");
            if (!g_queue)
                return E_FAIL;

            g_watcher = winrt::make_self<VisualTreeWatcher>();
            std::thread([watcher = g_watcher]()
            {
                // AdviseVisualTreeChange 会把回调带到 UI 线程;在独立线程调用避免卡死。
                winrt::check_hresult(g_vts->AdviseVisualTreeChange(watcher.get()));
            }).detach();

            if (!CreateThread(nullptr, 0, ControlThreadProc, nullptr, 0, nullptr))
                return HRESULT_FROM_WIN32(GetLastError());
            return S_OK;
        }
        catch (...)
        {
            return winrt::to_hresult();
        }
    }

    HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** ppvSite) noexcept override
    {
        return m_site.as(riid, ppvSite);
    }
};

// ---- 类工厂 ----
struct SiteFactory : winrt::implements<SiteFactory, IClassFactory>
{
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (outer)
            return CLASS_E_NOAGGREGATION;
        auto site = winrt::make_self<Site>();
        return site.as(riid, ppv);
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL) noexcept override
    {
        return S_OK;
    }
};

extern "C" __declspec(dllexport) HRESULT WINAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
    if (rclsid == CLSID_DesktopIconToggleSite)
    {
        auto factory = winrt::make<SiteFactory>();
        return factory.as(riid, ppv);
    }
    return CLASS_E_CLASSNOTAVAILABLE;
}

extern "C" __declspec(dllexport) LRESULT CALLBACK GHideTaskbarHookProc(
    int code, WPARAM wParam, LPARAM lParam)
{
    return CallNextHookEx(nullptr, code, wParam, lParam);
}

// ---- 初始化线程(由 DllMain 在 explorer 中启动) ----
static DWORD WINAPI InstallThreadProc(LPVOID)
{
    LogNative(L"InstallThreadProc enter");
    HMODULE wux = LoadLibraryExW(L"Windows.UI.Xaml.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    LogNative(wux ? L"Loaded Windows.UI.Xaml.dll" : L"FAILED to load Windows.UI.Xaml.dll");
    if (!wux)
        return 1;

    using PFN_InitializeXamlDiagnosticsEx = HRESULT(WINAPI*)(PCWSTR, DWORD, PCWSTR, PCWSTR, CLSID, PCWSTR);
    auto ixde = reinterpret_cast<PFN_InitializeXamlDiagnosticsEx>(
        GetProcAddress(wux, "InitializeXamlDiagnosticsEx"));
    LogNative(ixde ? L"Got InitializeXamlDiagnosticsEx" : L"FAILED GetProcAddress InitializeXamlDiagnosticsEx");
    if (!ixde)
        return 2;

    wchar_t dllPath[MAX_PATH] = {};
    GetModuleFileNameW(g_hInst, dllPath, MAX_PATH);
    DWORD pid = GetCurrentProcessId();

    HRESULT hr = E_FAIL;
    for (int attempt = 1; attempt <= 60; ++attempt)
    {
        // 连接名必须匹配 XAML 诊断服务的约定格式 VisualDiagConnection{n}
        std::wstring conn = L"VisualDiagConnection" + std::to_wstring(attempt);
        std::thread([&hr, ixde, conn, pid, dllPath]()
        {
            hr = ixde(conn.c_str(), pid, nullptr, dllPath, CLSID_DesktopIconToggleSite, nullptr);
        }).join();
        if (SUCCEEDED(hr))
        {
            LogNative(L"InitializeXamlDiagnosticsEx OK");
            break;
        }
        if (attempt <= 3 || attempt % 10 == 0)
        {
            wchar_t buf[128] = {};
            swprintf_s(buf, L"InitializeXamlDiagnosticsEx attempt=%d hr=0x%08X", attempt, (unsigned)hr);
            LogNative(buf);
        }
        Sleep(500);
    }

    if (SUCCEEDED(hr))
    {
        // 标记已注入,供主程序识别
        HANDLE map = OpenFileMappingW(FILE_MAP_WRITE, FALSE, STATE_NAME);
        if (map)
        {
            SharedState* p = static_cast<SharedState*>(MapViewOfFile(map, FILE_MAP_WRITE, 0, 0, 0));
            if (p && p->magic == STATE_MAGIC)
                p->injected = 1;
            if (p)
                UnmapViewOfFile(p);
            CloseHandle(map);
        }
        LogNative(L"Injected flag set");
    }
    return SUCCEEDED(hr) ? 0 : 3;
}

// ---- DLL 入口:仅当被注入到 explorer.exe 时启动初始化 ----
BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_hInst = hinst;
        DisableThreadLibraryCalls(hinst);

        wchar_t exePath[MAX_PATH] = {};
        GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        std::wstring exe(exePath);
        auto pos = exe.find_last_of(L'\\');
        std::wstring name = pos == std::wstring::npos ? exe : exe.substr(pos + 1);
        if (_wcsicmp(name.c_str(), L"explorer.exe") == 0)
        {
            if (!CreateThread(nullptr, 0, InstallThreadProc, nullptr, 0, nullptr))
                return FALSE;
        }
    }
    return TRUE;
}
