// Интерфейсы APO из Windows SDK (audioenginebaseapo.h / audiomediatype.h). В MinGW их нет,
// а базового класса CBaseAudioProcessingObject (audioenginebaseapo.lib) — тем более, поэтому
// объявляем сами: порядок методов и GUID один в один с SDK.
#pragma once
#include <windows.h>
#include <unknwn.h>
#include <objbase.h>
#include <mmreg.h>
#include <propsys.h>
#include <mmdeviceapi.h>
#include <audioapotypes.h>

typedef struct _UNCOMPRESSEDAUDIOFORMAT {
    GUID guidFormatType;
    DWORD dwSamplesPerFrame;
    DWORD dwBytesPerSampleContainer;
    DWORD dwValidBitsPerSample;
    FLOAT fFramesPerSecond;
    DWORD dwChannelMask;
} UNCOMPRESSEDAUDIOFORMAT;

#define AUDIOMEDIATYPE_EQUAL_FORMAT_TYPES     0x00000002
#define AUDIOMEDIATYPE_EQUAL_FORMAT_DATA      0x00000004
#define AUDIOMEDIATYPE_EQUAL_FORMAT_USER_DATA 0x00000008

struct IAudioMediaType : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE IsCompressedFormat(BOOL* pfCompressed) = 0;
    virtual HRESULT STDMETHODCALLTYPE IsEqual(IAudioMediaType* pIAudioType, DWORD* pdwFlags) = 0;
    virtual const WAVEFORMATEX* STDMETHODCALLTYPE GetAudioFormat() = 0;
    virtual HRESULT STDMETHODCALLTYPE GetUncompressedAudioFormat(UNCOMPRESSEDAUDIOFORMAT* pFormat) = 0;
};

enum APO_CONNECTION_BUFFER_TYPE {
    APO_CONNECTION_BUFFER_TYPE_ALLOCATED = 0,
    APO_CONNECTION_BUFFER_TYPE_EXTERNAL = 1,
    APO_CONNECTION_BUFFER_TYPE_DEPENDANT = 2
};

struct APO_CONNECTION_DESCRIPTOR {
    APO_CONNECTION_BUFFER_TYPE Type;
    UINT_PTR pBuffer;
    UINT32 u32MaxFrameCount;
    IAudioMediaType* pFormat;
    UINT32 u32Signature;
};

enum APO_FLAG {
    APO_FLAG_NONE = 0,
    APO_FLAG_INPLACE = 0x1,
    APO_FLAG_SAMPLESPERFRAME_MUST_MATCH = 0x2,
    APO_FLAG_FRAMESPERSECOND_MUST_MATCH = 0x4,
    APO_FLAG_BITSPERSAMPLE_MUST_MATCH = 0x8,
    APO_FLAG_MIXER = 0x10,
    APO_FLAG_DEFAULT = 0xE
};

struct APO_REG_PROPERTIES {
    CLSID clsid;
    APO_FLAG Flags;
    WCHAR szFriendlyName[256];
    WCHAR szCopyrightInfo[256];
    UINT32 u32MajorVersion;
    UINT32 u32MinorVersion;
    UINT32 u32MinInputConnections;
    UINT32 u32MaxInputConnections;
    UINT32 u32MinOutputConnections;
    UINT32 u32MaxOutputConnections;
    UINT32 u32MaxInstances;
    UINT32 u32NumAPOInterfaces;
    IID iidAPOInterfaceList[1];
};

struct APOInitBaseStruct {
    UINT32 cbSize;
    CLSID clsid;
};

struct APOInitSystemEffects {
    APOInitBaseStruct APOInit;
    IPropertyStore* pAPOEndpointProperties;
    IPropertyStore* pAPOSystemEffectsProperties;
    void* pReserved;
    IMMDeviceCollection* pDeviceCollection;
};

struct APOInitSystemEffects2 {
    APOInitBaseStruct APOInit;
    IPropertyStore* pAPOEndpointProperties;
    IPropertyStore* pAPOSystemEffectsProperties;
    void* pReserved;
    IMMDeviceCollection* pDeviceCollection;
    UINT nSoftwareIoDeviceInCollection;
    UINT nSoftwareIoConnectorIndex;
    GUID AudioProcessingMode;
    BOOL InitializeForDiscoveryOnly;
};

struct IAudioProcessingObjectRT : public IUnknown {
    virtual void STDMETHODCALLTYPE APOProcess(UINT32 u32NumInputConnections, APO_CONNECTION_PROPERTY** ppInputConnections,
                                              UINT32 u32NumOutputConnections, APO_CONNECTION_PROPERTY** ppOutputConnections) = 0;
    virtual UINT32 STDMETHODCALLTYPE CalcInputFrames(UINT32 u32OutputFrameCount) = 0;
    virtual UINT32 STDMETHODCALLTYPE CalcOutputFrames(UINT32 u32InputFrameCount) = 0;
};

