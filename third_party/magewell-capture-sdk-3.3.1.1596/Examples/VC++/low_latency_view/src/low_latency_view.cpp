/************************************************************************************************/
// scale_capture.cpp : Defines the entry point for the console application.

// MAGEWELL PROPRIETARY INFORMATION

// The following license only applies to head files and library within Magewell’s SDK 
// and not to Magewell’s SDK as a whole. 

// Copyrights © Nanjing Magewell Electronics Co., Ltd. (“Magewell”) All rights reserved.

// Magewell grands to any person who obtains the copy of Magewell’s head files and library 
// the rights,including without limitation, to use, modify, publish, sublicense, distribute
// the Software on the conditions that all the following terms are met:
// - The above copyright notice shall be retained in any circumstances.
// -The following disclaimer shall be included in the software and documentation and/or 
// other materials provided for the purpose of publish, distribution or sublicense.

// THE SOFTWARE IS PROVIDED BY MAGEWELL “AS IS” AND ANY EXPRESS, INCLUDING BUT NOT LIMITED TO,
// THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.
// IN NO EVENT SHALL MAGEWELL BE LIABLE 

// FOR ANY CLAIM, DIRECT OR INDIRECT DAMAGES OR OTHER LIABILITY, WHETHER IN CONTRACT,
// TORT OR OTHERWISE, ARISING IN ANY WAY OF USING THE SOFTWARE.

// CONTACT INFORMATION:
// SDK@magewell.net
// http://www.magewell.com/
//
/************************************************************************************************/
#include <stdio.h>

#include "imgui.h"
#include "imgui_glfw/imgui_impl_glfw.h"
#include "imgui_opengl3/imgui_impl_opengl3.h"
#include "opengl_render/opengl_render.h"
#include "low_latency_capture.h"
// using gl3w to load opengl functions
#include "GL/glew.h"

// include glfw3.h after opengl definitions
#include "GLFW/glfw3.h"

#define GLFW_EXPOSE_NATIVE_WIN32
#include "GLFW/glfw3native.h"

#define MAX_CAPTURE_DEVICE 32
#include "dsound_player/dsound_player.h"

GLFWwindow* g_p_window = NULL;
CLowLatencyCapture *g_p_capture = NULL;
MWOpenGLRender *g_p_video_render = NULL;
char g_device_name[MAX_CAPTURE_DEVICE][128];
int g_device_index[MAX_CAPTURE_DEVICE];
int g_device_num = 0;
int g_render_count = 0;
unsigned char *g_p_render_frame = NULL;
char g_low_latency_info[1024] = {0};
HANDLE g_capture_thread_handle = NULL;
bool g_capturing = NULL;
bool create_windows();

bool imgui_init();
bool imgui_ui();
bool imgui_render();
void imgui_deinit();

void list_device();
void start_capture(int index, bool low_latency);
void stop_capture();

int main(int argc, char* argv[])
{
    unsigned char *p_prev_render_frame = NULL;
    CLowLatencyCapture::init();
    create_windows();
    imgui_init();
    mw_render_ctrl_t ctrl = {0};
    while (!glfwWindowShouldClose(g_p_window)) {
        imgui_ui();
        glfwGetFramebufferSize(g_p_window, (int*)&ctrl.display_w, (int *)&ctrl.display_h);

        if (g_capturing) {
            int no_data_times = 0;
            while (1) {
                if (g_p_render_frame && (p_prev_render_frame != g_p_render_frame)) {
                    p_prev_render_frame = g_p_render_frame;
                    g_p_video_render->render(g_p_render_frame, &ctrl);
                    g_render_count++;
                    break;
                }
                else {
                    Sleep(1);
                    no_data_times++;
                }
                if (no_data_times > 500) {
                    glClearColor(0.2, 0.3, 0.3, 1.0);
                    stop_capture();
                    break;
                }
            }
        }else {
            glClearColor(0.2, 0.3, 0.3, 1.0);
        }
        imgui_render();
        glfwSwapBuffers(g_p_window);
        glfwPollEvents();
    }
    if (g_p_video_render) {
        delete g_p_video_render;
    }
    if (g_p_capture) {
        delete g_p_capture;
    }
    CLowLatencyCapture::exit();
    imgui_deinit();
    glfwDestroyWindow(g_p_window);
    glfwTerminate();
    return 0;
}
static void glfw_error_callback(int error, const char* des)
{
    printf("glfw error %d: %s\n", error, des);
}
bool create_windows()
{
    glfwSetErrorCallback(glfw_error_callback);
    glfwInit();
    glfwWindowHint(GLFW_CONTEXT_VERSION_MAJOR, 3);
    glfwWindowHint(GLFW_CONTEXT_VERSION_MINOR, 0);
    g_p_window = glfwCreateWindow(960, 540, "low_latency_view", NULL, NULL);
    //HWND handle = glfwGetWin32Window(g_p_window);
    glfwMakeContextCurrent(g_p_window);
    glfwSwapInterval(1);
    glewInit();
    glClearColor(0.2, 0.3, 0.3, 1.0);
    return false;
}
bool imgui_init()
{
    IMGUI_CHECKVERSION();
    ImGuiContext* m_p_imgui_ctx = ImGui::CreateContext();
    ImGui::StyleColorsDark();

    const char* t_cs_glsl_version = "#version 130";
    ImGui_ImplOpenGL3_Init(t_cs_glsl_version);
    ImGui_ImplGlfw_InitForOpenGL(g_p_window, true);
    return false;
}

