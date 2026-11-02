/*
 * Starshot.Codecs — 编码器原生垫片 (shim)
 *
 * 薄 C 层,包装三个 LGPL 动态库:
 *   - libx264 (0.164, 8-bit): NV12 -> H.264, CRF 质量
 *   - libx265 (4.3, 10-bit): P010 (planar I420 10-bit) -> HEVC, HDR10
 *   - faac (2.2.0): float32 PCM -> AAC-LC (raw frames + AudioSpecificConfig)
 *
 * 设计要点:
 *   - 通过 LoadLibrary + GetProcAddress 加载三个 DLL,避免生成 import library,
 *     CI 只需 cl 编译本文件 (LGPL 动态链接,满足替换义务)。
 *   - 结构体布局全部封闭在 C 层 (x264_param_t 约 1.4KB,含函数指针/float/对齐),
 *     C# 侧只传平面指针 + 参数名/值,拿到的输出是已按 MP4 sample 布局
 *     ([4字节长度][NAL] 拼接) 的字节流。
 *   - 所有导出函数都是 "失败返回 0/-1, 成功返回 0/句柄" 的简单约定。
 *
 * 三个编码器 DLL 是独立的 LGPL 组件,可单独替换;本垫片随 Starshot 编译。
 *
 * x264/x265 为 LGPL-2.1+,faac 为 LGPL-2.1+。
 */

#include <windows.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "x264.h"
#include "x265.h"
#include "faac.h"

/* ------------------------------------------------------------------ */
/* 函数指针类型 (与 0.164 / 4.3 / 2.2.0 公开头文件一致)              */
/* ------------------------------------------------------------------ */

/* x264 (8-bit, plain 导出; x264_encoder_open 宏展开为 x264_encoder_open_164) */
typedef void (*fn_x264_param_default)(x264_param_t *);
typedef int (*fn_x264_param_default_preset)(x264_param_t *, const char *, const char *);
typedef int (*fn_x264_param_parse)(x264_param_t *, const char *, const char *);
typedef void (*fn_x264_param_cleanup)(x264_param_t *);
typedef x264_t *(*fn_x264_encoder_open)(x264_param_t *);
typedef int (*fn_x264_encoder_headers)(x264_t *, x264_nal_t **, int *);
typedef int (*fn_x264_encoder_encode)(x264_t *, x264_nal_t **, int *, x264_picture_t *, x264_picture_t *);
typedef void (*fn_x264_encoder_close)(x264_t *);
typedef void (*fn_x264_picture_init)(x264_picture_t *);

/* x265 4.3 (x265_encoder_open 宏展开为 x265_encoder_open_217) */
typedef x265_param *(*fn_x265_param_alloc)(void);
typedef void (*fn_x265_param_free)(x265_param *);
typedef void (*fn_x265_param_default)(x265_param *);
typedef int (*fn_x265_param_default_preset)(x265_param *, const char *, const char *);
typedef int (*fn_x265_param_parse)(x265_param *, const char *, const char *);
typedef x265_encoder *(*fn_x265_encoder_open)(x265_param *);
typedef int (*fn_x265_encoder_headers)(x265_encoder *, x265_nal **, uint32_t *);
typedef int (*fn_x265_encoder_encode)(x265_encoder *, x265_nal **, uint32_t *, x265_picture *, x265_picture *);
typedef void (*fn_x265_encoder_close)(x265_encoder *);
typedef void (*fn_x265_picture_init)(x265_param *, x265_picture *);
typedef x265_picture *(*fn_x265_picture_alloc)(void);
typedef void (*fn_x265_picture_free)(x265_picture *);

/* faac 2.2 (faac_status 固定 32 位, 与 int 等价) */
typedef int (*fn_faac_params_init)(faac_params *, uint32_t);
typedef int (*fn_faac_encoder_open)(const faac_params *, faac_encoder **);
typedef int (*fn_faac_encoder_close)(faac_encoder **);
typedef int (*fn_faac_encoder_get_info)(faac_encoder *, faac_encoder_info *);
typedef int (*fn_faac_encoder_asc)(faac_encoder *, const uint8_t **, uint32_t *);
typedef int (*fn_faac_encoder_encode)(faac_encoder *, const void *, uint32_t, uint8_t *, uint32_t, uint32_t *);

