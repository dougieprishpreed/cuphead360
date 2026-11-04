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

#include <algorithm>
#include <map>
#include <new>
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

// What the guest thread is doing, for the heartbeat: "game" while the
// player's own code runs, otherwise the host call or runtime wait it is in.
volatile const char* g_stage = "start";
volatile unsigned g_frames_seen = 0;
volatile bool g_player_started = false;
// The runtime's file calls (srt_plat_*, wrapped below): counts, bytes, time.
volatile unsigned g_io_opens = 0, g_io_reads = 0, g_io_seeks = 0;
volatile unsigned long long g_io_bytes = 0;
volatile double g_io_ms = 0;

volatile size_t g_texture_bytes = 0;   // the renderer's, for the heartbeat

void flush_log(bool sync = false) {
    std::string lines;
    pthread_mutex_lock(&g_log_lock);
    lines.swap(g_log_queue);
    pthread_mutex_unlock(&g_log_lock);
    if (lines.empty()) return;
    if (!g_log_file) g_log_file = fopen("game:/cuphead360.txt", "ab");
    if (g_log_file) {
        fwrite(lines.data(), 1, lines.size(), g_log_file);
        fflush(g_log_file);
        // On the drive even if the console freezes next (no fault handler).
        if (sync) fsync(fileno(g_log_file));
    }
}

void logf(const char* fmt, ...);
double now_ms();
void memory_mb(unsigned* free_mb, unsigned* total_mb);
extern "C" void srt_get_stats(void* out);

// The USB drive is slow per write: lines queue and a thread writes them every
// 100 ms. Every 2 s for the first two minutes, then every 10 s, it logs a
// heartbeat: the frame count and where the guest thread is, so a stall shows
// its place, and a log that simply ends means the whole console froze.
void* log_writer(void*) {
    xsdk_thread_move(5);
    double start = now_ms();
    for (unsigned tick = 1;; ++tick) {
        struct timespec ts = { 0, 100000000 };
        nanosleep(&ts, NULL);
        unsigned s = (unsigned)((now_ms() - start) / 1000.0);
        bool beat = tick % (s < 120 ? 20 : 100) == 0;
        // The player's output during a long frame (otherwise drained at the next frame).
        if (g_player_started) ch360_drain_output();
        if (beat) {
            struct { unsigned long long memory, capacity, stdout_bytes, wasi_calls; } st;
            srt_get_stats(&st);
            unsigned free_mb, total_mb;
            memory_mb(&free_mb, &total_mb);
            logf("alive %u s: frame %u, %s; %u MB free, player %u MB, textures %u MB; wasi calls %u; files: %u opens, %u reads, "
                 "%u MB in %u ms, %u seeks",
                 s, g_frames_seen, (const char*)g_stage, free_mb, ch360_memory_kb() / 1024, (unsigned)(g_texture_bytes >> 20),
                 (unsigned)st.wasi_calls, g_io_opens, g_io_reads, (unsigned)(g_io_bytes >> 20), (unsigned)g_io_ms, g_io_seeks);
        }
        // Synced twice a second: a freeze keeps what led up to it.
        flush_log(beat || tick % 5 == 0);
    }
    return NULL;
}

struct Stage {
    const char* prev;
    explicit Stage(const char* s) : prev((const char*)g_stage) { g_stage = s; }
    ~Stage() { g_stage = prev; }
};

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

// ------------------------------------------------------------ renderer

// GCC turns small copies into 8-byte stores, which fault in the GPU's
// write-combined memory unless aligned; xsdk's memcpy keeps to what that
// memory takes (Spire360's lesson), so call it, never inline it.
void* (*volatile g_memcpy)(void*, const void*, size_t) = memcpy;

const int W = 1280, H = 720, FRAMES = 2;
const VkFormat TARGET_FORMAT = VK_FORMAT_B8G8R8A8_UNORM;
// Vertices a frame (two frames in flight): 87,000 at 24 bytes; frames draw up to
// ~16,000 since hidden UI is skipped (8 MB a frame held 12 MB more than needed).
const VkDeviceSize VERTEX_RING = 2 * 1024 * 1024;
// Evict least recently used textures above this: the XEX image is ~100 MB and
// the player's linear memory reaches ~220 MB in a level, of 512 MB. A new
// texture first makes room from textures not drawn lately, so the set stays
// near the budget across a scene change (the map's first frame draws 68 MB
// while the title's and slot select's 53 MB are still loaded). With sprites at
// full size the map draws ~91 MB; memory (MEMORY_FLOOR_MB) was what held the
// set lower in the twelfth run, so the budget is only a little above that.
const size_t TEXTURE_BUDGET = 112u << 20;
// Uploads go through one staging buffer, run (submitted and waited) when it
// fills: one buffer per texture until the frame ended doubled a scene's first
// frame in memory, and the fifth run froze there (docs/VERIFICATION.md).
const size_t STAGING_RING = 8u << 20;
// Free memory kept for the player's linear memory (it grows during scene
// loads), the kernel and the driver: textures are not loaded into it.
const unsigned MEMORY_FLOOR_MB = 24;


enum { MODE_SPRITE, MODE_SCREEN, MODE_MULTIPLY, MODE_OVERLAY, MODE_SDF, MODE_LIGHTRAY, MODES };

