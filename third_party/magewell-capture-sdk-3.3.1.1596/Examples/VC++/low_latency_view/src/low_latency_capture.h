#pragma once
#include "LibMWCapture\MWCapture.h"
class CLowLatencyCapture {
public:
    CLowLatencyCapture();
    virtual ~CLowLatencyCapture();

    static void init();
    static void exit();
    static bool refresh_devices();
    static int get_channel_count();
    static bool get_channel_info_by_index(int index, MWCAP_CHANNEL_INFO *p_channel_info);

    bool set_resolution(int width, int height);
    bool set_mw_fourcc(DWORD mw_fourcc);
    bool set_device(int magewell_device_index);
    bool start_capture(bool low_latency);
    unsigned char* capture_frame(uint32_t *p_buffer_time, uint32_t *p_capture_start_time, uint32_t *p_capture_end_time);

    float get_capture_fps();
    bool get_resolution(int *p_width, int *p_height);
    bool get_mw_fourcc(DWORD *p_mw_fourcc);
protected:
    int								m_width;
    int								m_height;
    bool                            m_low_latency;
    DWORD                           m_mw_fourcc;

    unsigned char*                  m_p_frame_buffer[16];
    int                             m_frame_buffer_num;
    int                             m_frame_size;
    int                             m_frame_stride;

    HCHANNEL                        m_channel_handle;
    HANDLE                          m_capture_event;
    HANDLE                          m_notify_event;
    HNOTIFY                         m_notify;
    bool                            m_have_signal;

    int                             m_prev_frame_count;
    long long                       m_prev_time;
    float                           m_fps;
    int                             m_capture_frame_count;
};