/* ------------------------------------------------------------------ */
/* 全局加载状态 (fp_ 前缀避免与头文件里的函数名冲突)                  */
/* ------------------------------------------------------------------ */

static HMODULE g_x264 = NULL;
static HMODULE g_x265 = NULL;
static HMODULE g_faac = NULL;

static fn_x264_param_default fp_x264_param_default;
static fn_x264_param_default_preset fp_x264_param_default_preset;
static fn_x264_param_parse fp_x264_param_parse;
static fn_x264_param_cleanup fp_x264_param_cleanup;
static fn_x264_encoder_open fp_x264_encoder_open;
static fn_x264_encoder_headers fp_x264_encoder_headers;
static fn_x264_encoder_encode fp_x264_encoder_encode;
static fn_x264_encoder_close fp_x264_encoder_close;
static fn_x264_picture_init fp_x264_picture_init;

static fn_x265_param_alloc fp_x265_param_alloc;
static fn_x265_param_free fp_x265_param_free;
static fn_x265_param_default fp_x265_param_default;
static fn_x265_param_default_preset fp_x265_param_default_preset;
static fn_x265_param_parse fp_x265_param_parse;
static fn_x265_encoder_open fp_x265_encoder_open;
static fn_x265_encoder_headers fp_x265_encoder_headers;
static fn_x265_encoder_encode fp_x265_encoder_encode;
static fn_x265_encoder_close fp_x265_encoder_close;
static fn_x265_picture_init fp_x265_picture_init;
static fn_x265_picture_alloc fp_x265_picture_alloc;
static fn_x265_picture_free fp_x265_picture_free;

static fn_faac_params_init fp_faac_params_init;
static fn_faac_encoder_open fp_faac_encoder_open;
static fn_faac_encoder_close fp_faac_encoder_close;
static fn_faac_encoder_get_info fp_faac_encoder_get_info;
static fn_faac_encoder_asc fp_faac_encoder_asc;
static fn_faac_encoder_encode fp_faac_encoder_encode;

/* x264 编码会话: param + 编码器 + 输入 picture (平面由调用方提供) */
typedef struct
{
    x264_param_t *param;
    x264_t *enc;
    x264_picture_t in;
    x264_picture_t out;
} SsX264;

/* x265 编码会话 */
typedef struct
{
    x265_param *param;
    x265_encoder *enc;
    x265_picture *in;
    x265_picture *out;
} SsX265;

/* faac 编码会话 (含解析后的编码器属性) */
typedef struct
{
    faac_encoder *enc;
    uint32_t frameSamples; /* 每声道每帧样本数 (AAC-LC = 1024) */
    uint32_t maxOutBytes;  /* 单次 encode 输出上限 */
} SsFaac;

#define SS_DBG(msg) OutputDebugStringA(msg)

/* 从 DLL 加载一个导出, 失败返回 0 */
static void *loadfn(HMODULE h, const char *name, const char *what)
{
    void *p = (void *)GetProcAddress(h, name);
    if (!p)
    {
        char msg[128];
        wsprintfA(msg, "[Starshot.Codecs] missing export: %s\n", what);
        SS_DBG(msg);
    }
    return p;
}

/* ------------------------------------------------------------------ */
/* 初始化 / 关闭                                                       */
/* ------------------------------------------------------------------ */

