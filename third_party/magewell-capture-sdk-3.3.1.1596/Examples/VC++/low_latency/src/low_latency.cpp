/************************************************************************************************/
// low_latency.cpp : Defines the entry point for the console application.

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
#include "LibMWCapture\MWCapture.h"
#pragma comment(lib, "winmm.lib")
HCHANNEL					g_channel_handle = NULL;
MWCAP_VIDEO_SIGNAL_STATUS	g_video_signal_status;
#define CAPTURE_FOURCC MWFOURCC_YUY2
void	 show_info();
BOOL	 open_channel(int argc, char* argv[]);
BOOL	 check_signal();
BOOL	 create_buffer();
void	 destory_buffer();
void	 normal_capture();
void     latency_capture();
void	 close_channel();

int main(int argc, char* argv[])
{
    MWCaptureInitInstance();
    show_info();

    if (!open_channel(argc, argv)) {
        MWCaptureExitInstance();
        getchar();
        return 1;
    }

    if (!check_signal()) {
        close_channel();
        MWCaptureExitInstance();
        getchar();
        return 1;
    }
    if (!create_buffer()) {
        close_channel();
        MWCaptureExitInstance();
        getchar();
        return 1;
    }

    normal_capture();
    latency_capture();
    close_channel();
    destory_buffer();
    MWCaptureExitInstance();
    getchar();
    return 0;
}

//show the basic information of our capture card
void show_info()
{
    BYTE maj, min;
    WORD build;
    MWGetVersion(&maj, &min, &build);

    printf("Magewell MWCapture SDK %d.%d.1.%d - low_latency\n", maj, min, build);
    printf("just support pro device\n");
    printf("Usage:\n");
    printf("low_latency.exe <channel_index>\n");
}


BOOL open_channel(int argc, char* argv[]) 
{
    int index = 0;
    if (argc == 2) {
        index = atoi(argv[1]);
    }
    int total_device = MWGetChannelCount();
    if (total_device <= 0) {
        printf("not find magewell device or not install device\n");
        return false;
    }
    for (int i = 0; i < total_device; i++) {
        MWCAP_CHANNEL_INFO info;
        MW_RESULT mr = MWGetChannelInfoByIndex(i, &info);
        if ((MW_SUCCEEDED == mr) && (info.wFamilyID == MW_FAMILY_ID_PRO_CAPTURE)) {
            if (index) {
                index--;
                continue;
            }
            WCHAR path[128] = { 0 };
            MWGetDevicePath(i, path);
            g_channel_handle = MWOpenChannelByPath(path);
            if (NULL == g_channel_handle) {
                printf("open fail\n");
                return false;
            }
            return true;
        }
    }
    printf("index out of range\n");
    return false;
}

//check the input status and related information of the video signal
BOOL check_signal()
{
    MWGetVideoSignalStatus(g_channel_handle, &g_video_signal_status);
    switch (g_video_signal_status.state)
    {
    case MWCAP_VIDEO_SIGNAL_NONE:
        printf("Input signal status: NONE\n");
        break;
    case MWCAP_VIDEO_SIGNAL_UNSUPPORTED:
        printf("Input signal status: Unsupported\n");
        break;
    case MWCAP_VIDEO_SIGNAL_LOCKING:
        printf("Input signal status: Locking\n");
        break;
    case MWCAP_VIDEO_SIGNAL_LOCKED:
        printf("Input signal status: Locked\n");
        break;
    }
//     g_video_signal_status.cx = 1280;
//     g_video_signal_status.cy = 720;
    if (g_video_signal_status.state == MWCAP_VIDEO_SIGNAL_LOCKED){
        printf("Input signal resolution: %d x %d\n", g_video_signal_status.cx, g_video_signal_status.cy);
        double fps = (g_video_signal_status.bInterlaced == TRUE) ? (double)20000000LL / g_video_signal_status.dwFrameDuration : (double)10000000LL / g_video_signal_status.dwFrameDuration;
        printf("Input signal fps: %.2f\n", fps);
        printf("Input signal interlaced: %d\n", g_video_signal_status.bInterlaced);
        printf("Input signal frame segmented: %d\n", g_video_signal_status.bSegmentedFrame);
        return TRUE;
    }
    return FALSE;
}
#define FRAME_BUFFER_NUM 4
uint8_t *g_frame[FRAME_BUFFER_NUM] = { NULL, NULL, NULL, NULL };
uint32_t g_completed_line[FRAME_BUFFER_NUM] = { 0,0,0,0 };

