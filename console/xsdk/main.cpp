// Cuphead360 on the open xSDK (xsdk + vulkan4xenon): the console host. The
// player (the user's own game code and the engine, translated to C) runs on a
// guest thread and calls the platform layer below each frame (ch360_host.h):
// the pads, the frame's draw list, textures and the mixed audio.
//
//   game:\default.xex
//   game:\GameData\objects\  converted object files (tools/convert_objects.py)
//   game:\GameData\ctex\     console textures (tools/convert_console.py)
//   game:\GameData\audio\    clips (tools/convert_audio.py)
//   game:\GameData\fonts\    arial.ttf (the PC's own, copied by the staging step)
//   game:\save\              the game's saves and settings
//   game:\cuphead360.txt     log
//
// Pad: the game's own controls. Back writes the log now; hold Back + Start
// to quit to the dashboard.
#include <fcntl.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

#include <map>
#include <string>
#include <vector>

#include <vulkan/vulkan.h>
#include <vulkan4xenon.h>
#include <xsdk/audio.h>
#include <xsdk/kernel.h>
#include <xsdk/present.h>
#include <xsdk/thread.h>

#include "../ch360_host.h"
extern "C" {
#include "ch360_shaders.h"
}

#undef VK_NULL_HANDLE
#define VK_NULL_HANDLE 0

#ifndef CH360_GIT_COMMIT
#define CH360_GIT_COMMIT "unknown"
#endif

namespace {

// ------------------------------------------------------------ log

pthread_mutex_t g_log_lock = PTHREAD_MUTEX_INITIALIZER;
std::string g_log_queue;
FILE* g_log_file = NULL;
volatile bool g_log_thread = false;

void flush_log() {
    std::string lines;
    pthread_mutex_lock(&g_log_lock);
    lines.swap(g_log_queue);
    pthread_mutex_unlock(&g_log_lock);
    if (lines.empty()) return;
    if (!g_log_file) g_log_file = fopen("game:/cuphead360.txt", "ab");
    if (g_log_file) {
        fwrite(lines.data(), 1, lines.size(), g_log_file);
        fflush(g_log_file);
    }
}

// The USB drive is slow per write: lines queue and a thread writes them every 100 ms.
void* log_writer(void*) {
    xsdk_thread_move(5);
    for (;;) {
        struct timespec ts = { 0, 100000000 };
        nanosleep(&ts, NULL);
        flush_log();
    }
    return NULL;
}

void log_text(const char* text, size_t len) {
    pthread_mutex_lock(&g_log_lock);
    g_log_queue.append(text, len);
    pthread_mutex_unlock(&g_log_lock);
    if (!g_log_thread) flush_log();
}

void logf(const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(buf, sizeof(buf) - 2, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n > (int)sizeof(buf) - 2) n = sizeof(buf) - 2;
    buf[n++] = '\n';
    log_text(buf, (size_t)n);
}

double now_ms() {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (double)ts.tv_sec * 1000.0 + (double)ts.tv_nsec / 1e6;
}

void memory_mb(unsigned* free_mb, unsigned* total_mb) {
    MM_QUERY_STATISTICS_RESULT st;
    memset(&st, 0, sizeof(st));
    st.size = sizeof(st);
    if (!NT_SUCCESS(MmQueryStatistics(&st))) {
        *free_mb = *total_mb = 0;
        return;
    }
    *free_mb = (unsigned)(((unsigned long long)st.title.available_pages * 4096) >> 20);
    *total_mb = (unsigned)(((unsigned long long)st.total_physical_pages * 4096) >> 20);
}

// Files: POSIX reads of 256 KB pieces (newlib's stdio refills 1 KB per USB request).
bool read_file(const std::string& path, std::vector<unsigned char>& out) {
    int fd = open(path.c_str(), O_RDONLY);
    if (fd < 0) return false;
    off_t size = lseek(fd, 0, SEEK_END);
    lseek(fd, 0, SEEK_SET);
    if (size <= 0) {
        close(fd);
        return false;
    }
    out.resize((size_t)size);
    size_t got = 0;
    while (got < out.size()) {
        size_t want = out.size() - got;
        if (want > 256 * 1024) want = 256 * 1024;
        ssize_t n = read(fd, &out[got], want);
        if (n <= 0) break;
        got += (size_t)n;
    }
    close(fd);
    return got == out.size();
}

// ------------------------------------------------------------ renderer

// GCC turns small copies into 8-byte stores, which fault in the GPU's
// write-combined memory unless aligned; xsdk's memcpy keeps to what that
// memory takes (Spire360's lesson), so call it, never inline it.
void* (*volatile g_memcpy)(void*, const void*, size_t) = memcpy;

const int W = 1280, H = 720, FRAMES = 2;
const VkFormat TARGET_FORMAT = VK_FORMAT_B8G8R8A8_UNORM;
const VkDeviceSize VERTEX_RING = 8 * 1024 * 1024;
// Evict least recently used textures above this: the XEX image is ~100 MB and
// the player's linear memory reaches ~350 MB in a level, of 512 MB.
const size_t TEXTURE_BUDGET = 112u << 20;

struct Vertex {
    float x, y, u, v;
    float r, g, b, a;
    float br, bg, bb, ba;
};

enum { MODE_SPRITE, MODE_SCREEN, MODE_MULTIPLY, MODE_OVERLAY, MODE_SDF, MODES };

struct Tex {
    VkImage image;
    VkDeviceMemory mem;
    VkImageView view;
    VkDescriptorSet set;
    int w, h;
    VkFormat format;
    size_t bytes;
    unsigned last_used;
    std::string key;   // a converted texture (loaded on first use); empty: run-time pixels
    bool failed;
    Tex() : image(VK_NULL_HANDLE), mem(VK_NULL_HANDLE), view(VK_NULL_HANDLE), set(VK_NULL_HANDLE), w(0), h(0),
            format(VK_FORMAT_UNDEFINED), bytes(0), last_used(0), failed(false) {}
};

struct Garbage {
    VkImage image;
    VkDeviceMemory mem;
    VkImageView view;
    VkDescriptorSet set;
};

struct Frame {
    VkCommandBuffer cmd;
    VkFence fence;
    VkSemaphore acquired, rendered;
    std::vector<Garbage> dead;
};

class Renderer {
public:
    bool init();
    void frame(const unsigned char* list, unsigned len);
    void texture_file(int id, const char* key);
    void texture_pixels(int id, int w, int h, const unsigned char* bgra);
    size_t texture_bytes() const { return texture_bytes_; }
    double gpu_wait_ms, present_ms;
    unsigned uploads, evictions;

private:
    bool vk(VkResult r, const char* what) {
        if (r == VK_SUCCESS) return true;
        logf("vk: %s failed (%d)", what, (int)r);
        return false;
    }
    uint32_t memory_type(uint32_t bits, VkMemoryPropertyFlags want);
    bool make_buffer(VkBuffer* buf, VkDeviceMemory* mem, void** map, VkDeviceSize size, VkBufferUsageFlags usage);
    VkShaderModule module(const uint32_t* code, unsigned bytes);
    VkPipeline make_pipeline(VkShaderModule frag);
    bool upload(Tex& t, VkFormat format, int w, int h, const unsigned char* data, size_t bytes);
    void destroy(Tex& t, bool now);
    void load(Tex& t);
    void evict();
    Tex* tex(int id);