/* 从给定路径加载三个编码器 DLL。返回 0 成功; -1 x264; -2 x265; -3 faac。 */
__declspec(dllexport) int ss_init(const char *x264Path, const char *x265Path, const char *faacPath)
{
    if (g_x264 && g_x265 && g_faac)
        return 0; /* 已初始化 */

    g_x264 = LoadLibraryA(x264Path);
    if (!g_x264)
        return -1;
    fp_x264_param_default = (fn_x264_param_default)loadfn(g_x264, "x264_param_default", "x264_param_default");
    fp_x264_param_default_preset = (fn_x264_param_default_preset)loadfn(g_x264, "x264_param_default_preset", "x264_param_default_preset");
    fp_x264_param_parse = (fn_x264_param_parse)loadfn(g_x264, "x264_param_parse", "x264_param_parse");
    fp_x264_param_cleanup = (fn_x264_param_cleanup)loadfn(g_x264, "x264_param_cleanup", "x264_param_cleanup");
    fp_x264_encoder_open = (fn_x264_encoder_open)loadfn(g_x264, "x264_encoder_open_164", "x264_encoder_open_164");
    fp_x264_encoder_headers = (fn_x264_encoder_headers)loadfn(g_x264, "x264_encoder_headers", "x264_encoder_headers");
    fp_x264_encoder_encode = (fn_x264_encoder_encode)loadfn(g_x264, "x264_encoder_encode", "x264_encoder_encode");
    fp_x264_encoder_close = (fn_x264_encoder_close)loadfn(g_x264, "x264_encoder_close", "x264_encoder_close");
    fp_x264_picture_init = (fn_x264_picture_init)loadfn(g_x264, "x264_picture_init", "x264_picture_init");
    if (!fp_x264_param_default || !fp_x264_param_parse || !fp_x264_encoder_open || !fp_x264_encoder_encode)
    {
        FreeLibrary(g_x264);
        g_x264 = NULL;
        return -1;
    }

    g_x265 = LoadLibraryA(x265Path);
    if (!g_x265)
        return -2;
    fp_x265_param_alloc = (fn_x265_param_alloc)loadfn(g_x265, "x265_param_alloc", "x265_param_alloc");
    fp_x265_param_free = (fn_x265_param_free)loadfn(g_x265, "x265_param_free", "x265_param_free");
    fp_x265_param_default = (fn_x265_param_default)loadfn(g_x265, "x265_param_default", "x265_param_default");
    fp_x265_param_default_preset = (fn_x265_param_default_preset)loadfn(g_x265, "x265_param_default_preset", "x265_param_default_preset");
    fp_x265_param_parse = (fn_x265_param_parse)loadfn(g_x265, "x265_param_parse", "x265_param_parse");
    fp_x265_encoder_open = (fn_x265_encoder_open)loadfn(g_x265, "x265_encoder_open_217", "x265_encoder_open_217");
    fp_x265_encoder_headers = (fn_x265_encoder_headers)loadfn(g_x265, "x265_encoder_headers", "x265_encoder_headers");
    fp_x265_encoder_encode = (fn_x265_encoder_encode)loadfn(g_x265, "x265_encoder_encode", "x265_encoder_encode");
    fp_x265_encoder_close = (fn_x265_encoder_close)loadfn(g_x265, "x265_encoder_close", "x265_encoder_close");
    fp_x265_picture_init = (fn_x265_picture_init)loadfn(g_x265, "x265_picture_init", "x265_picture_init");
    fp_x265_picture_alloc = (fn_x265_picture_alloc)loadfn(g_x265, "x265_picture_alloc", "x265_picture_alloc");
    fp_x265_picture_free = (fn_x265_picture_free)loadfn(g_x265, "x265_picture_free", "x265_picture_free");
    if (!fp_x265_param_alloc || !fp_x265_param_parse || !fp_x265_encoder_open || !fp_x265_encoder_encode)
    {
        FreeLibrary(g_x265);
        g_x265 = NULL;
        return -2;
    }

    g_faac = LoadLibraryA(faacPath);
    if (!g_faac)
        return -3;
    fp_faac_params_init = (fn_faac_params_init)loadfn(g_faac, "faac_params_init", "faac_params_init");
    fp_faac_encoder_open = (fn_faac_encoder_open)loadfn(g_faac, "faac_encoder_open", "faac_encoder_open");
    fp_faac_encoder_close = (fn_faac_encoder_close)loadfn(g_faac, "faac_encoder_close", "faac_encoder_close");
    fp_faac_encoder_get_info = (fn_faac_encoder_get_info)loadfn(g_faac, "faac_encoder_get_info", "faac_encoder_get_info");
    fp_faac_encoder_asc = (fn_faac_encoder_asc)loadfn(g_faac, "faac_encoder_asc", "faac_encoder_asc");
    fp_faac_encoder_encode = (fn_faac_encoder_encode)loadfn(g_faac, "faac_encoder_encode", "faac_encoder_encode");
    if (!fp_faac_params_init || !fp_faac_encoder_open || !fp_faac_encoder_encode)
    {
        FreeLibrary(g_faac);
        g_faac = NULL;
        return -3;
    }

    SS_DBG("[Starshot.Codecs] initialized (x264/x265/faac)\n");
    return 0;
}

