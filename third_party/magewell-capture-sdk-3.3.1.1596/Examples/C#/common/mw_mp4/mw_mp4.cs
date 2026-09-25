using System;
using System.Runtime.InteropServices;
namespace MWModle
{
    public class CMWMp4
    {
        public enum mw_mp4_status_t
        {
            MW_MP4_STATUS_SUCCESS = 0,
            MW_MP4_STATUS_UNEXPECTED_ERROR,
            MW_MP4_STATUS_INVALID_HANDLE,
            MW_MP4_STATUS_FILE_NAME_TOO_LONG,
            MW_MP4_STATUS_INVALID_PARAMETER,
            MW_MP4_STATUS_OPEN_FILE_FAILED,
            MW_MP4_STATUS_INVALID_CODEC_TYPE,
            MW_MP4_STATUS_INVALID_SUBTITLE_TYPE,
            MW_MP4_STATUS_INVALID_ADTS_STREAM,
            MW_MP4_STATUS_INVALID_VIDEO_STREAM,
            MW_MP4_STATUS_INVALID_VIDEO_TRACK,
            MW_MP4_STATUS_UNSUPPORTED,
            MW_MP4_STATUS_OPERATOR_NOT_PERMITTED,

            MW_MP4_STATUS_NO_SUCH_FILE_OR_DIR,
            MW_MP4_STATUS_PERMISSION_DENIED,
            MW_MP4_STATUS_DEVICE_OR_RESOURCE_BUSY,
            MW_MP4_STATUS_TOO_MANY_OPEN_FILES,
            MW_MP4_STATUS_NO_SPACE_LEFT,
            MW_MP4_STATUS_READONLY_FILE_SYSTEM,

            MW_MP4_STATUS_EXTERNAL_FUNC_FAILED,
            MW_MP4_STATUS_FILE_NOT_OPEN,
            MW_MP4_STATUS_NO_SPACE_LEFT_IN_HEADER,
        }

        public enum mw_mp4_video_type_t
        {
            MW_MP4_VIDEO_TYPE_UNKNOWN = 0,
            MW_MP4_VIDEO_TYPE_H264 = 1,
            MW_MP4_VIDEO_TYPE_HEVC = 2,
            MW_MP4_VIDEO_TYPE_H265 = MW_MP4_VIDEO_TYPE_HEVC
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct mw_mp4_h264_parameter_set_t
        {
            public IntPtr sps;        // can be nullptr if it's contained in the stream
            public Int16 sps_size;   // can be 0 if it's contained in the stream
            public IntPtr pps;        // can be nullptr if it's contained in the stream
            public Int16 pps_size;   // can be 0 if it's contained in the stream
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct mw_mp4_hevc_parameter_set_t
        {
            IntPtr sps;            // can be nullptr if it's contained in the stream
            Int16 sps_size;       // can be 0 if it's contained in the stream
            IntPtr pps;            // can be nullptr if it's contained in the stream
            Int16 pps_size;       // can be 0 if it's contained in the stream
            IntPtr vps;            // can be nullptr if it's contained in the stream
            Int16 vps_size;       // can be 0 if it's contained in the stream
        }
        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Ansi, Pack = 1)]
        public struct mw_mp4_video_info_t
        {
            [FieldOffset(0)]
            public mw_mp4_video_type_t codec_type;
            [FieldOffset(4)]
            public UInt32 timescale;
            [FieldOffset(8)]
            public UInt16 width;
            [FieldOffset(10)]
            public UInt16 height;
            [FieldOffset(12)]
            public mw_mp4_h264_parameter_set_t h264;
            [FieldOffset(12)]
            public mw_mp4_hevc_parameter_set_t hevc;
        }
        public enum mw_mp4_audio_codec_t
        {
            MW_MP4_AUDIO_TYPE_UNKNOWN = 0,
            MW_MP4_AUDIO_TYPE_AAC = 1,
            MW_MP4_AUDIO_TYPE_ADTS_AAC = 2
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct mw_mp4_audio_info_t
        {
            public mw_mp4_audio_codec_t codec_type;
            public UInt32 timescale;
            public UInt16 sample_rate; // can be 0 if codec is aac with adts
            public UInt16 channels; // can be 0 if codec is aac with  adts
            public Byte profile; // can be 0 if codec is aac with  adts
        }