struct IAudioProcessingObjectConfiguration : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE LockForProcess(UINT32 u32NumInputConnections, APO_CONNECTION_DESCRIPTOR** ppInputConnections,
                                                     UINT32 u32NumOutputConnections, APO_CONNECTION_DESCRIPTOR** ppOutputConnections) = 0;
    virtual HRESULT STDMETHODCALLTYPE UnlockForProcess() = 0;
};

struct IAudioProcessingObject : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE Reset() = 0;
    virtual HRESULT STDMETHODCALLTYPE GetLatency(HNSTIME* pTime) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetRegistrationProperties(APO_REG_PROPERTIES** ppRegProps) = 0;
    virtual HRESULT STDMETHODCALLTYPE Initialize(UINT32 cbDataSize, BYTE* pbyData) = 0;
    virtual HRESULT STDMETHODCALLTYPE IsInputFormatSupported(IAudioMediaType* pOppositeFormat, IAudioMediaType* pRequestedInputFormat,
                                                             IAudioMediaType** ppSupportedInputFormat) = 0;
    virtual HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(IAudioMediaType* pOppositeFormat, IAudioMediaType* pRequestedOutputFormat,
                                                              IAudioMediaType** ppSupportedOutputFormat) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetInputChannelCount(UINT32* pu32ChannelCount) = 0;
};

struct IAudioSystemEffects : public IUnknown {};

struct IAudioSystemEffects2 : public IAudioSystemEffects {
    virtual HRESULT STDMETHODCALLTYPE GetEffectsList(LPGUID* ppEffectsIds, UINT* pcEffects, HANDLE Event) = 0;
};

// {4E997F73-B71F-4798-873B-ED7DFCF15B4D}
static const IID IID_IAudioMediaType_ = {0x4E997F73, 0xB71F, 0x4798, {0x87, 0x3B, 0xED, 0x7D, 0xFC, 0xF1, 0x5B, 0x4D}};
// {9E1D6A6D-DDBC-4E95-A4C7-AD64BA37846C}
static const IID IID_IAudioProcessingObjectRT_ = {0x9E1D6A6D, 0xDDBC, 0x4E95, {0xA4, 0xC7, 0xAD, 0x64, 0xBA, 0x37, 0x84, 0x6C}};
// {0E5ED805-ABA6-49C3-8F9A-2B8C889C4FA8}
static const IID IID_IAudioProcessingObjectConfiguration_ = {0x0E5ED805, 0xABA6, 0x49C3, {0x8F, 0x9A, 0x2B, 0x8C, 0x88, 0x9C, 0x4F, 0xA8}};
// {FD7F2B29-24D0-4B5C-B177-592C39F9CA10}
static const IID IID_IAudioProcessingObject_ = {0xFD7F2B29, 0x24D0, 0x4B5C, {0xB1, 0x77, 0x59, 0x2C, 0x39, 0xF9, 0xCA, 0x10}};
// {5FA00F27-ADD6-499A-8A9D-6B98521FA75B}
static const IID IID_IAudioSystemEffects_ = {0x5FA00F27, 0xADD6, 0x499A, {0x8A, 0x9D, 0x6B, 0x98, 0x52, 0x1F, 0xA7, 0x5B}};
// {BAFE99D2-7436-44CE-9E0E-4D89AFBFFF56}
static const IID IID_IAudioSystemEffects2_ = {0xBAFE99D2, 0x7436, 0x44CE, {0x9E, 0x0E, 0x4D, 0x89, 0xAF, 0xBF, 0xFF, 0x56}};
// KSDATAFORMAT_SUBTYPE_IEEE_FLOAT {00000003-0000-0010-8000-00AA00389B71}
static const GUID SUBTYPE_IEEE_FLOAT_ = {0x00000003, 0x0000, 0x0010, {0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71}};

#define APOERR_ALREADY_INITIALIZED   ((HRESULT)0x887D0001L)
#define APOERR_NOT_INITIALIZED       ((HRESULT)0x887D0002L)
#define APOERR_FORMAT_NOT_SUPPORTED  ((HRESULT)0x887D0003L)
#define APOERR_INVALID_APO_CLSID     ((HRESULT)0x887D0004L)
#define APOERR_ALREADY_UNLOCKED      ((HRESULT)0x887D0006L)
#define APOERR_NUM_CONNECTIONS_INVALID ((HRESULT)0x887D0007L)
#define APOERR_APO_LOCKED            ((HRESULT)0x887D000AL)