__declspec(dllexport) void ss_shutdown(void)
{
    if (g_x264) { FreeLibrary(g_x264); g_x264 = NULL; }
    if (g_x265) { FreeLibrary(g_x265); g_x265 = NULL; }
    if (g_faac) { FreeLibrary(g_faac); g_faac = NULL; }
}

/* ------------------------------------------------------------------ */
/* x264: SDR (NV12 8-bit -> H.264)                                     */
/* ------------------------------------------------------------------ */

/* 创建 param (malloc + 默认值)。返回句柄, 0 = 失败 */
__declspec(dllexport) void *ss_x264_param_create(void)
{
    if (!fp_x264_param_default)
        return 0;
    x264_param_t *p = (x264_param_t *)calloc(1, sizeof(x264_param_t));
    if (!p)
        return 0;
    fp_x264_param_default(p);
    return p;
}

/* 释放 param (param_cleanup 释放内部 parse 分配 + 结构体本身) */
__declspec(dllexport) void ss_x264_param_free(void *h)
{
    if (!h)
        return;
    if (fp_x264_param_cleanup)
        fp_x264_param_cleanup((x264_param_t *)h);
    free(h);
}

__declspec(dllexport) int ss_x264_preset(void *h, const char *preset, const char *tune)
{
    if (!h || !fp_x264_param_default_preset)
        return -1;
    return fp_x264_param_default_preset((x264_param_t *)h, preset, tune);
}

__declspec(dllexport) int ss_x264_parse(void *h, const char *name, const char *value)
{
    if (!h || !fp_x264_param_parse)
        return -1;
    return fp_x264_param_parse((x264_param_t *)h, name, value);
}

/* 尺寸 + 帧率 (param_parse 不覆盖这些字段, 必须直接赋值) */
__declspec(dllexport) void ss_x264_set_geometry(void *h, int width, int height, int fpsNum, int fpsDen)
{
    if (!h)
        return;
    x264_param_t *p = (x264_param_t *)h;
    p->i_width = width;
    p->i_height = height;
    p->i_fps_num = (uint32_t)fpsNum;
    p->i_fps_den = (uint32_t)fpsDen;
}

/* 色域 + NV12 (SDR: BT.709 limited; 与 muxer 写的 colr nclx 对应) */
__declspec(dllexport) void ss_x264_set_color(void *h, int colorprim, int transfer, int matrix, int fullrange)
{
    if (!h)
        return;
    x264_param_t *p = (x264_param_t *)h;
    p->i_csp = X264_CSP_NV12;
    p->vui.i_colorprim = colorprim;
    p->vui.i_transfer = transfer;
    p->vui.i_colmatrix = matrix;
    p->vui.b_fullrange = fullrange;
}

__declspec(dllexport) void ss_x264_set_crf(void *h, double crf, int keyint, int bframes, int threads)
{
    if (!h)
        return;
    x264_param_t *p = (x264_param_t *)h;
    p->rc.i_rc_method = X264_RC_CRF;
    p->rc.f_rf_constant = (float)crf;
    if (keyint > 0)
        p->i_keyint_max = keyint;
    p->i_bframe = bframes;
    if (threads > 0)
        p->i_threads = threads;
}

/* Switch rate control to ABR (target average bitrate, kb/s): file size ~=
 * bitrate x duration. Call AFTER ss_x264_set_crf (keyint/bframes/threads keep
 * the values set_crf set). kbps <= 0 = no-op (keeps the CRF mode set_crf set). */
__declspec(dllexport) void ss_x264_set_bitrate(void *h, int kbps)
{
    if (!h || kbps <= 0)
        return;
    x264_param_t *p = (x264_param_t *)h;
    p->rc.i_rc_method = X264_RC_ABR;
    p->rc.i_bitrate = kbps;
}

/* 打开编码器 (picture 在会话内, 平面由调用方填充) */
__declspec(dllexport) void *ss_x264_open(void *param)
{
    if (!param || !fp_x264_encoder_open)
        return 0;
    SsX264 *s = (SsX264 *)calloc(1, sizeof(SsX264));
    if (!s)
        return 0;
    s->param = (x264_param_t *)param;
    s->enc = fp_x264_encoder_open(s->param);
    if (!s->enc)
    {
        free(s);
        return 0;
    }
    if (fp_x264_picture_init)
    {
        fp_x264_picture_init(&s->in);
        fp_x264_picture_init(&s->out);
    }
    return s;
}

