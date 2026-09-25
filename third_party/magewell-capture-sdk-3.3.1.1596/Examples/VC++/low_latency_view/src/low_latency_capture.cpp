#include <stdio.h>
#include "low_latency_capture.h"

CLowLatencyCapture::CLowLatencyCapture()
{

    m_width = 0;
    m_height = 0;
    m_low_latency = false;
    m_mw_fourcc = MWFOURCC_YUY2;
    m_frame_buffer_num = 0;
    m_frame_size = 0;
    m_frame_stride = 0;

    m_channel_handle = NULL;
    m_capture_event = NULL;
    m_notify_event = NULL;
    m_notify = NULL;
    m_have_signal = true;

    m_prev_frame_count = 0;
    m_prev_time = 0;
    m_fps = 0;
    m_capture_frame_count = 0;
}

CLowLatencyCapture::~CLowLatencyCapture()
{
    if (m_notify) {
        MWUnregisterNotify(m_channel_handle, m_notify);
        m_notify = NULL;
    }
    MWStopVideoCapture(m_channel_handle);
    if (m_notify_event) {
        CloseHandle(m_notify_event);
        m_notify_event = NULL;
    }
    if (m_capture_event) {
        CloseHandle(m_capture_event);
        m_capture_event = NULL;
    }
    if (NULL == m_channel_handle) {
        MWCloseChannel(m_channel_handle);
        m_channel_handle = NULL;
    }
    for (int i = 0; i < m_frame_buffer_num; i++) {
        free(m_p_frame_buffer[i]);
    }
    m_frame_buffer_num = 0;
}

void CLowLatencyCapture::init()
{
    MWCaptureInitInstance();
}

void CLowLatencyCapture::exit()
{
    MWCaptureExitInstance();
}

bool CLowLatencyCapture::refresh_devices()
{
    if (MWRefreshDevice() != MW_SUCCEEDED) {
        return false;
    }
    return true;
}

int CLowLatencyCapture::get_channel_count()
{
    return MWGetChannelCount();
}

bool CLowLatencyCapture::get_channel_info_by_index(int index, MWCAP_CHANNEL_INFO *p_channel_info)
{
    if (MW_SUCCEEDED != MWGetChannelInfoByIndex(index, p_channel_info)) {
        return false;
    }
    return true;
}