LONGLONG g_buffer_start[FRAME_BUFFER_NUM] = { 0,0,0,0 };
LONGLONG g_buffer_end[FRAME_BUFFER_NUM] = { 0,0,0,0 };
LONGLONG g_capture_start[FRAME_BUFFER_NUM] = { 0,0,0,0 };
LONGLONG g_capture_end[FRAME_BUFFER_NUM] = { 0,0,0,0 };

int32_t g_write_num = 0;
int32_t g_read_num = 0;
BOOL create_buffer()
{
    DWORD stride = FOURCC_CalcMinStride(CAPTURE_FOURCC, g_video_signal_status.cx, 2);
    DWORD frame_size = FOURCC_CalcImageSize(CAPTURE_FOURCC, g_video_signal_status.cx, g_video_signal_status.cy, stride);
    for (int i = 0; i < FRAME_BUFFER_NUM; i++) {
        g_frame[i] = (uint8_t*)malloc(frame_size);
        if (NULL == g_frame[i]) {
            printf("malloc frame %d fail\n",i);
            return false;
        }
    }
    return true;
}
void destory_buffer()
{
    for (int i = 0; i < FRAME_BUFFER_NUM; i++) {
        if (NULL == g_frame[i]) {
            return;
        }
        free(g_frame[i]);
    }
}