struct Tex {
    VkImage image;
    VkDeviceMemory mem;
    VkImageView view;
    VkDescriptorSet set;
    int w, h;
    VkFormat format;
    size_t bytes;
    unsigned last_used;
    unsigned retry_at;   // after running out of memory: the frame to try loading again
    std::string key;     // a converted texture (loaded on first use); empty: run-time pixels
    bool failed;
    Tex() : image(VK_NULL_HANDLE), mem(VK_NULL_HANDLE), view(VK_NULL_HANDLE), set(VK_NULL_HANDLE), w(0), h(0),
            format(VK_FORMAT_UNDEFINED), bytes(0), last_used(0), retry_at(0), failed(false) {}
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
    void frame(const ch360_frame* f);
    void texture_file(int id, const char* key);
    void texture_pixels(int id, int w, int h, const unsigned char* bgra);
    size_t texture_bytes() const { return texture_bytes_; }
    // Frees textures not drawn lately for the player's memory (the commit wrap).
    size_t make_room(size_t bytes) { return release_unused(bytes); }
    double gpu_wait_ms, present_ms;
    double host_ms_[8];
    unsigned host_draws_, host_items_;
    unsigned long long host_vertices_;
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
    VkPipeline make_pipeline(VkShaderModule frag, VkPipelineLayout layout);
    bool make_image(Tex& t, VkFormat format, int w, int h);
    unsigned char* staging(size_t bytes, VkBuffer* buf, VkDeviceSize* off);
    void record_copy(Tex& t, VkBuffer buf, VkDeviceSize off);
    void flush_uploads();
    bool upload(Tex& t, VkFormat format, int w, int h, const unsigned char* data, size_t bytes);
    void destroy(Tex& t, bool now);
    void load(Tex& t);
    void load_packed(Tex& t);
    void evict();
    size_t release_unused(size_t want);
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
    VkPipelineLayout ray_layout_;   // the light ray: three textures, its floats for the fragment stage
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
    VkBuffer ring_buf_;
    VkDeviceMemory ring_mem_;
    unsigned char* ring_map_;
    size_t ring_used_;
    std::vector<std::pair<VkBuffer, VkDeviceMemory> > staging_;   // uploads larger than the ring
    std::vector<unsigned char> bounce_;                            // file reads, copied on into staging
    bool staging_cached_;                                          // staging in the CPU's cached memory
    std::vector<unsigned char> on_view_;                           // per item of the frame's list
    bool overflow_logged_;
    unsigned loads_logged_;
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
    // Buffers only copied from (staging) take the CPU's cached memory where
    // there is one: vulkan4xenon copies DXT data into images on the CPU, and
    // reading the GPU's write-combined memory back ran at 18.5 MB/s
    // (vulkan4xenon's transfer.c), ~220 ms of every 4 MB atlas in the eighth run.
    const VkMemoryPropertyFlags visible = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;
    bool cached = false;
    ai.memoryTypeIndex = memory_type(req.memoryTypeBits, visible);
    if (usage == VK_BUFFER_USAGE_TRANSFER_SRC_BIT)
        for (uint32_t i = 0; i < mem_props_.memoryTypeCount; ++i)
            if ((req.memoryTypeBits & (1u << i)) &&
                (mem_props_.memoryTypes[i].propertyFlags & (visible | VK_MEMORY_PROPERTY_HOST_CACHED_BIT)) ==
                    (visible | VK_MEMORY_PROPERTY_HOST_CACHED_BIT)) {
                ai.memoryTypeIndex = i;
                cached = true;
                break;
            }
    if (usage == VK_BUFFER_USAGE_TRANSFER_SRC_BIT) staging_cached_ = cached;
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
    loads_logged_ = 0;
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
    VkDescriptorSetLayout three[3] = { set_layout_, set_layout_, set_layout_ };
    VkPushConstantRange ray_push[2];
    ray_push[0] = push;
    ray_push[1].stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    ray_push[1].offset = 16;
    ray_push[1].size = 16;
    pli.setLayoutCount = 3;
    pli.pSetLayouts = three;
    pli.pushConstantRangeCount = 2;
    pli.pPushConstantRanges = ray_push;
    if (!vk(vkCreatePipelineLayout(dev_, &pli, NULL, &ray_layout_), "vkCreatePipelineLayout (light ray)")) return false;
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
                                    module(sdf_frag_spirv, SDF_FRAG_SPIRV_BYTES),
                                    module(lightray_frag_spirv, LIGHTRAY_FRAG_SPIRV_BYTES) };
    vert_ = vert;
    for (int i = 0; i < MODES; ++i) {
        pipes_[i] = make_pipeline(frags[i], i == MODE_LIGHTRAY ? ray_layout_ : pipe_layout_);
        if (!pipes_[i]) return false;
    }
    void* vm = NULL;
    if (!make_buffer(&vbuf_, &vmem_, &vm, VERTEX_RING * FRAMES, VK_BUFFER_USAGE_VERTEX_BUFFER_BIT)) return false;
    vmap_ = (unsigned char*)vm;
    void* rm = NULL;
    if (!make_buffer(&ring_buf_, &ring_mem_, &rm, STAGING_RING, VK_BUFFER_USAGE_TRANSFER_SRC_BIT)) return false;
    ring_map_ = (unsigned char*)rm;
    ring_used_ = 0;
    logf("vk: staging in %s memory", staging_cached_ ? "cached" : "write-combined");
    bounce_.resize(256 * 1024);
    unsigned char px[4] = { 255, 255, 255, 255 };
    if (!upload(white_, TARGET_FORMAT, 1, 1, px, 4)) return false;
    VkPhysicalDeviceProperties props;
    vkGetPhysicalDeviceProperties(phys_, &props);
    logf("vk: %s, %dx%d, %u swapchain images", props.deviceName, W, H, count);
    return true;
}

