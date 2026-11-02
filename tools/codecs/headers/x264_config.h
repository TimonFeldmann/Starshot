/* x264_config.h — 最小化手写版本（x264 0.164 系列，8-bit 构建）
 *
 * x264.h 会 include 本文件。正常由 configure 生成，这里只保留
 * 编译头文件所需的宏。对应我们随包发布的 libx264-164.dll
 * （OBS/m-labs 构建线，plain x264_* 导出为 8-bit API，x264_10_* 为 10-bit）。
 *
 * LGPL-2.1+，与 x264 本体相同。
 */
#ifndef X264_CONFIG_H
#define X264_CONFIG_H

/* 8-bit 构建（plain x264_* 导出） */
#define X264_BIT_DEPTH 8
/* 支持全部色度格式 */
#define X264_CSP 7
/* 0 = 无限制 */
#define X264_CHROMA_FORMAT 0

#endif /* X264_CONFIG_H */