/* 把 x264 NAL 写为 [len4][NAL]... 拼接 (MP4 avc1 sample 布局)。
 * b_annexb=0 时每个 NAL 的 p_payload 本身就以 4 字节大端长度开头, 直接整体拷贝 */
static int x264_write_nals(x264_nal_t *nals, int count, uint8_t *buf, int cap)
{
    int total = 0;
    for (int i = 0; i < count; i++)
    {
        int nlen = nals[i].i_payload; /* 含 4 字节长度前缀 */
        if (nlen <= 4 || total + nlen > cap)
            return -1;
        memcpy(buf + total, nals[i].p_payload, nlen);
        total += nlen;
    }
    return total;
}

/* 取 SPS/PPS: 输出 [len4][SPS][len4][PPS] (avcC 的 NAL 数组布局)。
 * 返回写入字节数, 负数 = 缓冲区不足/错误。 */
__declspec(dllexport) int ss_x264_headers(void *h, uint8_t *buf, int cap, int *outBytes)
{
    if (!h || !buf || !fp_x264_encoder_headers)
        return -1;
    SsX264 *s = (SsX264 *)h;
    x264_nal_t *nals = NULL;
    int count = 0;
    int r = fp_x264_encoder_headers(s->enc, &nals, &count);
    if (r < 0 || count == 0)
        return -1;
    int written = x264_write_nals(nals, count, buf, cap);
    if (outBytes)
        *outBytes = written;
    return written;
}

/* 编码一帧 NV12。y/uv 为调用方缓冲区指针 (stride 按字节)。
 * 输出 [len4][NAL]... 拼接 (MP4 sample 布局)。
 * 返回写入字节数; 0 = 帧还在编码器内部 (正常, B 帧延迟); 负数 = 错误。 */
__declspec(dllexport) int ss_x264_encode(void *h, const uint8_t *y, int strideY, const uint8_t *uv, int strideUV, int64_t pts, uint8_t *buf, int cap, int *outBytes)
{
    if (!h || !y || !uv || !fp_x264_encoder_encode)
        return -1;
    SsX264 *s = (SsX264 *)h;
    s->in.img.i_csp = X264_CSP_NV12;
    s->in.img.i_plane = 2;
    s->in.img.i_stride[0] = strideY;
    s->in.img.i_stride[1] = strideUV;
    s->in.img.plane[0] = (uint8_t *)y;
    s->in.img.plane[1] = (uint8_t *)uv;
    s->in.i_pts = pts;
    x264_nal_t *nals = NULL;
    int count = 0;
    int r = fp_x264_encoder_encode(s->enc, &nals, &count, &s->in, &s->out);
    if (r < 0)
        return -1;
    if (count == 0)
        return 0;
    int written = x264_write_nals(nals, count, buf, cap);
    if (outBytes)
        *outBytes = written;
    return written;
}

/* Flush: repeatedly encode(NULL) until drained. Returns the bytes written this
 * round (may be 0). On buffer overflow it returns -1 but still reports in
 * *outBytes how many bytes were already written (all complete NALs; the caller
 * muxes that part, grows the buffer, and retries - the remaining frames are
 * still inside the encoder and can be flushed on). */
__declspec(dllexport) int ss_x264_flush(void *h, uint8_t *buf, int cap, int *outBytes)
{
    if (!h || !fp_x264_encoder_encode)
        return -1;
    SsX264 *s = (SsX264 *)h;
    int total = 0;
    for (int i = 0; i < 256; i++)
    {
        x264_nal_t *nals = NULL;
        int count = 0;
        int r = fp_x264_encoder_encode(s->enc, &nals, &count, NULL, &s->out);
        if (r < 0)
            goto fail;
        if (count == 0)
            break;
        int written = x264_write_nals(nals, count, buf + total, cap - total);
        if (written < 0)
            goto fail;
        total += written;
    }
    if (outBytes)
        *outBytes = total;
    return total;
fail:
    if (outBytes)
        *outBytes = total;
    return -1;
}

__declspec(dllexport) void ss_x264_close(void *h)
{
    if (!h)
        return;
    SsX264 *s = (SsX264 *)h;
    if (s->enc && fp_x264_encoder_close)
        fp_x264_encoder_close(s->enc);
    free(s);
}