bool CLowLatencyCapture::set_device(int magewell_device_index)
{
    MWCAP_CHANNEL_INFO channel_info;
    if (MW_SUCCEEDED != MWGetChannelInfoByIndex(magewell_device_index, &channel_info)) {
        return false;
    }
    if (MW_FAMILY_ID_PRO_CAPTURE != channel_info.wFamilyID) {
        printf("the device is not pro device\n");
        return false;
    }
    WCHAR path[128] = { 0 };
    MWGetDevicePath(magewell_device_index, path);
    m_channel_handle = MWOpenChannelByPath(path);
    if (NULL == m_channel_handle) {
        printf("open fail\n");
        return false;
    }
    MWCAP_VIDEO_SIGNAL_STATUS	video_signal_status;
    MWGetVideoSignalStatus(m_channel_handle, &video_signal_status);
    switch (video_signal_status.state)
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
    
    if (MWCAP_VIDEO_SIGNAL_LOCKED != video_signal_status.state) {
        return false;
    }
    printf("Input signal resolution: %d x %d\n", video_signal_status.cx, video_signal_status.cy);
    double fps = (video_signal_status.bInterlaced == TRUE) ? (double)20000000LL / video_signal_status.dwFrameDuration : (double)10000000LL / video_signal_status.dwFrameDuration;
    printf("Input signal fps: %.2f\n", fps);
    printf("Input signal interlaced: %d\n", video_signal_status.bInterlaced);
    printf("Input signal frame segmented: %d\n", video_signal_status.bSegmentedFrame);
    if ((0 == m_width) || (0 == m_height)) {
        if (strstr(channel_info.szProductName,"4K") || strstr(channel_info.szProductName, "4k")) {
            m_width = 3840;// video_signal_status.cx;
            m_height = 2160;// video_signal_status.cy;
        }
        else {
            m_width = 1920;
            m_height = 1080;
        }
    }
    if (0 == m_mw_fourcc) {
        m_mw_fourcc = MWFOURCC_YUY2;
    }
    return true;
}
bool CLowLatencyCapture::start_capture(bool low_latency)
{
    m_low_latency = low_latency;
    m_capture_event = CreateEvent(NULL, FALSE, FALSE, NULL);
    if (NULL == m_capture_event) {
        printf("create event fail\n");
        return false;
    }
    if (MWStartVideoCapture(m_channel_handle, m_capture_event) != MW_SUCCEEDED) {
        printf("start video capture fail\n");
        return false;
    }

    m_notify_event = CreateEvent(NULL, FALSE, FALSE, NULL);
    if (low_latency) {
        m_notify = MWRegisterNotify(m_channel_handle, m_notify_event, MWCAP_NOTIFY_VIDEO_FRAME_BUFFERING | MWCAP_NOTIFY_VIDEO_SIGNAL_CHANGE);
    }
    else {
        m_notify = MWRegisterNotify(m_channel_handle, m_notify_event, MWCAP_NOTIFY_VIDEO_FRAME_BUFFERED | MWCAP_NOTIFY_VIDEO_SIGNAL_CHANGE);
    }

    if (m_notify == NULL) {
        printf("register notify fail\n");
        return false;
    }
    m_frame_stride = FOURCC_CalcMinStride(m_mw_fourcc, m_width, 2);
    m_frame_size = FOURCC_CalcImageSize(m_mw_fourcc, m_width, m_height, m_frame_stride);
    if (0 == m_frame_buffer_num) {
        for (int i = 0; i < 4; i++) {
            m_p_frame_buffer[i] = (unsigned char *)malloc(m_frame_size);
            if (NULL == m_p_frame_buffer[i]) {
                return false;
            }
            m_frame_buffer_num++;
        }
    }
    for (int i = 0; i < m_frame_buffer_num; i++) {
        MWPinVideoBuffer(m_channel_handle, m_p_frame_buffer[i], m_frame_size);
    }
    return true;
}
unsigned char* CLowLatencyCapture::capture_frame(uint32_t *p_buffer_time, uint32_t *p_capture_start_time, uint32_t *p_capture_end_time)
{
    DWORD event_wait_time = m_have_signal?1000:25;
    if (WaitForSingleObject(m_notify_event, event_wait_time)) {
        if (m_have_signal) {
            return NULL;
        }
        
    }
    else {
        ULONGLONG notify_status = 0;
        if (MWGetNotifyStatus(m_channel_handle, m_notify, &notify_status) != MW_SUCCEEDED) {
            return NULL;
        }
        if (notify_status & MWCAP_NOTIFY_VIDEO_SIGNAL_CHANGE) {
            MWCAP_VIDEO_SIGNAL_STATUS video_signal_status;
            MWGetVideoSignalStatus(m_channel_handle, &video_signal_status);
            if (MWCAP_VIDEO_SIGNAL_LOCKED == video_signal_status.state) {
                m_have_signal = true;
            }
            else {
                m_have_signal = false;
            }
            return capture_frame(p_buffer_time, p_capture_start_time, p_capture_end_time);
        }
    }
    MWCAP_VIDEO_BUFFER_INFO video_buffer_info;
    if (MWGetVideoBufferInfo(m_channel_handle, &video_buffer_info) != MW_SUCCEEDED) {
        return NULL;
    }

    unsigned char* p_frame = m_p_frame_buffer[m_capture_frame_count%m_frame_buffer_num];
    LONGLONG start_tm = 0LL;
    int frame_id = MWCAP_VIDEO_FRAME_ID_NEWEST_BUFFERED;
    if (m_have_signal) {
        frame_id = m_low_latency ? video_buffer_info.iNewestBuffering : video_buffer_info.iNewestBufferedFullFrame;
    }
    MWGetDeviceTime(m_channel_handle, &start_tm);
    MWCaptureVideoFrameToVirtualAddressEx(m_channel_handle, frame_id, (LPBYTE)p_frame,
        m_frame_size, m_frame_stride, 0, NULL, m_mw_fourcc, m_width, m_height, 0, 64, NULL, NULL, 0, 100, 0, 100, 0, MWCAP_VIDEO_DEINTERLACE_WEAVE,
        MWCAP_VIDEO_ASPECT_RATIO_IGNORE,NULL, NULL, 0, 0, MWCAP_VIDEO_COLOR_FORMAT_UNKNOWN, MWCAP_VIDEO_QUANTIZATION_UNKNOWN, MWCAP_VIDEO_SATURATION_UNKNOWN);
    do {
        if (WaitForSingleObject(m_capture_event, 1000)) {
            return NULL;
        }
        MWCAP_VIDEO_CAPTURE_STATUS capture_status;
        
        if (MW_SUCCEEDED != MWGetVideoCaptureStatus(m_channel_handle, &capture_status)) {
            return NULL;
        }
        if (capture_status.bFrameCompleted) {
            break;
        }
    } while (1);
    LONGLONG end_tm = 0LL;
    MWGetDeviceTime(m_channel_handle, &end_tm);
	MWCAP_VIDEO_FRAME_INFO video_frame_info;
	MWGetVideoFrameInfo(m_channel_handle, m_low_latency ? video_buffer_info.iNewestBuffering : video_buffer_info.iNewestBufferedFullFrame, &video_frame_info);
    if (m_have_signal&&p_buffer_time) {
        *p_buffer_time = ((video_frame_info.bInterlaced ? video_frame_info.allFieldBufferedTimes[1] : video_frame_info.allFieldBufferedTimes[0]) - video_frame_info.allFieldStartTimes[0])/10;
    }
    if (m_have_signal&&p_capture_start_time) {
        *p_capture_start_time = (start_tm-video_frame_info.allFieldStartTimes[0])/10;
    }
    if (m_have_signal&&p_capture_end_time) {
        *p_capture_end_time = (end_tm-video_frame_info.allFieldStartTimes[0])/10;
    }
    m_capture_frame_count++;
    return p_frame;
}
float CLowLatencyCapture::get_capture_fps()
{
    if ((m_capture_frame_count - m_prev_frame_count) < 30) {
        return m_fps;
    }
    long long now_time = GetTickCount();
    int diff = now_time - m_prev_time;
    if (diff <= 0) {
        return m_fps;
    }
    m_fps = (m_capture_frame_count - m_prev_frame_count)*1000.0 / diff;
    m_prev_time = now_time;
    m_prev_frame_count = m_capture_frame_count;
    return m_fps;
}


bool CLowLatencyCapture::set_resolution(int width, int height)
{
    m_width = width;
    m_height = height;
    return true;
}
bool CLowLatencyCapture::set_mw_fourcc(DWORD mw_fourcc)
{
    m_mw_fourcc = mw_fourcc;
    return true;
}

bool CLowLatencyCapture::get_resolution(int *p_width, int *p_height)
{
    *p_width = m_width;
    *p_height = m_height;
    return true;
}
bool CLowLatencyCapture::get_mw_fourcc(DWORD *p_mw_fourcc)
{
    *p_mw_fourcc = m_mw_fourcc;
    return true;
}