        public enum mw_mp4_subtitle_type_t
        {
            MW_MP4_SUBTITLE_TYPE_UNKNOWN = 0,
            MW_MP4_SUBTITLE_TYPE_CC608 = 1,
            MW_MP4_SUBTITLE_TYPE_CC708 = 2
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct mw_mp4_subtitle_info_t
        {
            mw_mp4_subtitle_type_t codec_type;
            UInt32 timescale;
        }
#if DEBUG
        [DllImport("mw_mp4_d.dll", CallingConvention = CallingConvention.Cdecl)]
#else
        [DllImport("mw_mp4.dll", CallingConvention = CallingConvention.Cdecl)]
#endif
        public static extern IntPtr mw_mp4_open(string p_path);

#if DEBUG
        [DllImport("mw_mp4_d.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
#else
        [DllImport("mw_mp4.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
#endif
        public static extern IntPtr mw_mp4_open_ex(string p_path);

#if DEBUG
        [DllImport("mw_mp4_d.dll", CallingConvention = CallingConvention.Cdecl)]
#else
        [DllImport("mw_mp4.dll", CallingConvention = CallingConvention.Cdecl)]
#endif
        public static extern mw_mp4_status_t mw_mp4_set_video(IntPtr handle, ref mw_mp4_video_info_t p_info);

#if DEBUG
        [DllImport("mw_mp4_d.dll", CallingConvention = CallingConvention.Cdecl)]
#else
        [DllImport("mw_mp4.dll", CallingConvention = CallingConvention.Cdecl)]
#endif
        public static extern mw_mp4_status_t mw_mp4_set_audio(IntPtr handle, ref mw_mp4_audio_info_t p_info);

#if DEBUG
        [DllImport("mw_mp4_d.dll", CallingConvention = CallingConvention.Cdecl)]
#else
        [DllImport("mw_mp4.dll", CallingConvention = CallingConvention.Cdecl)]
#endif
        public static extern mw_mp4_status_t mw_mp4_set_subtitle(IntPtr handle, ref mw_mp4_subtitle_info_t p_info);
#if DEBUG
        [DllImport("mw_mp4_d.dll", CallingConvention = CallingConvention.Cdecl)]
#else
        [DllImport("mw_mp4.dll", CallingConvention = CallingConvention.Cdecl)]
#endif
        public static extern mw_mp4_status_t mw_mp4_write_video(IntPtr handle, IntPtr p_stream, UInt32 size, UInt64 timestamp);

#if DEBUG
        [DllImport("mw_mp4_d.dll", CallingConvention = CallingConvention.Cdecl)]
#else
        [DllImport("mw_mp4.dll", CallingConvention = CallingConvention.Cdecl)]
#endif
        public static extern mw_mp4_status_t mw_mp4_write_audio(IntPtr handle, IntPtr p_stream, UInt32 size, UInt64 timestamp);

#if DEBUG
        [DllImport("mw_mp4_d.dll", CallingConvention = CallingConvention.Cdecl)]
#else
        [DllImport("mw_mp4.dll", CallingConvention = CallingConvention.Cdecl)]
#endif
        public static extern mw_mp4_status_t mw_mp4_write_subtitle(IntPtr handle, IntPtr p_stream, UInt32 size, UInt64 timestamp);

#if DEBUG
        [DllImport("mw_mp4_d.dll", CallingConvention = CallingConvention.Cdecl)]
#else
        [DllImport("mw_mp4.dll", CallingConvention = CallingConvention.Cdecl)]
#endif
        public static extern mw_mp4_status_t mw_mp4_close(IntPtr handle);

#if DEBUG
        [DllImport("mw_mp4_d.dll", CallingConvention = CallingConvention.Cdecl)]
#else
        [DllImport("mw_mp4.dll", CallingConvention = CallingConvention.Cdecl)]
#endif
        public static extern mw_mp4_status_t mw_mp4_repair(string p_path , bool del);

#if DEBUG
        [DllImport("mw_mp4_d.dll", CallingConvention = CallingConvention.Cdecl)]
#else
        [DllImport("mw_mp4.dll", CallingConvention = CallingConvention.Cdecl)]
#endif
        public static extern mw_mp4_status_t mw_mp4_repair_ex(char[] p_path, bool del);

    }
}