void imgui_deinit()
{
    ImGui_ImplOpenGL3_Shutdown();
    ImGui::DestroyContext();
}
void list_device()
{
    int capture_num;
    MWCAP_CHANNEL_INFO mci;
    CLowLatencyCapture::refresh_devices();
    g_device_num = 0;
    capture_num = CLowLatencyCapture::get_channel_count();
    for (int i = 0; i < capture_num; i++) {
        CLowLatencyCapture::get_channel_info_by_index(i, &mci);
        if (mci.wFamilyID != MW_FAMILY_ID_PRO_CAPTURE) {
            continue;
        }
        sprintf(g_device_name[g_device_num], "%d %s", i, mci.szProductName);
        g_device_index[g_device_num] = i;
        g_device_num++;
        if (g_device_num >= MAX_CAPTURE_DEVICE) {
            printf("too many capture, just list %d\n", MAX_CAPTURE_DEVICE);
            break;
        }
    }
}
DWORD WINAPI capture_thread(LPVOID p_param) 
{
    while (g_capturing) {
        uint32_t buffer_time = 0;
        uint32_t capture_start_time = 0;
        uint32_t capture_end_time = 0;
        g_p_render_frame = g_p_capture->capture_frame(&buffer_time, &capture_start_time, &capture_end_time);

        char buffer_info[64];
        char start_info[64];
        char end_info[64];
		char space_info[64];
        int buffer_num = buffer_time / 1000 + 1;
        int start_num = capture_start_time / 1000 + 1;
        int end_num = (capture_end_time- capture_start_time) / 1000 + 1;
        if ((buffer_num >= 20) || (end_num >= 20) || start_num >= 20) {
            continue;
        }
        int i;
        memset(buffer_info, ' ', 64);
        for (i = 0; i < buffer_num;i++) {
            buffer_info[i] = '-';
        }
        buffer_info[i] = '|';
        //buffer_info[start_num + end_num + 2] = 0;
		buffer_info[40] = 0;
        for (i = 0; i < start_num; i++) {
            start_info[i] = ' ';
        }
        start_info[i] = 0;
        for (i = 0; i < end_num; i++) {
            end_info[i] = '-';
        }
        end_info[i] = 0;
		int space_len = 40 - (start_num + end_num + 2);
		memset(space_info,' ',64);
		space_info[space_len] = 0;
        sprintf(g_low_latency_info,"buffer :|%sstart(0)+buffer(%5d)->%dus\n\capture:|%s|%s|%sstart(%5d)+capture(%5d)->%5dus", 
            buffer_info, buffer_time, buffer_time, 
            start_info, end_info, space_info, capture_start_time, capture_end_time - capture_start_time, capture_end_time);
    }
    return 1;
}
void start_capture(int index, bool low_latency)
{
    static int render_w = 0;
    static int render_h = 0;
	static DWORD render_fourcc;
    g_p_capture = new CLowLatencyCapture();// mw_capture_create(index);
    if (NULL == g_p_capture) {
        return;
    }
    if (!g_p_capture->set_device(index)) {
        return;
    }
    if(!g_p_capture->start_capture(low_latency))
    {
        return;
    }
    if (NULL == g_p_video_render) {
        g_p_video_render = new MWOpenGLRender();
    }
    int capture_width;
    int capture_height;
    int capture_sample_rate;
    int capture_channel_num;
	DWORD capture_fourcc;
    g_p_capture->get_resolution(&capture_width, &capture_height);
	g_p_capture->get_mw_fourcc(&capture_fourcc);
    if (g_p_video_render && ((render_w != capture_width) || (render_h != capture_height) ||(render_fourcc != capture_fourcc))) {
        mw_render_init_t rinit = { 0 };
        rinit.width = capture_width;
        rinit.height = capture_height;
        rinit.render_fourcc = capture_fourcc;
        g_p_video_render->open(&rinit);
        render_w = capture_width;
        render_h = capture_height;
		render_fourcc = capture_fourcc;
    }
    g_capturing = true;
    g_capture_thread_handle = CreateThread(NULL, 0, capture_thread, NULL, 0, NULL);
}
void stop_capture()
{
    if (g_capture_thread_handle) {
        g_capturing = false;
        WaitForSingleObject(g_capture_thread_handle, INFINITE);
        g_capture_thread_handle = NULL;
    }
    g_p_render_frame = NULL;
    g_low_latency_info[0] = 0;
    if (g_p_capture) {
        delete g_p_capture;
    }
    g_p_capture = NULL;
}
float get_render_fps()
{
    static float fps = 0.0;
    static int prev_render_count = 0;
    static int prev_time = 0;
    long long now_time = GetTickCount();
    int diff = now_time - prev_time;
    if (diff <= 1000) {
        return fps;
    }
    fps = (g_render_count - prev_render_count)*1000.0 / diff;
    prev_time = now_time;
    prev_render_count = g_render_count;
    return fps;
}
void imgui_draw_fps()
{
    bool m_b_open = true;
    ImGuiIO& io = ImGui::GetIO();
    ImVec2 pos;
    ImVec2 size;
    size.x = 0;
    size.y = 0;
    ImGuiWindowFlags m_imgui_flags = ImGuiWindowFlags_NoMove |
        ImGuiWindowFlags_NoTitleBar |
        ImGuiWindowFlags_NoResize |
        ImGuiWindowFlags_NoScrollbar |
        ImGuiWindowFlags_NoCollapse |
        ImGuiWindowFlags_AlwaysAutoResize |
        ImGuiWindowFlags_NoSavedSettings |
        ImGuiWindowFlags_NoFocusOnAppearing |
        ImGuiWindowFlags_NoNav;
    pos.x = io.DisplaySize.x - 260;// -size.x - 10;
    pos.y = io.DisplaySize.y - 30;// -size.y - 10;
    ImGui::SetNextWindowPos(pos, ImGuiCond_Always);
    ImGui::SetNextWindowSize(size, ImGuiCond_Always);
    ImGui::SetNextWindowBgAlpha(0.5);
    ImGui::Begin("status", &m_b_open, m_imgui_flags);
    ImGui::Text("capture fps:%0.2f|render fps:%0.2f", g_p_capture ? g_p_capture->get_capture_fps():0.0, get_render_fps());
    ImGui::End();
    pos.x = 0;// -size.x - 10;
    pos.y = io.DisplaySize.y - 40;// -size.y - 10;
    ImGui::SetNextWindowPos(pos, ImGuiCond_Always);
    ImGui::SetNextWindowSize(size, ImGuiCond_Always);
    ImGui::SetNextWindowBgAlpha(0.5);
    ImGui::Begin("low", &m_b_open, m_imgui_flags);
    ImGui::Text("%s", g_low_latency_info);
    ImGui::End();
}
bool imgui_ui()
{
    static int capture_mode = 0;
    static int choose_device_index = 0;
    static bool open_device = false;
    bool in_device = false;
    glClear(GL_COLOR_BUFFER_BIT);
    ImGui_ImplOpenGL3_NewFrame();
    ImGui_ImplGlfw_NewFrame();
    ImGui::NewFrame();
    static bool choose[MAX_CAPTURE_DEVICE] = { NULL };
    if (ImGui::BeginMainMenuBar()) {
        if (ImGui::BeginMenu("Devices")) {
            if (!open_device) {
                list_device();
                open_device = true;
            }
            if (NULL == g_p_capture) {
                memset(choose, 0, sizeof(choose));
            }
            in_device = true;
            for (int i = 0; i < g_device_num; i++) {
                if (ImGui::MenuItem(g_device_name[i], NULL, &choose[i])) {
                    if (choose[i]) {
                        memset(choose, 0, sizeof(choose));
                        choose[i] = true;
                        stop_capture();
                        choose_device_index = g_device_index[i];
                        start_capture(g_device_index[i], capture_mode);
                    }
                    else {
                        stop_capture();
                    }
                }
            }
            ImGui::EndMenu();
        }
        int prev_capture_mode = capture_mode;
        ImGui::RadioButton("normal", &capture_mode, 0);
        ImGui::RadioButton("low_latency", &capture_mode, 1);
        if ((prev_capture_mode != capture_mode) && g_p_capture) {
            stop_capture();
            start_capture(choose_device_index, capture_mode);
        }
        ImGui::EndMainMenuBar();
    }
    imgui_draw_fps();
    if (!in_device && open_device) {
        open_device = false;
    }
    return false;
}

bool imgui_render()
{
    ImGui::Render();
    ImGui_ImplOpenGL3_RenderDrawData(ImGui::GetDrawData());
    return false;
}