VkPipeline Renderer::make_pipeline(VkShaderModule frag, VkPipelineLayout layout) {
    VkPipelineShaderStageCreateInfo st[2];
    memset(st, 0, sizeof(st));
    st[0].sType = st[1].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
    st[0].stage = VK_SHADER_STAGE_VERTEX_BIT;
    st[0].module = vert_;
    st[0].pName = "main";
    st[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT;
    st[1].module = frag;
    st[1].pName = "main";
    // ch360_vertex: position and uv as floats, the colour and blend colour 8 bits each by address.
    VkVertexInputBindingDescription vb = { 0, (uint32_t)sizeof(ch360_vertex), VK_VERTEX_INPUT_RATE_VERTEX };
    VkVertexInputAttributeDescription va[4] = { { 0, 0, VK_FORMAT_R32G32_SFLOAT, 0 },
                                                { 1, 0, VK_FORMAT_R8G8B8A8_UNORM, 16 },
                                                { 2, 0, VK_FORMAT_R32G32_SFLOAT, 8 },
                                                { 3, 0, VK_FORMAT_R8G8B8A8_UNORM, 20 } };
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
    ci.layout = layout;
    ci.renderPass = pass_;
    VkPipeline p = VK_NULL_HANDLE;
    vk(vkCreateGraphicsPipelines(dev_, VK_NULL_HANDLE, 1, &ci, NULL, &p), "vkCreateGraphicsPipelines");
    return p;
}

// A texture's image, memory, view and descriptor set. Room comes first from
// textures not drawn lately: down to the budget, then above the memory floor,
// then once more if the allocation still fails.
bool Renderer::make_image(Tex& t, VkFormat format, int w, int h) {
    // In use from now: making room below must not take this texture's own
    // image (never drawn, it was the oldest; the sixth run crashed so).
    t.last_used = frame_no_;
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
    size_t size = (size_t)req.size;
    if (texture_bytes_ + size > TEXTURE_BUDGET) release_unused(texture_bytes_ + size - TEXTURE_BUDGET);
    unsigned free_mb, total_mb;
    memory_mb(&free_mb, &total_mb);
    unsigned need_mb = MEMORY_FLOOR_MB + (unsigned)(size >> 20) + 1;
    if (free_mb < need_mb) {
        release_unused((size_t)(need_mb - free_mb) << 20);
        memory_mb(&free_mb, &total_mb);
    }
    VkResult r = VK_ERROR_OUT_OF_DEVICE_MEMORY;
    if (free_mb >= need_mb) {
        VkMemoryAllocateInfo ai;
        memset(&ai, 0, sizeof(ai));
        ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
        ai.allocationSize = req.size;
        ai.memoryTypeIndex = memory_type(req.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
        r = vkAllocateMemory(dev_, &ai, NULL, &t.mem);
        if (r != VK_SUCCESS && release_unused(size * 2) > 0) r = vkAllocateMemory(dev_, &ai, NULL, &t.mem);
    }
    if (r != VK_SUCCESS) {
        logf("vk: no memory for a %dx%d texture (%u KB): %u MB free, textures %u MB", w, h, (unsigned)(size >> 10), free_mb,
             (unsigned)(texture_bytes_ >> 20));
        vkDestroyImage(dev_, t.image, NULL);
        t.image = VK_NULL_HANDLE;
        t.mem = VK_NULL_HANDLE;
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
    if (!vk(vkCreateImageView(dev_, &vi, NULL, &t.view), "vkCreateImageView")) {
        vkDestroyImage(dev_, t.image, NULL);
        vkFreeMemory(dev_, t.mem, NULL);
        t.image = VK_NULL_HANDLE;
        t.mem = VK_NULL_HANDLE;
        t.view = VK_NULL_HANDLE;
        return false;
    }
    VkDescriptorSetAllocateInfo sai;
    memset(&sai, 0, sizeof(sai));
    sai.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
    sai.descriptorPool = desc_pool_;
    sai.descriptorSetCount = 1;
    sai.pSetLayouts = &set_layout_;
    if (!vk(vkAllocateDescriptorSets(dev_, &sai, &t.set), "vkAllocateDescriptorSets")) {
        vkDestroyImageView(dev_, t.view, NULL);
        vkDestroyImage(dev_, t.image, NULL);
        vkFreeMemory(dev_, t.mem, NULL);
        t.image = VK_NULL_HANDLE;
        t.mem = VK_NULL_HANDLE;
        t.view = VK_NULL_HANDLE;
        t.set = VK_NULL_HANDLE;
        return false;
    }
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
    t.bytes = size;
    texture_bytes_ += t.bytes;
    return true;
}

// Uploads are recorded into one command buffer that runs before the frame's
// (the frame submits it and waits: scene loads, never the steady state), or
// sooner when the staging ring is full.
void Renderer::flush_uploads() {
    if (!upload_open_) return;
    Stage marker("vk: uploading textures");
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
    ring_used_ = 0;
}

// Room for bytes of upload data: in the ring (run what it holds first if it
// is full), or a buffer of its own for data larger than the ring.
unsigned char* Renderer::staging(size_t bytes, VkBuffer* buf, VkDeviceSize* off) {
    size_t need = (bytes + 4095) & ~(size_t)4095;
    if (need <= STAGING_RING) {
        if (ring_used_ + need > STAGING_RING) flush_uploads();
        *buf = ring_buf_;
        *off = ring_used_;
        ring_used_ += need;
        return ring_map_ + *off;
    }
    flush_uploads();
    VkBuffer sb;
    VkDeviceMemory sm;
    void* map = NULL;
    if (!make_buffer(&sb, &sm, &map, bytes, VK_BUFFER_USAGE_TRANSFER_SRC_BIT) &&
        (release_unused(bytes) == 0 || !make_buffer(&sb, &sm, &map, bytes, VK_BUFFER_USAGE_TRANSFER_SRC_BIT)))
        return NULL;
    staging_.push_back(std::make_pair(sb, sm));
    *buf = sb;
    *off = 0;
    return (unsigned char*)map;
}

void Renderer::record_copy(Tex& t, VkBuffer buf, VkDeviceSize off) {
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
    region.bufferOffset = off;
    region.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    region.imageSubresource.layerCount = 1;
    region.imageExtent.width = (uint32_t)t.w;
    region.imageExtent.height = (uint32_t)t.h;
    region.imageExtent.depth = 1;
    vkCmdCopyBufferToImage(upload_cmd_, buf, t.image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
    bar.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    bar.dstAccessMask = VK_ACCESS_SHADER_READ_BIT;
    bar.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    bar.newLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    vkCmdPipelineBarrier(upload_cmd_, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, 0, 0, NULL, 0,
                         NULL, 1, &bar);
    ++uploads;
}

bool Renderer::upload(Tex& t, VkFormat format, int w, int h, const unsigned char* data, size_t bytes) {
    if (t.image && (t.w != w || t.h != h || t.format != format)) destroy(t, false);
    if (!t.image && !make_image(t, format, w, h)) return false;
    VkBuffer buf;
    VkDeviceSize off;
    unsigned char* dst = staging(bytes, &buf, &off);
    if (!dst) return false;
    g_memcpy(dst, data, bytes);
    record_copy(t, buf, off);
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

// A converted texture (tools/convert_console.py's .ctx, or a full-size
// sprite page of tools/cluster_console.py) on first use, from the texture
// pack (CTEX*.PAK, LZ4) decoded straight into staging when that is cached
// memory, else in pieces through a bounce buffer (the kernel's copies into
// the GPU's write-combined memory aren't safe: g_memcpy is). Without a pack,
// the loose ctex/XX/YYYYYYYY.CTX files. Out of memory, it is drawn without
// and tried again two seconds later.
void Renderer::load(Tex& t) {
    if (t.image || t.failed || t.key.empty() || frame_no_ < t.retry_at) return;
    if (ch360_ctex_pack >= 0) {
        load_packed(t);
        return;
    }
    char staged[12];
    ch360_staged_name(t.key.c_str(), t.key.size(), staged);
    std::string path = std::string("game:/GameData/ctex/") + staged + ".CTX";
    double t0 = now_ms();
    unsigned char head[28];
    int fd = open(path.c_str(), O_RDONLY);
    bool ok = fd >= 0 && read(fd, head, sizeof(head)) == (ssize_t)sizeof(head) && ch360_u32(head) == 0x31585443u;
    unsigned fmt = ok ? ch360_u32(head + 4) : 0, w = ok ? ch360_u32(head + 8) : 0, h = ok ? ch360_u32(head + 12) : 0,
             n = ok ? ch360_u32(head + 24) : 0;
    if (!ok || w == 0 || h == 0 || w > 8192 || h > 8192 || n == 0) {
        if (fd >= 0) close(fd);
        logf("texture %s (%s): missing or unreadable (run tools/convert_console.py, stage_console.py)", t.key.c_str(), path.c_str());
        t.failed = true;
        return;
    }
    VkFormat f = fmt == 1 ? VK_FORMAT_BC1_RGBA_UNORM_BLOCK : fmt == 2 ? VK_FORMAT_BC3_UNORM_BLOCK : TARGET_FORMAT;
    VkBuffer buf;
    VkDeviceSize off;
    unsigned char* dst = NULL;
    if (make_image(t, f, (int)w, (int)h)) {
        dst = staging(n, &buf, &off);
        if (!dst) destroy(t, true);
    }
    if (!dst) {
        close(fd);
        t.retry_at = frame_no_ + 120;
        logf("texture %s: no memory, drawn without it for now", t.key.c_str());
        return;
    }
    size_t got = 0;
    while (got < n) {
        size_t want = n - got < bounce_.size() ? n - got : bounce_.size();
        if (staging_cached_) want = n - got < (4u << 20) ? n - got : (4u << 20);
        ssize_t r = read(fd, staging_cached_ ? dst + got : &bounce_[0], want);
        if (r <= 0) break;
        if (!staging_cached_) g_memcpy(dst + got, &bounce_[0], (size_t)r);
        got += (size_t)r;
    }
    close(fd);
    if (got != n) {
        destroy(t, true);
        logf("texture %s: short file (%u of %u bytes)", path.c_str(), (unsigned)got, n);
        t.failed = true;
        return;
    }
    record_copy(t, buf, off);
    g_texture_bytes = texture_bytes_;
    if (loads_logged_ < 400) {
        ++loads_logged_;
        unsigned free_mb, total_mb;
        memory_mb(&free_mb, &total_mb);
        logf("texture %s: format %u, %ux%u, %u KB in %u ms; %u MB free, textures %u MB", t.key.c_str(), fmt, w, h, n >> 10,
             (unsigned)(now_ms() - t0), free_mb, (unsigned)(texture_bytes_ >> 20));
    }
}

void Renderer::load_packed(Tex& t) {
    double t0 = now_ms();
    ch360_pack_entry e;
    unsigned char head[28];
    bool ok = ch360_pack_find(ch360_ctex_pack, t.key.c_str(), t.key.size(), &e) && e.size >= 28 &&
              ch360_pack_read(&e, 0, head, sizeof(head)) == 0 && ch360_u32(head) == 0x31585443u;
    unsigned fmt = ok ? ch360_u32(head + 4) : 0, w = ok ? ch360_u32(head + 8) : 0, h = ok ? ch360_u32(head + 12) : 0,
             n = ok ? ch360_u32(head + 24) : 0;
    if (!ok || w == 0 || h == 0 || w > 8192 || h > 8192 || n == 0 || n != e.size - 28) {
        logf("texture %s: missing from the pack or unreadable (run tools/stage_console.py)", t.key.c_str());
        t.failed = true;
        return;
    }
    VkFormat f = fmt == 1 ? VK_FORMAT_BC1_RGBA_UNORM_BLOCK : fmt == 2 ? VK_FORMAT_BC3_UNORM_BLOCK : TARGET_FORMAT;
    VkBuffer buf;
    VkDeviceSize off;
    unsigned char* dst = NULL;
    if (make_image(t, f, (int)w, (int)h)) {
        dst = staging(n, &buf, &off);
        if (!dst) destroy(t, true);
    }
    if (!dst) {
        t.retry_at = frame_no_ + 120;
        logf("texture %s: no memory, drawn without it for now", t.key.c_str());
        return;
    }
    double t1 = now_ms();
    bool read = true;
    if (staging_cached_) {
        read = ch360_pack_read(&e, 28, dst, n) == 0;
    } else {
        for (unsigned got = 0; read && got < n;) {
            unsigned want = n - got < bounce_.size() ? n - got : (unsigned)bounce_.size();
            read = ch360_pack_read(&e, 28 + got, &bounce_[0], want) == 0;
            if (read) g_memcpy(dst + got, &bounce_[0], want);
            got += want;
        }
    }
    if (!read) {
        destroy(t, true);
        logf("texture %s: bad read from the pack", t.key.c_str());
        t.failed = true;
        return;
    }
    double t2 = now_ms();
    record_copy(t, buf, off);
    g_texture_bytes = texture_bytes_;
    if (loads_logged_ < 400) {
        ++loads_logged_;
        unsigned free_mb, total_mb;
        memory_mb(&free_mb, &total_mb);
        logf("texture %s: format %u, %ux%u, %u KB (%u KB stored) in %u ms (read and decode %u); %u MB free, textures %u MB",
             t.key.c_str(), fmt, w, h, n >> 10, e.stored >> 10, (unsigned)(now_ms() - t0), (unsigned)(t2 - t1), free_mb,
             (unsigned)(texture_bytes_ >> 20));
    }
}

// Frees textures not drawn in the last three frames (the GPU is done with
// them: two frames are in flight), oldest first, until want bytes are freed.
size_t Renderer::release_unused(size_t want) {
    std::vector<std::pair<unsigned, size_t> > order;
    for (size_t i = 0; i < textures_.size(); ++i)
        if (textures_[i].image && !textures_[i].key.empty() && frame_no_ - textures_[i].last_used > 2)
            order.push_back(std::make_pair(textures_[i].last_used, i));
    std::sort(order.begin(), order.end());
    size_t freed = 0;
    for (size_t k = 0; k < order.size() && freed < want; ++k) {
        Tex& t = textures_[order[k].second];
        freed += t.bytes;
        destroy(t, true);
        ++evictions;
    }
    return freed;
}

// Textures not drawn for a while go when the set passes the budget.
void Renderer::evict() {
    if (texture_bytes_ <= TEXTURE_BUDGET) return;
    for (unsigned age = 600; age >= 30 && texture_bytes_ > TEXTURE_BUDGET * 7 / 8; age /= 2)
        for (size_t i = 0; i < textures_.size() && texture_bytes_ > TEXTURE_BUDGET * 7 / 8; ++i) {
            Tex& t = textures_[i];
            if (t.image && !t.key.empty() && frame_no_ - t.last_used > age) {
                destroy(t, false);
                ++evictions;
            }
        }
}

void Renderer::frame(const ch360_frame* f) {
    ++frame_no_;
    double t0 = now_ms();

    // Pass 1: which items meet their view (the glue worked out their boxes;
    // the scissor would clip the rest away: the map draws 17 MB of textures
    // wholly off screen), load what those draw, then run the uploads.
    Stage marker("vk: loading textures");
    on_view_.assign(f->item_count, 0);
    for (unsigned v = 0; v < f->view_count; ++v) {
        const ch360_view& view = f->views[v];
        float rx0 = view.x < 0 ? 0 : view.x, ry0 = view.y < 0 ? 0 : view.y;
        float rx1 = view.x + view.w > W ? W : view.x + view.w, ry1 = view.y + view.h > H ? H : view.y + view.h;
        unsigned end = view.first_item + view.items;
        for (unsigned i = view.first_item; i < end && i < f->item_count; ++i) {
            const ch360_item& it = f->items[i];
            bool on = it.count > 0 && (it.x0 > it.x1 || (it.x1 >= rx0 && it.x0 <= rx1 && it.y1 >= ry0 && it.y0 <= ry1));
            on_view_[i] = on ? 1 : 0;
            Tex* t = on && it.texture >= 0 ? tex(it.texture) : NULL;
            if (t) {
                load(*t);
                t->last_used = frame_no_;
            }
            for (int k = 0; on && it.mode == 6 && k < 2; ++k) {
                Tex* e = it.textures[k] >= 0 ? tex(it.textures[k]) : NULL;
                if (e) {
                    load(*e);
                    e->last_used = frame_no_;
                }
            }
        }
    }
    double t1 = now_ms();
    Frame& fr = frames_[frame_];
    g_stage = "vk: waiting for the frame's fence";
    vkWaitForFences(dev_, 1, &fr.fence, VK_TRUE, ~(uint64_t)0);
    double t2 = now_ms();
    gpu_wait_ms += t2 - t1;
    vkResetFences(dev_, 1, &fr.fence);
    for (size_t i = 0; i < fr.dead.size(); ++i) {
        vkFreeDescriptorSets(dev_, desc_pool_, 1, &fr.dead[i].set);
        vkDestroyImageView(dev_, fr.dead[i].view, NULL);
        vkDestroyImage(dev_, fr.dead[i].image, NULL);
        vkFreeMemory(dev_, fr.dead[i].mem, NULL);
    }
    fr.dead.clear();
    flush_uploads();
    double t3 = now_ms();

    uint32_t image = 0;
    g_stage = "vk: acquiring a swapchain image";
    VkResult r = vkAcquireNextImageKHR(dev_, swapchain_, ~(uint64_t)0, fr.acquired, VK_NULL_HANDLE, &image);
    if (r != VK_SUCCESS && r != VK_SUBOPTIMAL_KHR) {
        vk(r, "vkAcquireNextImageKHR");
        return;
    }
    double t4 = now_ms();
    g_stage = "vk: recording";
    VkCommandBuffer cmd = fr.cmd;
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
    // Unity's screen pixels (bottom-left origin) straight to clip space: y flips here.
    float pc[4] = { 2.0f / W, -2.0f / H, -1.0f, 1.0f };
    vkCmdPushConstants(cmd, pipe_layout_, VK_SHADER_STAGE_VERTEX_BIT, 0, sizeof(pc), pc);
    VkDeviceSize base = (VkDeviceSize)frame_ * VERTEX_RING;
    vkCmdBindVertexBuffers(cmd, 0, 1, &vbuf_, &base);
    ch360_vertex* ring = (ch360_vertex*)(vmap_ + base);
    size_t used = 0, cap = VERTEX_RING / sizeof(ch360_vertex);

    // Pass 2: record the views' draws, consecutive items of one pipeline and
    // texture batched into one draw; each item's vertices are copied as the
    // glue wrote them.
    VkPipeline bound = VK_NULL_HANDLE;
    VkDescriptorSet bound_set = VK_NULL_HANDLE;
    size_t batch_start = 0, batch_count = 0;
    unsigned draws = 0;
    bool repush = false;
    for (unsigned v = 0; v < f->view_count; ++v) {
        const ch360_view& view = f->views[v];
        if (batch_count) {
            vkCmdDraw(cmd, (uint32_t)batch_count, 1, (uint32_t)batch_start, 0);
            ++draws;
            batch_count = 0;
        }
        VkRect2D sc;
        int x0 = (int)view.x, y0 = H - (int)(view.y + view.h);
        sc.offset.x = x0 < 0 ? 0 : x0;
        sc.offset.y = y0 < 0 ? 0 : y0;
        sc.extent.width = (uint32_t)(view.w < 0 ? 0 : (view.w > W ? W : view.w));
        sc.extent.height = (uint32_t)(view.h < 0 ? 0 : (view.h > H ? H : view.h));
        vkCmdSetScissor(cmd, 0, 1, &sc);
        if (view.clear) {
            VkClearAttachment ca;
            memset(&ca, 0, sizeof(ca));
            ca.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
            ca.clearValue.color.float32[0] = view.r;
            ca.clearValue.color.float32[1] = view.g;
            ca.clearValue.color.float32[2] = view.b;
            ca.clearValue.color.float32[3] = 1.0f;
            VkClearRect cr;
            cr.rect = sc;
            cr.baseArrayLayer = 0;
            cr.layerCount = 1;
            vkCmdClearAttachments(cmd, 1, &ca, 1, &cr);
        }
        unsigned end = view.first_item + view.items;
        for (unsigned i = view.first_item; i < end && i < f->item_count; ++i) {
            const ch360_item& it = f->items[i];
            if (!on_view_[i]) continue;
            Tex* t = it.texture >= 0 ? tex(it.texture) : &white_;
            if (!t || !t->image) continue;
            unsigned mode = it.mode;
            int m = mode == 1 ? MODE_SCREEN : mode == 2 ? MODE_MULTIPLY : mode == 3 ? MODE_OVERLAY : mode == 5 ? MODE_SDF
                  : mode == 6 ? MODE_LIGHTRAY : MODE_SPRITE;
            if (used + it.count > cap) {
                if (!overflow_logged_) logf("vk: vertex ring full (%u vertices)", (unsigned)cap);
                overflow_logged_ = true;
                break;
            }
            if (m == MODE_LIGHTRAY) {
                // A draw of its own: three textures and its floats.
                if (batch_count) {
                    vkCmdDraw(cmd, (uint32_t)batch_count, 1, (uint32_t)batch_start, 0);
                    ++draws;
                    batch_count = 0;
                }
                Tex* e[2];
                for (int k = 0; k < 2; ++k) {
                    e[k] = it.textures[k] >= 0 ? tex(it.textures[k]) : NULL;
                    if (!e[k] || !e[k]->image) e[k] = &white_;
                }
                VkDescriptorSet sets[3] = { t->set, e[0]->set, e[1]->set };
                vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, pipes_[m]);
                vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, ray_layout_, 0, 3, sets, 0, NULL);
                vkCmdPushConstants(cmd, ray_layout_, VK_SHADER_STAGE_VERTEX_BIT, 0, sizeof(pc), pc);
                vkCmdPushConstants(cmd, ray_layout_, VK_SHADER_STAGE_FRAGMENT_BIT, 16, sizeof(it.params), it.params);
                g_memcpy(&ring[used], &f->vertices[it.first], it.count * sizeof(ch360_vertex));
                vkCmdDraw(cmd, it.count, 1, (uint32_t)used, 0);
                ++draws;
                used += it.count;
                bound = pipes_[m];
                bound_set = VK_NULL_HANDLE;
                repush = true;
                continue;
            }
            if (pipes_[m] != bound || t->set != bound_set) {
                if (batch_count) {
                    vkCmdDraw(cmd, (uint32_t)batch_count, 1, (uint32_t)batch_start, 0);
                    ++draws;
                }
                batch_start = used;
                batch_count = 0;
                if (pipes_[m] != bound) vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, pipes_[m]);
                if (repush) {   // after a light ray's layout
                    vkCmdPushConstants(cmd, pipe_layout_, VK_SHADER_STAGE_VERTEX_BIT, 0, sizeof(pc), pc);
                    repush = false;
                }
                if (t->set != bound_set)
                    vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, pipe_layout_, 0, 1, &t->set, 0, NULL);
                bound = pipes_[m];
                bound_set = t->set;
            }
            g_memcpy(&ring[used], &f->vertices[it.first], it.count * sizeof(ch360_vertex));
            used += it.count;
            batch_count += it.count;
        }
    }
    if (batch_count) {
        vkCmdDraw(cmd, (uint32_t)batch_count, 1, (uint32_t)batch_start, 0);
        ++draws;
    }
    vkCmdEndRenderPass(cmd);
    vkEndCommandBuffer(cmd);
    double t5 = now_ms();
    VkPipelineStageFlags stage = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    VkSubmitInfo si;
    memset(&si, 0, sizeof(si));
    si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    si.waitSemaphoreCount = 1;
    si.pWaitSemaphores = &fr.acquired;
    si.pWaitDstStageMask = &stage;
    si.commandBufferCount = 1;
    si.pCommandBuffers = &cmd;
    si.signalSemaphoreCount = 1;
    si.pSignalSemaphores = &fr.rendered;
    g_stage = "vk: submitting";
    vk(vkQueueSubmit(queue_, 1, &si, fr.fence), "vkQueueSubmit");
    double t6 = now_ms();
    VkPresentInfoKHR pi;
    memset(&pi, 0, sizeof(pi));
    pi.sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR;
    pi.waitSemaphoreCount = 1;
    pi.pWaitSemaphores = &fr.rendered;
    pi.swapchainCount = 1;
    pi.pSwapchains = &swapchain_;
    pi.pImageIndices = &image;
    g_stage = "vk: presenting";
    vk(vkQueuePresentKHR(queue_, &pi), "vkQueuePresentKHR");
    double t7 = now_ms();
    present_ms += t7 - t6;
    frame_ = (frame_ + 1) % FRAMES;
    g_stage = "vk: evicting";
    evict();
    g_texture_bytes = texture_bytes_;

    // Where the host's part of a frame goes, averaged over 300 frames (after
    // the player's FRAMES line): the glue writing the list, pass 1 with its
    // texture loads, the GPU fence, uploads, acquiring, recording (vertex
    // copies), vkQueueSubmit and present.
    host_ms_[0] += f->build_ms;
    host_ms_[1] += t1 - t0;
    host_ms_[2] += t2 - t1;
    host_ms_[3] += t3 - t2;
    host_ms_[4] += t4 - t3;
    host_ms_[5] += t5 - t4;
    host_ms_[6] += t6 - t5;
    host_ms_[7] += t7 - t6;
    host_draws_ += draws;
    host_vertices_ += used;
    host_items_ += f->item_count;
    if (frame_no_ % 300 == 0) {
        logf("HOST frames %u-%u: glue %.1f, boxes+loads %.1f, fence %.1f, uploads %.1f, acquire %.1f, record %.1f, submit %.1f, "
             "present %.1f ms/frame; %u draws, %u vertices, %u items a frame; %u items left on halved atlas pages so far",
             frame_no_ - 299, frame_no_, host_ms_[0] / 300, host_ms_[1] / 300, host_ms_[2] / 300, host_ms_[3] / 300,
             host_ms_[4] / 300, host_ms_[5] / 300, host_ms_[6] / 300, host_ms_[7] / 300, host_draws_ / 300,
             (unsigned)(host_vertices_ / 300), host_items_ / 300, ch360_remap_misses());
        memset(host_ms_, 0, sizeof(host_ms_));
        host_draws_ = host_items_ = 0;
        host_vertices_ = 0;
    }
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
            float l = g_ring[at], r = g_ring[at + 1];
            left[i] = l < -1.0f ? -1.0f : l > 1.0f ? 1.0f : l;
            right[i] = r < -1.0f ? -1.0f : r > 1.0f ? 1.0f : r;
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

extern "C" int ch360_probe64(void (*log)(const char* line), void (*sync)(void), const char* where, int detail);
static void probe_line(const char* line) { logf("%s", line); }
static void probe_sync() { flush_log(true); }

// The CPU probe on a thread of its own: the hardware thread to move to (or -1),
// created as the runtime creates the game's thread (pthread, its stack size).
struct ProbeThread { int hw; const char* where; };
static void* probe_thread(void* arg) {
    ProbeThread* p = (ProbeThread*)arg;
    if (p->hw >= 0) xsdk_thread_move((uint32_t)p->hw);
    ch360_probe64(probe_line, probe_sync, p->where, 0);
    return NULL;
}
static void probe_on_thread(int hw, size_t stack, const char* where) {
    static ProbeThread arg;
    arg.hw = hw;
    arg.where = where;
    pthread_attr_t attr;
    pthread_attr_init(&attr);
    if (stack) pthread_attr_setstacksize(&attr, stack);
    pthread_t th;
    if (pthread_create(&th, &attr, probe_thread, &arg) == 0) pthread_join(th, NULL);
    pthread_attr_destroy(&attr);
}

extern "C" int ch360_plat_frame_begin(void) {
    g_stage = "host: frame begin";
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
        // Times in tenths of a millisecond (integers only in the log).
        logf("frame %u: %u ms/frame x10 (worst %u, %u over 17.5); gpu wait %u, present %u; memory %u MB free of %u, player %u MB, "
             "textures %u MB (%u uploads, %u evicted); audio underruns %u",
             g_frames, (unsigned)(g_logic_ms / 60), (unsigned)(g_worst_ms * 10), g_slow, (unsigned)(g_renderer->gpu_wait_ms / 60),
             (unsigned)(g_renderer->present_ms / 60), f, tot, ch360_memory_kb() / 1024, (unsigned)(g_renderer->texture_bytes() >> 20),
             g_renderer->uploads, g_renderer->evictions, g_audio_underruns);
        g_logic_ms = g_worst_ms = 0;
        g_slow = 0;
        g_renderer->gpu_wait_ms = g_renderer->present_ms = 0;
    }
    ++g_frames;
    g_frames_seen = g_frames;
    if (g_frames <= 3 || g_frames == 10 || g_frames == 60 || g_frames == 300) logf("frame %u", g_frames);
    if (g_frames == 1) ch360_probe64(probe_line, probe_sync, "game thread, frame 1", 0);
    if (g_frames == 600) ch360_probe64(probe_line, probe_sync, "game thread, frame 600", 0);
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
    g_stage = "game";
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

extern "C" void ch360_plat_draw(const ch360_frame* f) {
    g_renderer->frame(f);
    g_stage = "game";
}

extern "C" void ch360_plat_texture_file(int id, const char* key) { g_renderer->texture_file(id, key); }

extern "C" void ch360_plat_texture_pixels(int id, int width, int height, const unsigned char* bgra) {
    Stage stage("vk: run-time texture");
    g_renderer->texture_pixels(id, width, height, bgra);
}

extern "C" void ch360_plat_audio(const unsigned char* s, int frames) {
    if (!g_audio_open) return;
    Stage stage("host: audio");
    pthread_mutex_lock(&g_audio_lock);
    // Little-endian floats into the ring as byte-swapped words: no trip
    // through the FPU (each one a load-hit-store stall on this CPU).
    for (int i = 0; i < frames; ++i, s += 8) {
        if (g_ring_write - g_ring_read >= AUDIO_RING) ++g_ring_read;   // full: drop the oldest
        unsigned at = (g_ring_write % AUDIO_RING) * 2;
        uint32_t l = (uint32_t)s[0] | ((uint32_t)s[1] << 8) | ((uint32_t)s[2] << 16) | ((uint32_t)s[3] << 24);
        uint32_t r = (uint32_t)s[4] | ((uint32_t)s[5] << 8) | ((uint32_t)s[6] << 16) | ((uint32_t)s[7] << 24);
        memcpy(&g_ring[at], &l, 4);
        memcpy(&g_ring[at + 1], &r, 4);
        ++g_ring_write;
    }
    pthread_mutex_unlock(&g_audio_lock);
}

extern "C" void ch360_plat_log(const char* text, size_t len) { log_text(text, len); }

// The wasm runtime's waits (poll_oneoff, wasi clocks) and sleeps, marked for
// the heartbeat; a sleep over two seconds is logged (in hex).
extern "C" void __real_srt_clock_wait_until(unsigned long long deadline_ns);
extern "C" void __wrap_srt_clock_wait_until(unsigned long long deadline_ns) {
    Stage stage("runtime: waiting for a deadline");
    __real_srt_clock_wait_until(deadline_ns);
}
extern "C" void __real_srt_plat_sleep_ns(unsigned long long ns);
extern "C" void __wrap_srt_plat_sleep_ns(unsigned long long ns) {
    if (ns > 2000000000ull) logf("runtime: sleep of 0x%08x%08x ns", (unsigned)(ns >> 32), (unsigned)ns);
    Stage stage("runtime: sleeping");
    __real_srt_plat_sleep_ns(ns);
}

// The runtime's file calls, counted and timed for the heartbeat.
extern "C" int __real_srt_plat_open(const char* path, int flags, void** out);
extern "C" int __wrap_srt_plat_open(const char* path, int flags, void** out) {
    Stage stage("runtime: opening a file");
    ++g_io_opens;
    double t0 = now_ms();
    int r = __real_srt_plat_open(path, flags, out);
    g_io_ms += now_ms() - t0;
    return r;
}
extern "C" int __real_srt_plat_read(void* f, void* buf, size_t len, size_t* done);
extern "C" int __wrap_srt_plat_read(void* f, void* buf, size_t len, size_t* done) {
    Stage stage("runtime: reading a file");
    ++g_io_reads;
    double t0 = now_ms();
    int r = __real_srt_plat_read(f, buf, len, done);
    g_io_ms += now_ms() - t0;
    g_io_bytes += *done;
    return r;
}
extern "C" int __real_srt_plat_seek(void* f, long long offset, int whence, unsigned long long* pos);
extern "C" int __wrap_srt_plat_seek(void* f, long long offset, int whence, unsigned long long* pos) {
    ++g_io_seeks;
    return __real_srt_plat_seek(f, offset, whence, pos);
}

// The player's linear memory grows by committing pages (physical memory:
// the console has no paging). Textures not drawn lately make room first,
// so the game gets memory the renderer could give back: in the sixth run
// the player reached 183 MB at the map (150 MB on PC) with textures at
// their 96 MB budget and 39 MB free.
extern "C" int __real_srt_plat_commit(void* base, unsigned long long from, unsigned long long to);
extern "C" int __wrap_srt_plat_commit(void* base, unsigned long long from, unsigned long long to) {
    if (g_renderer && to > from) {
        unsigned free_mb, total_mb;
        memory_mb(&free_mb, &total_mb);
        // Each growth, to find why the console's player outgrows the PC's.
        logf("memory: player %u -> %u MB at frame %u (%s); %u MB free, textures %u MB", (unsigned)(from >> 20),
             (unsigned)(to >> 20), g_frames_seen, (const char*)g_stage, free_mb, (unsigned)(g_texture_bytes >> 20));
        unsigned need_mb = (unsigned)((to - from) >> 20) + 1 + MEMORY_FLOOR_MB;
        if (free_mb < need_mb) {
            size_t freed = g_renderer->make_room((size_t)(need_mb - free_mb) << 20);
            logf("memory: the player grows to %u MB with %u MB free; %u KB of textures let go", (unsigned)(to >> 20), free_mb,
                 (unsigned)(freed >> 10));
        }
    }
    int r = __real_srt_plat_commit(base, from, to);
    if (r != 0 && g_renderer && g_renderer->make_room((size_t)(to - from) * 2) > 0) r = __real_srt_plat_commit(base, from, to);
    if (r != 0) logf("memory: the player couldn't grow to %u MB", (unsigned)(to >> 20));
    return r;
}


int main() {
    FILE* fresh = fopen("game:/cuphead360.txt", "wb");
    if (fresh) fclose(fresh);
    logf("Cuphead360 (xSDK) %s", CH360_GIT_COMMIT);
    unsigned f, t;
    memory_mb(&f, &t);
    logf("memory at start: %u MB free of %u MB", f, t);
    // The second to fourth runs: compares right here, wrong in the game (VERIFICATION rounds 9-10).
    ch360_probe64(probe_line, probe_sync, "main thread", 1);
    probe_on_thread(0, 16u << 20, "new thread like the game's (hw 0, 16 MB stack)");
    probe_on_thread(2, 0, "new thread on hw 2");
    flush_log(true);
    // The player's linear memory is reserved first (committed as it grows):
    // xsdk's heap takes the free address space at its first growth.
    static const char* const argv[] = { "cuphead360", "run", "--console", "1", "--data", "/GameData", "--save", "/save" };
    ch360_staged_names = 1;   // GameData/ctex and audio as stage_console.py writes them
    if (ch360_prepare("game:/", (int)(sizeof(argv) / sizeof(argv[0])), argv, 448) != 0) {
        logf("player set-up failed");
        flush_log();
        return 1;
    }
    memory_mb(&f, &t);
    logf("player module ready: %u MB free", f);
    {
        unsigned boxes, targets, pages = ch360_remap_counts(&boxes, &targets);
        logf("packs: textures %s; sprite pages for %u atlas pages (%u sprites, %u pages)",
             ch360_ctex_pack >= 0 ? "CTEX*.PAK" : "none (loose files)", pages, boxes, targets);
    }
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
    g_stage = "game";
    ch360_frame_drain = 0;   // the log thread drains the player's output instead
    g_player_started = true;
    int code = ch360_start(16, 0);
    memory_mb(&f, &t);
    logf("player exit %d after %u frames; memory %u MB free, player %u MB", code, g_frames, f, ch360_memory_kb() / 1024);
    flush_log();
    return 0;
}