/* ------------------------------------------------------------------ */
/* x265: HDR (planar P010 10-bit I420 -> HEVC)                          */
/* ------------------------------------------------------------------ */

__declspec(dllexport) void *ss_x265_param_create(void)
{
    if (!fp_x265_param_alloc || !fp_x265_param_default)
        return 0;
    x265_param *p = fp_x265_param_alloc();
    if (!p)
        return 0;
    /* param_alloc 只零初始化, 需要再填默认值 */
    fp_x265_param_default(p);
    return p;
}

__declspec(dllexport) void ss_x265_param_free(void *h)
{
    if (h && fp_x265_param_free)
        fp_x265_param_free((x265_param *)h);
}

__declspec(dllexport) int ss_x265_preset(void *h, const char *preset, const char *tune)
{
    if (!h || !fp_x265_param_default_preset)
        return -1;
    return fp_x265_param_default_preset((x265_param *)h, preset, tune);
}

__declspec(dllexport) int ss_x265_parse(void *h, const char *name, const char *value)
{
    if (!h || !fp_x265_param_parse)
        return -1;
    return fp_x265_param_parse((x265_param *)h, name, value);
}

__declspec(dllexport) void ss_x265_set_geometry(void *h, int width, int height, int fpsNum, int fpsDen)
{
    if (!h)
        return;
    x265_param *p = (x265_param *)h;
    p->sourceWidth = width;
    p->sourceHeight = height;
    p->fpsNum = (uint32_t)fpsNum;
    p->fpsDenom = (uint32_t)fpsDen;
}

/* HDR10: 10-bit planar I420 + BT.2020 / PQ (smpte2084) */
__declspec(dllexport) void ss_x265_set_hdr(void *h, int bitDepth, int colorprim, int transfer, int matrix)
{
    if (!h)
        return;
    x265_param *p = (x265_param *)h;
    p->internalBitDepth = bitDepth; /* 10 */
    p->internalCsp = X265_CSP_I420; /* planar: Y/U/V 三个 16-bit 平面 */
    /* 4.3 里颜色域字段在 vui 子结构里, 且必须置 VUI color description 标志 */
    p->vui.bEnableColorDescriptionPresentFlag = 1;
    p->vui.colorPrimaries = colorprim;
    p->vui.transferCharacteristics = transfer;
    p->vui.matrixCoeffs = matrix;
}

__declspec(dllexport) void ss_x265_set_crf(void *h, double crf, int keyint, int bframes, int threads)
{
    if (!h)
        return;
    x265_param *p = (x265_param *)h;
    p->rc.rateControlMode = X265_RC_CRF;
    p->rc.rfConstant = crf;
    if (keyint > 0)
        p->keyframeMax = keyint;
    p->bframes = bframes;
    if (threads > 0)
        p->frameNumThreads = threads;
}

/* Switch rate control to ABR (target average bitrate, kb/s): file size ~=
 * bitrate x duration. Call AFTER ss_x265_set_crf (keyint/bframes/threads keep
 * the values set_crf set). kbps <= 0 = no-op (keeps the CRF mode set_crf set). */
__declspec(dllexport) void ss_x265_set_bitrate(void *h, int kbps)
{
    if (!h || kbps <= 0)
        return;
    x265_param *p = (x265_param *)h;
    p->rc.rateControlMode = X265_RC_ABR;
    p->rc.bitrate = kbps;
}

__declspec(dllexport) void *ss_x265_open(void *param)
{
    if (!param || !fp_x265_encoder_open)
        return 0;
    SsX265 *s = (SsX265 *)calloc(1, sizeof(SsX265));
    if (!s)
        return 0;
    s->param = (x265_param *)param;
    s->enc = fp_x265_encoder_open(s->param);
    if (!s->enc)
    {
        free(s);
        return 0;
    }
    s->in = fp_x265_picture_alloc();
    s->out = fp_x265_picture_alloc();
    if (!s->in || !s->out)
    {
        if (fp_x265_encoder_close)
            fp_x265_encoder_close(s->enc);
        if (s->in)
            fp_x265_picture_free(s->in);
        if (s->out)
            fp_x265_picture_free(s->out);
        free(s);
        return 0;
    }
    fp_x265_picture_init(s->param, s->in);
    fp_x265_picture_init(s->param, s->out);
    return s;
}