DWORD WINAPI process_frame(LPVOID pM)//add
{
    DWORD stride = FOURCC_CalcMinStride(CAPTURE_FOURCC, g_video_signal_status.cx, 2);
    DWORD frame_size = FOURCC_CalcImageSize(CAPTURE_FOURCC, g_video_signal_status.cx, g_video_signal_status.cy, stride);
    
    uint8_t *p_process_runing = (uint8_t*)pM;
    LONGLONG buffer_time = 0;
    LONGLONG capture_time = 0;
    LONGLONG process_time = 0;
    LONGLONG capture_start = 0;
    LONGLONG process_start = 0;
    uint32_t process_frame_num = 0;
	timeBeginPeriod(1);
    while (*p_process_runing) {
        uint32_t read_index = g_read_num % FRAME_BUFFER_NUM;
        if ((g_write_num < g_read_num) || (g_completed_line[read_index] == 0)) {
            Sleep(1);
            continue;
        }
        uint32_t process_line = 0;
        uint8_t *p_frame = g_frame[read_index];
        uint64_t total = 0;
        LONGLONG process_begin = 0;
        
        MWGetDeviceTime(g_channel_handle, &process_begin);
        while (process_line < g_video_signal_status.cy) {
            if (process_line == g_completed_line[read_index]) {
                Sleep(1);
                continue;
            }
            process_line = g_completed_line[read_index];
            uint8_t *p_process_end = g_frame[read_index] + process_line*stride;
            while (p_frame <= p_process_end){
                total += *p_frame;
                p_frame++;
            }
        }
        LONGLONG process_end = 0;
        MWGetDeviceTime(g_channel_handle, &process_end);
        process_frame_num++;
        
        g_read_num++;
        buffer_time += g_buffer_end[read_index] - g_buffer_start[read_index];
        capture_time += g_capture_end[read_index] - g_capture_start[read_index];
        process_time += process_end - process_begin;
        capture_start += g_capture_start[read_index] - g_buffer_start[read_index];
        process_start += process_begin - g_buffer_start[read_index];
    }
	timeEndPeriod(1);
    if (process_frame_num) {
        buffer_time /= process_frame_num*10000;
        capture_time /= process_frame_num * 10000;
        process_time /= process_frame_num * 10000;
        capture_start /= process_frame_num * 10000;
        process_start /= process_frame_num * 10000;
        capture_time += 1;
        process_time += 1;
        if ((process_start + process_time) > 80 || (capture_start+ capture_time) > 80 || buffer_time > 80) {
            printf("error\n");
            return 1;
        }
        char buffer[256];
        char capture[256];
        char process[256];
        memset(buffer, 0x20, 256);
        memset(capture, 0x20, 256);
        memset(process, 0x20, 256);
        int i = 0;
        for (i = 0; i < buffer_time;i++) {
            buffer[i] = '-';
        }
        buffer[i] = '|';
        buffer[process_start + process_time+1] = 0;
        for (i = 0; i < capture_start; i++) {
            capture[i] = ' ';
        }
        for (i; i < capture_time + capture_start; i++) {
            capture[i] = '-';
        }
        capture[i] = '|';
        capture[process_start + process_time+1] = 0;

        for (i = 0; i < process_start; i++) {
            process[i] = ' ';
        }
        for (i; i < process_time + process_start; i++) {
            process[i] = '-';
        }
        process[i] = 0;
        printf("buffer :|%sstart(0)+buffer(%lld)->%lldms\n", buffer, buffer_time, buffer_time);
        printf("capture:|%sstart(%lld)+capture(%lld)->%lldms\n", capture, capture_start, capture_time, capture_start+ capture_time);
        printf("process:|%s|start(%lld)+process(%lld)->%lldms\n", process, process_start, process_time, process_start+ process_time);
    }
    return 1;
}
void normal_capture()
{
    uint8_t process_runing = 1;
    uint8_t is_start_capture = 0;
    HANDLE notify_event = NULL;
    HNOTIFY notify = NULL;
    HANDLE thread_handle = CreateThread(NULL, 0, process_frame, &process_runing, 0, NULL);
    HANDLE capture_event = CreateEvent(NULL, FALSE, FALSE, NULL);
    if (MWStartVideoCapture(g_channel_handle, capture_event) != MW_SUCCEEDED) {
        printf("Open Video Capture error!\n");
        goto end_and_free;
    }
    is_start_capture = 1;
    MWCAP_VIDEO_BUFFER_INFO video_buffer_info;
    MWCAP_VIDEO_FRAME_INFO video_frame_info;

    notify_event = CreateEvent(NULL, FALSE, FALSE, NULL);
    notify = MWRegisterNotify(g_channel_handle, notify_event, MWCAP_NOTIFY_VIDEO_FRAME_BUFFERED);
    if (notify == NULL) {
        printf("register notify error\n");
        goto end_and_free;
    }

    int cx = g_video_signal_status.cx;
    int cy = g_video_signal_status.cy;
    DWORD stride = FOURCC_CalcMinStride(CAPTURE_FOURCC, cx, 2);
    DWORD frame_size = FOURCC_CalcImageSize(CAPTURE_FOURCC, cx, cy, stride);
    for (int i = 0; i < FRAME_BUFFER_NUM; i++) {
        MWPinVideoBuffer(g_channel_handle, g_frame[i], frame_size);
    }

    while(1){
        WaitForSingleObject(notify_event, INFINITE);

        ULONGLONG notify_status = 0;
        if (MWGetNotifyStatus(g_channel_handle, notify, &notify_status) != MW_SUCCEEDED){
            continue;
        }
        if (!(notify_status & MWCAP_NOTIFY_VIDEO_FRAME_BUFFERED)) {
            continue;
        }
        if (MWGetVideoBufferInfo(g_channel_handle, &video_buffer_info) != MW_SUCCEEDED) {
            continue;
        }

        if (MWGetVideoFrameInfo(g_channel_handle, video_buffer_info.iNewestBuffered, &video_frame_info) != MW_SUCCEEDED){
            continue;
		}

        uint32_t write_index = g_write_num % FRAME_BUFFER_NUM;
        MWGetDeviceTime(g_channel_handle, &g_capture_start[write_index]);
        g_completed_line[write_index] = 0;
        MWCaptureVideoFrameToVirtualAddressEx(g_channel_handle, MWCAP_VIDEO_FRAME_ID_NEWEST_BUFFERED, (LPBYTE)g_frame[write_index],
            frame_size, stride, 1, NULL, CAPTURE_FOURCC, cx, cy, 0, 0, NULL, NULL, 0, 100, 0, 100, 0, MWCAP_VIDEO_DEINTERLACE_WEAVE,
            MWCAP_VIDEO_ASPECT_RATIO_IGNORE,//
            NULL, NULL,
            0, 0, MWCAP_VIDEO_COLOR_FORMAT_UNKNOWN, MWCAP_VIDEO_QUANTIZATION_UNKNOWN, MWCAP_VIDEO_SATURATION_UNKNOWN
        );
        WaitForSingleObject(capture_event, INFINITE);
        
        g_buffer_start[write_index] = video_frame_info.allFieldStartTimes[0];
        g_buffer_end[write_index] = g_video_signal_status.bInterlaced ? video_frame_info.allFieldBufferedTimes[1] : video_frame_info.allFieldBufferedTimes[0];
        MWGetDeviceTime(g_channel_handle, &g_capture_end[write_index]);
        g_completed_line[write_index] = cy;

        if ((g_write_num - g_read_num) < (FRAME_BUFFER_NUM - 1)) {
            g_completed_line[(g_write_num + 1) % FRAME_BUFFER_NUM] = 0;
            g_write_num++;
        }
        else {
            g_completed_line[write_index] = 0;
        }
        if (g_write_num > 50) {
            break;
        }
        MWCAP_VIDEO_CAPTURE_STATUS capture_status;
        MWGetVideoCaptureStatus(g_channel_handle, &capture_status);
    }
    for (int i = 0; i < FRAME_BUFFER_NUM; i++) {
        MWUnpinVideoBuffer(g_channel_handle, (LPBYTE)g_frame[i]);
    }
    
    printf("normal mode\n");

end_and_free:
    if (thread_handle) {
        process_runing = 0;
        WaitForSingleObject(thread_handle, INFINITE);
    }
    if (is_start_capture) {
        MWStopVideoCapture(g_channel_handle);
    }
    if (notify_event) {
        CloseHandle(notify_event);
    }
    if (capture_event) {
        CloseHandle(capture_event);
    }
    if (notify) {
        MWUnregisterNotify(g_channel_handle, notify);
    }
    g_write_num = 0;
    g_read_num = 0;
    g_completed_line[0] = 0;
}
void latency_capture() 
{
    uint8_t process_runing = 1;
    uint8_t is_start_capture = 0;
    HANDLE notify_event = NULL;
    HNOTIFY notify = NULL;
    HANDLE thread_handle = CreateThread(NULL, 0, process_frame, &process_runing, 0, NULL);
    HANDLE capture_event = CreateEvent(NULL, FALSE, FALSE, NULL);
    if (MWStartVideoCapture(g_channel_handle, capture_event) != MW_SUCCEEDED) {
        printf("Open Video Capture error!\n");
        goto end_and_free;
    }
    is_start_capture = 1;
    MWCAP_VIDEO_BUFFER_INFO video_buffer_info;
    MWGetVideoBufferInfo(g_channel_handle, &video_buffer_info);

    MWCAP_VIDEO_FRAME_INFO video_frame_info;
    MWGetVideoFrameInfo(g_channel_handle, video_buffer_info.iNewestBufferedFullFrame, &video_frame_info);

    notify_event = CreateEvent(NULL, FALSE, FALSE, NULL);
    notify = MWRegisterNotify(g_channel_handle, notify_event, MWCAP_NOTIFY_VIDEO_FRAME_BUFFERING);
    if (notify == NULL) {
        printf("register notify error\n");
        goto end_and_free;
    }

    int cx = g_video_signal_status.cx;
    int cy = g_video_signal_status.cy;
    DWORD stride = FOURCC_CalcMinStride(CAPTURE_FOURCC, cx, 2);
    DWORD frame_size = FOURCC_CalcImageSize(CAPTURE_FOURCC, cx, cy, stride);
    for (int i = 0; i < FRAME_BUFFER_NUM; i++) {
        MWPinVideoBuffer(g_channel_handle, g_frame[i], frame_size);
    }

    while (1) {
        WaitForSingleObject(notify_event, INFINITE);

        ULONGLONG notify_status = 0;
        if (MWGetNotifyStatus(g_channel_handle, notify, &notify_status) != MW_SUCCEEDED) {
            continue;
        }

        if (MWGetVideoBufferInfo(g_channel_handle, &video_buffer_info) != MW_SUCCEEDED) {
            continue;
        }

        if (!(notify_status & MWCAP_NOTIFY_VIDEO_FRAME_BUFFERING)) {
            continue;
        }
        uint32_t write_index = g_write_num % FRAME_BUFFER_NUM;
        
        g_completed_line[write_index] = 0;
        MWCaptureVideoFrameToVirtualAddressEx(g_channel_handle, video_buffer_info.iNewestBuffering, (LPBYTE)g_frame[write_index],
            frame_size, stride, FALSE, NULL, CAPTURE_FOURCC, cx, cy, 0, 64, NULL, NULL, 0, 100, 0, 100, 0, MWCAP_VIDEO_DEINTERLACE_WEAVE,
            MWCAP_VIDEO_ASPECT_RATIO_IGNORE,//
            NULL, NULL,
            0, 0, MWCAP_VIDEO_COLOR_FORMAT_UNKNOWN, MWCAP_VIDEO_QUANTIZATION_UNKNOWN, MWCAP_VIDEO_SATURATION_UNKNOWN
        );
        do {
            WaitForSingleObject(capture_event, INFINITE);
            MWCAP_VIDEO_CAPTURE_STATUS capture_status;
            if (MWGetVideoCaptureStatus(g_channel_handle, &capture_status) != MW_SUCCEEDED) {
                break;
            }
            if (0 == g_completed_line[write_index]) {
                MWGetDeviceTime(g_channel_handle, &g_capture_start[write_index]);
            } 
            g_completed_line[write_index] = capture_status.cyCompleted;
            if (capture_status.bFrameCompleted){
                MWGetVideoFrameInfo(g_channel_handle, video_buffer_info.iNewestBuffering, &video_frame_info);
                g_buffer_start[write_index] = video_frame_info.allFieldStartTimes[0];
                g_buffer_end[write_index] = g_video_signal_status.bInterlaced ? video_frame_info.allFieldBufferedTimes[1] : video_frame_info.allFieldBufferedTimes[0];
                MWGetDeviceTime(g_channel_handle, &g_capture_end[write_index]);
                g_completed_line[write_index] = cy;
                break;
            }
        } while (1);

        if ((g_write_num - g_read_num) < (FRAME_BUFFER_NUM - 1)) {
            g_completed_line[(g_write_num+1) % FRAME_BUFFER_NUM] = 0;
            g_write_num++; 
        }
        else {
            g_completed_line[write_index] = 0;
        }
        if (g_write_num > 50) {
            break;
        }
        MWCAP_VIDEO_CAPTURE_STATUS capture_status;
        MWGetVideoCaptureStatus(g_channel_handle, &capture_status);
    }
    for (int i = 0; i < FRAME_BUFFER_NUM; i++) {
        MWUnpinVideoBuffer(g_channel_handle, (LPBYTE)g_frame[i]);
    }
    printf("low latency mode\n");
end_and_free:
    if (thread_handle) {
        process_runing = 0;
        WaitForSingleObject(thread_handle, INFINITE);
    }
    if (is_start_capture) {
        MWStopVideoCapture(g_channel_handle);
    }
    if (notify_event) {
        CloseHandle(notify_event);
    }
    if (capture_event) {
        CloseHandle(capture_event);
    }
    if (notify) {
        MWUnregisterNotify(g_channel_handle, notify);
    }
}
void close_channel()
{
    if (g_channel_handle != NULL) {
        MWCloseChannel(g_channel_handle);
        g_channel_handle = NULL;
    }
}
