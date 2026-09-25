using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FFmpeg.AutoGen;
namespace MWModle
{
    public unsafe class CMp4Record
    {
        Int32 m_gpu_index = -1;
        Int32 m_width = 0;
        Int32 m_height = 0;
        UInt32 m_mw_fourcc = 0;
        Int64 m_frame_duration = 0;
        Int32 m_sample_rate = 0;
        Int32 m_channels = 0;
        Int32 m_bit_per_sample = 0;
        IntPtr m_mp4 = IntPtr.Zero;
        IntPtr m_mw_venc = IntPtr.Zero;
        byte[] m_audio_buffer = null;

        AVCodecContext* m_p_audio_ctxt = null;
        AVFrame* m_p_audio_frame = null;
        AVFrame* m_p_swr_frame = null;// ffmpeg.av_frame_alloc();
        SwrContext* m_p_audio_swr;
        Int32 m_audio_buffer_size;
        Int32 m_audio_frame_size;

        AVCodecContext* m_p_video_ctxt = null;
        AVFrame* m_p_video_frame = null;
        protected CMWVenc.MW_ENCODER_CALLBACK m_mw_venc_callback = null;
        public CMp4Record(string file_name)
        {
            m_mp4 = CMWMp4.mw_mp4_open_ex(file_name);
            if (IntPtr.Zero == m_mp4) {
                Console.WriteLine("open mp4 "+ file_name+" fail");
            }
            ffmpeg.avcodec_register_all();
            Console.WriteLine("open mp4 "+ file_name);
        }
        public virtual void Dispose()
        {
            if (IntPtr.Zero != m_mw_venc){
                CMWVenc.mw_venc_destory(m_mw_venc);
                m_mw_venc = IntPtr.Zero;
            }
            if (null != m_p_audio_ctxt) {
                fixed (AVCodecContext** m_pp_audio_ctxt = &m_p_audio_ctxt) {
                    ffmpeg.avcodec_free_context(m_pp_audio_ctxt);
                }
            }
            if (null != m_p_audio_frame){
                fixed (AVFrame** m_pp_audio_frame = &m_p_audio_frame){
                    ffmpeg.av_frame_free(m_pp_audio_frame);
                }

            }
            if (null != m_p_swr_frame){
                m_p_swr_frame->data0 = null;
                fixed (AVFrame** m_pp_swr_frame = &m_p_swr_frame){
                    ffmpeg.av_frame_free(m_pp_swr_frame);
                }

            }
            if (null != m_p_audio_swr){
                fixed (SwrContext** m_pp_audio_swr = &m_p_audio_swr){
                    ffmpeg.swr_free(m_pp_audio_swr);
                }
            }
            if (null != m_p_video_ctxt){
                fixed (AVCodecContext** m_pp_video_ctxt = &m_p_video_ctxt){
                    ffmpeg.avcodec_free_context(m_pp_video_ctxt);
                }
            }
            if (null != m_p_video_frame){
                fixed (AVFrame** m_pp_video_frame = &m_p_video_frame){
                    ffmpeg.av_frame_free(m_pp_video_frame);
                }
            }
            if (IntPtr.Zero != m_mp4){
                CMWMp4.mw_mp4_close(m_mp4);
                m_mp4 = IntPtr.Zero;
            }
            m_mw_venc_callback = null;
            m_audio_buffer = null;
            Console.WriteLine("close mp4 ");
        }
        ~CMp4Record()
        {
            Dispose();
        }
        public bool set_video_encoder_on_gpu(Int32 gpu_index)
        {
            m_gpu_index = gpu_index;
            return true;
        }
        private CMWVenc.mw_venc_fourcc_t fourcc(UInt32 mw_fourcc)
        {
            if (MWFOURCC.MWFOURCC_NV12 == mw_fourcc){
                return CMWVenc.mw_venc_fourcc_t.MW_VENC_FOURCC_NV12;
            }
            else if (MWFOURCC.MWFOURCC_YUY2 == mw_fourcc){
                return CMWVenc.mw_venc_fourcc_t.MW_VENC_FOURCC_YUY2;
            }

            return CMWVenc.mw_venc_fourcc_t.MW_VENC_FOURCC_NV12;
        }
        private void mw_venc_frame_callback(IntPtr user_ptr, IntPtr p_frame, UInt32 frame_len, IntPtr p_frame_info)
        {
            if (null != user_ptr){

                CMWVenc.mw_venc_frame_info_t frame_info = (CMWVenc.mw_venc_frame_info_t)Marshal.PtrToStructure(p_frame_info, typeof(CMWVenc.mw_venc_frame_info_t));
                CMWMp4.mw_mp4_write_video(user_ptr, p_frame, frame_len, (UInt64)frame_info.pts);
            }
        }
        bool create_mw_venc() {
            CMWVenc.mw_venc_param_t enc_param = new CMWVenc.mw_venc_param_t();
            CMWVenc.mw_venc_get_default_param(ref enc_param);
            enc_param.width = m_width;
            enc_param.height = m_height;
            enc_param.fourcc = fourcc(m_mw_fourcc);
            enc_param.code_type = CMWVenc.mw_venc_code_type_t.MW_VENC_CODE_TYPE_H264;
            enc_param.rate_control.bitrate.target_bitrate = 1024;
            enc_param.rate_control.mode = CMWVenc.mw_venc_rate_control_mode_t.MW_VENC_RATECONTROL_CBR;
            enc_param.fps.den =  (int)m_frame_duration;
            enc_param.fps.num =  1000000;
            enc_param.amd_mem_reserved = CMWVenc.mw_venc_amd_mem_type_t.MW_VENC_AMD_MEM_CPU;
            m_mw_venc_callback = new CMWVenc.MW_ENCODER_CALLBACK(mw_venc_frame_callback);
            m_mw_venc = CMWVenc.mw_venc_create_by_index(m_gpu_index, ref enc_param, m_mw_venc_callback, m_mp4);
            if (IntPtr.Zero == m_mw_venc) {
                Console.WriteLine("create encode on gpu fail");
                return false;
            }
            return true;
        }
        bool create_ff_venc()
        {
            AVCodec* p_video_codec;
            if (m_mw_fourcc != MWFOURCC.MWFOURCC_NV12) {
                Console.WriteLine("ffmpeg not support this fourcc");
                return false;
            }
            p_video_codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
            if (null == p_video_codec){
                Console.WriteLine("ffmpeg find h264 encoder fail");
                return false;
            }

            m_p_video_ctxt = ffmpeg.avcodec_alloc_context3(p_video_codec);
            if (null == m_p_video_ctxt){
                Console.WriteLine("ffmpeg alloc fail");
                return false;
            }
            m_p_video_ctxt->codec_id = AVCodecID.AV_CODEC_ID_H264;
            m_p_video_ctxt->bit_rate = 4096000;
            m_p_video_ctxt->width = m_width;
            m_p_video_ctxt->height = m_height;

            ffmpeg.av_opt_set(m_p_video_ctxt->priv_data, "preset", "ultrafast", 0);
            ffmpeg.av_opt_set(m_p_video_ctxt->priv_data, "tune", "zerolatency", 0);

            m_p_video_ctxt->time_base.num = (int)m_frame_duration;
            m_p_video_ctxt->time_base.den = 1000000;

            m_p_video_ctxt->gop_size = 60; /* emit one intra frame every twelve frames at most */
            m_p_video_ctxt->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
            //m_p_video_ctxt->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUYV422;
            if (ffmpeg.avcodec_open2(m_p_video_ctxt, p_video_codec, null) < 0){
                Console.WriteLine("ffmpeg open encoder fail");
                return false;
            }
            return true;
        }
        bool create_ff_aenc()
        {
            if (null == m_audio_buffer){
                m_audio_frame_size = 1024 * m_channels * m_bit_per_sample / 8;
                m_audio_buffer = new byte[m_audio_frame_size];
            }
            if (null == m_audio_buffer) {
                return false;
            }
            AVCodec* p_audio_codec;
            p_audio_codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
            if (null == p_audio_codec){
                Console.WriteLine("ffmpeg find aac encoder fail");
                return false;
            }
            m_p_audio_ctxt = ffmpeg.avcodec_alloc_context3(p_audio_codec);
            if (null == m_p_audio_ctxt){
                Console.WriteLine("ffmpeg alloc fail");
                return false;
            }

            m_p_audio_ctxt->bit_rate = 64000;
            AVSampleFormat in_sample_fmt;
            //support sample_fmt
            if (8 == m_bit_per_sample){
                in_sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_U8;
            }
            else if (16 == m_bit_per_sample){
                in_sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S16;
            }
            else if (32 == m_bit_per_sample){
                in_sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S32;
            }
            else {
                Console.WriteLine("ffmpeg aac not support"+ m_bit_per_sample+"bit per sample");
                return false;
            }
            int i;
            bool need_us_swr = false;
            if (null != p_audio_codec->sample_fmts){
                m_p_audio_ctxt->sample_fmt = p_audio_codec->sample_fmts[0];
                for (i = 0;AVSampleFormat.AV_SAMPLE_FMT_NONE != p_audio_codec->sample_fmts[i]; i++){
                    if (p_audio_codec->sample_fmts[i] == in_sample_fmt){
                        m_p_audio_ctxt->sample_fmt = in_sample_fmt;
                    }
                }
                if (m_p_audio_ctxt->sample_fmt != in_sample_fmt){
                    need_us_swr = true;
                }
            }
            //support sample_rate
            m_p_audio_ctxt->sample_rate = m_sample_rate;
            if (null != p_audio_codec->supported_samplerates){
                m_p_audio_ctxt->sample_rate = p_audio_codec->supported_samplerates[0];
                for (i = 0; 0 != p_audio_codec->supported_samplerates[i]; i++){
                    if (p_audio_codec->supported_samplerates[i] == m_sample_rate){
                        m_p_audio_ctxt->sample_rate = m_sample_rate;
                        break;
                    }
                }
                if (m_p_audio_ctxt->sample_rate != m_sample_rate){
                    need_us_swr = true;
                }
            }

            //support channels
            m_p_audio_ctxt->channels = m_channels;
            m_p_audio_ctxt->channel_layout = (UInt64)ffmpeg.av_get_default_channel_layout(m_channels);
            if (null != p_audio_codec->channel_layouts){
                m_p_audio_ctxt->channel_layout = p_audio_codec->channel_layouts[0];
                m_p_audio_ctxt->channels = ffmpeg.av_get_channel_layout_nb_channels(m_p_audio_ctxt->channel_layout);
                for (i = 0; 0 != p_audio_codec->channel_layouts[i]; i++){
                    int channels = ffmpeg.av_get_channel_layout_nb_channels(p_audio_codec->channel_layouts[i]);
                    if (channels == m_channels){
                        m_p_audio_ctxt->channels = m_channels;
                        m_p_audio_ctxt->channel_layout = p_audio_codec->channel_layouts[i];
                    }
                }
                if (m_p_audio_ctxt->channels != m_channels){
                    need_us_swr = true;
                }
            }
            if (ffmpeg.avcodec_open2(m_p_audio_ctxt, p_audio_codec, null) < 0){
                Console.WriteLine("ffmpeg open encoder fail");
                return false;
            }
            /* create resampler context */
            m_p_audio_frame = ffmpeg.av_frame_alloc();
            if (null == m_p_audio_frame){
                Console.WriteLine("ffmpeg alloc frame fail");
                return false;
            }
            m_p_audio_frame->format = (int)m_p_audio_ctxt->sample_fmt;
            m_p_audio_frame->channel_layout = m_p_audio_ctxt->channel_layout;
            m_p_audio_frame->sample_rate = m_p_audio_ctxt->sample_rate;
            m_p_audio_frame->nb_samples = 1024;
            if (need_us_swr){
                /* set options */
                m_p_audio_swr = ffmpeg.swr_alloc_set_opts(null, (long)m_p_audio_ctxt->channel_layout, m_p_audio_ctxt->sample_fmt, m_p_audio_ctxt->sample_rate, 
                    ffmpeg.av_get_default_channel_layout(m_channels), in_sample_fmt, m_p_audio_ctxt->sample_rate, 0, null);
                if (null == m_p_audio_swr){
                    Console.WriteLine("ffmpeg swr alloc fail");
                    return false;
                }

                /* initialize the resampling context */
                if (ffmpeg.swr_init(m_p_audio_swr) < 0){
                    Console.WriteLine("ffmpeg swr init fail");
                    return false;
                }
                ffmpeg.av_frame_get_buffer(m_p_audio_frame, 0);
                m_p_swr_frame =  ffmpeg.av_frame_alloc();
                if (null == m_p_swr_frame){
                    Console.WriteLine("ffmpeg alloc frame fail");
                    return false;
                }
            }
            m_audio_buffer_size = 0;
            return true;
        }
        public bool set_video(Int32 width, Int32 height, UInt32 mw_fourcc, Int64 frame_duration)
        {
            m_width = width;
            m_height = height;
            m_mw_fourcc = mw_fourcc;
            m_frame_duration = frame_duration;
            bool ret = false;
            if (m_gpu_index < 0){
                ret = create_ff_venc();
            }else {
                ret = create_mw_venc();
            }
            if (!ret) {
                return ret;
            }
            CMWMp4.mw_mp4_video_info_t video_info = new CMWMp4.mw_mp4_video_info_t();
            video_info.codec_type = CMWMp4.mw_mp4_video_type_t.MW_MP4_VIDEO_TYPE_H264;
            video_info.timescale = 1000;
            video_info.width = (UInt16)m_width;
            video_info.height = (UInt16)m_height;
            video_info.h264.sps = IntPtr.Zero;
            video_info.h264.sps_size = 0;
            video_info.h264.pps = IntPtr.Zero;
            video_info.h264.pps_size = 0;
            if (CMWMp4.mw_mp4_status_t.MW_MP4_STATUS_SUCCESS != CMWMp4.mw_mp4_set_video(m_mp4, ref video_info)) {
                Console.WriteLine("mp4 set video fail");
                return false;
            }
            m_p_video_frame = ffmpeg.av_frame_alloc();
            return true;
        }
        bool get_aac_profile(ref byte profile)
        {
            if ((0 == m_p_audio_ctxt->extradata_size) || (null == m_p_audio_ctxt->extradata)){
                Console.WriteLine("not get extradata from ffmpeg");
                return false;
            }
            profile = (byte)((((Int32)(m_p_audio_ctxt->extradata[0] & 0xf8)) >> 3) - 1);
            return true;
        }
        public bool set_audio(Int32 sample_rate, Int32 channels, Int32 bit_per_sample)
        {
            m_sample_rate = sample_rate;
            m_channels = channels;
            m_bit_per_sample = bit_per_sample;
            if (!create_ff_aenc()){
                return false;
            }
            CMWMp4.mw_mp4_audio_info_t audio_info = new CMWMp4.mw_mp4_audio_info_t();
            audio_info.codec_type = CMWMp4.mw_mp4_audio_codec_t.MW_MP4_AUDIO_TYPE_AAC;
            audio_info.timescale = 1000;
            audio_info.sample_rate = (UInt16)sample_rate;
            audio_info.channels = (UInt16)channels;

            if (!get_aac_profile(ref audio_info.profile)) {
                return false;
            }

            if (CMWMp4.mw_mp4_status_t.MW_MP4_STATUS_SUCCESS != CMWMp4.mw_mp4_set_audio(m_mp4, ref audio_info)){
                Console.WriteLine("mp4 set audio fail");
                return false;
            }
            
            return true;
        }
        public bool write_video(Byte[] frame, Int64 pts)
        {
            if (IntPtr.Zero != m_mw_venc) {
                CMWVenc.mw_venc_put_frame_ex(m_mw_venc, frame, pts);
                return true;
            }
            if (null == m_p_video_ctxt) {
                Console.WriteLine("please set video first");
                return false;
            }
            int got_packet;
            AVPacket pkt;

            m_p_video_frame->format = (int)m_p_video_ctxt->pix_fmt;
            m_p_video_frame->width = m_p_video_ctxt->width;
            m_p_video_frame->height = m_p_video_ctxt->height;
            m_p_video_frame->pts = pts;


            /* a hack to avoid data copy with some raw video muxers */
            ffmpeg.av_init_packet(&pkt);
            //nv12
            m_p_video_frame->data0 = (sbyte*)Marshal.UnsafeAddrOfPinnedArrayElement(frame, 0);
            m_p_video_frame->data1 = m_p_video_frame->data0 + m_p_video_ctxt->width * m_p_video_ctxt->height;
            m_p_video_frame->linesize[0] = m_p_video_ctxt->width;
            m_p_video_frame->linesize[1] = m_p_video_ctxt->width;

            ffmpeg.avcodec_encode_video2(m_p_video_ctxt, &pkt, m_p_video_frame, &got_packet);
            if ((0 == got_packet) || (0 == pkt.size)){
                return true;
            }
            if (null == m_mp4){
                return false;
            }
            if (CMWMp4.mw_mp4_status_t.MW_MP4_STATUS_SUCCESS != CMWMp4.mw_mp4_write_video(m_mp4, (IntPtr)pkt.data, (UInt32)pkt.size, (UInt64)pkt.pts)) {
                return false;
            }
            //av_free_packet(&pkt);

            return true;
        }
        public bool write_audio(Byte[] frame, Int32 len, Int64 pts)
        {
            if (null == m_p_audio_ctxt) {
                Console.WriteLine("please set audio first");
            }
            Int32 offset = 0;
            while (offset < len) {
                if (0 == m_audio_buffer_size) {
                    if ((len - offset) < m_audio_frame_size){
                        Marshal.Copy(frame, offset, Marshal.UnsafeAddrOfPinnedArrayElement(m_audio_buffer, 0), len - offset);
                        m_audio_buffer_size = len - offset;
                        return true;
                    }
                }
                else if ((len - offset) >= (m_audio_frame_size - m_audio_buffer_size)){
                    Marshal.Copy(frame, offset, (IntPtr)((ulong)Marshal.UnsafeAddrOfPinnedArrayElement(m_audio_buffer, 0) + (ulong)m_audio_buffer_size), m_audio_frame_size - m_audio_buffer_size);
                    offset += m_audio_frame_size - m_audio_buffer_size;
                    m_audio_buffer_size = m_audio_frame_size;
                    
                } else {
                    Marshal.Copy(frame, offset, (IntPtr)((ulong)Marshal.UnsafeAddrOfPinnedArrayElement(m_audio_buffer, 0) + (ulong)m_audio_buffer_size), len - offset);
                    m_audio_buffer_size += len - offset;
                    return true;
                }
                if (null != m_p_audio_swr){
                    if (0 == m_audio_buffer_size) {
                        m_p_swr_frame->data0 = (sbyte*)Marshal.UnsafeAddrOfPinnedArrayElement(frame, 0);
                        offset += m_audio_frame_size;
                    } else {
                        m_p_swr_frame->data0 = (sbyte*)Marshal.UnsafeAddrOfPinnedArrayElement(m_audio_buffer, 0);
                        m_audio_buffer_size = 0;
                    }
                    int ret = ffmpeg.swr_convert(m_p_audio_swr, &m_p_audio_frame->data0, 1024, &m_p_swr_frame->data0, 1024);
                    if (ret < 0) {
                        Console.WriteLine("ffmpeg swr audio fail");
                        return false;
                    }
                }
                else {
                    if (0 == m_audio_buffer_size){
                        m_p_audio_frame->data0 = (sbyte*)Marshal.UnsafeAddrOfPinnedArrayElement(frame, 0);
                        offset += m_audio_frame_size;
                    }
                    else{
                        m_p_audio_frame->data0 = (sbyte*)Marshal.UnsafeAddrOfPinnedArrayElement(m_audio_buffer, 0);
                        m_audio_buffer_size = 0;
                    }
                }
                m_p_audio_frame->nb_samples = 1024;
                m_p_audio_frame->format = (int)m_p_audio_ctxt->sample_fmt;
                m_p_audio_frame->channel_layout = m_p_audio_ctxt->channel_layout;
                m_p_audio_frame->sample_rate = m_p_audio_ctxt->sample_rate;
                //m_pAFrame->pts = u64TimeStamp;
                int got_packet = 0;
                AVPacket pkt;
                pkt.size = 0;
                pkt.data = null;
                ffmpeg.avcodec_encode_audio2(m_p_audio_ctxt, &pkt, m_p_audio_frame, &got_packet);
                if ((0 == got_packet) || (0 == pkt.size)){
                    continue;
                }
                if (null == m_mp4){
                    return false;
                }
                if (CMWMp4.mw_mp4_status_t.MW_MP4_STATUS_SUCCESS != CMWMp4.mw_mp4_write_audio(m_mp4, (IntPtr)pkt.data, (UInt32)pkt.size, (UInt64)pts)){
                    return false;
                }
            }
            return true;
        }
    }
}
