using System;
using System.Runtime.InteropServices;
namespace MWModle
{
    public class CMWVenc
    {
        public enum mw_venc_frame_type_t
        {
            MW_VENC_FRAME_TYPE_UNKNOWN,                         ///<Unknown frame
            MW_VENC_FRAME_TYPE_IDR,                             ///<IDR frame
            MW_VENC_FRAME_TYPE_I,                               ///<I-frame
            MW_VENC_FRAME_TYPE_P,                               ///<P-frame
            MW_VENC_FRAME_TYPE_B,                               ///<B-frame
            MW_VENC_FRAME_TYPE_COUNT                            ///<Number of frame types enumerated
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct mw_venc_frame_info_t
        {
            public mw_venc_frame_type_t frame_type;
            public Int32 delay;
            public Int64 pts;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void MW_ENCODER_CALLBACK(IntPtr user_ptr, IntPtr p_frame, UInt32 frame_len, IntPtr p_frame_info);
        public enum mw_venc_platform_t
        {
            MW_VENC_PLATFORM_UNKNOWN = 0,                       ///<Unknown hardware type, the binary value is 0000 0000
            MW_VENC_PLATFORM_AMD = 1,                       ///<AMD graphics, the binary value is 0000 0001
            MW_VENC_PLATFORM_INTEL = 2,                     ///<Intel graphics, the binary value is 0000 0010
            MW_VENC_PLATFORM_NVIDIA = 4,                        ///<Nvidia graphics, the binary value is 0000 0100
            MW_VENC_PLATFORM_COUNT                              ///<The maximum input value 
        }

        public enum mw_venc_code_type_t
        {
            MW_VENC_CODE_TYPE_UNKNOWN,                          ///<Unknown
            MW_VENC_CODE_TYPE_AVC,                              ///<AVC/H264
            MW_VENC_CODE_TYPE_HEVC,                             ///<HEVC/H265
            MW_VENC_CODE_TYPE_H264 = MW_VENC_CODE_TYPE_AVC,     ///<H264
            MW_VENC_CODE_TYPE_H265 = MW_VENC_CODE_TYPE_HEVC,    ///<H265
            MW_VENC_CODE_TYPE_COUNT                             ///<The maximum input value 
        }
        public enum mw_venc_targetusage_t
        {
            MW_VENC_TARGETUSAGE_UNKNOWN,                        ///<Unknown
            MW_VENC_TARGETUSAGE_BEST_QUALITY,                   ///<Quality first
            MW_VENC_TARGETUSAGE_BALANCED,                       ///<Balance the coding quality and speed
            MW_VENC_TARGETUSAGE_BEST_SPEED,                     ///<Speed first
            MW_VENC_TARGETUSAGE_COUNT                           ///<The maximum input value 
        }
        public enum mw_venc_rate_control_mode_t
        {
            MW_VENC_RATECONTROL_UNKNOWN,                        ///<Unknown
            MW_VENC_RATECONTROL_CBR,                            ///<Constant Bit Rate
            MW_VENC_RATECONTROL_VBR,                            ///<Variable Bit Rate
            MW_VENC_RATECONTROL_CQP,                            ///<Constant Quantization Parameter
            MW_VENC_RATECONTROL_COUNT                           ///<The maximum input value 
        }
        public enum mw_venc_profile_t
        {
            MW_VENC_PROFILE_UNKNOWN,                            ///<Unknown
            MW_VENC_PROFILE_H264_BASELINE,                      ///<H264 baseline
            MW_VENC_PROFILE_H264_MAIN,                          ///<H264 main
            MW_VENC_PROFILE_H264_HIGH,                          ///<H264 high
            MW_VENC_PROFILE_H265_MAIN,                          ///<H265 main
            MW_VENC_PROFILE_COUNT                               ///<The maximum input value 
        }
        public enum mw_venc_level_t
        {
            MW_VENC_LEVEL_UNKNOWN,                              ///<Unknown
            MW_VENC_LEVEL_2_1,                                  ///<Level 2.1
            MW_VENC_LEVEL_3_1,                                  ///<Level 3.1
            MW_VENC_LEVEL_4_1,                                  ///<Level 4.1
            MW_VENC_LEVEL_5_1,                                  ///<Level 5.1
            MW_VENC_LEVEL_5_2,                                  ///<Level 5.2
            MW_VENC_LEVEL_6_1,                                  ///<Level 6.1
            MW_VENC_LEVEL_6_2,                                  ///<Level 6.2
            MW_VENC_LEVEL_COUNT                                 ///<The maximum input value 
        }
        public enum mw_venc_fourcc_t
        {
            MW_VENC_FOURCC_UNKNOWN,                             ///<Unknown
            MW_VENC_FOURCC_NV12,                                ///<NV12 equals #MWFOURCC_NV12
            MW_VENC_FOURCC_NV21,                                ///<NV21 equals #MWFOURCC_NV21
            MW_VENC_FOURCC_YV12,                                ///<YV12 equals #MWFOURCC_YV12
            MW_VENC_FOURCC_I420,                                ///<I420 equals #MWFOURCC_I420
            MW_VENC_FOURCC_YUY2,                                ///<YUY2 equals #MWFOURCC_YUY2
            MW_VENC_FOURCC_P010,                                ///<P010 equals #MWFOURCC_P010
            MW_VENC_FOURCC_BGRA,                                ///<BGRA equals #MWFOURCC_BGRA
            MW_VENC_FOURCC_RGBA,                                ///<RGBA equals #MWFOURCC_RGBA
            MW_VENC_FOURCC_ARGB,                                ///<ARGB equals #MWFOURCC_ARGB
            MW_VENC_FOURCC_ABGR,                                ///<ABGR equals #MWFOURCC_ABGR
            MW_VENC_FOURCC_COUNT                                ///<The maximum input value 
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct bitrate_t
        {
            public UInt32 target_bitrate;                   ///<Target bitrate: only valid when the bitrate is variable or constant.
            public UInt32 max_bitrate;						///<The maximun bitrate: only valid when the bitrate is variable.
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct qp_t
        {
            public Byte qpi;                                ///<I-Frame QP
            public Byte qpb;                                ///<B-Frame QP
            public Byte qpp;                                ///<P-Frame QP
            public Byte reserved;                           ///<Reserved      
        }

        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Ansi, Pack = 1)]
        public struct mw_venc_rate_control_t
        {
            [FieldOffset(0)]
            public mw_venc_rate_control_mode_t mode;                   ///<Bitrate controlling methods
            [FieldOffset(4)]
            public bitrate_t bitrate;
            [FieldOffset(4)]
            public qp_t qp;
        };

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct mw_venc_fps_t
        {
            public Int32 num;                                    ///<Numerator of frames
            public Int32 den;									///<Denominator of frames
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct mw_venc_extdata_t
        {
            public Byte[] p_extdata;									///<Extended data pointer. The data includes vps(Video Parameter Set),sps(Sequence Parameter Set) and pps(Picture Parameter Set).
            public UInt32 extdata_len;                              ///<The total length of entended data
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public UInt32[] len;									///<len[0] vps_len;len[1] sps_len;len[2] pps_len
        }

        public enum mw_venc_property_t
        {
            MW_VENC_PROPERTY_UNKNOWN,							///<Unknown
	        MW_VENC_PROPERTY_RATE_CONTROL,						///<Rate: #mw_venc_rate_control_t default MW_VENC_RATECONTROL_CBR 4096k
	        MW_VENC_PROPERTY_FPS,								///<Frame rate: #mw_venc_fps_t default 60/1
	        MW_VENC_PROPERTY_GOP_SIZE,							///<GOP: int32_t default 60
	        MW_VENC_PROPERTY_SLICE_NUM,							///<Number of slices: int32_t default 1
	        MW_VENC_PROPERTY_GOP_REF_SIZE,						///<GOP reference size: int32_t defalut  0, if = 1 -> no b-frame
            MW_VENC_PROPERTY_EXTDATA,       					///<Extened data: #mw_venc_extdata_t just get vps sps pps
            MW_VENC_PROPERTY_FORCE_IDR,     					///<Force IDR 
	        MW_VENC_PROPERTY_COUNT								///<The maximum input value 
        }
;

        public enum mw_venc_status_t
        {
            MW_VENC_STATUS_SUCCESS,								///<Success
	        MW_VENC_STATUS_FAIL,								///<Fail
	        MW_VENC_STATUS_UNSUPPORT,							///<Not supported
            MW_VENC_STATUS_BUSY,								///<Busy
	        MW_VENC_STATUS_INVALID_PARAM,						///<Invalid parameter
	        MW_VENC_STATUS_UNKNOWN_ERROR,						///<Unknown error
	        MW_VENC_STATUS_COUNT								///<The maximum input value 
        }
        public enum mw_venc_amd_mem_type_t
        {
            MW_VENC_AMD_MEM_AUTO,								///<Uses memory determined by system
	        MW_VENC_AMD_MEM_CPU,								///<Uses memory
	        MW_VENC_AMD_MEM_DX9,								///<Uses DX9
	        MW_VENC_AMD_MEM_DX11,								///<Uses DX11
	        MW_VENC_AMD_MEM_OPENGL,								///<Uses OpenGL
            MW_VENC_AMD_MEM_VULKAN,								///<Uses Vulkan
	        MW_VENC_AMD_MEM_COUNT								///<The maximum input value 
        }
        public enum mw_venc_color_primaries_t
        {
            MW_VENC_COLOR_PRI_RESERVED0 = 0,
            MW_VENC_COLOR_PRI_BT709 = 1,  ///< also ITU-R BT1361 / IEC 61966-2-4 / SMPTE RP177 Annex B
            MW_VENC_COLOR_PRI_UNSPECIFIED = 2,
            MW_VENC_COLOR_PRI_RESERVED = 3,
            MW_VENC_COLOR_PRI_BT470M = 4,  ///< also FCC Title 47 Code of Federal Regulations 73.682 (a)(20)

            MW_VENC_COLOR_PRI_BT470BG = 5,  ///< also ITU-R BT601-6 625 / ITU-R BT1358 625 / ITU-R BT1700 625 PAL & SECAM
            MW_VENC_COLOR_PRI_SMPTE170M = 6,  ///< also ITU-R BT601-6 525 / ITU-R BT1358 525 / ITU-R BT1700 NTSC
            MW_VENC_COLOR_PRI_SMPTE240M = 7,  ///< functionally identical to above
            MW_VENC_COLOR_PRI_FILM = 8,  ///< colour filters using Illuminant C
            MW_VENC_COLOR_PRI_BT2020 = 9,  ///< ITU-R BT2020
            MW_VENC_COLOR_PRI_SMPTE428 = 10, ///< SMPTE ST 428-1 (CIE 1931 XYZ)
            MW_VENC_COLOR_PRI_SMPTEST428_1 = MW_VENC_COLOR_PRI_SMPTE428,
            MW_VENC_COLOR_PRI_SMPTE431 = 11, ///< SMPTE ST 431-2 (2011) / DCI P3
            MW_VENC_COLOR_PRI_SMPTE432 = 12, ///< SMPTE ST 432-1 (2010) / P3 D65 / Display P3
            MW_VENC_COLOR_PRI_JEDEC_P22 = 22, ///< JEDEC P22 phosphors
            MW_VENC_COLOR_PRI_COUNT                ///< Not part of ABI
        }
        public enum mw_venc_color_transfer_characteristic_t
        {
            MW_VENC_COLOR_TRC_RESERVED0 = 0,
            MW_VENC_COLOR_TRC_BT709 = 1,  ///< also ITU-R BT1361
            MW_VENC_COLOR_TRC_UNSPECIFIED = 2,
            MW_VENC_COLOR_TRC_RESERVED = 3,
            MW_VENC_COLOR_TRC_GAMMA22 = 4,  ///< also ITU-R BT470M / ITU-R BT1700 625 PAL & SECAM
            MW_VENC_COLOR_TRC_GAMMA28 = 5,  ///< also ITU-R BT470BG
            MW_VENC_COLOR_TRC_SMPTE170M = 6,  ///< also ITU-R BT601-6 525 or 625 / ITU-R BT1358 525 or 625 / ITU-R BT1700 NTSC
            MW_VENC_COLOR_TRC_SMPTE240M = 7,
            MW_VENC_COLOR_TRC_LINEAR = 8,  ///< "Linear transfer characteristics"
            MW_VENC_COLOR_TRC_LOG = 9,  ///< "Logarithmic transfer characteristic (100:1 range)"
            MW_VENC_COLOR_TRC_LOG_SQRT = 10, ///< "Logarithmic transfer characteristic (100 * Sqrt(10) : 1 range)"
            MW_VENC_COLOR_TRC_IEC61966_2_4 = 11, ///< IEC 61966-2-4
            MW_VENC_COLOR_TRC_BT1361_ECG = 12, ///< ITU-R BT1361 Extended Colour Gamut
            MW_VENC_COLOR_TRC_IEC61966_2_1 = 13, ///< IEC 61966-2-1 (sRGB or sYCC)
            MW_VENC_COLOR_TRC_BT2020_10 = 14, ///< ITU-R BT2020 for 10-bit system
            MW_VENC_COLOR_TRC_BT2020_12 = 15, ///< ITU-R BT2020 for 12-bit system
            MW_VENC_COLOR_TRC_SMPTE2084 = 16, ///< SMPTE ST 2084 for 10-, 12-, 14- and 16-bit systems
            MW_VENC_COLOR_TRC_SMPTEST2084 = MW_VENC_COLOR_TRC_SMPTE2084,
            MW_VENC_COLOR_TRC_SMPTE428 = 17, ///< SMPTE ST 428-1
            MW_VENC_COLOR_TRC_SMPTEST428_1 = MW_VENC_COLOR_TRC_SMPTE428,
            MW_VENC_COLOR_TRC_ARIB_STD_B67 = 18, ///< ARIB STD-B67, known as "Hybrid log-gamma"
            MW_VENC_COLOR_TRC_COUNT                 ///< Not part of ABI
        }
        public enum mw_venc_color_space_t
        {
            MW_VENC_COLOR_SPACE_RGB = 0,  ///< order of coefficients is actually GBR, also IEC 61966-2-1 (sRGB)
            MW_VENC_COLOR_SPACE_BT709 = 1,  ///< also ITU-R BT1361 / IEC 61966-2-4 xvYCC709 / SMPTE RP177 Annex B
            MW_VENC_COLOR_SPACE_UNSPECIFIED = 2,
            MW_VENC_COLOR_SPACE_RESERVED = 3,
            MW_VENC_COLOR_SPACE_FCC = 4,  ///< FCC Title 47 Code of Federal Regulations 73.682 (a)(20)
            MW_VENC_COLOR_SPACE_BT470BG = 5,  ///< also ITU-R BT601-6 625 / ITU-R BT1358 625 / ITU-R BT1700 625 PAL & SECAM / IEC 61966-2-4 xvYCC601
            MW_VENC_COLOR_SPACE_SMPTE170M = 6,  ///< also ITU-R BT601-6 525 / ITU-R BT1358 525 / ITU-R BT1700 NTSC
            MW_VENC_COLOR_SPACE_SMPTE240M = 7,  ///< functionally identical to above
            MW_VENC_COLOR_SPACE_YCGCO = 8,  ///< Used by Dirac / VC-2 and H.264 FRext, see ITU-T SG16
            MW_VENC_COLOR_SPACE_YCOCG = MW_VENC_COLOR_SPACE_YCGCO,
            MW_VENC_COLOR_SPACE_BT2020_NCL = 9,  ///< ITU-R BT2020 non-constant luminance system
            MW_VENC_COLOR_SPACE_BT2020_CL = 10, ///< ITU-R BT2020 constant luminance system
            MW_VENC_COLOR_SPACE_SMPTE2085 = 11, ///< SMPTE 2085, Y'D'zD'x
            MW_VENC_COLOR_SPACE_CHROMA_DERIVED_NCL = 12, ///< Chromaticity-derived non-constant luminance system
            MW_VENC_COLOR_SPACE_CHROMA_DERIVED_CL = 13, ///< Chromaticity-derived constant luminance system
            MW_VENC_COLOR_SPACE_ICTCP = 14, ///< ITU-R BT.2100-0, ICtCp
            MW_VENC_COLOR_SPACE_COUNT                ///< Not part of ABI
        }
        public enum mw_venc_frame_field_mode_t
        {
            MW_VENC_FRAME_FIELD_MODE_PROGRESSIVE = 0x00,
            MW_VENC_FRAME_FIELD_MODE_FIELD = 0x01,
            MW_VENC_FRAME_FIELD_MODE_MBAFF = 0x02,
            MW_VENC_FRAME_FIELD_MODE_COUNT
        }
        public enum mw_venc_pic_struct_t
        {
            MW_VENC_PICSTRUCT_PROGRESSIVE = 0x00,
            MW_VENC_PICSTRUCT_FIELD_TFF = 0x01,
            MW_VENC_PICSTRUCT_FIELD_BFF = 0x02,
            MW_VENC_PICSTRUCT_COUNT
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct mw_venc_param_t
        {
            public mw_venc_code_type_t code_type;						///<Code type, H264 or H265 
	        public mw_venc_fourcc_t fourcc;							///<Color format of input data 
	        public mw_venc_targetusage_t targetusage;					///<Preset
	        public mw_venc_rate_control_t rate_control;				///<Frame control 
	        public Int32 width;										///<width of input video
	        public Int32 height;										///<Height of input video
	        public mw_venc_fps_t fps;									///<Frame rate 
	        public Int32 slice_num;									///<Slice number 
	        public Int32 gop_pic_size;								///<GOP size
	        public Int32 gop_ref_size;								///<Referenced GOP size 
	        public mw_venc_profile_t profile;							///<Profile
	        public mw_venc_level_t level;								///<Level
            public Int32 intel_async_depth;
            public mw_venc_amd_mem_type_t amd_mem_reserved;			///<AMD storage type, which are valid only if you are using AMD to encode. 
            public Int32 yuv_is_full_range;
            public mw_venc_color_primaries_t color_primaries;
            public mw_venc_color_transfer_characteristic_t color_trc;
            public mw_venc_color_space_t color_space;
            public mw_venc_frame_field_mode_t frame_field_mode;
            public mw_venc_pic_struct_t pic_struct;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct mw_venc_gpu_info_t
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string gpu_name;
	        public mw_venc_platform_t platform;						///gpu platform
        }


        [DllImport("mw_venc.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern mw_venc_status_t mw_venc_init();

        [DllImport("mw_venc.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern mw_venc_status_t mw_venc_deinit();
        [DllImport("mw_venc.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern Int32 mw_venc_get_gpu_num();

        [DllImport("mw_venc.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern mw_venc_status_t mw_venc_get_gpu_info_by_index(Int32 index, ref mw_venc_gpu_info_t info);

        [DllImport("mw_venc.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern mw_venc_status_t mw_venc_get_default_param(ref mw_venc_param_t p_param);

        [DllImport("mw_venc.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern UInt32 mw_venc_get_support_platfrom();

        [DllImport("mw_venc.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mw_venc_create(mw_venc_platform_t platform, ref mw_venc_param_t p_param, MW_ENCODER_CALLBACK frame_callback, IntPtr user_ptr);

        [DllImport("mw_venc.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mw_venc_create_by_index(Int32 index, ref mw_venc_param_t p_param, MW_ENCODER_CALLBACK frame_callback, IntPtr user_ptr);

        [DllImport("mw_venc.dll", CallingConvention = CallingConvention.Cdecl)]
        //public static extern mw_venc_status_t mw_venc_put_frame(IntPtr handle, IntPtr p_frame);
        public static extern mw_venc_status_t mw_venc_put_frame(IntPtr handle, Byte[] p_frame);

        [DllImport("mw_venc.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern mw_venc_status_t mw_venc_put_frame_ex(IntPtr handle, Byte[] p_frame, Int64 pts);

        [DllImport("mw_venc.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern mw_venc_status_t mw_venc_destory(IntPtr handle);

        [DllImport("mw_venc.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern
        mw_venc_status_t mw_venc_get_property(IntPtr handle, mw_venc_property_t param, IntPtr args);

        [DllImport("mw_venc.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern mw_venc_status_t mw_venc_set_property(IntPtr handle, mw_venc_property_t param, IntPtr args);


    }
}