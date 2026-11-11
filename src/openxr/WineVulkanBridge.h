#pragma once
#include <windows.h>
#include <vulkan/vulkan.h>

namespace kharvox {
inline bool runningUnderWine() {
    static const bool wine = [] {
        const HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        return ntdll && GetProcAddress(ntdll, "wine_get_version");
    }();
    return wine;
}

// Wine's OpenXR bridge reads winevulkan objects directly. The private Wine
// loader (tools/wine_loader) exports the lookups from its own wrappers; no
// other vulkan-1.dll has them.
struct WineVulkanHandles {
    VkInstance (VKAPI_CALL* icdInstance)(VkInstance){};
    VkPhysicalDevice (VKAPI_CALL* icdPhysicalDevice)(VkPhysicalDevice){};
    bool available() const { return icdInstance && icdPhysicalDevice; }
};

inline WineVulkanHandles findWineVulkanHandles() {
    WineVulkanHandles handles{};
    if (!runningUnderWine()) return handles;
    const HMODULE loader = GetModuleHandleW(L"vulkan-1.dll");
    if (!loader) return handles;
    handles.icdInstance = reinterpret_cast<decltype(handles.icdInstance)>(
        GetProcAddress(loader, "kharvoxWineIcdInstance"));
    handles.icdPhysicalDevice = reinterpret_cast<decltype(handles.icdPhysicalDevice)>(
        GetProcAddress(loader, "kharvoxWineIcdPhysicalDevice"));
    return handles;
}
} // namespace kharvox