/* x265 NAL 直接拼接 (MP4 hvc1 sample 布局)。
 * 前提: 调用方必须已设 bAnnexB=0 (ss_x265_parse "annexb" "0"),
 * 否则 NAL 带 Annex B 起始码。bAnnexB=0 时每个 NAL 的 payload 本身就以
 * 4 字节大端长度开头 (见 x265 NALList::serialize: payload 指向前缀,
 * sizeBytes 含前缀), 直接整体拷贝即可 */
static int x265_write_nals(x265_nal *nals, int count, uint8_t *buf, int cap)
{
    int total = 0;
    for (int i = 0; i < count; i++)
    {
        uint32_t nlen = nals[i].sizeBytes; /* 含 4 字节长度前缀 */
        if (nlen <= 4 || total + (int)nlen > cap)
            return -1;
        memcpy(buf + total, nals[i].payload, nlen);
        total += (int)nlen;
    }
    return total;
}

/* 取 SPS/PPS (可能含 VPS)。返回写入字节数 + NAL 个数 (hvcC 需要)。 */
__declspec(dllexport) int ss_x265_headers(void *h, uint8_t *buf, int cap, int *outBytes, int *outNumNals)
{
    if (!h || !buf || !fp_x265_encoder_headers)
        return -1;
    SsX265 *s = (SsX265 *)h;
    x265_nal *nals = NULL;
    uint32_t count = 0;
    int r = fp_x265_encoder_headers(s->enc, &nals, &count);
    if (r < 0 || count == 0)
        return -1;
    int written = x265_write_nals(nals, (int)count, buf, cap);
    if (outBytes)
        *outBytes = written;
    if (outNumNals)
        *outNumNals = (int)count;
    return written;
}

/* 编码一帧 planar P010 (Y/U/V 各 2 字节/样本, stride 按字节)。
 * pts 单位: 100ns。输出 [len4][NAL]... 拼接。 */
__declspec(dllexport) int ss_x265_encode(void *h, const uint8_t *y, int strideY, const uint8_t *u, int strideU, const uint8_t *v, int strideV, int64_t pts, uint8_t *buf, int cap, int *outBytes)
{
    if (!h || !y || !u || !v || !fp_x265_encoder_encode)
        return -1;
    SsX265 *s = (SsX265 *)h;
    x265_picture *pic = s->in;
    pic->planes[0] = (void *)y;
    pic->planes[1] = (void *)u;
    pic->planes[2] = (void *)v;
    pic->stride[0] = strideY;
    pic->stride[1] = strideU;
    pic->stride[2] = strideV;
    pic->pts = pts;
    x265_nal *nals = NULL;
    uint32_t count = 0;
    int r = fp_x265_encoder_encode(s->enc, &nals, &count, pic, s->out);
    if (r < 0)
        return -1;
    if (count == 0)
        return 0;
    int written = x265_write_nals(nals, (int)count, buf, cap);
    if (outBytes)
        *outBytes = written;
    return written;
}

/* Same contract as ss_x264_flush: -1 on overflow with *outBytes = the
 * recoverable partial (complete NALs only; remaining frames stay queued). */
__declspec(dllexport) int ss_x265_flush(void *h, uint8_t *buf, int cap, int *outBytes)
{
    if (!h || !fp_x265_encoder_encode)
        return -1;
    SsX265 *s = (SsX265 *)h;
    int total = 0;
    for (int i = 0; i < 256; i++)
    {
        x265_nal *nals = NULL;
        uint32_t count = 0;
        int r = fp_x265_encoder_encode(s->enc, &nals, &count, NULL, s->out);
        if (r < 0)
            goto fail;
        if (count == 0)
            break;
        int written = x265_write_nals(nals, (int)count, buf + total, cap - total);
        if (written < 0)
            goto fail;
        total += written;
    }
    if (outBytes)
        *outBytes = total;
    return total;
fail:
    if (outBytes)
        *outBytes = total;
    return -1;
}

__declspec(dllexport) void ss_x265_close(void *h)
{
    if (!h)
        return;
    SsX265 *s = (SsX265 *)h;
    if (s->enc && fp_x265_encoder_close)
        fp_x265_encoder_close(s->enc);
    if (s->in && fp_x265_picture_free)
        fp_x265_picture_free(s->in);
    if (s->out && fp_x265_picture_free)
        fp_x265_picture_free(s->out);
    free(s);
}

