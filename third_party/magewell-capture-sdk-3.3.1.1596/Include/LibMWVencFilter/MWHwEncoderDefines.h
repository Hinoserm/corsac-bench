#pragma once

#include <initguid.h>
#include "../LibMWVenc/mw_venc.h"

#define MW_ENC_FILTER_NAME L"Magewell Hardware Encoder"

/*
*	filter CLSID
*/
//filter {7D9E6600-FF98-4C1B-8FD5-1893067CED5D}
static const GUID CLSID_MW_HwEncoder = 
{ 0x7d9e6600, 0xff98, 0x4c1b, { 0x8f, 0xd5, 0x18, 0x93, 0x6, 0x7c, 0xed, 0x5d } };

// {ABD83DE5-D169-40E7-9B1F-42414CD2D2D8}
DEFINE_GUID(CLSID_MW_HwEnProPage,
	0xabd83de5, 0xd169, 0x40e7, 0x9b, 0x1f, 0x42, 0x41, 0x4c, 0xd2, 0xd2, 0xd8);

/*
*	MediaData CLSID
*/
// 43564548-0000-0010-8000-00AA00389B71
DEFINE_GUID(MEDIASUBTYPE_HEVC,
	MAKEFOURCC('H', 'E', 'V', 'C'), 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

//MEDIASUBTYPE_I420 {30323449-0000-0010-8000-00AA00389B71}
DEFINE_GUID(MEDIASUBTYPE_I420,
	MAKEFOURCC('I', '4', '2', '0'), 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

DEFINE_GUID(MEDIASUBTYPE_NV21,
	MAKEFOURCC('N', 'V', '2', '1'), 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);


/*
*	filter IID
*/
//encode interface, {C861AA90-A35A-4129-BC68-BDCF47F7F593}
static const GUID IID_IMW_HWEnc =
{ 0xc861aa90, 0xa35a, 0x4129, { 0xbc, 0x68, 0xbd, 0xcf, 0x47, 0xf7, 0xf5, 0x93 } };

/*
*	filter interface
*/
DECLARE_INTERFACE_(I_MWHwEncoder, IUnknown)
{
	STDMETHOD(GetConnected)()PURE;

	STDMETHOD(get_gpu_num)(int32_t * num)PURE;
	STDMETHOD(get_gpu_info_by_index)(int32_t index, mw_venc_gpu_info_t *info)PURE;
	STDMETHOD(get_default_param) (mw_venc_param_t *p_param)PURE;

	STDMETHOD(set_param)(int index, mw_venc_param_t param)PURE;
	STDMETHOD(get_param)(int* index, mw_venc_param_t* param)PURE;

	STDMETHOD(SetEncoderFilter)() PURE; //Re Create
};