    VkInstance instance_;
    VkPhysicalDevice phys_;
    VkPhysicalDeviceMemoryProperties mem_props_;
    VkDevice dev_;
    VkQueue queue_;
    uint32_t family_;
    VkSurfaceKHR surface_;
    VkSwapchainKHR swapchain_;
    std::vector<VkImage> images_;
    std::vector<VkImageView> views_;
    std::vector<VkFramebuffer> fbs_;
    VkRenderPass pass_;
    VkCommandPool pool_;
    VkCommandBuffer upload_cmd_;
    VkFence upload_fence_;
    VkDescriptorSetLayout set_layout_;
    VkDescriptorPool desc_pool_;
    VkPipelineLayout pipe_layout_;
    VkShaderModule vert_;
    VkPipeline pipes_[MODES];
    VkSampler sampler_;
    VkBuffer vbuf_;
    VkDeviceMemory vmem_;
    unsigned char* vmap_;
    Frame frames_[FRAMES];
    int frame_;
    unsigned frame_no_;
    std::vector<Tex> textures_;
    Tex white_;
    size_t texture_bytes_;
    bool upload_open_;
    std::vector<std::pair<VkBuffer, VkDeviceMemory> > staging_;
    bool overflow_logged_;
};

uint32_t Renderer::memory_type(uint32_t bits, VkMemoryPropertyFlags want) {
    for (uint32_t i = 0; i < mem_props_.memoryTypeCount; ++i)
        if ((bits & (1u << i)) && (mem_props_.memoryTypes[i].propertyFlags & want) == want) return i;
    for (uint32_t i = 0; i < mem_props_.memoryTypeCount; ++i)
        if (bits & (1u << i)) return i;
    return 0;
}

bool Renderer::make_buffer(VkBuffer* buf, VkDeviceMemory* mem, void** map, VkDeviceSize size, VkBufferUsageFlags usage) {
    VkBufferCreateInfo ci;
    memset(&ci, 0, sizeof(ci));
    ci.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
    ci.size = size;
    ci.usage = usage;
    ci.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    if (!vk(vkCreateBuffer(dev_, &ci, NULL, buf), "vkCreateBuffer")) return false;
    VkMemoryRequirements req;
    vkGetBufferMemoryRequirements(dev_, *buf, &req);
    VkMemoryAllocateInfo ai;
    memset(&ai, 0, sizeof(ai));
    ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    ai.allocationSize = req.size;
    ai.memoryTypeIndex = memory_type(req.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    if (!vk(vkAllocateMemory(dev_, &ai, NULL, mem), "vkAllocateMemory (buffer)")) {
        vkDestroyBuffer(dev_, *buf, NULL);
        return false;
    }
    vkBindBufferMemory(dev_, *buf, *mem, 0);
    return vk(vkMapMemory(dev_, *mem, 0, VK_WHOLE_SIZE, 0, map), "vkMapMemory");
}

VkShaderModule Renderer::module(const uint32_t* code, unsigned bytes) {
    VkShaderModuleCreateInfo ci;
    memset(&ci, 0, sizeof(ci));
    ci.sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO;
    ci.codeSize = bytes;
    ci.pCode = code;
    VkShaderModule m = VK_NULL_HANDLE;
    vk(vkCreateShaderModule(dev_, &ci, NULL, &m), "vkCreateShaderModule");
    return m;
}

bool Renderer::init() {
    gpu_wait_ms = present_ms = 0;
    uploads = evictions = 0;
    frame_ = 0;
    frame_no_ = 0;
    texture_bytes_ = 0;
    upload_open_ = false;
    overflow_logged_ = false;
    const char* iexts[2] = { VK_KHR_SURFACE_EXTENSION_NAME, VK_KHR_DISPLAY_EXTENSION_NAME };
    VkApplicationInfo app;
    memset(&app, 0, sizeof(app));
    app.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
    app.pApplicationName = "cuphead360";
    app.apiVersion = VK_API_VERSION_1_0;
    VkInstanceCreateInfo ici;
    memset(&ici, 0, sizeof(ici));
    ici.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    ici.pApplicationInfo = &app;
    ici.enabledExtensionCount = 2;
    ici.ppEnabledExtensionNames = iexts;
    if (!vk(vkCreateInstance(&ici, NULL, &instance_), "vkCreateInstance")) return false;
    uint32_t n = 1;
    if (vkEnumeratePhysicalDevices(instance_, &n, &phys_) < 0 || n == 0) {
        logf("vk: no device");
        return false;
    }
    vkGetPhysicalDeviceMemoryProperties(phys_, &mem_props_);
    uint32_t qn = 0;
    vkGetPhysicalDeviceQueueFamilyProperties(phys_, &qn, NULL);
    std::vector<VkQueueFamilyProperties> qf(qn ? qn : 1);
    if (qn) vkGetPhysicalDeviceQueueFamilyProperties(phys_, &qn, &qf[0]);
    family_ = 0;
    for (uint32_t i = 0; i < qn; ++i)
        if (qf[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) {
            family_ = i;
            break;
        }
    float prio = 1.0f;
    VkDeviceQueueCreateInfo qi;
    memset(&qi, 0, sizeof(qi));
    qi.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
    qi.queueFamilyIndex = family_;
    qi.queueCount = 1;
    qi.pQueuePriorities = &prio;
    const char* dexts[1] = { VK_KHR_SWAPCHAIN_EXTENSION_NAME };
    VkDeviceCreateInfo dci;
    memset(&dci, 0, sizeof(dci));
    dci.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    dci.queueCreateInfoCount = 1;
    dci.pQueueCreateInfos = &qi;
    dci.enabledExtensionCount = 1;
    dci.ppEnabledExtensionNames = dexts;
    if (!vk(vkCreateDevice(phys_, &dci, NULL, &dev_), "vkCreateDevice")) return false;
    vkGetDeviceQueue(dev_, family_, 0, &queue_);

    // The display plane's swapchain at 1280x720, FIFO (vsync).
    uint32_t dn = 0;
    if (vkGetPhysicalDeviceDisplayPropertiesKHR(phys_, &dn, NULL) != VK_SUCCESS || !dn) return false;
    std::vector<VkDisplayPropertiesKHR> displays(dn);
    vkGetPhysicalDeviceDisplayPropertiesKHR(phys_, &dn, &displays[0]);
    uint32_t mn = 0;
    vkGetDisplayModePropertiesKHR(phys_, displays[0].display, &mn, NULL);
    if (!mn) return false;
    std::vector<VkDisplayModePropertiesKHR> modes(mn);
    vkGetDisplayModePropertiesKHR(phys_, displays[0].display, &mn, &modes[0]);
    VkDisplaySurfaceCreateInfoKHR sci;
    memset(&sci, 0, sizeof(sci));
    sci.sType = VK_STRUCTURE_TYPE_DISPLAY_SURFACE_CREATE_INFO_KHR;
    sci.displayMode = modes[0].displayMode;
    sci.transform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    sci.globalAlpha = 1.0f;
    sci.alphaMode = VK_DISPLAY_PLANE_ALPHA_OPAQUE_BIT_KHR;
    sci.imageExtent = modes[0].parameters.visibleRegion;
    if (!vk(vkCreateDisplayPlaneSurfaceKHR(instance_, &sci, NULL, &surface_), "vkCreateDisplayPlaneSurfaceKHR")) return false;
    VkSurfaceCapabilitiesKHR caps;
    vkGetPhysicalDeviceSurfaceCapabilitiesKHR(phys_, surface_, &caps);
    VkSwapchainCreateInfoKHR swci;
    memset(&swci, 0, sizeof(swci));
    swci.sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR;
    swci.surface = surface_;
    swci.minImageCount = caps.minImageCount > 2 ? caps.minImageCount : 2;
    swci.imageFormat = TARGET_FORMAT;
    swci.imageColorSpace = VK_COLOR_SPACE_SRGB_NONLINEAR_KHR;
    swci.imageExtent.width = W;
    swci.imageExtent.height = H;
    swci.imageArrayLayers = 1;
    swci.imageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    swci.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
    swci.preTransform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    swci.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    swci.presentMode = VK_PRESENT_MODE_FIFO_KHR;
    swci.clipped = VK_TRUE;
    if (!vk(vkCreateSwapchainKHR(dev_, &swci, NULL, &swapchain_), "vkCreateSwapchainKHR")) return false;
    uint32_t count = 0;
    vkGetSwapchainImagesKHR(dev_, swapchain_, &count, NULL);
    images_.resize(count);
    vkGetSwapchainImagesKHR(dev_, swapchain_, &count, &images_[0]);

    // One render pass: the swapchain image, cleared, then presented.
    VkAttachmentDescription a;
    memset(&a, 0, sizeof(a));
    a.format = TARGET_FORMAT;
    a.samples = VK_SAMPLE_COUNT_1_BIT;
    a.loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR;
    a.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
    a.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
    a.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    a.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    a.finalLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    VkAttachmentReference ref = { 0, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL };
    VkSubpassDescription sub;
    memset(&sub, 0, sizeof(sub));
    sub.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
    sub.colorAttachmentCount = 1;
    sub.pColorAttachments = &ref;
    VkRenderPassCreateInfo rpi;
    memset(&rpi, 0, sizeof(rpi));
    rpi.sType = VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO;
    rpi.attachmentCount = 1;
    rpi.pAttachments = &a;
    rpi.subpassCount = 1;
    rpi.pSubpasses = &sub;
    if (!vk(vkCreateRenderPass(dev_, &rpi, NULL, &pass_), "vkCreateRenderPass")) return false;
    views_.resize(count);
    fbs_.resize(count);
    for (uint32_t i = 0; i < count; ++i) {
        VkImageViewCreateInfo vi;
        memset(&vi, 0, sizeof(vi));
        vi.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
        vi.image = images_[i];
        vi.viewType = VK_IMAGE_VIEW_TYPE_2D;
        vi.format = TARGET_FORMAT;
        vi.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        vi.subresourceRange.levelCount = 1;
        vi.subresourceRange.layerCount = 1;
        if (!vk(vkCreateImageView(dev_, &vi, NULL, &views_[i]), "vkCreateImageView")) return false;
        VkFramebufferCreateInfo fi;
        memset(&fi, 0, sizeof(fi));
        fi.sType = VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO;
        fi.renderPass = pass_;
        fi.attachmentCount = 1;
        fi.pAttachments = &views_[i];
        fi.width = W;
        fi.height = H;
        fi.layers = 1;
        if (!vk(vkCreateFramebuffer(dev_, &fi, NULL, &fbs_[i]), "vkCreateFramebuffer")) return false;
    }

    VkCommandPoolCreateInfo pci;
    memset(&pci, 0, sizeof(pci));
    pci.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
    pci.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    pci.queueFamilyIndex = family_;
    if (!vk(vkCreateCommandPool(dev_, &pci, NULL, &pool_), "vkCreateCommandPool")) return false;
    VkCommandBufferAllocateInfo cai;
    memset(&cai, 0, sizeof(cai));
    cai.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
    cai.commandPool = pool_;
    cai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    cai.commandBufferCount = 1;
    VkFenceCreateInfo fci;
    memset(&fci, 0, sizeof(fci));
    fci.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
    fci.flags = VK_FENCE_CREATE_SIGNALED_BIT;
    VkSemaphoreCreateInfo semi;
    memset(&semi, 0, sizeof(semi));
    semi.sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO;
    for (int i = 0; i < FRAMES; ++i) {
        if (!vk(vkAllocateCommandBuffers(dev_, &cai, &frames_[i].cmd), "vkAllocateCommandBuffers")) return false;
        vkCreateFence(dev_, &fci, NULL, &frames_[i].fence);
        vkCreateSemaphore(dev_, &semi, NULL, &frames_[i].acquired);
        vkCreateSemaphore(dev_, &semi, NULL, &frames_[i].rendered);
    }
    if (!vk(vkAllocateCommandBuffers(dev_, &cai, &upload_cmd_), "vkAllocateCommandBuffers")) return false;
    fci.flags = 0;
    vkCreateFence(dev_, &fci, NULL, &upload_fence_);

    VkDescriptorSetLayoutBinding b;
    memset(&b, 0, sizeof(b));
    b.binding = 0;
    b.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    b.descriptorCount = 1;
    b.stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    VkDescriptorSetLayoutCreateInfo li;
    memset(&li, 0, sizeof(li));
    li.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
    li.bindingCount = 1;
    li.pBindings = &b;
    if (!vk(vkCreateDescriptorSetLayout(dev_, &li, NULL, &set_layout_), "vkCreateDescriptorSetLayout")) return false;
    VkPushConstantRange push;
    push.stageFlags = VK_SHADER_STAGE_VERTEX_BIT;
    push.offset = 0;
    push.size = 16;
    VkPipelineLayoutCreateInfo pli;
    memset(&pli, 0, sizeof(pli));
    pli.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
    pli.setLayoutCount = 1;
    pli.pSetLayouts = &set_layout_;
    pli.pushConstantRangeCount = 1;
    pli.pPushConstantRanges = &push;
    if (!vk(vkCreatePipelineLayout(dev_, &pli, NULL, &pipe_layout_), "vkCreatePipelineLayout")) return false;
    VkDescriptorPoolSize ps;
    ps.type = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    ps.descriptorCount = 4096;
    VkDescriptorPoolCreateInfo dpi;
    memset(&dpi, 0, sizeof(dpi));
    dpi.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
    dpi.flags = VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT;
    dpi.maxSets = 4096;
    dpi.poolSizeCount = 1;
    dpi.pPoolSizes = &ps;
    if (!vk(vkCreateDescriptorPool(dev_, &dpi, NULL, &desc_pool_), "vkCreateDescriptorPool")) return false;
    VkSamplerCreateInfo smi;
    memset(&smi, 0, sizeof(smi));
    smi.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO;
    smi.magFilter = smi.minFilter = VK_FILTER_LINEAR;
    smi.mipmapMode = VK_SAMPLER_MIPMAP_MODE_NEAREST;
    smi.addressModeU = smi.addressModeV = smi.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    if (!vk(vkCreateSampler(dev_, &smi, NULL, &sampler_), "vkCreateSampler")) return false;

    VkShaderModule vert = module(ch360_vert_spirv, CH360_VERT_SPIRV_BYTES);
    VkShaderModule frags[MODES] = { module(sprite_frag_spirv, SPRITE_FRAG_SPIRV_BYTES),
                                    module(screen_frag_spirv, SCREEN_FRAG_SPIRV_BYTES),
                                    module(multiply_frag_spirv, MULTIPLY_FRAG_SPIRV_BYTES),
                                    module(overlay_frag_spirv, OVERLAY_FRAG_SPIRV_BYTES),
                                    module(sdf_frag_spirv, SDF_FRAG_SPIRV_BYTES) };
    vert_ = vert;
    for (int i = 0; i < MODES; ++i) {
        pipes_[i] = make_pipeline(frags[i]);
        if (!pipes_[i]) return false;
    }
    void* vm = NULL;
    if (!make_buffer(&vbuf_, &vmem_, &vm, VERTEX_RING * FRAMES, VK_BUFFER_USAGE_VERTEX_BUFFER_BIT)) return false;
    vmap_ = (unsigned char*)vm;
    unsigned char px[4] = { 255, 255, 255, 255 };
    if (!upload(white_, TARGET_FORMAT, 1, 1, px, 4)) return false;
    VkPhysicalDeviceProperties props;
    vkGetPhysicalDeviceProperties(phys_, &props);
    logf("vk: %s, %dx%d, %u swapchain images", props.deviceName, W, H, count);
    return true;
}

VkPipeline Renderer::make_pipeline(VkShaderModule frag) {
    VkPipelineShaderStageCreateInfo st[2];
    memset(st, 0, sizeof(st));
    st[0].sType = st[1].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
    st[0].stage = VK_SHADER_STAGE_VERTEX_BIT;
    st[0].module = vert_;
    st[0].pName = "main";
    st[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT;
    st[1].module = frag;
    st[1].pName = "main";
    VkVertexInputBindingDescription vb = { 0, (uint32_t)sizeof(Vertex), VK_VERTEX_INPUT_RATE_VERTEX };
    VkVertexInputAttributeDescription va[4] = { { 0, 0, VK_FORMAT_R32G32_SFLOAT, 0 },
                                                { 1, 0, VK_FORMAT_R32G32B32A32_SFLOAT, 16 },
                                                { 2, 0, VK_FORMAT_R32G32_SFLOAT, 8 },
                                                { 3, 0, VK_FORMAT_R32G32B32A32_SFLOAT, 32 } };
    VkPipelineVertexInputStateCreateInfo vi;
    memset(&vi, 0, sizeof(vi));
    vi.sType = VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO;
    vi.vertexBindingDescriptionCount = 1;
    vi.pVertexBindingDescriptions = &vb;
    vi.vertexAttributeDescriptionCount = 4;
    vi.pVertexAttributeDescriptions = va;
    VkPipelineInputAssemblyStateCreateInfo ia;
    memset(&ia, 0, sizeof(ia));
    ia.sType = VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO;
    ia.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
    VkPipelineViewportStateCreateInfo vps;
    memset(&vps, 0, sizeof(vps));
    vps.sType = VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO;
    vps.viewportCount = 1;
    vps.scissorCount = 1;
    VkPipelineRasterizationStateCreateInfo rs;
    memset(&rs, 0, sizeof(rs));
    rs.sType = VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO;
    rs.polygonMode = VK_POLYGON_MODE_FILL;
    rs.cullMode = VK_CULL_MODE_NONE;
    rs.frontFace = VK_FRONT_FACE_COUNTER_CLOCKWISE;
    rs.lineWidth = 1.0f;
    VkPipelineMultisampleStateCreateInfo ms;
    memset(&ms, 0, sizeof(ms));
    ms.sType = VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO;
    ms.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;
    // Premultiplied alpha over: the shaders output colour * alpha.
    VkPipelineColorBlendAttachmentState ba;
    memset(&ba, 0, sizeof(ba));
    ba.colorWriteMask = VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT | VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT;
    ba.blendEnable = VK_TRUE;
    ba.colorBlendOp = ba.alphaBlendOp = VK_BLEND_OP_ADD;
    ba.srcColorBlendFactor = ba.srcAlphaBlendFactor = VK_BLEND_FACTOR_ONE;
    ba.dstColorBlendFactor = ba.dstAlphaBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
    VkPipelineColorBlendStateCreateInfo cb;
    memset(&cb, 0, sizeof(cb));
    cb.sType = VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO;
    cb.attachmentCount = 1;
    cb.pAttachments = &ba;
    VkDynamicState dyn[2] = { VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR };
    VkPipelineDynamicStateCreateInfo ds;
    memset(&ds, 0, sizeof(ds));
    ds.sType = VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO;
    ds.dynamicStateCount = 2;
    ds.pDynamicStates = dyn;
    VkGraphicsPipelineCreateInfo ci;
    memset(&ci, 0, sizeof(ci));
    ci.sType = VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO;
    ci.stageCount = 2;
    ci.pStages = st;
    ci.pVertexInputState = &vi;
    ci.pInputAssemblyState = &ia;
    ci.pViewportState = &vps;
    ci.pRasterizationState = &rs;
    ci.pMultisampleState = &ms;
    ci.pColorBlendState = &cb;
    ci.pDynamicState = &ds;
    ci.layout = pipe_layout_;
    ci.renderPass = pass_;
    VkPipeline p = VK_NULL_HANDLE;
    vk(vkCreateGraphicsPipelines(dev_, VK_NULL_HANDLE, 1, &ci, NULL, &p), "vkCreateGraphicsPipelines");
    return p;
}

// Uploads go into one command buffer that runs before the frame's (the
// frame submits it and waits: scene loads, never the steady state).
bool Renderer::upload(Tex& t, VkFormat format, int w, int h, const unsigned char* data, size_t bytes) {
    if (t.image && (t.w != w || t.h != h || t.format != format)) destroy(t, false);
    if (!t.image) {
        VkImageCreateInfo ci;
        memset(&ci, 0, sizeof(ci));
        ci.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
        ci.imageType = VK_IMAGE_TYPE_2D;
        ci.format = format;
        ci.extent.width = (uint32_t)w;
        ci.extent.height = (uint32_t)h;
        ci.extent.depth = 1;
        ci.mipLevels = 1;
        ci.arrayLayers = 1;
        ci.samples = VK_SAMPLE_COUNT_1_BIT;
        ci.tiling = VK_IMAGE_TILING_OPTIMAL;
        ci.usage = VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
        ci.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
        ci.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
        if (!vk(vkCreateImage(dev_, &ci, NULL, &t.image), "vkCreateImage")) {
            t.image = VK_NULL_HANDLE;
            return false;
        }
        VkMemoryRequirements req;
        vkGetImageMemoryRequirements(dev_, t.image, &req);
        VkMemoryAllocateInfo ai;
        memset(&ai, 0, sizeof(ai));
        ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
        ai.allocationSize = req.size;
        ai.memoryTypeIndex = memory_type(req.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
        if (!vk(vkAllocateMemory(dev_, &ai, NULL, &t.mem), "vkAllocateMemory (texture)")) {
            vkDestroyImage(dev_, t.image, NULL);
            t.image = VK_NULL_HANDLE;
            return false;
        }
        vkBindImageMemory(dev_, t.image, t.mem, 0);
        VkImageViewCreateInfo vi;
        memset(&vi, 0, sizeof(vi));
        vi.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
        vi.image = t.image;
        vi.viewType = VK_IMAGE_VIEW_TYPE_2D;
        vi.format = format;
        vi.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        vi.subresourceRange.levelCount = 1;
        vi.subresourceRange.layerCount = 1;
        vkCreateImageView(dev_, &vi, NULL, &t.view);
        VkDescriptorSetAllocateInfo sai;
        memset(&sai, 0, sizeof(sai));
        sai.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
        sai.descriptorPool = desc_pool_;
        sai.descriptorSetCount = 1;
        sai.pSetLayouts = &set_layout_;
        vk(vkAllocateDescriptorSets(dev_, &sai, &t.set), "vkAllocateDescriptorSets");
        VkDescriptorImageInfo ii;
        ii.sampler = sampler_;
        ii.imageView = t.view;
        ii.imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
        VkWriteDescriptorSet wd;
        memset(&wd, 0, sizeof(wd));
        wd.sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
        wd.dstSet = t.set;
        wd.descriptorCount = 1;
        wd.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
        wd.pImageInfo = &ii;
        vkUpdateDescriptorSets(dev_, 1, &wd, 0, NULL);
        t.w = w;
        t.h = h;
        t.format = format;
        t.bytes = (size_t)req.size;
        texture_bytes_ += t.bytes;
    }
    VkBuffer sb;
    VkDeviceMemory sm;
    void* map = NULL;
    if (!make_buffer(&sb, &sm, &map, bytes, VK_BUFFER_USAGE_TRANSFER_SRC_BIT)) return false;
    g_memcpy(map, data, bytes);
    staging_.push_back(std::make_pair(sb, sm));
    if (!upload_open_) {
        VkCommandBufferBeginInfo bi;
        memset(&bi, 0, sizeof(bi));
        bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
        bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
        vkResetCommandBuffer(upload_cmd_, 0);
        vkBeginCommandBuffer(upload_cmd_, &bi);
        upload_open_ = true;
    }
    VkImageMemoryBarrier bar;
    memset(&bar, 0, sizeof(bar));
    bar.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    bar.srcAccessMask = VK_ACCESS_SHADER_READ_BIT;
    bar.dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    bar.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    bar.newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    bar.srcQueueFamilyIndex = bar.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    bar.image = t.image;
    bar.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    bar.subresourceRange.levelCount = 1;
    bar.subresourceRange.layerCount = 1;
    vkCmdPipelineBarrier(upload_cmd_, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT | VK_PIPELINE_STAGE_TRANSFER_BIT,
                         VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, NULL, 0, NULL, 1, &bar);
    VkBufferImageCopy region;
    memset(&region, 0, sizeof(region));
    region.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    region.imageSubresource.layerCount = 1;
    region.imageExtent.width = (uint32_t)w;
    region.imageExtent.height = (uint32_t)h;
    region.imageExtent.depth = 1;
    vkCmdCopyBufferToImage(upload_cmd_, sb, t.image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
    bar.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    bar.dstAccessMask = VK_ACCESS_SHADER_READ_BIT;
    bar.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    bar.newLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    vkCmdPipelineBarrier(upload_cmd_, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, 0, 0, NULL, 0,
                         NULL, 1, &bar);
    ++uploads;
    return true;
}

void Renderer::destroy(Tex& t, bool now) {
    if (!t.image) return;
    Garbage g = { t.image, t.mem, t.view, t.set };
    if (now) {
        vkFreeDescriptorSets(dev_, desc_pool_, 1, &g.set);
        vkDestroyImageView(dev_, g.view, NULL);
        vkDestroyImage(dev_, g.image, NULL);
        vkFreeMemory(dev_, g.mem, NULL);
    } else {
        frames_[frame_].dead.push_back(g);
    }
    texture_bytes_ -= t.bytes;
    t.image = VK_NULL_HANDLE;
    t.mem = VK_NULL_HANDLE;
    t.view = VK_NULL_HANDLE;
    t.set = VK_NULL_HANDLE;
    t.bytes = 0;
}

Tex* Renderer::tex(int id) {
    if (id < 0 || id > 65535) return NULL;
    if ((size_t)id >= textures_.size()) textures_.resize((size_t)id + 1);
    return &textures_[(size_t)id];
}

void Renderer::texture_file(int id, const char* key) {
    Tex* t = tex(id);
    if (t) {
        t->key = key;
        t->failed = false;
    }
}

void Renderer::texture_pixels(int id, int w, int h, const unsigned char* bgra) {
    Tex* t = tex(id);
    if (t) {
        t->key.clear();
        upload(*t, TARGET_FORMAT, w, h, bgra, (size_t)w * h * 4);
    }
}

// A converted texture (tools/convert_console.py's .ctx) on first use.
void Renderer::load(Tex& t) {
    if (t.image || t.failed || t.key.empty()) return;
    std::vector<unsigned char> file;
    std::string path = "game:/GameData/ctex/" + t.key + ".ctx";
    if (!read_file(path, file) || file.size() < 28 || ch360_u32(&file[0]) != 0x31585443u) {
        logf("texture %s: missing or unreadable (run tools/convert_console.py)", path.c_str());
        t.failed = true;
        return;
    }
    unsigned fmt = ch360_u32(&file[4]), w = ch360_u32(&file[8]), h = ch360_u32(&file[12]), n = ch360_u32(&file[24]);
    if (28 + (size_t)n > file.size() || w == 0 || h == 0) {
        t.failed = true;
        return;
    }
    VkFormat f = fmt == 1 ? VK_FORMAT_BC1_RGBA_UNORM_BLOCK : fmt == 2 ? VK_FORMAT_BC3_UNORM_BLOCK : TARGET_FORMAT;
    if (!upload(t, f, (int)w, (int)h, &file[28], n)) t.failed = true;
}

// Textures not drawn for a while go when the set passes the budget.
void Renderer::evict() {
    if (texture_bytes_ <= TEXTURE_BUDGET) return;
    for (unsigned age = 600; age >= 30 && texture_bytes_ > TEXTURE_BUDGET * 3 / 4; age /= 2)
        for (size_t i = 0; i < textures_.size() && texture_bytes_ > TEXTURE_BUDGET * 3 / 4; ++i) {
            Tex& t = textures_[i];
            if (t.image && !t.key.empty() && frame_no_ - t.last_used > age) {
                destroy(t, false);
                ++evictions;
            }
        }
}

void Renderer::frame(const unsigned char* d, unsigned len) {
    ++frame_no_;
    if (len < 8 || ch360_u32(d) != 0x31464843u) return;
    unsigned views = ch360_u32(d + 4);

    // Pass 1: load what the list draws, then run the uploads.
    unsigned p = 8;
    for (unsigned v = 0; v < views && p + 40 <= len; ++v) {
        unsigned items = ch360_u32(d + p + 36);
        p += 40;
        for (unsigned i = 0; i < items && p + 28 <= len; ++i) {
            int id = (int)ch360_u32(d + p);
            unsigned n = ch360_u32(d + p + 24);
            Tex* t = id >= 0 ? tex(id) : NULL;
            if (t) {
                load(*t);
                t->last_used = frame_no_;
            }
            p += 28 + n * 20;
        }
    }
    Frame& f = frames_[frame_];
    double w0 = now_ms();
    vkWaitForFences(dev_, 1, &f.fence, VK_TRUE, ~(uint64_t)0);
    gpu_wait_ms += now_ms() - w0;
    vkResetFences(dev_, 1, &f.fence);
    for (size_t i = 0; i < f.dead.size(); ++i) {
        vkFreeDescriptorSets(dev_, desc_pool_, 1, &f.dead[i].set);
        vkDestroyImageView(dev_, f.dead[i].view, NULL);
        vkDestroyImage(dev_, f.dead[i].image, NULL);
        vkFreeMemory(dev_, f.dead[i].mem, NULL);
    }
    f.dead.clear();
    if (upload_open_) {
        vkEndCommandBuffer(upload_cmd_);
        VkSubmitInfo si;
        memset(&si, 0, sizeof(si));
        si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
        si.commandBufferCount = 1;
        si.pCommandBuffers = &upload_cmd_;
        vkQueueSubmit(queue_, 1, &si, upload_fence_);
        vkWaitForFences(dev_, 1, &upload_fence_, VK_TRUE, ~(uint64_t)0);
        vkResetFences(dev_, 1, &upload_fence_);
        upload_open_ = false;
        for (size_t i = 0; i < staging_.size(); ++i) {
            vkDestroyBuffer(dev_, staging_[i].first, NULL);
            vkFreeMemory(dev_, staging_[i].second, NULL);
        }
        staging_.clear();
    }

    uint32_t image = 0;
    VkResult r = vkAcquireNextImageKHR(dev_, swapchain_, ~(uint64_t)0, f.acquired, VK_NULL_HANDLE, &image);
    if (r != VK_SUCCESS && r != VK_SUBOPTIMAL_KHR) {
        vk(r, "vkAcquireNextImageKHR");
        return;
    }
    VkCommandBuffer cmd = f.cmd;
    vkResetCommandBuffer(cmd, 0);
    VkCommandBufferBeginInfo bi;
    memset(&bi, 0, sizeof(bi));
    bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    vkBeginCommandBuffer(cmd, &bi);
    VkClearValue black;
    memset(&black, 0, sizeof(black));
    black.color.float32[3] = 1.0f;
    VkRenderPassBeginInfo rbi;
    memset(&rbi, 0, sizeof(rbi));
    rbi.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO;
    rbi.renderPass = pass_;
    rbi.framebuffer = fbs_[image];
    rbi.renderArea.extent.width = W;
    rbi.renderArea.extent.height = H;
    rbi.clearValueCount = 1;
    rbi.pClearValues = &black;
    vkCmdBeginRenderPass(cmd, &rbi, VK_SUBPASS_CONTENTS_INLINE);
    VkViewport vp = { 0, 0, (float)W, (float)H, 0, 1 };
    vkCmdSetViewport(cmd, 0, 1, &vp);
    float pc[4] = { 2.0f / W, 2.0f / H, -1.0f, -1.0f };
    vkCmdPushConstants(cmd, pipe_layout_, VK_SHADER_STAGE_VERTEX_BIT, 0, sizeof(pc), pc);
    VkDeviceSize base = (VkDeviceSize)frame_ * VERTEX_RING;
    vkCmdBindVertexBuffers(cmd, 0, 1, &vbuf_, &base);
    Vertex* ring = (Vertex*)(vmap_ + base);
    size_t used = 0, cap = VERTEX_RING / sizeof(Vertex);

    // Pass 2: record the views' draws, consecutive items of one pipeline and
    // texture batched into one draw.
    VkPipeline bound = VK_NULL_HANDLE;
    VkDescriptorSet bound_set = VK_NULL_HANDLE;
    size_t batch_start = 0, batch_count = 0;
    p = 8;
    for (unsigned v = 0; v < views && p + 40 <= len; ++v) {
        float vx = ch360_f32(d + p), vy = ch360_f32(d + p + 4), vw = ch360_f32(d + p + 8), vh = ch360_f32(d + p + 12);
        bool clear = ch360_u32(d + p + 16) != 0;
        unsigned items = ch360_u32(d + p + 36);
        if (batch_count) {
            vkCmdDraw(cmd, (uint32_t)batch_count, 1, (uint32_t)batch_start, 0);
            batch_count = 0;
        }
        VkRect2D sc;
        int x0 = (int)vx, y0 = H - (int)(vy + vh);
        sc.offset.x = x0 < 0 ? 0 : x0;
        sc.offset.y = y0 < 0 ? 0 : y0;
        sc.extent.width = (uint32_t)(vw < 0 ? 0 : (vw > W ? W : vw));
        sc.extent.height = (uint32_t)(vh < 0 ? 0 : (vh > H ? H : vh));
        vkCmdSetScissor(cmd, 0, 1, &sc);
        if (clear) {
            VkClearAttachment ca;
            memset(&ca, 0, sizeof(ca));
            ca.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
            ca.clearValue.color.float32[0] = ch360_f32(d + p + 20);
            ca.clearValue.color.float32[1] = ch360_f32(d + p + 24);
            ca.clearValue.color.float32[2] = ch360_f32(d + p + 28);
            ca.clearValue.color.float32[3] = 1.0f;
            VkClearRect cr;
            cr.rect = sc;
            cr.baseArrayLayer = 0;
            cr.layerCount = 1;
            vkCmdClearAttachments(cmd, 1, &ca, 1, &cr);
        }
        p += 40;
        for (unsigned i = 0; i < items && p + 28 <= len; ++i) {
            int id = (int)ch360_u32(d + p);
            unsigned mode = ch360_u32(d + p + 4);
            float br = ch360_f32(d + p + 8), bg = ch360_f32(d + p + 12), bb = ch360_f32(d + p + 16), ba = ch360_f32(d + p + 20);
            unsigned n = ch360_u32(d + p + 24);
            const unsigned char* vs = d + p + 28;
            p += 28 + n * 20;
            Tex* t = id >= 0 ? tex(id) : &white_;
            if (!t || !t->image) continue;
            int m = mode == 1 ? MODE_SCREEN : mode == 2 ? MODE_MULTIPLY : mode == 3 ? MODE_OVERLAY : mode == 5 ? MODE_SDF : MODE_SPRITE;
            if (used + n > cap) {
                if (!overflow_logged_) logf("vk: vertex ring full (%u vertices)", (unsigned)cap);
                overflow_logged_ = true;
                break;
            }
            if (pipes_[m] != bound || t->set != bound_set) {
                if (batch_count) vkCmdDraw(cmd, (uint32_t)batch_count, 1, (uint32_t)batch_start, 0);
                batch_start = used;
                batch_count = 0;
                if (pipes_[m] != bound) vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, pipes_[m]);
                if (t->set != bound_set)
                    vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, pipe_layout_, 0, 1, &t->set, 0, NULL);
                bound = pipes_[m];
                bound_set = t->set;
            }
            Vertex tmp;
            for (unsigned k = 0; k < n; ++k, vs += 20) {
                tmp.x = ch360_f32(vs);
                tmp.y = (float)H - ch360_f32(vs + 4);
                tmp.u = ch360_f32(vs + 8);
                tmp.v = ch360_f32(vs + 12);
                tmp.r = vs[16] / 255.0f;
                tmp.g = vs[17] / 255.0f;
                tmp.b = vs[18] / 255.0f;
                tmp.a = vs[19] / 255.0f;
                tmp.br = br;
                tmp.bg = bg;
                tmp.bb = bb;
                tmp.ba = ba;
                g_memcpy(&ring[used + k], &tmp, sizeof(tmp));
            }
            used += n;
            batch_count += n;
        }
    }
    if (batch_count) vkCmdDraw(cmd, (uint32_t)batch_count, 1, (uint32_t)batch_start, 0);
    vkCmdEndRenderPass(cmd);
    vkEndCommandBuffer(cmd);
    VkPipelineStageFlags stage = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    VkSubmitInfo si;
    memset(&si, 0, sizeof(si));
    si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    si.waitSemaphoreCount = 1;
    si.pWaitSemaphores = &f.acquired;
    si.pWaitDstStageMask = &stage;
    si.commandBufferCount = 1;
    si.pCommandBuffers = &cmd;
    si.signalSemaphoreCount = 1;
    si.pSignalSemaphores = &f.rendered;
    vk(vkQueueSubmit(queue_, 1, &si, f.fence), "vkQueueSubmit");
    VkPresentInfoKHR pi;
    memset(&pi, 0, sizeof(pi));
    pi.sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR;
    pi.waitSemaphoreCount = 1;
    pi.pWaitSemaphores = &f.rendered;
    pi.swapchainCount = 1;
    pi.pSwapchains = &swapchain_;
    pi.pImageIndices = &image;
    double p0 = now_ms();
    vk(vkQueuePresentKHR(queue_, &pi), "vkQueuePresentKHR");
    present_ms += now_ms() - p0;
    frame_ = (frame_ + 1) % FRAMES;
    evict();
}

Renderer* g_renderer = NULL;

// ------------------------------------------------------------ audio

// The player mixes 48 kHz stereo each frame (ch360_plat_audio); a thread
// feeds xsdk's audio frames (256 samples, six channels) from a ring, silence
// when it runs dry; a full ring drops the oldest samples.
const unsigned AUDIO_RING = 48000; // frames (1 s)
float g_ring[AUDIO_RING * 2];
volatile unsigned g_ring_read = 0, g_ring_write = 0;
pthread_mutex_t g_audio_lock = PTHREAD_MUTEX_INITIALIZER;
bool g_audio_open = false;
unsigned g_audio_underruns = 0;

void* audio_thread(void*) {
    xsdk_thread_move(4);
    for (;;) {
        float* out = NULL;
        if (xsdk_audio_next_frame(0, &out) != 0 || !out) {
            struct timespec ts = { 0, 2000000 };
            nanosleep(&ts, NULL);
            continue;
        }
        memset(out, 0, XSDK_AUDIO_FRAME_FLOATS * sizeof(float));
        float* left = out + XSDK_AUDIO_FRONT_LEFT * XSDK_AUDIO_FRAME_SAMPLES;
        float* right = out + XSDK_AUDIO_FRONT_RIGHT * XSDK_AUDIO_FRAME_SAMPLES;
        pthread_mutex_lock(&g_audio_lock);
        unsigned avail = g_ring_write - g_ring_read;
        if (avail < XSDK_AUDIO_FRAME_SAMPLES) ++g_audio_underruns;
        for (int i = 0; i < XSDK_AUDIO_FRAME_SAMPLES && g_ring_read != g_ring_write; ++i) {
            unsigned at = (g_ring_read % AUDIO_RING) * 2;
            left[i] = g_ring[at];
            right[i] = g_ring[at + 1];
            ++g_ring_read;
        }
        pthread_mutex_unlock(&g_audio_lock);
        xsdk_audio_queue_frame();
    }
    return NULL;
}

// ------------------------------------------------------------ frame loop state

unsigned g_frames = 0;
double g_frame_start = 0, g_logic_ms = 0, g_worst_ms = 0;
unsigned g_slow = 0, g_held_quit = 0;
bool g_back_was = false;

}  // namespace

// ------------------------------------------------------------ the platform layer

extern "C" int ch360_plat_frame_begin(void) {
    double t = now_ms();
    if (g_frames > 0) {
        double ms = t - g_frame_start;
        g_logic_ms += ms;
        if (ms > g_worst_ms) g_worst_ms = ms;
        if (ms > 17.5) ++g_slow;
    }
    g_frame_start = t;
    if (g_frames > 0 && g_frames % 600 == 0) {
        unsigned f, tot;
        memory_mb(&f, &tot);
        logf("frame %u: %.1f ms/frame (worst %.1f, %u over 17.5); gpu wait %.1f, present %.1f; memory %u MB free of %u, player %u MB, "
             "textures %u MB (%u uploads, %u evicted); audio underruns %u",
             g_frames, g_logic_ms / 600, g_worst_ms, g_slow, g_renderer->gpu_wait_ms / 600, g_renderer->present_ms / 600, f, tot,
             ch360_memory_kb() / 1024, (unsigned)(g_renderer->texture_bytes() >> 20), g_renderer->uploads, g_renderer->evictions,
             g_audio_underruns);
        g_logic_ms = g_worst_ms = 0;
        g_slow = 0;
        g_renderer->gpu_wait_ms = g_renderer->present_ms = 0;
    }
    ++g_frames;
    // Back + Start held for a second quits; Back alone writes the log now.
    unsigned held = 0;
    for (uint32_t user = 0; user < 4; ++user) {
        INPUT_STATE xs;
        memset(&xs, 0, sizeof(xs));
        if (XamInputGetState(user, 0, &xs) == 0) held |= xs.gamepad.buttons;
    }
    bool back = (held & INPUT_GAMEPAD_BACK) != 0;
    if (back && !g_back_was) flush_log();
    g_back_was = back;
    g_held_quit = back && (held & INPUT_GAMEPAD_START) ? g_held_quit + 1 : 0;
    if (g_held_quit > 60) {
        logf("Back + Start: quit");
        flush_log();
        return 0;
    }
    return 1;
}

extern "C" int ch360_plat_pad(int index, ch360_pad* out) {
    INPUT_STATE xs;
    memset(&xs, 0, sizeof(xs));
    if (index < 0 || index > 3 || XamInputGetState((uint32_t)index, 0, &xs) != 0) return 0;
    out->buttons = xs.gamepad.buttons;
    out->lx = xs.gamepad.thumb_lx;
    out->ly = xs.gamepad.thumb_ly;
    out->rx = xs.gamepad.thumb_rx;
    out->ry = xs.gamepad.thumb_ry;
    out->lt = xs.gamepad.left_trigger;
    out->rt = xs.gamepad.right_trigger;
    return 1;
}

extern "C" void ch360_plat_submit(const unsigned char* data, unsigned len) { g_renderer->frame(data, len); }

extern "C" void ch360_plat_texture_file(int id, const char* key) { g_renderer->texture_file(id, key); }

extern "C" void ch360_plat_texture_pixels(int id, int width, int height, const unsigned char* bgra) {
    g_renderer->texture_pixels(id, width, height, bgra);
}

extern "C" void ch360_plat_audio(const unsigned char* s, int frames) {
    if (!g_audio_open) return;
    pthread_mutex_lock(&g_audio_lock);
    for (int i = 0; i < frames; ++i, s += 4) {
        if (g_ring_write - g_ring_read >= AUDIO_RING) ++g_ring_read;   // full: drop the oldest
        unsigned at = (g_ring_write % AUDIO_RING) * 2;
        g_ring[at] = (short)(s[0] | (s[1] << 8)) / 32768.0f;
        g_ring[at + 1] = (short)(s[2] | (s[3] << 8)) / 32768.0f;
        ++g_ring_write;
    }
    pthread_mutex_unlock(&g_audio_lock);
}

extern "C" void ch360_plat_log(const char* text, size_t len) { log_text(text, len); }

int main() {
    FILE* fresh = fopen("game:/cuphead360.txt", "wb");
    if (fresh) fclose(fresh);
    logf("Cuphead360 (xSDK) %s", CH360_GIT_COMMIT);
    unsigned f, t;
    memory_mb(&f, &t);
    logf("memory at start: %u MB free of %u MB", f, t);
    // The player's linear memory is reserved first (committed as it grows):
    // xsdk's heap takes the free address space at its first growth.
    static const char* const argv[] = { "cuphead360", "run", "--console", "1", "--data", "/GameData", "--save", "/save" };
    if (ch360_prepare("game:/", (int)(sizeof(argv) / sizeof(argv[0])), argv, 448) != 0) {
        logf("player set-up failed");
        flush_log();
        return 1;
    }
    memory_mb(&f, &t);
    logf("player module ready: %u MB free", f);
    pthread_t th;
    if (pthread_create(&th, NULL, log_writer, NULL) == 0) g_log_thread = true;
    static Renderer renderer;
    g_renderer = &renderer;
    if (!renderer.init()) {
        logf("renderer set-up failed");
        flush_log();
        return 1;
    }
    if (xsdk_audio_open(0) == 0) {
        g_audio_open = true;
        pthread_t ath;
        pthread_create(&ath, NULL, audio_thread, NULL);
    } else {
        logf("no audio output");
    }
    int code = ch360_start(16, 0);
    memory_mb(&f, &t);
    logf("player exit %d after %u frames; memory %u MB free, player %u MB", code, g_frames, f, ch360_memory_kb() / 1024);
    flush_log();
    return 0;
}