/* ------------------------------------------------------------------ */
/* faac: WASAPI float32 -> AAC-LC                                       */
/* ------------------------------------------------------------------ */

/* 打开编码器。bitRate 为每声道 bps (0 = VBR, 用 quantQuality 1..5000)。
 * 成功返回会话句柄, 并把解析后的 frameSamples / maxOutBytes 写回。 */
__declspec(dllexport) void *ss_faac_open(int sampleRate, int channels, int bitRate, int quantQuality, uint32_t *frameSamples, uint32_t *maxOutBytes)
{
    if (!fp_faac_params_init || !fp_faac_encoder_open)
        return 0;
    faac_params p;
    if (fp_faac_params_init(&p, (uint32_t)sizeof(faac_params)) != 0)
        return 0;
    p.sample_rate = (uint32_t)sampleRate;
    p.num_channels = (uint32_t)channels;
    p.mpeg_version = FAAC_MPEG4;
    p.object_type = FAAC_OBJ_LOW; /* AAC-LC */
    p.joint_mode = FAAC_JOINT_MIXED;
    p.bit_rate = (uint32_t)bitRate;
    p.quant_quality = (uint32_t)quantQuality;
    p.output_format = FAAC_STREAM_RAW;
    p.input_format = FAAC_INPUT_FLOAT;
    p.short_control = FAAC_SHORTCTL_NORMAL;
    faac_encoder *enc = NULL;
    if (fp_faac_encoder_open(&p, &enc) != 0 || !enc)
        return 0;
    SsFaac *s = (SsFaac *)calloc(1, sizeof(SsFaac));
    if (!s)
    {
        fp_faac_encoder_close(&enc);
        return 0;
    }
    s->enc = enc;
    if (fp_faac_encoder_get_info)
    {
        faac_encoder_info info;
        memset(&info, 0, sizeof(info));
        info.struct_size = (uint32_t)sizeof(faac_encoder_info);
        if (fp_faac_encoder_get_info(enc, &info) == 0)
        {
            s->frameSamples = info.frame_samples;
            s->maxOutBytes = info.max_output_bytes;
        }
    }
    if (frameSamples)
        *frameSamples = s->frameSamples;
    if (maxOutBytes)
        *maxOutBytes = s->maxOutBytes;
    return s;
}

/* 取 AudioSpecificConfig (esds 的 decoderSpecificInfo)。
 * 返回拷贝的字节数, 负数 = 失败。 */
__declspec(dllexport) int ss_faac_asc(void *h, uint8_t *buf, int cap, int *outLen)
{
    if (!h || !buf || !fp_faac_encoder_asc)
        return -1;
    SsFaac *s = (SsFaac *)h;
    const uint8_t *asc = NULL;
    uint32_t len = 0;
    if (fp_faac_encoder_asc(s->enc, &asc, &len) != 0 || !asc)
        return -1;
    if (len > (uint32_t)cap)
        return -1;
    memcpy(buf, asc, len);
    if (outLen)
        *outLen = (int)len;
    return (int)len;
}

/* 编码 float32 交错 PCM。inSamples 为总样本数 (所有声道合计)。
 * 每次最多产出一帧; 结束时传 inSamples=0 冲刷直到返回 0。
 * 返回写入字节数 (0 = 本帧还在累积, 正常); 负数 = 错误。 */
__declspec(dllexport) int ss_faac_encode(void *h, const float *in, uint32_t inSamples, uint8_t *out, uint32_t cap, uint32_t *outWritten)
{
    if (!h || !fp_faac_encoder_encode)
        return -1;
    SsFaac *s = (SsFaac *)h;
    uint32_t written = 0;
    int r = fp_faac_encoder_encode(s->enc, in, inSamples, out, cap, &written);
    if (outWritten)
        *outWritten = written;
    if (r != 0)
        return -1;
    return (int)written;
}

__declspec(dllexport) void ss_faac_close(void *h)
{
    if (!h)
        return;
    SsFaac *s = (SsFaac *)h;
    if (s->enc && fp_faac_encoder_close)
        fp_faac_encoder_close(&s->enc);
    free(s);
}
